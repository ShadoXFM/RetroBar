using ManagedShell;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using RetroBar.Converters;
using RetroBar.Utilities;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// The seek popup as a window: dragging it, keeping it on top or as an ordinary window, closing it on a click elsewhere or Escape, following the monitor it is on.
    /// </summary>
    public partial class MediaPlayer
    {
        // Dragging the popup: anywhere on it except the buttons and the slider. Its window is moved directly (a Popup's own offsets
        // keep it on the monitor of the track text), to any monitor; it takes that monitor's DPI and adjustments (see
        // RefreshSeekPopupMonitor). It goes back above the track text when it opens next.
        private volatile bool _seekPopupDragging;

        // Guards _seekPopupMovedTo and _seekPopupDragging between the dragging thread and the closing of the popup.
        private readonly object _seekPopupDragLock = new();

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private POINT _seekPopupDragStart;

        private POINT _seekPopupDragWindow;

        private HwndSource _seekPopupSource;

        // "Always on top" for the popup that is open now; shared by every monitor's player (they each have their own popup, one open at
        // a time), and off again when it closes.
        private static bool _seekPopupPinned;

        private static MediaPlayer _openSeekPlayer;

        // Where the popup was dragged to. WPF puts a popup back above the track text whenever its content changes size (the track
        // changing, from the taskbar's buttons), so every move of its window is kept there instead (see SeekPopupHook).
        private POINT? _seekPopupMovedTo;

        // Where an ordinary popup (not moved, not on top) is once it has opened. When the track changes, WPF places it again above the
        // track text, which has changed width, so it would slide sideways: it keeps this X until it is opened next.
        private int? _seekPopupHeldX;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr Window, InsertAfter;
            public int X, Y, Width, Height;
            public uint Flags;
        }

        private const int WM_WINDOWPOSCHANGING = 0x0046;

        private IntPtr SeekPopupHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            POINT? movedTo;
            lock (_seekPopupDragLock)
            {
                movedTo = _seekPopupMovedTo;
            }

            if (msg == WM_WINDOWPOSCHANGING && _seekPopupHeldX is int heldX && movedTo == null && !_seekPopupPinned)
            {
                WINDOWPOS held = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((held.Flags & SWP_NOMOVE) == 0 && held.X != heldX)
                {
                    held.X = heldX;
                    Marshal.StructureToPtr(held, lParam, false);
                }
            }

            if (msg == WM_WINDOWPOSCHANGING && (movedTo != null || _seekPopupPinned))
            {
                WINDOWPOS position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                bool changed = false;

                if (movedTo is POINT moved && (position.Flags & SWP_NOMOVE) == 0 && (position.X != moved.X || position.Y != moved.Y))
                {
                    position.X = moved.X;
                    position.Y = moved.Y;
                    changed = true;
                }

                // WPF puts its popups' windows at the top of the topmost ones whenever it places them again: one that is not on top
                // is left where it is among the ordinary windows.
                if (!_seekPopupPinned && (position.Flags & SWP_NOZORDER) == 0 && position.InsertAfter == HWND_TOPMOST)
                {
                    position.InsertAfter = HWND_NOTOPMOST;
                    changed = true;
                }

                if (changed)
                {
                    Marshal.StructureToPtr(position, lParam, false);
                }
            }

            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int Size;
            public RECT Monitor, Work;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        private const int GWLP_HWNDPARENT = -8;

        private const int GWL_EXSTYLE = -20;

        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private static readonly IntPtr HWND_NOTOPMOST = new(-2);

        private const uint SWP_NOMOVE = 0x0002;

        private const uint SWP_NOSIZE = 0x0001;

        private const uint SWP_NOZORDER = 0x0004;

        private const uint SWP_NOACTIVATE = 0x0010;

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private const int VK_LBUTTON = 0x01;

        private const int DragThreshold = 4;

        private DispatcherTimer _seekPopupDragTimer;

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        // A popup's window is never activated by a click, so an ordinary window (moved, not on top) would stay behind the others
        // however often it is clicked: a click brings it in front of them.
        private void SeekPopupRoot_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if ((_seekPopupMovedTo != null || _seekPopupPinned) && PresentationSource.FromVisual(SeekPopupRoot) is HwndSource source)
            {
                SetWindowPos(source.Handle, _seekPopupPinned ? HWND_TOPMOST : HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        private void SeekPopupRoot_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            for (DependencyObject node = e.OriginalSource as DependencyObject; node != null && node != SeekPopupRoot; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.RangeBase)
                {
                    return;
                }
            }

            if (PresentationSource.FromVisual(SeekPopupRoot) is not HwndSource source || !GetWindowRect(source.Handle, out RECT window))
            {
                return;
            }

            // Nothing about the popup changes for a click: it only starts to be dragged once the pointer has gone a few pixels with the
            // button held down (mouse capture is left alone, which is what keeps the popup closing when a click lands elsewhere).
            GetCursorPos(out _seekPopupDragStart);
            _seekPopupDragWindow = new POINT { X = window.Left, Y = window.Top };
            _seekPopupDragging = false;

            _seekPopupDragTimer ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(8) };
            _seekPopupDragTimer.Tick -= SeekPopupDragTimer_OnTick;
            _seekPopupDragTimer.Tick += SeekPopupDragTimer_OnTick;
            _seekPopupDragTimer.Start();
        }

        private void SeekPopupDragTimer_OnTick(object sender, EventArgs e)
        {
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0 || !SeekPopup.IsOpen ||
                PresentationSource.FromVisual(SeekPopupRoot) is not HwndSource source)
            {
                _seekPopupDragTimer.Stop();
                _seekPopupDragging = false;
                return;
            }

            GetCursorPos(out POINT now);
            int dx = now.X - _seekPopupDragStart.X;
            int dy = now.Y - _seekPopupDragStart.Y;

            if (!_seekPopupDragging)
            {
                if (Math.Abs(dx) + Math.Abs(dy) < DragThreshold)
                {
                    return;
                }

                // Moved: from here the popup is a window of its own that stays where it is put.
                _seekPopupDragging = true;
                _seekPopupMovedTo = _seekPopupDragWindow;
                ApplySeekPopupTopmost();
            }

            // From here the window follows the pointer once for every refresh of the screen: a thread waits for each one and has this thread
            // move the window (a timer here runs at the system timer's resolution, about 64 times a second, which showed on a 165 Hz screen).
            // The window is only ever moved from this thread: moving it from another one froze the popup when it crossed to a monitor of
            // another DPI, where this thread resizes it in the same moment.
            _seekPopupDragTimer.Stop();
            var thread = new System.Threading.Thread(PaceDrag) { IsBackground = true, Name = "SeekPopupDrag" };
            thread.Start();
        }

        private int _seekPopupDragMovePending;

        private void PaceDrag()
        {
            while (_seekPopupDragging && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            {
                if (DwmFlush() != 0)
                {
                    System.Threading.Thread.Sleep(4);
                }

                // One move waiting at a time: a busy thread gets the latest position when it comes to it, not a queue of old ones.
                if (System.Threading.Interlocked.Exchange(ref _seekPopupDragMovePending, 1) == 0)
                {
                    Dispatcher.BeginInvoke(new Action(MoveSeekPopupToPointer), DispatcherPriority.Send);
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MoveSeekPopupToPointer();
                lock (_seekPopupDragLock)
                {
                    _seekPopupDragging = false;
                }
            }), DispatcherPriority.Send);
        }

        private void MoveSeekPopupToPointer()
        {
            _seekPopupDragMovePending = 0;
            if (!_seekPopupDragging || !SeekPopup.IsOpen || PresentationSource.FromVisual(SeekPopupRoot) is not HwndSource source)
            {
                return;
            }

            GetCursorPos(out POINT now);
            var target = new POINT { X = _seekPopupDragWindow.X + now.X - _seekPopupDragStart.X, Y = _seekPopupDragWindow.Y + now.Y - _seekPopupDragStart.Y };
            lock (_seekPopupDragLock)
            {
                _seekPopupMovedTo = target;
            }

            SetWindowPos(source.Handle, IntPtr.Zero, target.X, target.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            RefreshSeekPopupMonitor(source);
        }

        // A popup's window is above every other window by default. Once it has been moved or set to be on top it is a window of its own
        // (no owner), which stays where it is and open; until then, clicking in it changes nothing about it. Without "Always on top"
        // that window is an ordinary one, which another window can come in front of.
        private void ApplySeekPopupTopmost()
        {
            if (_seekPopupMovedTo == null && !_seekPopupPinned)
            {
                return;
            }

            if (PresentationSource.FromVisual(SeekPopupRoot) is HwndSource source)
            {
                // The popup is owned by the taskbar's window, which is itself always on top, and a window owned by a topmost one is
                // topmost whatever it asks for: without the owner it can be an ordinary window.
                if (IntPtr.Size == 8)
                {
                    SetWindowLongPtr(source.Handle, GWLP_HWNDPARENT, IntPtr.Zero);
                }
                else
                {
                    SetWindowLong(source.Handle, GWLP_HWNDPARENT, 0);
                }

                // A popup's window is never activated by a click (WS_EX_NOACTIVATE), and Windows will not put a window that is not
                // the active one above the others whatever it is asked: as a window of its own it is activated by a click, which is
                // what brings it in front of the windows an ordinary window is above.
                int style = GetWindowLong(source.Handle, GWL_EXSTYLE);
                if ((style & WS_EX_NOACTIVATE) != 0)
                {
                    SetWindowLong(source.Handle, GWL_EXSTYLE, style & ~WS_EX_NOACTIVATE);
                }

                SetWindowPos(source.Handle, _seekPopupPinned ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        // A click elsewhere closes the popup, sliding down, and so does Escape, unless it is a window of its own (it has been moved, or is
        // set to be on top) - the popup itself never closes on a click (StaysOpen), since WPF would close it at once, with no slide. While it
        // is open an InputWatcher (low-level hooks: no polling) tells of the clicks and the keys, and a slow timer does the rest.
        private InputWatcher _seekInput;

        private DispatcherTimer _seekWatchTimer;

        private bool _seekMenuOpen;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        // Whatever WPF does to the popup's window, one that has been moved and is not on top is an ordinary window without an owner.
        private void EnsureSeekPopupIsOrdinary()
        {
            if ((_seekPopupMovedTo == null && !_seekPopupPinned) || PresentationSource.FromVisual(SeekPopupRoot) is not HwndSource source)
            {
                return;
            }

            if (GetWindow(source.Handle, 4 /* GW_OWNER */) != IntPtr.Zero || (!_seekPopupPinned && (GetWindowLong(source.Handle, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0))
            {
                ApplySeekPopupTopmost();
            }
        }

        private void StartSeekWatching()
        {
            _seekInput ??= new InputWatcher();
            _seekInput.MouseDown -= SeekInput_OnMouseDown;
            _seekInput.MouseDown += SeekInput_OnMouseDown;
            _seekInput.KeyDown -= SeekInput_OnKeyDown;
            _seekInput.KeyDown += SeekInput_OnKeyDown;
            _seekInput.Start();

            // Ten times a second is all the rest needs: the window's flags are kept by its message hook, and the spectrum only has to be told when
            // nothing comes (see SeekWatchTimer_OnTick).
            _seekWatchTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _seekWatchTimer.Tick -= SeekWatchTimer_OnTick;
            _seekWatchTimer.Tick += SeekWatchTimer_OnTick;
            _seekWatchTimer.Start();
        }

        private void StopSeekWatching()
        {
            _seekInput?.Stop();
            _seekWatchTimer?.Stop();
        }

        private void SeekWatchTimer_OnTick(object sender, EventArgs e)
        {
            if (!SeekPopup.IsOpen)
            {
                StopSeekWatching();
                return;
            }

            EnsureSeekPopupIsOrdinary();

            // A loopback capture delivers nothing while nothing is heard: the bars still have to sink, and the dashes go.
            if (DateTime.UtcNow - _lastSeekSpectrumUpdate > TimeSpan.FromMilliseconds(80))
            {
                UpdateSeekSpectrum(null);
            }
        }

        private bool CursorIsOnSeekPopup(int x, int y)
        {
            return PresentationSource.FromVisual(SeekPopupRoot) is HwndSource source && GetWindowRect(source.Handle, out RECT window) &&
                   x >= window.Left && x < window.Right && y >= window.Top && y < window.Bottom;
        }

        // Escape closes the popup while the pointer is on it or it is the window in front (Escape in other apps is not ours).
        private void SeekInput_OnKeyDown(int virtualKey)
        {
            if (virtualKey != 0x1B || !SeekPopup.IsOpen || _seekSlide.Closing)
            {
                return;
            }

            GetCursorPos(out POINT cursor);
            bool inFront = PresentationSource.FromVisual(SeekPopupRoot) is HwndSource source && GetForegroundWindow() == source.Handle;
            if (!CursorIsOnSeekPopup(cursor.X, cursor.Y) && !inFront)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                // A popup that is a window of its own (moved, or on top) just goes; the other one slides down.
                if (_seekPopupMovedTo != null || _seekPopupPinned)
                {
                    CloseSeekPopupNow();
                }
                else
                {
                    CloseSeekPopupAnimated();
                }
            }), DispatcherPriority.Send);
        }

        private void SeekInput_OnMouseDown(int x, int y)
        {
            if (!SeekPopup.IsOpen || _seekSlide.Closing || _seekMenuOpen || _seekPopupMovedTo != null || _seekPopupPinned || CursorIsOnSeekPopup(x, y))
            {
                return;
            }

            // The click on the track text that closes it must not open it again when the button comes up.
            try
            {
                Point onText = TrackTextCanvas.PointFromScreen(new Point(x, y));
                if (onText.X >= 0 && onText.Y >= 0 && onText.X <= TrackTextCanvas.ActualWidth && onText.Y <= TrackTextCanvas.ActualHeight)
                {
                    _ignoreNextTrackTextClick = true;
                    _ignoreTrackTextClickUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Dispatcher.BeginInvoke(new Action(CloseSeekPopupAnimated), DispatcherPriority.Send);
        }

        // The monitor the popup is on decides which adjustments of monitor-adjustments.json apply to it, and the pixel art is drawn for
        // that monitor's DPI (which WPF has already given the popup's window by now, or does when it raises DpiChanged below).
        private string _seekPopupMonitor;

        // The DPI the popup's window draws at (the monitor it opened on), which it keeps whatever monitor it is dragged to.
        private double _seekPopupHwndDpi;

        private void RefreshSeekPopupMonitor(HwndSource source)
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(MonitorFromWindow(source.Handle, MONITOR_DEFAULTTONEAREST), ref info) || info.DeviceName == _seekPopupMonitor)
            {
                return;
            }

            _seekPopupMonitor = info.DeviceName;
            MonitorOffset.SetDeviceNameOverride(SeekPopupRoot, info.DeviceName);
            ApplySeekPopupDpi(source, MonitorFromWindow(source.Handle, MONITOR_DEFAULTTONEAREST));
            ReapplySeekPopupForMonitor();
        }

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

        // The popup is laid out for the DPI of the monitor it is on. WPF does not do that for a popup's window when it is dragged to another
        // monitor: the window goes on turning what it draws into pixels at the DPI it was opened at (a popup of display 1's size on a
        // display of another DPI). So it is scaled by the difference, which makes it the size it has on that monitor's own taskbar.
        private void ApplySeekPopupDpi(HwndSource source, IntPtr monitor)
        {
            if (source.RootVisual == null || _seekPopupHwndDpi <= 0 || GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) != 0)
            {
                return;
            }

            double dpi = dpiX / 96.0;
            VisualTreeHelper.SetRootDpi(source.RootVisual, new DpiScale(dpi, dpiY / 96.0));
            double scale = dpi / _seekPopupHwndDpi;
            SeekPopupRoot.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        private void ReapplySeekPopupForMonitor()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!SeekPopup.IsOpen)
                {
                    return;
                }

                MonitorOffset.Refresh(SeekPopupRoot);
                ApplySeekGlyphs();
            }), DispatcherPriority.Loaded);
        }

        private void SeekPopupSource_OnDpiChanged(object sender, HwndDpiChangedEventArgs e)
        {
            _seekPopupHeldX = null;
            if (sender is HwndSource source)
            {
                _seekPopupMonitor = null;
                RefreshSeekPopupMonitor(source);
            }

            ReapplySeekPopupForMonitor();
        }

        // Right click: keep it above the other windows, or close it.
        private void SeekPopupRoot_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            var onTop = new MenuItem
            {
                Header = TryFindResource("media_popup_always_on_top") as string ?? "_Always on top",
                IsChecked = _seekPopupPinned,
            };
            onTop.Click += (s, args) =>
            {
                _seekPopupPinned = !_seekPopupPinned;
                ApplySeekPopupTopmost();

                // The menu was used, so the popup is the window being worked in: it stays in front as the active window (Windows would
                // otherwise put it behind the one that was active before, as it stops being topmost).
                if (!_seekPopupPinned && PresentationSource.FromVisual(SeekPopupRoot) is HwndSource source)
                {
                    SetWindowPos(source.Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                    SetForegroundWindow(source.Handle);
                }
            };

            var lyrics = new MenuItem
            {
                Header = TryFindResource("media_popup_show_lyrics") as string ?? "Show _lyrics",
                IsChecked = Settings.Instance.ShowMediaPopupLyrics,
            };
            lyrics.Click += (s, args) => Settings.Instance.ShowMediaPopupLyrics = !Settings.Instance.ShowMediaPopupLyrics;

            var close = new MenuItem { Header = TryFindResource("close") as string ?? "_Close" };
            close.Click += (s, args) => CloseSeekPopupNow();

            var menu = new ContextMenu
            {
                PlacementTarget = SeekPopupRoot,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            };
            menu.Items.Add(onTop);
            menu.Items.Add(lyrics);
            menu.Items.Add(MakeMenuSeparator());
            menu.Items.Add(close);
            menu.Opened += (s, args) => _seekMenuOpen = true;
            menu.Closed += (s, args) => Dispatcher.BeginInvoke(new Action(() => _seekMenuOpen = false), DispatcherPriority.Background);
            menu.IsOpen = true;
        }
    }
}
