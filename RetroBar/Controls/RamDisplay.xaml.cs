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
    /// Shows the memory in use as a percentage, read once every two seconds, with a graph of the last five minutes that opens when the
    /// pointer rests on it (the same as the CPU temperature's, which this is a copy of: see CpuTempDisplay). Collapses itself when
    /// Settings.ShowRam is off.
    /// </summary>
    public partial class RamDisplay : UserControl
    {
        private DispatcherTimer _timer;
        private double? _percent;
        private double _totalGb, _usedGb;

        public RamDisplay()
        {
            InitializeComponent();
            InitializeGraphSlide();

            // A right click inside the popup does nothing: it went on to the taskbar's own menu, opened over the popup.
            GraphFrame.PreviewMouseRightButtonDown += (s, e) => e.Handled = true;
            GraphFrame.PreviewMouseRightButtonUp += (s, e) => e.Handled = true;
            Graph.CollectingText = TryFindResource("cpu_temp_graph_collecting") as string ?? "Collecting readings…";
        }

        private void RamDisplay_OnLoaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            if (Settings.Instance.ShowRam)
            {
                Start();
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Settings.ShowRam))
            {
                return;
            }

            if (Settings.Instance.ShowRam)
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

            UpdateMemory();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, args) => UpdateMemory();
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

            RamText.Text = "";
            Visibility = Visibility.Collapsed;
        }

        private void UpdateMemory()
        {
            _percent = ReadMemory();
            if (_percent.HasValue)
            {
                RamHistory.Record(_percent.Value, DateTime.UtcNow);
            }

            ShowPercent();
            if (GraphPopup.IsOpen)
            {
                RefreshGraph();
            }
        }

        private double? ReadMemory()
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return null;
            }

            _totalGb = status.ullTotalPhys / 1073741824.0;
            _usedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1073741824.0;
            return status.dwMemoryLoad;
        }

        private void ShowPercent()
        {
            if (_percent.HasValue)
            {
                RamText.Text = $"{Math.Round(_percent.Value)}%";
                Visibility = Visibility.Visible;
            }
            else
            {
                RamText.Text = "";
                Visibility = Visibility.Collapsed;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

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

        private void RamPanel_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _graphCloseTimer?.Stop();
            if (GraphPopup.IsOpen && !_slide.Closing)
            {
                return;
            }

            _graphOpenTimer ??= MakeTimer(GraphOpenDelay, OpenGraph);
            _graphOpenTimer.Stop();

            // Going from the weather's forecast to this is quick; it is the rest on the widget that waits.
            _graphOpenTimer.Interval = HoverPopups.AnyOpen ? HoverPopups.SwitchDelay : GraphOpenDelay;
            _graphOpenTimer.Start();
        }

        private void RamPanel_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleGraphClose();

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
            _graphCloseTimer ??= MakeTimer(GraphCloseDelay, () => _slide.Hide());
            _graphCloseTimer.Stop();
            _graphCloseTimer.Start();
        }

        private void OpenGraph()
        {
            if ((GraphPopup.IsOpen && !_slide.Closing) || !RamPanel.IsMouseOver || !_percent.HasValue)
            {
                return;
            }

            // One at a time with the weather's forecast: if that is open it slides away first (see HoverPopups).
            HoverPopups.Open(_slide, () =>
            {
                if (!RamPanel.IsMouseOver)
                {
                    return;
                }

                GraphPopup.PlacementTarget = RamPanel;
                GraphPopup.Placement = PlacementMode.Custom;
                GraphPopup.CustomPopupPlacementCallback = PlaceGraph;
                RefreshGraph();
                _slide.Show();
            });
        }

        private void RefreshGraph()
        {
            string Percent(double value) => $"{Math.Round(value)}%";

            Graph.Refresh();
            RamHistory.Sample[] samples = RamHistory.Last();
            if (_percent.HasValue)
            {
                GraphHeader.Text = $"{Percent(_percent.Value)} · {_usedGb:0.0} / {_totalGb:0.0} GB";
            }

            // Each its own text, to be spread across the width: the word in the theme's font, the percentage in Tahoma bold as the others.
            void Stat(TextBlock block, string key, string fallback, double? value)
            {
                // Left as it is while it reads the same, so the line is not laid out and drawn again every two seconds.
                string reading = value == null ? null : (TryFindResource(key) as string ?? fallback) + Percent(value.Value);
                if (reading == (block.Tag as string))
                {
                    return;
                }

                block.Tag = reading;
                block.Inlines.Clear();
                if (value == null)
                {
                    return;
                }

                string[] parts = (TryFindResource(key) as string ?? fallback).Split("{0}");
                block.Inlines.Add(new System.Windows.Documents.Run(parts[0]));
                block.Inlines.Add(new System.Windows.Documents.Run(Percent(value.Value)) { FontFamily = new System.Windows.Media.FontFamily("Tahoma"), FontWeight = FontWeights.Bold });
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    block.Inlines.Add(new System.Windows.Documents.Run(parts[1]));
                }
            }

            Stat(GraphMin, "cpu_temp_graph_min", "Min {0}", samples.Length == 0 ? null : samples.Min(s => s.Percent));
            Stat(GraphAverage, "cpu_temp_graph_average", "Average {0}", samples.Length == 0 ? null : samples.Average(s => s.Percent));
            Stat(GraphMax, "cpu_temp_graph_max", "Max {0}", samples.Length == 0 ? null : samples.Max(s => s.Percent));
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

            if (PopupSlide.MeasureOverlap(GraphFrame, taskbar) is double measured)
            {
                return measured;
            }

            double? frameBottom = taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar);
            if (frameBottom == null)
            {
                try
                {
                    frameBottom = RamPanel.TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
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
                    sameAsMedia = mediaBottom - RamPanel.TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
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

        private void RamDisplay_OnUnloaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            Stop();
        }
    }
}
