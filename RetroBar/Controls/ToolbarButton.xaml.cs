using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

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

        // (Kept for the task list's binding; the place of the icon is worked out here now, see PlaceIcon.)
        public static readonly DependencyProperty IconMarginProperty = DependencyProperty.Register(
            nameof(IconMargin), typeof(Thickness), typeof(ToolbarButton), new PropertyMetadata(new Thickness()));

        public Thickness IconMargin
        {
            get => (Thickness)GetValue(IconMarginProperty);
            set => SetValue(IconMarginProperty, value);
        }

        // Where the middle of the picture of a tab's icon is in the frame of its tab (device pixels from the frame's top left), as the task
        // list reads it from a tab at rest; NaN until it is known. The icon of this pin is put at that place of its own box (which is the size
        // and place a tab's frame has).
        public static readonly DependencyProperty IconTargetProperty = DependencyProperty.Register(
            nameof(IconTarget), typeof(Vector), typeof(ToolbarButton), new PropertyMetadata(new Vector(double.NaN, double.NaN), (d, e) => ((ToolbarButton)d).PlaceIcon()));

        public Vector IconTarget
        {
            get => (Vector)GetValue(IconTargetProperty);
            set => SetValue(IconTargetProperty, value);
        }

        // The icon is an image of the size a tab's icon has (whole device pixels), at the top left of the button, and put where it goes by
        // its margin in whole device pixels, which layout rounding draws exactly there: no transform is put on it that is worked out again
        // when the layout changes, which moved it by a pixel for a moment (the size the task list gives the button changes just after it is
        // made, the tabs slide, a window opens or closes). It is the same when the button is pressed: the theme moves what is in it by a DIP,
        // which rounds to a pixel down and to the right.
        private void PlaceIcon()
        {
            if (!Stretched || !IsLoaded || ToolbarIcon.Source == null || double.IsNaN(IconTarget.X))
            {
                return;
            }

            try
            {
                var box = LauncherButton.Template?.FindName("ButtonRightBottomBorder", LauncherButton) as FrameworkElement;
                if (box == null || box.ActualWidth <= 0 || !ToolbarIcon.IsVisible || PresentationSource.FromVisual(this) == null)
                {
                    return;
                }

                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double step = 1 / (dpi > 0 ? dpi : 1);

                // Drawn by the monitor's TaskIconBar tag, as the icon of a tab is (its size, scale and the pixels it is drawn on: the same
                // icon at the same pixels whether the program is open or not, on every monitor); the tag clears the scaling it was given,
                // and the icon of a pin has always had the theme's HighQuality, which is softer.
                if (ToolbarIcon.Width != 16 || ToolbarIcon.Height != 16)
                {
                    ToolbarIcon.Width = 16;
                    ToolbarIcon.Height = 16;
                }

                if (RenderOptions.GetBitmapScalingMode(ToolbarIcon) != BitmapScalingMode.HighQuality)
                {
                    RenderOptions.SetBitmapScalingMode(ToolbarIcon, BitmapScalingMode.HighQuality);
                }

                // Where the icon is in the button for the margin it has, to take that margin off for what the button gives it.
                Point boxTopLeft = box.PointToScreen(new Point(0, 0));
                Point iconTopLeft = ToolbarIcon.PointToScreen(new Point(0, 0));
                Point iconBottomRight = ToolbarIcon.PointToScreen(new Point(ToolbarIcon.ActualWidth, ToolbarIcon.ActualHeight));
                double sidePixels = iconBottomRight.X - iconTopLeft.X;
                Thickness margin = ToolbarIcon.Margin;
                double baseX = iconTopLeft.X - boxTopLeft.X - Math.Round(margin.Left * dpi);
                double baseY = iconTopLeft.Y - boxTopLeft.Y - Math.Round(margin.Top * dpi);

                // The middle of the picture, not of the image, goes to the place the tab's icon has it.
                (double artX, double artY) = TaskButton.ArtCenterOffset(ToolbarIcon.Source, sidePixels, sidePixels);
                // The theme moves what is in a pressed button down and to the right by a DIP (the 3D effect): the place is taken from where the
                // icon is now, which has that move in it, so it was taken off again by the margin and the icon did not move; it is a pixel
                // down and to the right of where it is at rest instead.
                int press = LauncherButton.IsPressed ? 1 : 0;

                // (A half goes to the right and down, as it does for a tab: see TaskButton.WholePixels.)
                double wantX = TaskButton.WholePixels(IconTarget.X + press - sidePixels / 2 - artX - baseX);
                double wantY = TaskButton.WholePixels(IconTarget.Y + press - sidePixels / 2 - artY - baseY);
                var wanted = new Thickness(wantX * step, wantY * step, 0, 0);
                if (Math.Abs(margin.Left - wanted.Left) > step / 4 || Math.Abs(margin.Top - wanted.Top) > step / 4)
                {
                    ToolbarIcon.Margin = wanted;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        // The icon of the program the pin starts, the one its tab shows when it is open (not the small icon of the link, which is another
        // picture, with its own empty space at the sides, and was a pixel or two off its tab's icon when the program opened or closed).
        private void ApplyProgramIcon()
        {
            if (!Stretched)
            {
                return;
            }

            ImageSource program = null;
            try
            {
                if (DataContext is ManagedShell.ShellFolders.ShellFile file && !string.IsNullOrEmpty(file.Path))
                {
                    string exe = PinnedApps.TargetPath(file.Path);
                    if (!string.IsNullOrEmpty(exe) && !exe.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
                    {
                        program = TaskButton.ProgramIconOf(exe, null);
                    }
                }
            }
            catch (Exception)
            {
            }

            if (program != null)
            {
                BindingOperations.ClearBinding(ToolbarIcon, Image.SourceProperty);
                ToolbarIcon.Source = program;
            }
            else
            {
                setIconBinding("LargeIcon");
            }

            PlaceIcon();
        }

        private Canvas _iconCanvas;

        private void ApplyStretched()
        {
            if (Stretched)
            {
                MonitorOffset.SetTag(LauncherButton, null);
                LauncherButton.ClearValue(WidthProperty);
                LauncherButton.ClearValue(HeightProperty);
                LauncherButton.Margin = new Thickness(0);
                LauncherButton.ClearValue(RenderTransformProperty);
                LauncherButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                LauncherButton.VerticalAlignment = VerticalAlignment.Stretch;

                // In a canvas, which has no room of its own to cut its child to: the icon is made 16 DIP and shrunk by a transform, and in the
                // room the button gives it (smaller than that, with its margin) the layout cut it at the slot, a pixel or two of its bottom and right.
                if (!ReferenceEquals(LauncherButton.Content, _iconCanvas))
                {
                    LauncherButton.Content = null;
                    _iconCanvas ??= new Canvas();
                    _iconCanvas.Children.Clear();
                    _iconCanvas.Children.Add(ToolbarIcon);
                    LauncherButton.Content = _iconCanvas;
                }

                // The tag of the icons of the tabs (see PlaceIcon); the place of the icon is its margin, in whole pixels.
                MonitorOffset.SetTag(ToolbarIcon, "TaskIconBar");
                ToolbarIcon.HorizontalAlignment = HorizontalAlignment.Left;
                ToolbarIcon.VerticalAlignment = VerticalAlignment.Top;
                ToolbarIcon.Margin = new Thickness(0);
                // The shell's large icon, drawn the way the icon of a tab is (its scaling is the default one, not the theme's HighQuality, which
                // was softer than the tab's: the icon looked different when its program opened).
                RenderOptions.SetBitmapScalingMode(ToolbarIcon, BitmapScalingMode.HighQuality);
                ToolbarIcon.UseLayoutRounding = true;
                ToolbarIcon.SnapsToDevicePixels = true;
                ApplyProgramIcon();
            }
            else
            {
                if (_iconCanvas != null && ReferenceEquals(LauncherButton.Content, _iconCanvas))
                {
                    LauncherButton.Content = null;
                    _iconCanvas.Children.Clear();
                    LauncherButton.Content = ToolbarIcon;
                }

                ToolbarIcon.ClearValue(RenderTransformProperty);
                ToolbarIcon.ClearValue(RenderTransformOriginProperty);
                MonitorOffset.SetTag(ToolbarIcon, "ToolbarIcon");
                ToolbarIcon.ClearValue(HorizontalAlignmentProperty);
                ToolbarIcon.ClearValue(VerticalAlignmentProperty);
                ToolbarIcon.ClearValue(WidthProperty);
                ToolbarIcon.ClearValue(HeightProperty);
                ToolbarIcon.ClearValue(RenderOptions.BitmapScalingModeProperty);
                ToolbarIcon.ClearValue(UseLayoutRoundingProperty);
                ToolbarIcon.ClearValue(SnapsToDevicePixelsProperty);
                setIconBinding();
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
            DataContextChanged += (s, e) => ApplyProgramIcon();

            // After every layout: cheap (it only sets a margin when the place has changed), and a margin set here is laid out before the
            // frame is drawn.
            DependencyPropertyDescriptor pressedDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, typeof(Button));
            Loaded += (s, e) =>
            {
                LayoutUpdated += ToolbarButton_OnLayoutUpdated;
                pressedDescriptor.AddValueChanged(LauncherButton, ToolbarButton_OnPressedChanged);
                PlaceIcon();
            };
            Unloaded += (s, e) =>
            {
                LayoutUpdated -= ToolbarButton_OnLayoutUpdated;
                pressedDescriptor.RemoveValueChanged(LauncherButton, ToolbarButton_OnPressedChanged);
            };
        }

        private void ToolbarButton_OnLayoutUpdated(object sender, EventArgs e) => PlaceIcon();

        private void ToolbarButton_OnPressedChanged(object sender, EventArgs e) => PlaceIcon();

        private void setIconBinding(string forcedPath = null)
        {
            string bindingPath = "SmallIcon";
            bool useLargeIcons = Settings.Instance.TaskbarScale > 1 || (Application.Current.FindResource("UseLargeIcons") as bool? ?? false);

            if (useLargeIcons || forcedPath != null)
            {
                bindingPath = forcedPath ?? "LargeIcon";
            }

            Binding iconBinding = new Binding(bindingPath);
            iconBinding.Mode = BindingMode.OneWay;
            ToolbarIcon.SetBinding(Image.SourceProperty, iconBinding);
        }
    }
}
