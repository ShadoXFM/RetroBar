using RetroBar.Utilities;
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
                // The media player: out of the tray box, into a box of its own at the left.
                TrayContent.Children.Remove(MediaPlayerControl);
                _mediaBox = new GroupBox { Style = (Style)FindResource("Tray"), Content = MediaPlayerControl };
                DockPanel.SetDock(_mediaBox, Dock.Left);

                // The tabs: out of the dock, into a row that is centered in what the other boxes leave. The Start button stays where it
                // was, at the left edge, with the media player's box after it.
                TaskbarDock.Children.Remove(TaskListControl);
                _centerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
                _centerRow.Children.Add(TaskListControl);
                _centerStrip = new Grid();
                _centerStrip.Children.Add(_centerRow);
                _centerStrip.SizeChanged += (s, e) => UpdateCenterWidth();

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
                TrayContent.Children.Insert(0, MediaPlayerControl);

                TaskbarDock.Children.Add(TaskListControl);
                _centerStrip = null;
                _centerRow = null;
                _mediaBox = null;

                QuickLaunchToolbar.Visibility = Settings.Instance.ShowQuickLaunch ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // The room the tabs have: what is left of the middle.
        private void UpdateCenterWidth()
        {
            if (_centerStrip != null)
            {
                TaskListControl.AvailableWidth = System.Math.Max(0, _centerStrip.ActualWidth);
            }
        }
    }
}
