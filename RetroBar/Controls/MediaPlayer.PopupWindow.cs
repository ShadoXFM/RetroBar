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
        private bool _seekPopupDragging;

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
            if (msg == WM_WINDOWPOSCHANGING && (_seekPopupMovedTo != null || _seekPopupPinned))
            {
                WINDOWPOS position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                bool changed = false;

                if (_seekPopupMovedTo is POINT moved && (position.Flags & SWP_NOMOVE) == 0 && (position.X != moved.X || position.Y != moved.Y))
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

            _seekPopupMovedTo = new POINT { X = _seekPopupDragWindow.X + dx, Y = _seekPopupDragWindow.Y + dy };
            SetWindowPos(source.Handle, IntPtr.Zero, _seekPopupMovedTo.Value.X, _seekPopupMovedTo.Value.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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

        // A click elsewhere closes the popup, sliding down, unless it is a window of its own (it has been moved, or is set to be on top).
        // The popup itself never closes on a click (StaysOpen), since WPF would close it at once, with no slide: this timer watches the
        // mouse buttons instead.
        private DispatcherTimer _seekClickAwayTimer;

        private bool _seekClickAwayWasDown;

        private bool _seekMenuOpen;

        private bool _seekEscapeWasDown;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static bool IsAnyMouseButtonDown() => ((GetAsyncKeyState(0x01) | GetAsyncKeyState(0x02) | GetAsyncKeyState(0x04)) & 0x8000) != 0;

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

        private void StartSeekClickAwayTimer()
        {
            _seekClickAwayWasDown = IsAnyMouseButtonDown();
            _seekClickAwayTimer ??= new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
            _seekClickAwayTimer.Tick -= SeekClickAwayTimer_OnTick;
            _seekClickAwayTimer.Tick += SeekClickAwayTimer_OnTick;
            _seekClickAwayTimer.Start();
        }

        private void SeekClickAwayTimer_OnTick(object sender, EventArgs e)
        {
            if (!SeekPopup.IsOpen)
            {
                _seekClickAwayTimer.Stop();
                return;
            }

            EnsureSeekPopupIsOrdinary();

            // A loopback capture delivers nothing while nothing is heard: the bars still have to sink, and the dashes go.
            if (DateTime.UtcNow - _lastSeekSpectrumUpdate > TimeSpan.FromMilliseconds(80))
            {
                UpdateSeekSpectrum(null);
            }

            bool escape = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (escape && !_seekEscapeWasDown && !_seekSlide.Closing && PresentationSource.FromVisual(SeekPopupRoot) is HwndSource escapeSource &&
                GetWindowRect(escapeSource.Handle, out RECT escapeWindow) && GetCursorPos(out POINT escapeCursor) &&
                ((escapeCursor.X >= escapeWindow.Left && escapeCursor.X < escapeWindow.Right && escapeCursor.Y >= escapeWindow.Top && escapeCursor.Y < escapeWindow.Bottom) ||
                 GetForegroundWindow() == escapeSource.Handle))
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
            }

            _seekEscapeWasDown = escape;

            bool down = IsAnyMouseButtonDown();
            bool pressed = down && !_seekClickAwayWasDown;
            _seekClickAwayWasDown = down;

            if (!pressed || _seekSlide.Closing || _seekMenuOpen || _seekPopupMovedTo != null || _seekPopupPinned ||
                PresentationSource.FromVisual(SeekPopupRoot) is not HwndSource source || !GetWindowRect(source.Handle, out RECT window) ||
                !GetCursorPos(out POINT cursor))
            {
                return;
            }

            if (cursor.X >= window.Left && cursor.X < window.Right && cursor.Y >= window.Top && cursor.Y < window.Bottom)
            {
                return;
            }

            // The click on the track text that closes it must not open it again when the button comes up.
            Point onText = TrackTextCanvas.PointFromScreen(new Point(cursor.X, cursor.Y));
            if (onText.X >= 0 && onText.Y >= 0 && onText.X <= TrackTextCanvas.ActualWidth && onText.Y <= TrackTextCanvas.ActualHeight)
            {
                _ignoreNextTrackTextClick = true;
                _ignoreTrackTextClickUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
            }

            CloseSeekPopupAnimated();
        }

        // The monitor the popup is on decides which adjustments of monitor-adjustments.json apply to it, and the pixel art is drawn for
        // that monitor's DPI (which WPF has already given the popup's window by now, or does when it raises DpiChanged below).
        private string _seekPopupMonitor;

        private void RefreshSeekPopupMonitor(HwndSource source)
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(MonitorFromWindow(source.Handle, MONITOR_DEFAULTTONEAREST), ref info) || info.DeviceName == _seekPopupMonitor)
            {
                return;
            }

            _seekPopupMonitor = info.DeviceName;
            MonitorOffset.SetDeviceNameOverride(SeekPopupRoot, info.DeviceName);
            ReapplySeekPopupForMonitor();
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

            var close = new MenuItem { Header = TryFindResource("close") as string ?? "_Close" };
            close.Click += (s, args) => CloseSeekPopupNow();

            var menu = new ContextMenu
            {
                PlacementTarget = SeekPopupRoot,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            };
            menu.Items.Add(onTop);
            menu.Items.Add(MakeMenuSeparator());
            menu.Items.Add(close);
            menu.Opened += (s, args) => _seekMenuOpen = true;
            menu.Closed += (s, args) => Dispatcher.BeginInvoke(new Action(() => _seekMenuOpen = false), DispatcherPriority.Background);
            menu.IsOpen = true;
        }
    }
}
