using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;

namespace RetroBar
{
    // The taskbar laid out in the way of Windows 11 (Settings.TaskbarStyle): the program tabs (small, with only their icons) and the pinned
    // programs in the middle, the Start button at the left edge as ever, with the media player in a box of its own after it; the tray box
    // (the weather, the CPU temperature, the notification icons and the clock) stays at the right. The controls are moved between the
    // two layouts when the setting changes, not made twice.
    public partial class Taskbar
    {
        private GroupBox _mediaBox;
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
            if (windows11)
            {
                // The weather and the CPU temperature: out of the tray box, into a box of their own at the left; the media player is in
                // the tray box in their place (at its left, with the notification icons and the clock).
                TrayContent.Children.Remove(WeatherGroup);
                WeatherGroup.Margin = new Thickness(0);
                _mediaBox = new GroupBox { Style = (Style)FindResource("Tray"), Content = WeatherGroup, Margin = new Thickness(2, 0, 0, 0) };
                DockPanel.SetDock(_mediaBox, Dock.Left);

                // The tabs: out of the dock, into a row that is centered in what the other boxes leave. The Start button stays where it
                // was, at the left edge, with the media player's box after it.
                TaskbarDock.Children.Remove(TaskListControl);
                _centerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
                _centerRow.Children.Add(TaskListControl);
                _centerStrip = new Grid();
                _centerStrip.Children.Add(_centerRow);
                _centerStrip.SizeChanged += (s, e) => UpdateCenterWidth();
                _mediaBox.SizeChanged += (s, e) => UpdateCenterWidth();
                StartButton.SizeChanged += (s, e) => UpdateCenterWidth();
                TrayBox.SizeChanged += (s, e) => UpdateCenterWidth();

                TaskbarDock.Children.Insert(TaskbarDock.Children.IndexOf(StartButton) + 1, _mediaBox);
                TaskbarDock.Children.Add(_centerStrip);

                QuickLaunchToolbar.Visibility = Visibility.Collapsed;
                TaskListControl.IconOnly = true;
            }
            else
            {
                TaskListControl.IconOnly = false;

                _centerRow.Children.Remove(TaskListControl);
                TaskbarDock.Children.Remove(_centerStrip);
                TaskbarDock.Children.Remove(_mediaBox);
                _mediaBox.Content = null;
                WeatherGroup.Margin = new Thickness(5, 0, 0, 0);
                TrayContent.Children.Insert(TrayContent.Children.IndexOf(MediaPlayerControl) + 1, WeatherGroup);

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

            double left = StartButton.ActualWidth + (_mediaBox?.ActualWidth ?? 0) + (_mediaBox?.Margin.Left ?? 0);
            double right = TrayBox.ActualWidth;
            double difference = Math.Abs(left - right);
            _centerRow.Margin = left < right ? new Thickness(difference, 0, 0, 0) : new Thickness(0, 0, difference, 0);
            TaskListControl.AvailableWidth = Math.Max(0, _centerStrip.ActualWidth - difference);
        }
    }
}
