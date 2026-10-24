using RetroBar.Utilities;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for ToolbarButton.xaml
    /// </summary>
    public partial class ToolbarButton : UserControl
    {
        // True for a pinned program among the tabs of the Windows 11 style: the button is as big as the control is (the size of a tab's
        // frame, which the task list gives the control), not the size of the per-monitor ToolbarButtonBox tag.
        public static readonly DependencyProperty StretchedProperty = DependencyProperty.Register(
            nameof(Stretched), typeof(bool), typeof(ToolbarButton), new PropertyMetadata(false, (d, e) => ((ToolbarButton)d).ApplyStretched()));

        public bool Stretched
        {
            get => (bool)GetValue(StretchedProperty);
            set => SetValue(StretchedProperty, value);
        }

        private void ApplyStretched()
        {
            if (Stretched)
            {
                MonitorOffset.SetTag(LauncherButton, null);
                LauncherButton.ClearValue(WidthProperty);
                LauncherButton.ClearValue(HeightProperty);
                LauncherButton.ClearValue(MarginProperty);
                LauncherButton.ClearValue(RenderTransformProperty);
                LauncherButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                LauncherButton.VerticalAlignment = VerticalAlignment.Stretch;
                // The button is only as wide as a tab's frame, so no room is spared for the icon's own margin (it was cut at the right).
                ToolbarIcon.Margin = new Thickness(0);
            }
            else
            {
                ToolbarIcon.ClearValue(MarginProperty);
                LauncherButton.ClearValue(HorizontalAlignmentProperty);
                LauncherButton.ClearValue(VerticalAlignmentProperty);
                MonitorOffset.SetTag(LauncherButton, "ToolbarButtonBox");
            }
        }

        public ToolbarButton()
        {
            InitializeComponent();

            setIconBinding();
        }

        private void setIconBinding()
        {
            string bindingPath = "SmallIcon";
            bool useLargeIcons = Settings.Instance.TaskbarScale > 1 || (Application.Current.FindResource("UseLargeIcons") as bool? ?? false);

            if (useLargeIcons)
            {
                bindingPath = "LargeIcon";
            }

            Binding iconBinding = new Binding(bindingPath);
            iconBinding.Mode = BindingMode.OneWay;
            ToolbarIcon.SetBinding(Image.SourceProperty, iconBinding);
        }
    }
}
