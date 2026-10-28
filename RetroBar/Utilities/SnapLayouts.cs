using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using RetroBar.Controls;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>One cell of a snap layout: the part of a monitor's work area (0 to 1 each way) a window dropped on it takes.</summary>
    public sealed class SnapZone
    {
        public SnapZone(Rect fraction)
        {
            Fraction = fraction;
        }

        public Rect Fraction { get; }

        /// <summary>The cell that is the whole work area: the window is maximized.</summary>
        public bool IsFill => Fraction.Width >= 1 && Fraction.Height >= 1;
    }

    /// <summary>The layouts of Windows 11's snap layouts flyout, which are the ones the overlay offers.</summary>
    public static class SnapLayouts
    {
        private static Rect R(double x, double y, double w, double h) => new Rect(x, y, w, h);

        private static readonly Rect[][] Standard =
        {
            new[] { R(0, 0, 1, 1) },
            new[] { R(0, 0, 0.5, 1), R(0.5, 0, 0.5, 1) },
            new[] { R(0, 0, 2.0 / 3, 1), R(2.0 / 3, 0, 1.0 / 3, 1) },
            new[] { R(0, 0, 1.0 / 3, 1), R(1.0 / 3, 0, 1.0 / 3, 1), R(2.0 / 3, 0, 1.0 / 3, 1) },
            new[] { R(0, 0, 0.5, 1), R(0.5, 0, 0.5, 0.5), R(0.5, 0.5, 0.5, 0.5) },
            new[] { R(0, 0, 0.5, 0.5), R(0.5, 0, 0.5, 0.5), R(0, 0.5, 0.5, 0.5), R(0.5, 0.5, 0.5, 0.5) },
        };

        // A monitor this wide has a layout of a wide middle between two narrow sides too.
        private static readonly Rect[] UltraWide = { R(0, 0, 0.25, 1), R(0.25, 0, 0.5, 1), R(0.75, 0, 0.25, 1) };

        public static Rect[][] For(bool ultraWide) => ultraWide ? Standard.Concat(new[] { UltraWide }).ToArray() : Standard;
    }

    internal static class SnapNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public int Flags;
        }

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_THICKFRAME = 0x40000;
        public const int WS_MAXIMIZEBOX = 0x10000;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x8000000;
        public const uint SWP_NOSIZE = 0x1;
        public const uint SWP_NOZORDER = 0x4;
        public const uint SWP_NOACTIVATE = 0x10;
        public const uint SWP_SHOWWINDOW = 0x40;
        public const int SW_MAXIMIZE = 3;
        public const int SW_RESTORE = 9;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
    }

    /// <summary>
    /// A tab dragged up past the taskbar shows the snap layouts over the monitor the cursor is on, and dropped on one of their cells the
    /// window of the tab is put there (Windows 11's snap layouts, from the taskbar). The tab's own drag-to-rearrange is untouched while
    /// the cursor is in the taskbar: the layouts show only once it is out of it, and go again when it comes back.
    ///
    /// The drag is the drag library's own (an OLE drag, in which the taskbar gets no mouse events), so the cursor is followed with a timer
    /// and the drop is seen as the end of the drag (QueryContinueDrag).
    /// </summary>
    internal sealed class SnapLayoutDrag
    {
        // How far past the taskbar the cursor goes before the layouts show, and how near it has to come back to hide them (in device
        // independent pixels at 96 DPI).
        private const double ShowDistance = 14;
        private const double HideDistance = 4;

        private readonly Taskbar _host;
        private readonly DispatcherTimer _timer;
        private ApplicationWindow _window;
        private SnapLayoutOverlay _overlay;
        private IntPtr _overlayMonitor;
        private SnapZone _hover;
        private bool _shown;

        private SnapLayoutDrag(Taskbar host)
        {
            _host = host;
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (s, e) => Update();
        }

        public static void Attach(ItemsControl list, Taskbar host)
        {
            var drag = new SnapLayoutDrag(host);
            GongSolutions.Wpf.DragDrop.DragDrop.SetDragHandler(list, new DragHandler(drag));
            list.AddHandler(System.Windows.DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(drag.OnQueryContinueDrag));
            list.AddHandler(System.Windows.DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(drag.OnGiveFeedback));
        }

        private void Begin(ApplicationWindow window)
        {
            End();
            if (!Settings.Instance.SnapLayouts || window == null || window.Handle == IntPtr.Zero)
            {
                return;
            }

            _window = window;
            _timer.Start();
        }

        private void End()
        {
            _timer.Stop();
            _window = null;
            Hide();
        }

        private void Hide()
        {
            _shown = false;
            _hover = null;
            _overlay?.Close();
            _overlay = null;
            _overlayMonitor = IntPtr.Zero;
        }

        // Whether the cursor is out of the taskbar, on the side of the screen: far enough to show the layouts, not so near that they hide.
        private bool PastBar(SnapNative.POINT c)
        {
            if (_host.Handle == IntPtr.Zero || !SnapNative.GetWindowRect(_host.Handle, out SnapNative.RECT bar))
            {
                return false;
            }

            foreach (Taskbar other in System.Windows.Application.Current.Windows.OfType<Taskbar>())
            {
                if (other.Handle != IntPtr.Zero && SnapNative.GetWindowRect(other.Handle, out SnapNative.RECT r) &&
                    c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom)
                {
                    return false;
                }
            }

            double inward = _host.AppBarEdge switch
            {
                AppBarEdge.Bottom => bar.Top - c.Y,
                AppBarEdge.Top => c.Y - bar.Bottom,
                AppBarEdge.Left => c.X - bar.Right,
                _ => bar.Left - c.X,
            };

            double scale = VisualTreeHelper.GetDpi(_host).DpiScaleX;
            return inward > (_shown ? HideDistance : ShowDistance) * scale;
        }

        private void Update()
        {
            if (_window == null || !SnapNative.GetCursorPos(out SnapNative.POINT c))
            {
                return;
            }

            if (!PastBar(c))
            {
                Hide();
                return;
            }

            IntPtr monitor = SnapNative.MonitorFromPoint(c, SnapNative.MONITOR_DEFAULTTONEAREST);
            if (_overlay == null || monitor != _overlayMonitor)
            {
                _overlay?.Close();
                var info = new SnapNative.MONITORINFO { Size = Marshal.SizeOf<SnapNative.MONITORINFO>() };
                if (!SnapNative.GetMonitorInfo(monitor, ref info))
                {
                    _overlay = null;
                    return;
                }

                bool ultraWide = (double)info.Work.Width / Math.Max(1, info.Work.Height) >= 2.0;
                _overlay = new SnapLayoutOverlay(info.Work, _host.AppBarEdge, SnapLayouts.For(ultraWide));
                _overlayMonitor = monitor;
                _overlay.Show();
            }

            _shown = true;
            SnapZone zone = _overlay.HitTest(c);
            if (zone != _hover)
            {
                _hover = zone;
                _overlay.SetHover(zone);
            }
        }

        private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (_window == null)
            {
                return;
            }

            if (e.Action == DragAction.Drop && _hover != null && _overlay != null)
            {
                // Done after the drag has ended: the window is put where the cursor was released.
                ApplicationWindow window = _window;
                Rect fraction = _hover.Fraction;
                SnapNative.RECT work = _overlay.Work;
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => SnapPlacement.Place(window, fraction, work)), DispatcherPriority.Background);
            }
        }

        private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_hover != null)
            {
                e.UseDefaultCursors = false;
                Mouse.SetCursor(Cursors.Arrow);
                e.Handled = true;
            }
        }

        // The library's own drag source, noting which window is dragged and when the drag has ended.
        private sealed class DragHandler : GongSolutions.Wpf.DragDrop.IDragSource
        {
            private readonly SnapLayoutDrag _drag;

            public DragHandler(SnapLayoutDrag drag)
            {
                _drag = drag;
            }

            private static GongSolutions.Wpf.DragDrop.IDragSource Default => GongSolutions.Wpf.DragDrop.DragDrop.DefaultDragHandler;

            public void StartDrag(GongSolutions.Wpf.DragDrop.IDragInfo dragInfo)
            {
                Default.StartDrag(dragInfo);
                if (dragInfo.Effects != DragDropEffects.None)
                {
                    _drag.Begin(dragInfo.Data as ApplicationWindow);
                }
            }

            public bool CanStartDrag(GongSolutions.Wpf.DragDrop.IDragInfo dragInfo) => Default.CanStartDrag(dragInfo);

            public void Dropped(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo) => Default.Dropped(dropInfo);

            public void DragDropOperationFinished(DragDropEffects operationResult, GongSolutions.Wpf.DragDrop.IDragInfo dragInfo)
            {
                _drag.End();
                Default.DragDropOperationFinished(operationResult, dragInfo);
            }

            public void DragCancelled()
            {
                _drag.End();
                Default.DragCancelled();
            }

            public bool TryCatchOccurredException(Exception exception) => Default.TryCatchOccurredException(exception);
        }
    }

    /// <summary>Puts a window in a cell of a layout: restored if it was maximized or minimized, and the cell's size to the pixel.</summary>
    internal static class SnapPlacement
    {
        public static void Place(ApplicationWindow window, Rect fraction, SnapNative.RECT work)
        {
            IntPtr hWnd = window.Handle;
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            if (SnapNative.IsIconic(hWnd) || SnapNative.IsZoomed(hWnd))
            {
                SnapNative.ShowWindow(hWnd, SnapNative.SW_RESTORE);
            }

            int style = SnapNative.GetWindowLong(hWnd, SnapNative.GWL_STYLE);
            int left = work.Left + (int)Math.Round(fraction.X * work.Width);
            int top = work.Top + (int)Math.Round(fraction.Y * work.Height);
            int right = work.Left + (int)Math.Round(fraction.Right * work.Width);
            int bottom = work.Top + (int)Math.Round(fraction.Bottom * work.Height);

            bool fill = fraction.Width >= 1 && fraction.Height >= 1;
            if (fill && (style & SnapNative.WS_MAXIMIZEBOX) != 0)
            {
                // To the monitor first, so it is maximized there.
                SnapNative.SetWindowPos(hWnd, IntPtr.Zero, left, top, 0, 0, SnapNative.SWP_NOSIZE | SnapNative.SWP_NOZORDER | SnapNative.SWP_NOACTIVATE);
                SnapNative.ShowWindow(hWnd, SnapNative.SW_MAXIMIZE);
            }
            else if ((style & SnapNative.WS_THICKFRAME) == 0)
            {
                // A window that cannot be resized is put in the middle of the cell.
                if (SnapNative.GetWindowRect(hWnd, out SnapNative.RECT own))
                {
                    int x = left + ((right - left) - own.Width) / 2;
                    int y = top + ((bottom - top) - own.Height) / 2;
                    SnapNative.SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SnapNative.SWP_NOSIZE | SnapNative.SWP_NOZORDER | SnapNative.SWP_NOACTIVATE);
                }
            }
            else
            {
                // A window's frame has an invisible part (the sides and the bottom of it) that is not in what is drawn: the cell is that
                // much larger on those sides, so what is drawn fills it.
                int dl = 0, dt = 0, dr = 0, db = 0;
                if (SnapNative.GetWindowRect(hWnd, out SnapNative.RECT outer) &&
                    SnapNative.DwmGetWindowAttribute(hWnd, SnapNative.DWMWA_EXTENDED_FRAME_BOUNDS, out SnapNative.RECT drawn, Marshal.SizeOf<SnapNative.RECT>()) == 0)
                {
                    dl = drawn.Left - outer.Left;
                    dt = drawn.Top - outer.Top;
                    dr = outer.Right - drawn.Right;
                    db = outer.Bottom - drawn.Bottom;
                }

                // Twice: a window moved to a monitor of another scale is resized for it when it arrives.
                for (int i = 0; i < 2; i++)
                {
                    SnapNative.SetWindowPos(hWnd, IntPtr.Zero, left - dl, top - dt, right - left + dl + dr, bottom - top + dt + db, SnapNative.SWP_NOZORDER | SnapNative.SWP_NOACTIVATE);
                }
            }

            window.BringToFront();
        }
    }
}
