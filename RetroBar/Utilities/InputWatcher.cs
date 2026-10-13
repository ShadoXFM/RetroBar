using System;
using System.Runtime.InteropServices;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Watches the mouse buttons and the keyboard of the whole session through low-level hooks, for as long as it is started (no polling
    /// in between): the media popup uses it to close on a click elsewhere or on Escape. The events come on the thread that started it
    /// (the hook's callback runs in its message loop), and must return at once: a slow one delays every key and click of the session.
    /// It does not take any input away, only looks.
    /// </summary>
    public sealed class InputWatcher : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_XBUTTONDOWN = 0x020B;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        // Kept in fields: the hooks call into them for as long as they are installed.
        private readonly HookProc _mouseProc;
        private readonly HookProc _keyboardProc;
        private IntPtr _mouseHook;
        private IntPtr _keyboardHook;

        /// <summary>A mouse button went down, at this screen position (device pixels).</summary>
        public event Action<int, int> MouseDown;

        /// <summary>A key went down (its virtual key code).</summary>
        public event Action<int> KeyDown;

        public InputWatcher()
        {
            _mouseProc = MouseCallback;
            _keyboardProc = KeyboardCallback;
        }

        public bool IsStarted => _mouseHook != IntPtr.Zero;

        public void Start()
        {
            if (IsStarted)
            {
                return;
            }

            IntPtr module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        }

        public void Stop()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }

        private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = (int)wParam;
                if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
                {
                    MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    try
                    {
                        MouseDown?.Invoke(data.X, data.Y);
                    }
                    catch (Exception)
                    {
                        // A handler's own failure must not reach the hook's caller.
                    }
                }
            }

            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && ((int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN))
            {
                KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                try
                {
                    KeyDown?.Invoke((int)data.VirtualKey);
                }
                catch (Exception)
                {
                }
            }

            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
