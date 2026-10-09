using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Keeps Windows' own taskbar from showing. RetroBar hides it once at startup, but Windows shows it again for a moment
    /// now and then (a window such as Discord coming to the foreground does it): only a sliver at the very bottom of the
    /// screen, but its top edge showed above RetroBar's bar for a few frames.
    ///
    /// Two things are done. Its windows are made fully transparent (a layered window with an alpha of 0), so that when
    /// Windows does show them there is nothing to see, from the very first frame - a window can't be stopped from being
    /// shown by another process, only made invisible. And it is hidden again the moment it is shown.
    /// </summary>
    public class ExplorerTaskbarGuard : IDisposable
    {
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x2;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_HIDEWINDOW = 0x0080;

        private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        // Kept in a field so the delegate isn't collected while Windows still holds it.
        private readonly WinEventProc _callback;
        private readonly uint _ownProcessId = (uint)Process.GetCurrentProcess().Id;
        private readonly HashSet<IntPtr> _transparent = new HashSet<IntPtr>();
        private IntPtr _hook;

        public ExplorerTaskbarGuard()
        {
            _callback = OnWinEvent;

            // Out of context: called back on this thread (which pumps messages), not inside the shown window's process.
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

            EnumWindows((hwnd, lParam) =>
            {
                if (IsTaskbarWindow(hwnd))
                {
                    MakeTransparent(hwnd);
                }

                return true;
            }, IntPtr.Zero);
        }

        // Windows' own taskbar windows: RetroBar itself has a window of the same class for the shell messages it handles.
        private bool IsTaskbarWindow(IntPtr hwnd)
        {
            var className = new StringBuilder(32);
            if (GetClassName(hwnd, className, className.Capacity) == 0)
            {
                return false;
            }

            string name = className.ToString();
            if (name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd")
            {
                return false;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            return processId != _ownProcessId;
        }

        private void MakeTransparent(IntPtr hwnd)
        {
            try
            {
                long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((style & WS_EX_LAYERED) == 0)
                {
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_LAYERED));
                }

                if (SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA))
                {
                    _transparent.Add(hwnd);
                }
            }
            catch (Exception)
            {
                // Hiding it again below still applies.
            }
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero || !IsTaskbarWindow(hwnd))
            {
                return;
            }

            // A new taskbar window (Explorer restarting, a monitor being added), or one that lost its transparency.
            if (eventType == EVENT_OBJECT_CREATE || !_transparent.Contains(hwnd))
            {
                MakeTransparent(hwnd);
            }

            if (eventType == EVENT_OBJECT_SHOW && IsWindowVisible(hwnd))
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

            // Back to how Windows made them, for whoever shows them next.
            foreach (IntPtr hwnd in _transparent)
            {
                try
                {
                    long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style & ~WS_EX_LAYERED));
                }
                catch (Exception)
                {
                }
            }

            _transparent.Clear();
        }
    }
}
