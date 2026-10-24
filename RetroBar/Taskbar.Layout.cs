using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;

namespace RetroBar
{
    // The taskbar laid out in the way of Windows 11 (Settings.TaskbarStyle): the program tabs (small, with only their icons) and the pinned
    // programs in the middle, the Start button at the left edge as ever, with the media player in a box of its own after it; the tray box
    // (the weather, the CPU temperature, the notification icons and the clock) stays at the right. The controls are moved between the
    // two layouts when the setting changes, not made twice.
    public partial class Taskbar
    {
        private FrameworkElement _mediaBox;
        private Thickness _weatherBaseMargin;

        private void StartButton_IsVisibleChangedForWeather(object sender, DependencyPropertyChangedEventArgs e) => UpdateWeatherMargin();

        // With no Start button before it (the option is off, or this is a taskbar that has none) the weather is 2 pixels further from the edge.
        private void UpdateWeatherMargin()
        {
            if (!_windows11Applied)
            {
                return;
            }

            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            double extra = StartButton.Visibility == Visibility.Visible ? 0 : 2 / (dpi > 0 ? dpi : 1);
            WeatherGroup.Margin = new Thickness(_weatherBaseMargin.Left + extra, _weatherBaseMargin.Top, _weatherBaseMargin.Right, _weatherBaseMargin.Bottom);
        }
        private BindingBase _weatherTag;
        private StackPanel _weatherPlace;
        private Grid _centerStrip;
        private StackPanel _centerRow;
        private bool _windows11Applied;

        // Only for a taskbar at the top or the bottom: the middle of a vertical one has no left and right.
        private bool WantsWindows11Style => Settings.Instance.TaskbarStyle == TaskbarStyleOption.Windows11 && Orientation == Orientation.Horizontal;

        private void InitializeTaskbarStyle()
        {
            Settings.Instance.PropertyChanged += TaskbarStyle_SettingChanged;
            ApplyTaskbarStyle();
        }

        // A taskbar that is closed (the theme can make the taskbars again) no longer follows the style; left listening, it moved the controls of a
        // window that was gone, and asked the new ones' shared things to be set again.
        private void ReleaseTaskbarStyle()
        {
            Settings.Instance.PropertyChanged -= TaskbarStyle_SettingChanged;
        }

