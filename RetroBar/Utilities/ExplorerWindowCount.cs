using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// How many File Explorer windows are open (the ones that have a button on the taskbar), kept up to date once a second, so
    /// that the taskbar's icons for them can change when a second window is opened or the second-to-last is closed:
    /// TaskIconConverter gives a lone Explorer window its own icon and several of them the regular Explorer icon, and a
    /// binding to Count is what has the buttons ask again.
    /// </summary>
    public sealed class ExplorerWindowCount : INotifyPropertyChanged
    {
        public static ExplorerWindowCount Instance { get; } = new ExplorerWindowCount();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;

        private int _count;
        private readonly DispatcherTimer _timer;

        private ExplorerWindowCount()
        {
            _count = Count_();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Update();
            _timer.Start();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public int Count
        {
            get => _count;
            private set
            {
                if (_count != value)
                {
                    _count = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                }
            }
        }

        private void Update()
        {
            Count = Count_();
        }

        private static int Count_()
        {
            int count = 0;
            var className = new StringBuilder(64);
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                {
                    return true;
                }

                long exStyle = IntPtr.Size == 8 ? GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() : GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                {
                    return true;
                }

                className.Clear();
                GetClassName(hWnd, className, className.Capacity);
                string name = className.ToString();
                if (name == "CabinetWClass" || name == "ExploreWClass")
                {
                    count++;
                }

                return true;
            }, IntPtr.Zero);

            return count;
        }
    }
}
