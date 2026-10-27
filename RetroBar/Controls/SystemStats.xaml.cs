using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Small widgets for the taskbar: the CPU load, the memory in use and the network speed, each with its own setting
    /// (Settings.ShowStatsCpu and so on) and a little graph of the last half minute. They are read once a second, only while at least one
    /// of them is on; the control collapses when none is shown.
    /// </summary>
    public partial class SystemStats : UserControl
    {
        private const int HistoryLength = 30;

        private DispatcherTimer _timer;

        private readonly double[] _cpu = new double[HistoryLength];
        private readonly double[] _ram = new double[HistoryLength];
        private readonly double[] _net = new double[HistoryLength];

        private ulong _lastIdle, _lastKernel, _lastUser;
        private bool _haveCpuBase;
        private long _lastReceived = -1, _lastSent;
        private DateTime _lastBytesTime;

        public SystemStats()
        {
            InitializeComponent();
        }

        private void SystemStats_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
            UpdateRunning();
        }

        private void SystemStats_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            StopTimer();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null && e.PropertyName.StartsWith("ShowStats", StringComparison.Ordinal))
            {
                UpdateRunning();
            }
        }

        private void UpdateRunning()
        {
            Settings settings = Settings.Instance;
            if (!settings.ShowStatsCpu && !settings.ShowStatsRam && !settings.ShowStatsNetwork)
            {
                StopTimer();
                Visibility = Visibility.Collapsed;
                return;
            }

            if (_timer == null)
            {
                _haveCpuBase = false;
                _lastReceived = -1;
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (s, args) => Update();
                _timer.Start();
            }

            Update();
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer = null;
        }

        private static void Push(double[] history, double value)
        {
            Array.Copy(history, 1, history, 0, history.Length - 1);
            history[^1] = value;
        }

        private void Update()
        {
            Settings settings = Settings.Instance;

            bool cpuShown = false;
            if (settings.ShowStatsCpu && TryReadCpu(out double cpu))
            {
                Push(_cpu, cpu);
                CpuText.Text = $"CPU {Math.Round(cpu)}%";
                CpuItem.ToolTip = $"CPU: {Math.Round(cpu)}%";
                CpuGraph.SetValues(_cpu);
                cpuShown = true;
            }

            bool ramShown = false;
            if (settings.ShowStatsRam && ReadMemory(out double ramPercent, out double usedGb, out double totalGb))
            {
                Push(_ram, ramPercent);
                RamText.Text = $"RAM {Math.Round(ramPercent)}%";
                RamItem.ToolTip = string.Format(CultureInfo.CurrentCulture, "{0:0.0} / {1:0.0} GB", usedGb, totalGb);
                RamGraph.SetValues(_ram);
                ramShown = true;
            }

            bool netShown = false;
            if (settings.ShowStatsNetwork && ReadNetwork(out double down, out double up))
            {
                Push(_net, down + up);
                NetGraph.Maximum = Math.Max(_net.Max(), 256 * 1024);
                NetText.Text = $"↓{FormatRate(down)} ↑{FormatRate(up)}";
                NetItem.ToolTip = $"↓ {FormatRate(down)}B/s   ↑ {FormatRate(up)}B/s";
                NetGraph.SetValues(_net);
                netShown = true;
            }

            // An item that has no reading yet (the first second, or switched off) is not shown.
            CpuItem.Visibility = cpuShown ? Visibility.Visible : Visibility.Collapsed;
            RamItem.Visibility = ramShown ? Visibility.Visible : Visibility.Collapsed;
            NetItem.Visibility = netShown ? Visibility.Visible : Visibility.Collapsed;
            Visibility = cpuShown || ramShown || netShown ? Visibility.Visible : Visibility.Collapsed;
        }

        // "12K", "1.2M": the rate of a second, in the units the widget has room for.
        private static string FormatRate(double bytesPerSecond)
        {
            double mega = bytesPerSecond / (1024 * 1024);
            if (mega >= 10)
            {
                return $"{Math.Round(mega)}M";
            }

            if (mega >= 1)
            {
                return mega.ToString("0.0", CultureInfo.CurrentCulture) + "M";
            }

            return $"{Math.Round(bytesPerSecond / 1024)}K";
        }

        private bool TryReadCpu(out double percent)
        {
            percent = 0;
            if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            {
                return false;
            }

            ulong i = ToUInt64(idle), k = ToUInt64(kernel), u = ToUInt64(user);
            bool hadBase = _haveCpuBase;
            ulong idleDelta = i - _lastIdle;
            ulong total = (k - _lastKernel) + (u - _lastUser);
            _lastIdle = i;
            _lastKernel = k;
            _lastUser = u;
            _haveCpuBase = true;
            if (!hadBase || total == 0)
            {
                return false;
            }

            // The kernel time includes the idle time.
            percent = Math.Min(100, Math.Max(0, 100.0 * (total - idleDelta) / total));
            return true;
        }

        private static bool ReadMemory(out double percent, out double usedGb, out double totalGb)
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            percent = usedGb = totalGb = 0;
            if (!GlobalMemoryStatusEx(ref status))
            {
                return false;
            }

            percent = status.dwMemoryLoad;
            totalGb = status.ullTotalPhys / 1073741824.0;
            usedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0;
            return true;
        }

        private bool ReadNetwork(out double down, out double up)
        {
            down = up = 0;
            try
            {
                long received = 0, sent = 0;
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    IPInterfaceStatistics stats = nic.GetIPStatistics();
                    received += stats.BytesReceived;
                    sent += stats.BytesSent;
                }

                DateTime now = DateTime.UtcNow;
                bool hadBase = _lastReceived >= 0;
                double seconds = (now - _lastBytesTime).TotalSeconds;
                long lastReceived = _lastReceived, lastSent = _lastSent;
                _lastReceived = received;
                _lastSent = sent;
                _lastBytesTime = now;
                if (!hadBase || seconds < 0.2)
                {
                    return false;
                }

                down = Math.Max(0, (received - lastReceived) / seconds);
                up = Math.Max(0, (sent - lastSent) / seconds);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ulong ToUInt64(FILETIME t) => ((ulong)t.dwHighDateTime << 32) | t.dwLowDateTime;

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    }
}
