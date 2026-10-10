using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        // Above these the temperature is drawn in full strength, in amber and then red, instead of dimmer than the clock.
        private const double WarmFrom = 80;
        private const double HotFrom = 90;
        private static readonly Brush WarmBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xA8, 0x38)));
        private static readonly Brush HotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x52, 0x48)));

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

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        private void ShowTemperatureColor(double temperature)
        {
            if (temperature >= WarmFrom)
            {
                Brush brush = temperature >= HotFrom ? HotBrush : WarmBrush;
                CpuTempText.Foreground = brush;
                CpuTempChip.Fill = brush;
                CpuTempText.Opacity = CpuTempChip.Opacity = 1;
            }
            else
            {
                // Back to the theme's clock colour, which follows theme changes.
                CpuTempText.SetResourceReference(TextBlock.ForegroundProperty, "ClockForeground");
                CpuTempChip.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ClockForeground");
                CpuTempText.Opacity = CpuTempChip.Opacity = 0.7;
            }
        }

        private void UpdateTemperature()
        {
            double? temperature = _monitor.ReadTemperature();

            if (temperature.HasValue)
            {
                CpuTempText.Text = $"{Math.Round(temperature.Value)}°C";
                ShowTemperatureColor(temperature.Value);
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
