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
        }

        private void Start_OnClick(object sender, RoutedEventArgs e)
        {
            if (allowOpenStart)
            {
                OpenStartMenu();
                return;
            }

            SetStartMenuState(false);
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
            // Open Shell adds its own right-click menu to the Windows Start button, which doesn't exist here.
            // Like Open Shell, show its menu on a plain right-click and the Windows menu on Shift+right-click
            ShellLogger.Info($"StartButton: right-click, shift held = {(Keyboard.Modifiers & ModifierKeys.Shift) != 0}");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 && TryShowOpenShellMenu())
            {
                e.Handled = true;
                return;
            }

            if (EnvironmentHelper.IsWindows10OrBetter)
            {
                ShellHelper.ShowStartContextMenu();
                e.Handled = true;
            }
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

        private bool TryShowOpenShellMenu()
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

            menu.PlacementTarget = Start;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
            return true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            StartMenuMonitor.StartMenuVisibilityChanged += AppVisibilityHelper_StartMenuVisibilityChanged;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

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