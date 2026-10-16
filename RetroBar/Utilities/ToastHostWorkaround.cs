using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Windows keeps a window of its own for the notification banners ("Nouvelle notification", a Windows.UI.Core.CoreWindow of
    /// ShellExperienceHost) over the bottom of the screen above the taskbar - topmost, empty and invisible when there is no banner - and
    /// at times it takes the mouse for whatever is under it: the popups that open above the taskbar (the media popup's buttons and slider,
    /// the previews and the forecast) then do not react to the pointer, and nothing is highlighted or clicked until it goes away by itself.
    ///
    /// While the pointer is over such a window and it is as small as that idle one (a banner being shown is several times as tall), it is
    /// hidden: Windows shows it again by itself for the next banner, which then shows as usual.
    /// </summary>
    internal static class ToastHostWorkaround
    {
        private const string ClassName = "Windows.UI.Core.CoreWindow";

        // The idle window is a few dozen pixels tall; a banner is over a hundred.
        private const int IdleHeight = 80;

        private const uint GA_ROOT = 2;
        private const int SW_HIDE = 0;

        private static DispatcherTimer _timer;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        public static void Start()
        {
            if (_timer != null)
            {
                return;
            }

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (s, e) => Check();
            _timer.Start();
        }

        private static void Check()
        {
            try
            {
                if (!GetCursorPos(out POINT cursor))
                {
                    return;
                }

                IntPtr window = WindowFromPoint(cursor);
                if (window == IntPtr.Zero)
                {
                    return;
                }

                IntPtr root = GetAncestor(window, GA_ROOT);
                if (root == IntPtr.Zero)
                {
                    root = window;
                }

                var name = new StringBuilder(64);
                if (GetClassName(root, name, name.Capacity) == 0 || name.ToString() != ClassName)
                {
                    return;
                }

                GetWindowThreadProcessId(root, out uint processId);
                if (processId == Environment.ProcessId || !GetWindowRect(root, out RECT rect) || rect.Bottom - rect.Top > IdleHeight)
                {
                    return;
                }

                using Process owner = Process.GetProcessById((int)processId);
                if (string.Equals(owner.ProcessName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                {
                    ShowWindow(root, SW_HIDE);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The window or its process went away while it was looked at: nothing to do.
            }
        }
    }
}
