using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Keeps Windows' own taskbar hidden. RetroBar hides it once at startup, but Windows briefly shows it again for a
    /// moment now and then (a window such as Discord coming to the foreground does it): it is only a sliver at the very
    /// bottom of the screen, but its top edge shows up for a few frames above RetroBar's own bar. This hides it again
    /// the moment it is shown, instead of waiting for it to hide itself.
    /// </summary>
    public class ExplorerTaskbarGuard : IDisposable
    {
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_HIDEWINDOW = 0x0080;

        private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        // Kept in a field so the delegate isn't collected while Windows still holds it.
        private readonly WinEventProc _callback;
        private IntPtr _hook;

        public ExplorerTaskbarGuard()
        {
            _callback = OnWinEvent;
            // Out of context: called back on this thread (which pumps messages), not inside the shown window's process.
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
            {
                return;
            }

            var className = new StringBuilder(32);
            if (GetClassName(hwnd, className, className.Capacity) == 0)
            {
                return;
            }

            string name = className.ToString();
            if ((name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") && IsWindowVisible(hwnd))
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
