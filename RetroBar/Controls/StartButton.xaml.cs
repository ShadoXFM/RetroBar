#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for StartButton.xaml
    /// </summary>
    public partial class StartButton : UserControl
    {
        private FloatingStartButton? floatingStartButton;
        private bool allowOpenStart;
        private bool visibilityChanged;
        private DelayedActivationHandler? dragHandler;
        private readonly DispatcherTimer pendingOpenTimer;

        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(Taskbar), typeof(StartButton));
        public static DependencyProperty StartMenuMonitorProperty = DependencyProperty.Register(nameof(StartMenuMonitor), typeof(StartMenuMonitor), typeof(StartButton));

        public Taskbar Host
        {
            get { return (Taskbar)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        public StartMenuMonitor StartMenuMonitor
        {
            get { return (StartMenuMonitor)GetValue(StartMenuMonitorProperty); }
            set { SetValue(StartMenuMonitorProperty, value); }
        }

        /// <summary>Only the logo, on a square button (the Windows 11 style of the taskbar).</summary>
        public static DependencyProperty IconOnlyProperty = DependencyProperty.Register(nameof(IconOnly), typeof(bool), typeof(StartButton),
            new PropertyMetadata(false, (d, e) => ((StartButton)d).ApplyIconOnly()));

        public bool IconOnly
        {
            get { return (bool)GetValue(IconOnlyProperty); }
            set { SetValue(IconOnlyProperty, value); }
        }

        private void ApplyIconOnly()
        {
            Start.LayoutUpdated -= Start_LayoutUpdatedForSquare;
            if (IconOnly)
            {
                StartLabelText.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(StartLogoHost, 2);
                StartLogoHost.HorizontalAlignment = HorizontalAlignment.Center;
                // No room kept for the text beside the logo.
                StartLogo.Margin = new Thickness(0);
                StartLogoHost.RenderTransform = null;
                logoDx = logoDy = 0;
                logoPasses = 0;
                Start.LayoutUpdated += Start_LayoutUpdatedForSquare;
            }
            else
            {
                StartLabelText.Visibility = Visibility.Visible;
                Grid.SetColumnSpan(StartLogoHost, 1);
                StartLogoHost.ClearValue(HorizontalAlignmentProperty);
                StartLogoHost.RenderTransform = null;
                StartLogo.ClearValue(MarginProperty);
                Start.ClearValue(WidthProperty);
            }
        }

        // How far (in DIPs, as the logo is shown) the middle of the logo's visible drawing is from the middle of its image: the pixels that are
        // opaque and not dark, so a shadow is not counted.
        private (double X, double Y) ArtOffset(Rect shown)
        {
            if (StartLogo.Source is not System.Windows.Media.Imaging.BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return (0, 0);
            }

            try
            {
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                byte[] data = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(data, stride, 0);
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int y = 0; y < converted.PixelHeight; y++)
                {
                    for (int x = 0; x < converted.PixelWidth; x++)
                    {
                        int i = y * stride + x * 4;
                        if (data[i + 3] >= 160 && Math.Max(data[i], Math.Max(data[i + 1], data[i + 2])) >= 110)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }

                if (maxX < 0)
                {
                    return (0, 0);
                }

                // Drawn as large as fits (Uniform), in the middle of the image's place.
                double scale = Math.Min(shown.Width / converted.PixelWidth, shown.Height / converted.PixelHeight);
                return (((minX + maxX + 1) / 2.0 - converted.PixelWidth / 2.0) * scale, ((minY + maxY + 1) / 2.0 - converted.PixelHeight / 2.0) * scale);
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }

        private double logoDx;
        private double logoDy;
        private int logoPasses;
        private bool logoPending;

        // The frame that is drawn (the theme's outer border, inside the button's own padding) is made as wide as it is tall, and the logo put in
        // the middle of it.
        private void Start_LayoutUpdatedForSquare(object? sender, EventArgs e)
        {
            if (!IconOnly || Start.Template?.FindName("ButtonOuterBottomBorder", Start) is not FrameworkElement frame || frame.ActualHeight <= 0 || frame.ActualWidth <= 0)
            {
                return;
            }

            if (logoPasses < 8 && !logoPending && StartLogo.ActualWidth > 0 && Math.Abs(Start.ActualWidth - Start.ActualHeight) < 12)
            {
                try
                {
                    double pixel = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
                    Rect frameRect = frame.TransformToAncestor(Start).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight));
                    Rect logoRect = StartLogo.TransformToAncestor(Start).TransformBounds(new Rect(0, 0, StartLogo.ActualWidth, StartLogo.ActualHeight));
                    // The drawing is not always in the middle of its image (a logo with a shadow below and to the right of it is to the
                    // left of and above the middle): it is the drawing that is put in the middle of the frame.
                    (double artX, double artY) = ArtOffset(logoRect);
                    double dx = (frameRect.Left + frameRect.Width / 2) - (logoRect.Left + logoRect.Width / 2 + artX);
                    double dy = (frameRect.Top + frameRect.Height / 2) - (logoRect.Top + logoRect.Height / 2 + artY);
                    if (Math.Abs(dx) >= pixel * 0.55 || Math.Abs(dy) >= pixel * 0.55)
                    {
                        logoPasses++;
                        // The layout event comes several times before the new margin is in the layout: the next measure waits for it.
                        logoPending = true;
                        Dispatcher.BeginInvoke(new Action(() => logoPending = false), DispatcherPriority.Loaded);
                        logoDx += Math.Round(dx / pixel) * pixel;
                        logoDy += Math.Round(dy / pixel) * pixel;
                        StartLogoHost.RenderTransform = new TranslateTransform(logoDx, logoDy);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double padding = Start.ActualWidth - frame.ActualWidth;
            double width = Math.Round((frame.ActualHeight + padding) * dpi) / dpi;
            if (Math.Abs(width - Start.ActualWidth) > 0.5 / dpi && (double.IsNaN(Start.Width) || Math.Abs(Start.Width - width) > 0.5 / dpi))
            {
                Start.Width = width;
            }
        }

        public StartButton()
        {
            InitializeComponent();

            pendingOpenTimer = new DispatcherTimer(DispatcherPriority.Background);
            pendingOpenTimer.Interval = new TimeSpan(0, 0, 0, 1);
            pendingOpenTimer.Tick += (sender, args) =>
            {
                // if the start menu didn't open, flip the button back to unchecked
                SetStartMenuState(false);
            };
        }

        private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.Theme))
            {
                if (IconOnly)
                {
                    // The frame and the logo are the new theme's: the square and the middle are worked out again.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Start.ClearValue(WidthProperty);
                        ApplyIconOnly();
                    }), DispatcherPriority.Loaded);
                }

                bool useFloatingStartButton = Application.Current.FindResource("UseFloatingStartButton") as bool? ?? false;

                if (!useFloatingStartButton && floatingStartButton != null)
                {
                    closeFloatingStart();
                }
            }
        }

        public void SetStartMenuState(bool opened)
        {
            Dispatcher.Invoke(() =>
            {
                Start.IsChecked = opened;
                Host?.SetStartMenuOpen(opened);
            });
            pendingOpenTimer.Stop();

            if (opened)
            {
                RaiseFloatingStartAboveMenu();
            }
        }

        // The floating orb is over the start menu (as Windows 7's is, over its menu's corner), not behind it. The menu is a topmost window that
        // appears a moment after it is announced and slides in over the orb, so while the menu is open the orb is kept at the top of the
        // topmost windows (a cheap look at what is above it every few milliseconds), the whole time the menu slides.
        private DispatcherTimer? raiseOrbTimer;

        // The orb is kept above the menu while the button is down, and for a moment after the menu is announced (it is seen by a poll every
        // 100 ms, and by then it was already sliding over the orb): see WatchForStartMenu.
        private DateTime raiseUntilUtc = DateTime.MinValue;

        // The orb is also kept above the menu from a thread of its own, for as long as that is wanted: the call that asks Open Shell for its menu
        // does not return until the menu has slid in (the thread was blocked in it for the whole slide, 170 ms, and the menu was over the orb
        // all that time), and a thread that is waiting for another window's reply can still be sent the order to move.
        private long raiseUntilTicks;
        private volatile bool orbRaiserRunning;

        private void StartOrbRaiserThread(IntPtr orb)
        {
            if (orbRaiserRunning || orb == IntPtr.Zero)
            {
                return;
            }

            orbRaiserRunning = true;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    while (DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref raiseUntilTicks))
                    {
                        // Only while the orb is meant to be on top: it is not while a full-screen program is up (UpdateFloatingStartTopmost).
                        if (OrbIsTopmost(orb) && WindowOverOrb(orb) != IntPtr.Zero)
                        {
                            NativeMethods.SetWindowPos(
                                orb,
                                new IntPtr(-1),
                                0, 0, 0, 0,
                                (int)NativeMethods.SetWindowPosFlags.SWP_NOSIZE | (int)NativeMethods.SetWindowPosFlags.SWP_NOMOVE | (int)NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);
                        }

                        System.Threading.Thread.Sleep(1);
                    }
                }
                catch (Exception)
                {
                    // nothing to do: the timer and the hooks go on
                }
                finally
                {
                    orbRaiserRunning = false;
                }
            })
            {
                IsBackground = true,
                Name = "StartOrbRaiser",
            };
            thread.Start();
        }

        private void RaiseFloatingStartAboveMenu()
        {
            raiseUntilUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
            System.Threading.Interlocked.Exchange(ref raiseUntilTicks, raiseUntilUtc.Ticks);
            if (floatingStartButton != null)
            {
                StartOrbRaiserThread(floatingStartButton.Handle);
            }
            raiseOrbTimer ??= new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
            raiseOrbTimer.Tick -= RaiseOrbTimer_Tick;
            raiseOrbTimer.Tick += RaiseOrbTimer_Tick;
            raiseOrbTimer.Start();
            RaiseOrbTimer_Tick(null, EventArgs.Empty);

            // And at once whenever a window is shown or moves (the menu, and each step of its slide): the timer alone left the menu over the orb
            // for up to its interval, a frame or more, at the start of the slide.
            if (orbHook == IntPtr.Zero)
            {
                orbHookProc = (hook, eventType, hwnd, idObject, idChild, thread, time) =>
                {
                    if (idObject == 0 && floatingStartButton != null && hwnd != floatingStartButton.Handle)
                    {
                        RaiseOrbTimer_Tick(null, EventArgs.Empty);
                    }
                };
                // EVENT_OBJECT_SHOW (0x8002) to EVENT_OBJECT_LOCATIONCHANGE (0x800B), out of context: called on this thread, from its message loop.
                orbHook = SetWinEventHook(0x8002, 0x800B, IntPtr.Zero, orbHookProc, 0, 0, 0);
            }
        }

        private IntPtr orbHook;
        private WinEventDelegate? orbHookProc;

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate proc, uint processId, uint threadId, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        // While the floating orb exists, a start menu window being shown (or uncloaked, as the Windows 11 one is) starts keeping the orb above it
        // at once - also when it is opened by the Windows key, which the button does not hear of until the poll sees the menu.
        // RetroBar's own windows (the widgets' popups, which slide in by the orb, and their blur windows) are put at the top of the topmost windows
        // each time they move, which is over the orb for a moment until the orb is raised again after them: they are put just under the orb
        // instead, as the message that moves them is on its way (WM_WINDOWPOSCHANGING, seen by a hook on this thread's window procedures).
        private IntPtr zOrderHook;
        private HookProc? zOrderProc;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct CallWndProcData
        {
            public IntPtr lParam;
            public IntPtr wParam;
            public uint message;
            public IntPtr hwnd;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);

        private void InstallZOrderHook()
        {
            if (zOrderHook != IntPtr.Zero)
            {
                return;
            }

            zOrderProc = (code, wParam, lParam) =>
            {
                try
                {
                    if (code >= 0 && floatingStartButton != null)
                    {
                        CallWndProcData data = Marshal.PtrToStructure<CallWndProcData>(lParam);
                        IntPtr orb = floatingStartButton.Handle;
                        // WM_WINDOWPOSCHANGING (0x46); WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags.
                        if (data.message == 0x46 && data.hwnd != orb && orb != IntPtr.Zero)
                        {
                            IntPtr position = data.lParam;
                            IntPtr insertAfter = Marshal.ReadIntPtr(position, IntPtr.Size);
                            uint flags = (uint)Marshal.ReadInt32(position, IntPtr.Size * 2 + 16);
                            bool topmost = (GetWindowLong32(data.hwnd, -20) & 0x8) != 0;
                            // Put at the top of the topmost windows (HWND_TOPMOST, or HWND_TOP for one that is topmost): under the orb, which is there.
                            if ((flags & 0x4 /* SWP_NOZORDER */) == 0 && (insertAfter == new IntPtr(-1) || (insertAfter == IntPtr.Zero && topmost)) &&
                                (GetWindowLong32(orb, -20) & 0x8) != 0 && IsWindowVisible(orb))
                            {
                                Marshal.WriteIntPtr(position, IntPtr.Size, orb);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // never in the way of a window message
                }

                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            };
            zOrderHook = SetWindowsHookEx(4 /* WH_CALLWNDPROC */, zOrderProc, IntPtr.Zero, GetCurrentThreadId());
        }

        private void RemoveZOrderHook()
        {
            if (zOrderHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(zOrderHook);
                zOrderHook = IntPtr.Zero;
                zOrderProc = null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectData
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RectData rect);

        // Whether the orb is a topmost window now: it is not when it has been put under a full-screen program, and it must stay so.
        private static bool OrbIsTopmost(IntPtr orb) => (GetWindowLong32(orb, -20) & 0x8) != 0;

        // A visible window above the orb that is over it (not the system's invisible ones, nor windows elsewhere): null if there is none.
        private static IntPtr WindowOverOrb(IntPtr orb)
        {
            if (!GetWindowRect(orb, out RectData mine))
            {
                return IntPtr.Zero;
            }

            for (IntPtr w = GetWindow(orb, 3 /* GW_HWNDPREV */); w != IntPtr.Zero; w = GetWindow(w, 3))
            {
                if (!IsWindowVisible(w) || !GetWindowRect(w, out RectData other))
                {
                    continue;
                }

                bool over = other.Left < mine.Right && other.Right > mine.Left && other.Top < mine.Bottom && other.Bottom > mine.Top;
                if (over)
                {
                    var name = new System.Text.StringBuilder(64);
                    GetClassName(w, name, name.Capacity);
                    if (name.ToString() != "ForegroundStaging")
                    {
                        return w;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private IntPtr showHook;
        private IntPtr uncloakHook;
        private WinEventDelegate? watchProc;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private void WatchForStartMenu()
        {
            if (showHook != IntPtr.Zero)
            {
                return;
            }

            watchProc = (hook, eventType, hwnd, idObject, idChild, thread, time) =>
            {
                if (idObject != 0 || floatingStartButton == null || hwnd == floatingStartButton.Handle)
                {
                    return;
                }

                var name = new System.Text.StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                // The menus, and RetroBar's own popups (the weather widget's, which is by the orb, slides in over it as the menu does).
                GetWindowThreadProcessId(hwnd, out uint processId);
                if (name.ToString() is "DV2ControlHost" or "OpenShell.CMenuContainer" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" ||
                    processId == (uint)Environment.ProcessId)
                {
                    RaiseFloatingStartAboveMenu();
                }
            };
            InstallZOrderHook();
            showHook = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, watchProc, 0, 0, 0);
            uncloakHook = SetWinEventHook(0x8018, 0x8018, IntPtr.Zero, watchProc, 0, 0, 0);
        }

        private void StopWatchingForStartMenu()
        {
            RemoveZOrderHook();
            if (showHook != IntPtr.Zero)
            {
                UnhookWinEvent(showHook);
                showHook = IntPtr.Zero;
            }

            if (uncloakHook != IntPtr.Zero)
            {
                UnhookWinEvent(uncloakHook);
                uncloakHook = IntPtr.Zero;
            }

            watchProc = null;
        }

        private void StopRaisingOrb()
        {
            raiseOrbTimer?.Stop();
            if (orbHook != IntPtr.Zero)
            {
                UnhookWinEvent(orbHook);
                orbHook = IntPtr.Zero;
                orbHookProc = null;
            }
        }

        private void RaiseOrbTimer_Tick(object? sender, EventArgs e)
        {
            if (floatingStartButton == null || (Start.IsChecked != true && DateTime.UtcNow > raiseUntilUtc))
            {
                StopRaisingOrb();
                return;
            }

            IntPtr orb = floatingStartButton.Handle;
            // Something above it: the menu.
            IntPtr above = OrbIsTopmost(orb) ? WindowOverOrb(orb) : IntPtr.Zero;
            if (above != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    orb,
                    new IntPtr(-1),
                    0, 0, 0, 0,
                    (int)NativeMethods.SetWindowPosFlags.SWP_NOSIZE | (int)NativeMethods.SetWindowPosFlags.SWP_NOMOVE | (int)NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private void Start_OnClick(object sender, RoutedEventArgs e)
        {
            if (allowOpenStart)
            {
                OpenStartMenu();
                return;
            }

            SetStartMenuState(false);
        }

        /// <summary>Opens the Start menu, or closes it if it is open: for the edge of a taskbar with no Start button of its own.</summary>
        public void ToggleStartMenu()
        {
            if (Start.IsChecked == true)
            {
                SetStartMenuState(false);
                return;
            }

            OpenStartMenu();
        }

        private void OpenStartMenu()
        {
            // Open Shell positions its menu using the shell tray position. Moving the tray host to this
            // taskbar first makes it place and animate the menu differently than it does for the Windows
            // key, so leave the tray host alone while Open Shell is running
            if (NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "OpenShell.COwnerWindow", IntPtr.Zero) == IntPtr.Zero)
            {
                Host?.SetTrayHost();
            }

            // The orb is above the menu from the first moment of its slide, not from when the menu is seen to be open.
            RaiseFloatingStartAboveMenu();
            Host?.SetStartMenuOpen(true);
            pendingOpenTimer.Start();
            if (Host != null && StartMenuMonitor != null && Settings.Instance.ShowMultiMon && Settings.Instance.ShowStartButtonMultiMon)
            {
                StartMenuMonitor.ShowStartMenu(Host.Handle);
            }
            else
            {
                ShellHelper.ShowStartMenu();
            }
        }

        private void Start_DragEnter(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragEnter(e);
        }

        private void Start_DragLeave(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragLeave();
        }

        private void Start_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            allowOpenStart = Start.IsChecked == false;
        }

        private void Start_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ShowStartContextMenu(null))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// The right-click menu of the Start button. With no button (the option is off) the edge of the taskbar where it was has it too,
        /// the menu then opening over that edge (<paramref name="placementTarget"/>). Whether there was a menu to show.
        /// </summary>
        internal bool ShowStartContextMenu(UIElement? placementTarget)
        {
            // Open Shell adds its own right-click menu to the Windows Start button, which doesn't exist here.
            // Like Open Shell, show its menu on a plain right-click and the Windows menu on Shift+right-click
            ShellLogger.Info($"StartButton: right-click, shift held = {(Keyboard.Modifiers & ModifierKeys.Shift) != 0}");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 && TryShowOpenShellMenu(placementTarget))
            {
                return true;
            }

            if (EnvironmentHelper.IsWindows10OrBetter)
            {
                ShellHelper.ShowStartContextMenu();
                return true;
            }

            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);

        private static string? GetProcessImagePath(int processId)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                StringBuilder path = new StringBuilder(1024);
                uint size = (uint)path.Capacity;
                return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private bool TryShowOpenShellMenu(UIElement? placementTarget)
        {
            string? openShellPath;
            try
            {
                Process[] running = Process.GetProcessesByName("StartMenu");
                if (running.Length == 0)
                {
                    ShellLogger.Info("StartButton: right-click - no StartMenu process is running, showing the Windows menu");
                    return false;
                }

                // Process.MainModule is denied for a process started from another session or privilege level,
                // so ask for just the image path, which is allowed for processes of the same user
                openShellPath = running
                    .Select(p => GetProcessImagePath(p.Id))
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path));

                // it is running but can't be inspected: fall back to the places it is normally installed
                openShellPath ??= new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenShellFork", "StartMenu.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Open-Shell", "StartMenu.exe")
                }.FirstOrDefault(File.Exists);

                ShellLogger.Info($"StartButton: right-click - {running.Length} StartMenu process(es), path = {openShellPath ?? "(not found)"}");
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"StartButton: right-click - looking for Open Shell failed: {ex.Message}");
                return false;
            }

            if (openShellPath == null)
            {
                return false;
            }

            void Run(string argument)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(openShellPath, argument) { UseShellExecute = false });
                }
                catch
                {
                    // nothing else to do if it can't be started
                }
            }

            ContextMenu menu = new ContextMenu();

            MenuItem settings = new MenuItem { Header = "Open-Shell Settings" };
            settings.Click += (s, args) => Run("-settings");
            menu.Items.Add(settings);

            MenuItem windowsMenu = new MenuItem { Header = "Windows tools menu" };
            windowsMenu.Click += (s, args) => ShellHelper.ShowStartContextMenu();
            menu.Items.Add(windowsMenu);

            menu.Items.Add(new Separator());

            MenuItem exit = new MenuItem { Header = "Exit Open-Shell" };
            exit.Click += (s, args) => Run("-exit");
            menu.Items.Add(exit);

            menu.PlacementTarget = placementTarget ?? Start;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
            return true;
        }

        private void Start_OnChecked(object sender, RoutedEventArgs e)
        {
            RaiseFloatingStartAboveMenu();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            StartMenuMonitor.StartMenuVisibilityChanged += AppVisibilityHelper_StartMenuVisibilityChanged;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            // The menu starts to slide in before it is announced: the orb is kept above it from the click.
            Start.Checked += Start_OnChecked;

            dragHandler = new DelayedActivationHandler(() =>
            {
                if (Start.IsChecked == false)
                {
                    OpenStartMenu();
                }
            });

            openFloatingStart();

            IsVisibleChanged += StartButton_IsVisibleChanged;
            LayoutUpdated += StartButton_LayoutUpdated;

            if (Host != null)
            {
                Host.PropertyChanged += Taskbar_PropertyChanged;
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            StartMenuMonitor.StartMenuVisibilityChanged -= AppVisibilityHelper_StartMenuVisibilityChanged;
            Start.Checked -= Start_OnChecked;

            if (Host != null)
            {
                Host.PropertyChanged -= Taskbar_PropertyChanged;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            dragHandler?.Dispose();

            hideFloatingStart();
        }

        private void AppVisibilityHelper_StartMenuVisibilityChanged(object? sender, StartMenuMonitor.StartMenuMonitorEventArgs e)
        {
            if (e.Visible && Host != null &&
                ((e.TaskbarHwndActivated != IntPtr.Zero && e.TaskbarHwndActivated != Host.Handle) ||
                (e.StartHmonitor != IntPtr.Zero && e.StartHmonitor != Host.Screen.HMonitor)))
            {
                // Only set as visible when activated from our taskbar
                return;
            }
            SetStartMenuState(e.Visible);
        }

        private void StartButton_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            visibilityChanged = true;
        }

        private void StartButton_LayoutUpdated(object? sender, EventArgs e)
        {
            if (!visibilityChanged)
            {
                UpdateFloatingStartCoordinates();
                return;
            }

            visibilityChanged = false;

            if (IsVisible)
            {
                openFloatingStart();
            }
            else
            {
                hideFloatingStart();
            }
        }

        private void Taskbar_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (Host != null && e.PropertyName == nameof(Opacity))
            {
                if (Host.Opacity == 1)
                {
                    openFloatingStart();
                }
                else
                {
                    hideFloatingStart();
                }
            }
        }

        #region Floating start button

        private void openFloatingStart()
        {
            bool useFloatingStartButton = Application.Current.FindResource("UseFloatingStartButton") as bool? ?? false;

            if (!useFloatingStartButton || Visibility != Visibility.Visible) return;

            if (floatingStartButton == null)
            {
                floatingStartButton = new FloatingStartButton(this, getButtonRect());
                floatingStartButton.Show();
                WatchForStartMenu();
            }
            else
            {
                showFloatingStart();
            }
        }

        private void showFloatingStart()
        {
            if (floatingStartButton == null) return;

            UpdateFloatingStartCoordinates();
            floatingStartButton.Visibility = Visibility.Visible;
        }

        private void hideFloatingStart()
        {
            if (floatingStartButton == null) return;

            floatingStartButton.Visibility = Visibility.Hidden;
        }

        private void closeFloatingStart()
        {
            StopWatchingForStartMenu();
            StopRaisingOrb();
            floatingStartButton?.Close();
            floatingStartButton = null;
        }

        private NativeMethods.Rect getButtonRect()
        {
            // Get the pixel values of the start button's bounds
            Point buttonPosPixels = Start.PointToScreen(new Point(FlowDirection == FlowDirection.LeftToRight ? 0 : Start.ActualWidth, 0));
            Point buttonSizePixels = Start.PointToScreen(new Point(FlowDirection == FlowDirection.LeftToRight ? Start.ActualWidth : 0, Start.ActualHeight));

            // If the start button is currently translated, we get the translated position
            // and need to offset by that much to be positioned correctly.
            if (Host?.AutoHideElement?.RenderTransform is TranslateTransform tt)
            {
                buttonPosPixels.X -= (tt.X * Host.DpiScale);
                buttonPosPixels.Y -= (tt.Y * Host.DpiScale);
                buttonSizePixels.X -= (tt.X * Host.DpiScale);
                buttonSizePixels.Y -= (tt.Y * Host.DpiScale);
            }

            return new NativeMethods.Rect((int)buttonPosPixels.X, (int)buttonPosPixels.Y, (int)buttonSizePixels.X, (int)buttonSizePixels.Y);
        }

        public void UpdateFloatingStartCoordinates()
        {
            // Can't get our coordinates if we aren't visible.
            if (!IsVisible || floatingStartButton == null) return;

            floatingStartButton.SetPosition(getButtonRect());
        }

        public void UpdateFloatingStartTopmost(bool topmost)
        {
            if (floatingStartButton == null) return;

            floatingStartButton.Topmost = topmost;

            if (!topmost)
            {
                // Setting Topmost=false itself does not guarantee that we will go below a full-screen window.
                NativeMethods.SetWindowPos(
                floatingStartButton.Handle,
                Host.Handle,
                0, 0, 0, 0,
                (int)NativeMethods.SetWindowPosFlags.SWP_NOSIZE | (int)NativeMethods.SetWindowPosFlags.SWP_NOMOVE | (int)NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);
            }
        }

        #endregion
    }
}