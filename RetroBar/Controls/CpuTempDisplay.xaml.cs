using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private double? _celsius;

        public CpuTempDisplay()
        {
            InitializeComponent();
            InitializeGraphSlide();
            GraphTitle.Text = TryFindResource("cpu_temp_graph_title") as string ?? "CPU temperature";
            GraphAgo.Text = string.Format(TryFindResource("cpu_temp_graph_ago") as string ?? "{0} min ago", (int)CpuTemperatureHistory.Span.TotalMinutes);
            GraphEnd.Text = TryFindResource("cpu_temp_graph_now") as string ?? "now";
            Graph.CollectingText = TryFindResource("cpu_temp_graph_collecting") as string ?? "Collecting readings…";
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
            if (e.PropertyName == nameof(Settings.WeatherUseFahrenheit))
            {
                if (_timer != null)
                {
                    ShowTemperature();
                }

                return;
            }

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

            _graphOpenTimer?.Stop();
            _graphCloseTimer?.Stop();
            _slide.Reset();
            GraphPopup.IsOpen = false;

            CpuTempText.Text = "";
            Visibility = Visibility.Collapsed;
            SetIsCpuTempShown(false);
        }

        private void UpdateTemperature()
        {
            _celsius = _monitor.ReadTemperature();
            ShowTemperature();
            if (GraphPopup.IsOpen)
            {
                RefreshGraph();
            }
        }

        // In the unit of the weather's setting (Settings.WeatherUseFahrenheit, "Use Fahrenheit" for both temperatures).
        private void ShowTemperature()
        {
            if (_celsius.HasValue)
            {
                double value = Settings.Instance.WeatherUseFahrenheit ? _celsius.Value * 9 / 5 + 32 : _celsius.Value;
                CpuTempText.Text = $"{Math.Round(value)}°";
                Visibility = Visibility.Visible;
            }
            else
            {
                CpuTempText.Text = "";
                Visibility = Visibility.Collapsed;
            }

            SetIsCpuTempShown(Visibility == Visibility.Visible);
        }

        // The graph: opens when the pointer rests on the widget, closes shortly after it leaves the widget and the popup, as the weather's
        // forecast does (see WeatherDisplay).
        private static readonly TimeSpan GraphOpenDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan GraphCloseDelay = TimeSpan.FromMilliseconds(350);
        private const double ShadowRoom = 6;
        private const double FrameStyleMargin = 2.4;

        private DispatcherTimer _graphOpenTimer;
        private DispatcherTimer _graphCloseTimer;
        private PopupSlide _slide;

        private void InitializeGraphSlide()
        {
            _slide = new PopupSlide(GraphPopup, GraphFrame, GraphSlideHost,
                () => Settings.Instance.Edge switch
                {
                    ManagedShell.AppBar.AppBarEdge.Left => SlideDirection.Left,
                    ManagedShell.AppBar.AppBarEdge.Right => SlideDirection.Right,
                    ManagedShell.AppBar.AppBarEdge.Top => SlideDirection.Up,
                    _ => SlideDirection.Down,
                },
                GraphWindowOverlap);
        }

        private static DispatcherTimer MakeTimer(TimeSpan interval, Action tick)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                tick();
            };
            return timer;
        }

        private void CpuTempPanel_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _graphCloseTimer?.Stop();
            if (GraphPopup.IsOpen && !_slide.Closing)
            {
                return;
            }

            _graphOpenTimer ??= MakeTimer(GraphOpenDelay, OpenGraph);
            _graphOpenTimer.Stop();
            _graphOpenTimer.Start();
        }

        private void CpuTempPanel_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleGraphClose();

        private void GraphPopup_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _graphCloseTimer?.Stop();
            if (_slide.Closing)
            {
                _slide.Show();
            }
        }

        private void GraphPopup_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleGraphClose();

        private void ScheduleGraphClose()
        {
            _graphOpenTimer?.Stop();
            _graphCloseTimer ??= MakeTimer(GraphCloseDelay, _slide.Hide);
            _graphCloseTimer.Stop();
            _graphCloseTimer.Start();
        }

        private void OpenGraph()
        {
            if ((GraphPopup.IsOpen && !_slide.Closing) || !CpuTempPanel.IsMouseOver || !_celsius.HasValue)
            {
                return;
            }

            GraphPopup.PlacementTarget = CpuTempPanel;
            GraphPopup.Placement = PlacementMode.Custom;
            GraphPopup.CustomPopupPlacementCallback = PlaceGraph;
            RefreshGraph();
            _slide.Show();
        }

        private void RefreshGraph()
        {
            double InUnit(double celsius) => Settings.Instance.WeatherUseFahrenheit ? celsius * 9 / 5 + 32 : celsius;
            string Degrees(double celsius) => $"{Math.Round(InUnit(celsius))}°";

            Graph.Refresh();
            CpuTemperatureHistory.Sample[] samples = CpuTemperatureHistory.Last();
            if (_celsius.HasValue)
            {
                GraphNow.Text = Degrees(_celsius.Value);
            }

            GraphStats.Text = samples.Length == 0
                ? ""
                : string.Format(
                    TryFindResource("cpu_temp_graph_stats") as string ?? "Min {0}   Average {1}   Max {2}",
                    Degrees(samples.Min(s => s.Celsius)),
                    Degrees(samples.Average(s => s.Celsius)),
                    Degrees(samples.Max(s => s.Celsius)));
        }

        // On a taskbar at the bottom: how far the popup's window reaches below the taskbar's top, so that only what is above it shows while
        // it slides (see WeatherDisplay.ForecastWindowOverlap).
        private double? GraphWindowOverlap()
        {
            if (Window.GetWindow(this) is not Taskbar taskbar ||
                Settings.Instance.Edge is ManagedShell.AppBar.AppBarEdge.Left or ManagedShell.AppBar.AppBarEdge.Right or ManagedShell.AppBar.AppBarEdge.Top)
            {
                return null;
            }

            double? frameBottom = taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar);
            if (frameBottom == null)
            {
                try
                {
                    frameBottom = CpuTempPanel.TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                    frameBottom = 0;
                }
            }

            return Math.Max(0, frameBottom.Value + ShadowRoom + FrameStyleMargin);
        }

        private CustomPopupPlacement[] PlaceGraph(Size popupSize, Size targetSize, Point offset)
        {
            double sameAsMedia = 0;
            if (Window.GetWindow(this) is Taskbar taskbar && taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar) is double mediaBottom)
            {
                try
                {
                    sameAsMedia = mediaBottom - CpuTempPanel.TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                }
            }

            double centeredX = (targetSize.Width - popupSize.Width) / 2;
            double centeredY = (targetSize.Height - popupSize.Height) / 2;
            return Settings.Instance.Edge switch
            {
                ManagedShell.AppBar.AppBarEdge.Left => new[] { new CustomPopupPlacement(new Point(targetSize.Width + offset.X - ShadowRoom, centeredY), PopupPrimaryAxis.Vertical) },
                ManagedShell.AppBar.AppBarEdge.Right => new[] { new CustomPopupPlacement(new Point(-popupSize.Width + offset.X + ShadowRoom, centeredY), PopupPrimaryAxis.Vertical) },
                ManagedShell.AppBar.AppBarEdge.Top => new[] { new CustomPopupPlacement(new Point(centeredX, targetSize.Height + offset.Y - ShadowRoom), PopupPrimaryAxis.Horizontal) },
                _ => new[] { new CustomPopupPlacement(new Point(centeredX, -popupSize.Height + offset.Y + ShadowRoom + FrameStyleMargin + sameAsMedia), PopupPrimaryAxis.Horizontal) },
            };
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