        private void TaskbarStyle_SettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.TaskbarStyle) || e.PropertyName == nameof(Settings.Edge))
            {
                Dispatcher.BeginInvoke(new System.Action(ApplyTaskbarStyle));
            }
            else if (e.PropertyName == nameof(Settings.ShowQuickLaunch) && _windows11Applied)
            {
                // The pinned programs are among the tabs; the toolbar of them stays away.
                Dispatcher.BeginInvoke(new System.Action(() => QuickLaunchToolbar.Visibility = Visibility.Collapsed));
            }
        }

        private void ApplyTaskbarStyle()
        {
            bool windows11 = WantsWindows11Style;
            if (windows11 == _windows11Applied)
            {
                return;
            }

            _windows11Applied = windows11;
            UpdateTrayContentFlags();
            if (windows11)
            {
                // The weather and the CPU temperature: out of the tray box, onto the bar at the left, in no box; the media player is in
                // the tray box in their place (at its left, with the notification icons and the clock).
                // What the tray box has in the weather's place is the weather group with nothing in it, as with the weather and the CPU
                // temperature turned off: its margin is part of what the box's per-monitor margins (TrayNotifyListAlone and the like) were tuned with.
                int weatherAt = TrayContent.Children.IndexOf(WeatherGroup);
                TrayContent.Children.Remove(WeatherGroup);
                _weatherPlace = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = WeatherGroup.Margin };
                TrayContent.Children.Insert(weatherAt, _weatherPlace);

                _mediaBox = WeatherGroup;
                _weatherBaseMargin = WeatherGroup.Margin;
                StartButton.IsVisibleChanged += StartButton_IsVisibleChangedForWeather;
                UpdateWeatherMargin();

                // On the bar it has a tag of its own (WeatherGroupBar): the tag it has in the tray box (WeatherGroup, with Playing) is tuned
                // for being level with the clock in the box.
                _weatherTag = BindingOperations.GetBindingBase(WeatherGroup, MonitorOffset.TagProperty);
                BindingOperations.ClearBinding(WeatherGroup, MonitorOffset.TagProperty);
                MonitorOffset.SetTag(WeatherGroup, "WeatherGroupBar");
                DockPanel.SetDock(_mediaBox, Dock.Left);

                // The tabs: out of the dock, into a row that is centered in what the other boxes leave. The Start button stays where it
                // was, at the left edge, with the media player's box after it.
                TaskbarDock.Children.Remove(TaskListControl);
                // At the left, with a margin that is worked out in whole pixels (see ApplyCenterMargin): left to be centered by the layout,
                // the row moved by a pixel whenever a box beside it changed by a fraction of one, and its tabs with it.
                _centerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
                _centerRowLeft = double.NaN;
                _centerRow.Children.Add(TaskListControl);
                _centerStrip = new Grid();
                _centerStrip.Children.Add(_centerRow);
                _centerStrip.SizeChanged += (s, e) => UpdateCenterWidth();
                TaskListControl.SizeChanged += (s, e) => UpdateCenterWidth();
                _mediaBox.SizeChanged += (s, e) => UpdateCenterWidth();
                StartButton.SizeChanged += (s, e) => UpdateCenterWidth();
                TrayBox.SizeChanged += (s, e) => UpdateCenterWidth();

                // After the Start button and the edge that opens the menu when there is no button: before it, the edge came after the weather.
                TaskbarDock.Children.Insert(TaskbarDock.Children.IndexOf(StartEdge) + 1, _mediaBox);
                TaskbarDock.Children.Add(_centerStrip);

                QuickLaunchToolbar.Visibility = Visibility.Collapsed;
                TaskListControl.IconOnly = true;
                StartButton.IconOnly = true;
            }
            else
            {
                TaskListControl.IconOnly = false;
                StartButton.IconOnly = false;

                _centerRow.Children.Remove(TaskListControl);
                TaskbarDock.Children.Remove(_centerStrip);
                TaskbarDock.Children.Remove(_mediaBox);
                StartButton.IsVisibleChanged -= StartButton_IsVisibleChangedForWeather;
                WeatherGroup.Margin = _weatherBaseMargin;
                WeatherGroup.ClearValue(MonitorOffset.TagProperty);
                if (_weatherTag != null)
                {
                    WeatherGroup.SetBinding(MonitorOffset.TagProperty, _weatherTag);
                }
                int placeAt = TrayContent.Children.IndexOf(_weatherPlace);
                TrayContent.Children.Remove(_weatherPlace);
                _weatherPlace = null;
                TrayContent.Children.Insert(Math.Max(0, placeAt), WeatherGroup);

                TaskbarDock.Children.Add(TaskListControl);
                _centerStrip = null;
                _centerRow = null;
                _mediaBox = null;

                QuickLaunchToolbar.Visibility = Settings.Instance.ShowQuickLaunch ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // The tabs are centered on the whole bar, not in what the boxes at the left and the right leave (which moved them when the tray grew,
        // the hidden icons being shown, say): the row has a margin on the side of the smaller box, and the room it has is what is left
        // either side of the middle.
        private void UpdateCenterWidth()
        {
            if (_centerStrip == null || _centerRow == null)
            {
                return;
            }

            double difference = Math.Abs(CenterShift());
            ApplyCenterMargin();
            TaskListControl.AvailableWidth = Math.Max(0, _centerStrip.ActualWidth - difference);

            Dispatcher.BeginInvoke(new Action(() => CenterTabs(0)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private double _centerRowLeft = double.NaN;

        // How much more room there is at the left of the middle than at the right (the boxes either side), and what is left over from it.
        private double CenterShift()
        {
            double left = StartButton.ActualWidth + (_mediaBox?.ActualWidth ?? 0) + (_mediaBox?.Margin.Left ?? 0);
            return TrayBox.ActualWidth - left + _centerCorrection;
        }

        // The row of tabs is put in whole pixels, and moved only when it is off by more than a pixel: the boxes beside it are as wide as their text
        // is (a fraction of a pixel more or less), and with the row centered by the layout that was a pixel to the left or the right, all the
        // tabs together, now and then.
        private void ApplyCenterMargin()
        {
            double pixel = 1 / System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            double wanted = (_centerStrip.ActualWidth + CenterShift() - TaskListControl.ActualWidth) / 2;
            if (double.IsNaN(_centerRowLeft) || Math.Abs(wanted - _centerRowLeft) > pixel * 0.75)
            {
                _centerRowLeft = Math.Max(0, Math.Round(wanted / pixel) * pixel);
                _centerRow.Margin = new Thickness(_centerRowLeft, 0, 0, 0);
            }
        }

        // What is left over from the sum above (the room beside the tabs in their list, the gaps the boxes have): the middle of the tabs is
        // measured and the row moved by twice what it is off, a few times at most.
        private double _centerCorrection;

        private void CenterTabs(int pass)
        {
            if (_centerStrip == null || _centerRow == null || TaskListControl.TasksList.ActualWidth <= 0 || TaskbarDock.ActualWidth <= 0)
            {
                return;
            }

            try
            {
                Point middle = TaskListControl.TasksList.TranslatePoint(new Point(TaskListControl.TasksList.ActualWidth / 2, 0), TaskbarDock);
                double pixel = 1 / System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                double off = TaskbarDock.ActualWidth / 2 - middle.X;
                if (Math.Abs(off) < pixel * 0.75 || pass >= 3)
                {
                    return;
                }

                _centerCorrection += Math.Round(2 * off / pixel) * pixel;
                _centerRowLeft = double.NaN;
                ApplyCenterMargin();
                Dispatcher.BeginInvoke(new Action(() => CenterTabs(pass + 1)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
