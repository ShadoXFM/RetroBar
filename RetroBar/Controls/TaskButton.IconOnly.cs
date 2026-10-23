using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

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
