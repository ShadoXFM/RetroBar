using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Shows the CPU's average temperature, read from the shared file RetroBar.CpuTempHelper
    /// (a separate always-elevated process) writes to - see CpuTemperatureMonitor. Collapses
    /// itself entirely - rather than showing a "N/A" placeholder the way WeatherDisplay does for
    /// a missing location - whenever Settings.ShowCpuTemp is off or the helper isn't currently
    /// writing fresh readings (not running, or the user hasn't set up its scheduled task yet),
    /// since unlike weather there's no useful fallback state worth taking up taskbar space for.
    /// Each poll independently decides visibility, so this comes and goes live if the helper
    /// starts or stops while RetroBar is already running, rather than only being checked once at
    /// RetroBar's own startup.
    /// </summary>
    public partial class CpuTempDisplay : UserControl
    {
        private readonly CpuTemperatureMonitor _monitor = new CpuTemperatureMonitor();
        private DispatcherTimer _timer;

        public CpuTempDisplay()
        {
            InitializeComponent();
        }

        private void CpuTempDisplay_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            if (Settings.Instance.ShowCpuTemp)
            {
                Start();
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Settings.ShowCpuTemp))
            {
                return;
            }

            if (Settings.Instance.ShowCpuTemp)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }

        private void Start()
        {
            if (_timer != null)
            {
                return;
            }

            UpdateTemperature();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, args) => UpdateTemperature();
            _timer.Start();
        }

        private void Stop()
        {
            _timer?.Stop();
            _timer = null;

            CpuTempText.Text = "";
            Visibility = Visibility.Collapsed;
            SetIsCpuTempShown(false);
        }

        private void UpdateTemperature()
        {
            double? temperature = _monitor.ReadTemperature();

            if (temperature.HasValue)
            {
                CpuTempText.Text = $"{Math.Round(temperature.Value)}°C";
                Visibility = Visibility.Visible;
            }
            else
            {
                CpuTempText.Text = "";
                Visibility = Visibility.Collapsed;
            }

            SetIsCpuTempShown(Visibility == Visibility.Visible);
        }

        private void SetIsCpuTempShown(bool isShown)
        {
            if (Window.GetWindow(this) is Taskbar taskbar)
            {
                taskbar.IsCpuTempShown = isShown;
            }
        }

        private void CpuTempDisplay_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            Stop();
        }
    }
}
