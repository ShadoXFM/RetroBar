using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

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
            WatchIconCentering(_iconOnlyHost?.IconOnly == true);
        }

        // The theme draws a pressed tab, an active one and a resting one with frames that are not the same size and place (and moves the
        // content of a pressed one), so the icon is not in the middle of all three: after each change the icon is measured against the
        // frame that is drawn, and moved by the difference.
        private DependencyPropertyDescriptor _pressedDescriptor;
        private DependencyPropertyDescriptor _styleDescriptor;
        private bool _centeringQueued;

        private void WatchIconCentering(bool watch)
        {
            _pressedDescriptor ??= DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, typeof(Button));
            _styleDescriptor ??= DependencyPropertyDescriptor.FromProperty(FrameworkElement.StyleProperty, typeof(Button));
            _pressedDescriptor.RemoveValueChanged(AppButton, IconCentering_OnChanged);
            _styleDescriptor.RemoveValueChanged(AppButton, IconCentering_OnChanged);
            if (watch)
            {
                _pressedDescriptor.AddValueChanged(AppButton, IconCentering_OnChanged);
                _styleDescriptor.AddValueChanged(AppButton, IconCentering_OnChanged);
            }
        }

        private void IconCentering_OnChanged(object sender, EventArgs e)
        {
            if (_centeringQueued)
            {
                return;
            }

            _centeringQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _centeringQueued = false;
                CenterIcon();
            }), DispatcherPriority.Loaded);
        }

        private void CenterIcon()
        {
            if (_iconOnlyHost?.IconOnly != true || !IsLoaded)
            {
                return;
            }

            try
            {
                TaskIconImage.Margin = new Thickness(0);
                AppButton.UpdateLayout();

                var frame = AppButton.Template?.FindName("ButtonOuterBottomBorder", AppButton) as FrameworkElement;
                if (frame == null || frame.ActualWidth <= 0 || TaskIconImage.ActualWidth <= 0)
                {
                    return;
                }

                Rect icon = TaskIconImage.TransformToAncestor(frame).TransformBounds(new Rect(0, 0, TaskIconImage.ActualWidth, TaskIconImage.ActualHeight));
                double dx = frame.ActualWidth / 2 - (icon.Left + icon.Width / 2);
                double dy = frame.ActualHeight / 2 - (icon.Top + icon.Height / 2);

                double step = _iconOnlyHost.PixelStep > 0 ? _iconOnlyHost.PixelStep : 1;
                double left = Math.Round(Math.Max(0, 2 * dx) / step) * step;
                double right = Math.Round(Math.Max(0, -2 * dx) / step) * step;
                double top = Math.Round(Math.Max(0, 2 * dy) / step) * step;
                double bottom = Math.Round(Math.Max(0, -2 * dy) / step) * step;
                TaskIconImage.Margin = new Thickness(left, top, right, bottom);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ReleaseIconOnly()
        {
            WatchIconCentering(false);
            if (_iconOnlyHost != null)
            {
                _iconOnlyDescriptor?.RemoveValueChanged(_iconOnlyHost, IconOnly_OnChanged);
                _iconOnlyHost = null;
            }
        }

        private void IconOnly_OnChanged(object sender, EventArgs e)
        {
            ApplyIconOnly();
            WatchIconCentering(_iconOnlyHost?.IconOnly == true);
            IconCentering_OnChanged(this, EventArgs.Empty);
        }

        // The binding of the icon (the window's own, whatever its title bar shows: a folder, a page), put back when the tab is a classic one.
        private BindingBase _windowIconBinding;

        // The program's icon: its executable's (File Explorer's own, as the user may have set it), not the window's, which is the title
        // bar's and changes with what the window shows. Null for a window that has no executable to ask (a packaged app).
        private ImageSource ProgramIconForTab()
        {
            if (Window == null || Window.IsUWP || string.IsNullOrEmpty(Window.WinFileName))
            {
                return null;
            }

            if (Converters.TaskIconConverter.IsFileExplorerWindow(Window))
            {
                var converter = (IMultiValueConverter)FindResource("taskIconConverter");
                return converter.Convert(new object[] { Window, Window.Icon, 2 }, typeof(ImageSource), null, System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
            }

            return GetProgramIcon();
        }

        private void ApplyIconOnly()
        {
            bool only = _iconOnlyHost?.IconOnly == true;
            TaskLabelText.Visibility = only ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumnSpan(TaskIconImage, only ? 2 : 1);

            if (only)
            {
                // Centered, both ways, with no margin: the theme's margin leaves room for a label after the icon and is tuned, with the
                // per-monitor nudges of "TaskIcon", for an icon at the left of the tab. The icon has tags of its own (TaskIconOnly), whose
                // position is put on whole device pixels.
                TaskIconImage.HorizontalAlignment = HorizontalAlignment.Center;
                TaskIconImage.VerticalAlignment = VerticalAlignment.Center;
                TaskIconImage.Margin = new Thickness(0);
                SetMonitorTag(TaskIconImage, "TaskIconOnly");
                IconCentering_OnChanged(this, EventArgs.Empty);

                _windowIconBinding ??= BindingOperations.GetBindingBase(TaskIconImage, Image.SourceProperty);
                ImageSource program = ProgramIconForTab();
                if (program != null)
                {
                    BindingOperations.ClearBinding(TaskIconImage, Image.SourceProperty);
                    TaskIconImage.Source = program;
                }
            }
            else
            {
                TaskIconImage.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
                TaskIconImage.ClearValue(FrameworkElement.VerticalAlignmentProperty);
                TaskIconImage.ClearValue(FrameworkElement.MarginProperty);
                SetMonitorTag(TaskIconImage, "TaskIcon");

                if (_windowIconBinding != null)
                {
                    TaskIconImage.SetBinding(Image.SourceProperty, _windowIconBinding);
                }
            }
        }
    }
}
