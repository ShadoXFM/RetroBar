using RetroBar.Utilities;
using System;
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
                MonitorOffsetValue adjust = MonitorAdjustments.Get(MonitorOffset.FindDeviceName(this), "TaskIconBar");
                // Scaled the way the monitor's tag scales the icon of a tab: to a whole number of DIP (16 * 0.9 = 14.4 is 14), not of pixels.
                double scaleOfTab = Math.Max(1, Math.Round(16 * (adjust.Scale ?? 1))) / 16;
                double sidePixels = 16 * dpi * scaleOfTab;
                // Made at the size of the icon of a tab at 100% scale (16 DIP) and scaled down by a fixed transform: the way a tab's icon is
                // made, and crisp (made straight to its size, it was soft). The transform is never worked out again: only this size and the
                // margin decide where it is.
                double nativePixels = 16 * dpi;
                if (ToolbarIcon.Width != 16 || ToolbarIcon.Height != 16)
                {
                    ToolbarIcon.Width = 16;
                    ToolbarIcon.Height = 16;
                    ToolbarIcon.RenderTransformOrigin = new Point(0.5, 0.5);
                }

                // The scaled image is in the middle of its 16 DIP, which is a fraction of a pixel off the pixels when its size is not whole in pixels:
                // moved by the difference (as the tag does), so its top left is on a pixel.
                double offsetPixels = (nativePixels - sidePixels) / 2;
                double fix = (Math.Floor(offsetPixels + 0.5 + 1e-6) - offsetPixels) * step;
                if (ToolbarIcon.RenderTransform is not TransformGroup current || Math.Abs(((ScaleTransform)current.Children[0]).ScaleX - scaleOfTab) > 0.0001 ||
                    Math.Abs(((TranslateTransform)current.Children[1]).X - fix) > 0.0001)
                {
                    var group = new TransformGroup();
                    group.Children.Add(new ScaleTransform(scaleOfTab, scaleOfTab));
                    group.Children.Add(new TranslateTransform(fix, fix));
                    ToolbarIcon.RenderTransform = group;
                }

                // Where the icon is in the button for the margin it has, to take that margin off for what the button gives it.
                Point boxTopLeft = box.PointToScreen(new Point(0, 0));
                Point iconTopLeft = ToolbarIcon.PointToScreen(new Point(0, 0));
                Thickness margin = ToolbarIcon.Margin;
                double baseX = iconTopLeft.X - boxTopLeft.X - Math.Round(margin.Left * dpi);
                double baseY = iconTopLeft.Y - boxTopLeft.Y - Math.Round(margin.Top * dpi);

                // The middle of the picture, not of the image, goes to the place the tab's icon has it.
                (double artX, double artY) = TaskButton.ArtCenterOffset(ToolbarIcon.Source, sidePixels, sidePixels);
                // (A half goes to the right and down, as it does for a tab: see TaskButton.WholePixels.)
                double wantX = TaskButton.WholePixels(IconTarget.X - sidePixels / 2 - artX - baseX);
                double wantY = TaskButton.WholePixels(IconTarget.Y - sidePixels / 2 - artY - baseY);
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

                // No per-monitor transform on the icon (its size and place are worked out in whole pixels, see PlaceIcon).
                MonitorOffset.SetTag(ToolbarIcon, null);
                ToolbarIcon.ClearValue(RenderTransformProperty);
                ToolbarIcon.HorizontalAlignment = HorizontalAlignment.Left;
                ToolbarIcon.VerticalAlignment = VerticalAlignment.Top;
                ToolbarIcon.Margin = new Thickness(0);
                // The shell's large icon, drawn the way the icon of a tab is (its scaling is the default one, not the theme's HighQuality, which
                // was softer than the tab's: the icon looked different when its program opened).
                RenderOptions.SetBitmapScalingMode(ToolbarIcon, BitmapScalingMode.Unspecified);
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
            Loaded += (s, e) =>
            {
                LayoutUpdated += ToolbarButton_OnLayoutUpdated;
                PlaceIcon();
            };
            Unloaded += (s, e) => LayoutUpdated -= ToolbarButton_OnLayoutUpdated;
        }

        private void ToolbarButton_OnLayoutUpdated(object sender, EventArgs e) => PlaceIcon();

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
