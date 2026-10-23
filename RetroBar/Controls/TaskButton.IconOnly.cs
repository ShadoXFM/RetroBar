using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Controls
{
    // In the Windows 11 style of the taskbar (TaskList.IconOnly) a tab shows only its icon, in the middle of it.
    public partial class TaskButton
    {
        private TaskList _iconOnlyHost;
        private DependencyPropertyDescriptor _iconOnlyDescriptor;

        private void InitIconOnly()
        {
            ReleaseIconOnly();
            _iconOnlyHost = Host;
            if (_iconOnlyHost != null)
            {
                _iconOnlyDescriptor = DependencyPropertyDescriptor.FromProperty(TaskList.IconOnlyProperty, typeof(TaskList));
                _iconOnlyDescriptor.AddValueChanged(_iconOnlyHost, IconOnly_OnChanged);
            }

            ApplyIconOnly();
        }

        private void ReleaseIconOnly()
        {
            if (_iconOnlyHost != null)
            {
                _iconOnlyDescriptor?.RemoveValueChanged(_iconOnlyHost, IconOnly_OnChanged);
                _iconOnlyHost = null;
            }
        }

        private void IconOnly_OnChanged(object sender, EventArgs e) => ApplyIconOnly();

        private void ApplyIconOnly()
        {
            bool only = _iconOnlyHost?.IconOnly == true;
            TaskLabelText.Visibility = only ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(TaskIconImage, only ? 2 : 1);

            if (only)
            {
                // Centered: the theme's margin leaves room for a label after the icon.
                Thickness margin = TaskIconImage.Margin;
                TaskIconImage.HorizontalAlignment = HorizontalAlignment.Center;
                TaskIconImage.Margin = new Thickness(0, margin.Top, 0, margin.Bottom);
            }
            else
            {
                TaskIconImage.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
                TaskIconImage.ClearValue(FrameworkElement.MarginProperty);
                MonitorOffset.Refresh(TaskIconImage);
            }
        }
    }
}
