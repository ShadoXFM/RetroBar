using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Interop;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskThumbnail.xaml
    /// </summary>
    public partial class TaskThumbnail : UserControl
    {
        const double DEFAULT_WIDTH = 180;

        // The thumbnail area is a bit wider than the tab it belongs to.
        const double SIZE_FACTOR = 1.2;

        // Width of the preview's thumbnail area, in DIPs - bound to the tab's own width by the
        // preview template (TaskButton.xaml), so every preview is as wide as its tab. The height
        // follows the window's own shape (within limits, below), so the thumbnail fills the area with
        // the same padding around it on every preview rather than being letterboxed.
        public static DependencyProperty BoxWidthProperty = DependencyProperty.Register(nameof(BoxWidth), typeof(double), typeof(TaskThumbnail),
            new PropertyMetadata(DEFAULT_WIDTH, (d, e) => ((TaskThumbnail)d).Refresh()));

        public double BoxWidth
        {
            get { return (double)GetValue(BoxWidthProperty); }
            set { SetValue(BoxWidthProperty, value); }
        }

        // Like the Windows taskbar, previews live in a box of limited size: the height follows the
        // window's shape only up to MAX_HEIGHT_RATIO of the width (about 4:3), and a taller window gets
        // that same box with its picture fitted inside and centered (bars at the sides) instead of a
        // preview that keeps growing taller. Very wide windows are floored at MIN_HEIGHT_RATIO so they
        // don't become a sliver.
        private const double MIN_HEIGHT_RATIO = 0.35;
        private const double MAX_HEIGHT_RATIO = 0.75;

        // Height of the thumbnail area for the window currently being shown (set by Refresh).
        private double _boxHeight = Math.Round(DEFAULT_WIDTH * 2.0 / 3.0);
        private double BoxHeight => _boxHeight;

        // The area's width in DIPs, rounded to a whole number of device pixels so its edges (and the
        // frame around it) land cleanly on the pixel grid at any monitor scale.
        private double AreaWidth => Math.Round(BoxWidth * SIZE_FACTOR * DpiScale) / DpiScale;

        public double DpiScale = 1.0;

        private DispatcherTimer _toolTipTimer;
        private EventHandler _renderingHandler;

        public TaskThumbnail()
        {
            InitializeComponent();

            // Never show the title tooltip declared in the XAML - see UserControl_Loaded.
            ToolTip = null;

            _toolTipTimer = new DispatcherTimer();
            _toolTipTimer.Tick += ToolTipTimer_Tick;
            _toolTipTimer.Interval = new TimeSpan(0, 0, 0, 0, ToolTipService.GetInitialShowDelay(this));
        }

        public IntPtr Handle
        {
            get
            {
                HwndSource source = (HwndSource)PresentationSource.FromVisual(this);

                if (source == null)
                {
                    return IntPtr.Zero;
                }

                IntPtr handle = source.Handle;
                return handle;
            }
        }

        private IntPtr _thumbHandle;

        public static DependencyProperty SourceWindowHandleProperty = DependencyProperty.Register(nameof(SourceWindowHandle), typeof(IntPtr), typeof(TaskThumbnail), new PropertyMetadata(new IntPtr()));

        public IntPtr SourceWindowHandle
        {
            get
            {
                return (IntPtr)GetValue(SourceWindowHandleProperty);
            }
            set
            {
                SetValue(SourceWindowHandleProperty, value);
            }
        }

        // The app's icon, shown in place of the live picture when there isn't one (see Refresh).
        public static DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(ImageSource), typeof(TaskThumbnail),
            new PropertyMetadata(null, (d, e) => ((TaskThumbnail)d).BlankIcon.Source = e.NewValue as ImageSource));

        public ImageSource Icon
        {
            get { return (ImageSource)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        // A minimized window has no live picture for DWM to show (the thumbnail comes out empty), nor
        // does one that reports no size. A blank preview is no use, so those show the app's icon instead.
        private const double BLANK_HEIGHT_RATIO = 0.6;

        public static DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(TaskThumbnail), new PropertyMetadata(""));

        public string Title
        {
            get
            {
                return (string)GetValue(TitleProperty);
            }
            set
            {
                SetValue(TitleProperty, value);
            }
        }

        public NativeMethods.Rect Rect
        {
            get
            {
                try
                {
                    if (this == null)
                        return new NativeMethods.Rect(0, 0, 0, 0);

                    // Relative to the popup window's root (DWM wants window-client coordinates), not to
                    // this control's parent: the preview can have other content (a title row) above the
                    // thumbnail, so the parent isn't at the window's origin.
                    var generalTransform = TransformToAncestor(
                        (PresentationSource.FromVisual(this)?.RootVisual as System.Windows.Media.Visual) ?? (System.Windows.Media.Visual)Parent);
                    var leftTopPoint = generalTransform.Transform(new Point(0, 0));
                    // Rounded, not truncated: the position is a whole number of pixels in exact arithmetic
                    // (borders, margins and padding are snapped to the pixel grid), so a tiny floating
                    // point shortfall like 10.9999 must land on 11, not 10 - which shifted the picture
                    // a pixel off-center within its frame.
                    int left = (int)Math.Round(leftTopPoint.X * DpiScale);
                    int top = (int)Math.Round(leftTopPoint.Y * DpiScale);
                    return new NativeMethods.Rect(
                          left,
                          top,
                          left + (int)Math.Round(AreaWidth * DpiScale),
                          top + (int)Math.Round(BoxHeight * DpiScale)
                         );
                }
                catch
                {
                    return new NativeMethods.Rect(0, 0, 0, 0);
                }
            }
        }

        // The thumbnail is drawn by DWM, not WPF, so fading the preview popup (its ToolTip's Opacity -
        // see TaskButton.Preview.cs) doesn't fade it by itself; Refresh runs every frame and passes the
        // popup's current opacity on to DWM so the thumbnail fades together with the frame around it.
        private byte GetPopupOpacity()
        {
            DependencyObject current = this;
            while (current != null)
            {
                if (current is ToolTip tip)
                {
                    return (byte)Math.Round(255 * Math.Max(0, Math.Min(1, tip.Opacity)));
                }

                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return 255;
        }

        public void Refresh()
        {
            if (this == null)
                return;

            if (_thumbHandle == IntPtr.Zero)
                return;

            if (this != null)
            {
                var clientAreaProps = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                    fSourceClientAreaOnly = true
                };
                NativeMethods.DwmUpdateThumbnailProperties(_thumbHandle, ref clientAreaProps);

                NativeMethods.DwmQueryThumbnailSourceSize(_thumbHandle, out NativeMethods.PSIZE size);

                bool blank = size.x <= 0 || size.y <= 0 || IsIconic(SourceWindowHandle);
                BlankPlaceholder.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;

                var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
                {
                    fVisible = !blank,
                    dwFlags = NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_OPACITY,
                    rcDestination = Rect,
                    opacity = GetPopupOpacity()
                };

                // Width is the tab's (scaled up a little); height follows the window's shape (clamped)...
                double areaWidth = AreaWidth;
                if (blank)
                {
                    _boxHeight = Math.Round(areaWidth * BLANK_HEIGHT_RATIO * DpiScale) / DpiScale;
                }
                else if (size.x > 0 && size.y > 0)
                {
                    double ratio = Math.Min(MAX_HEIGHT_RATIO, Math.Max(MIN_HEIGHT_RATIO, (double)size.y / size.x));
                    _boxHeight = Math.Round(areaWidth * ratio * DpiScale) / DpiScale;
                }

                Width = areaWidth;
                Height = BoxHeight;

                if (!blank && size.x > 0 && size.y > 0)
                {
                    // ...and the picture is scaled to fill it - to the full width for a normal window,
                    // or fitted inside if the height was clamped - centered either way.
                    double boxWidthPx = areaWidth * DpiScale;
                    double boxHeightPx = BoxHeight * DpiScale;
                    double scale = Math.Min(boxWidthPx / size.x, boxHeightPx / size.y);
                    int boxWidth = (int)Math.Round(boxWidthPx);
                    int boxHeight = (int)Math.Round(boxHeightPx);
                    int width = Math.Max(1, (int)Math.Round(size.x * scale));
                    int height = Math.Max(1, (int)Math.Round(size.y * scale));

                    // The picture fills the area on whichever axis limited its size: truncating the scaled
                    // size left it a pixel short, which made the padding uneven (one pixel more on the
                    // right or bottom than the left or top).
                    if (Math.Abs(width - boxWidth) <= 1)
                    {
                        width = boxWidth;
                    }

                    if (Math.Abs(height - boxHeight) <= 1)
                    {
                        height = boxHeight;
                    }

                    int left = props.rcDestination.Left + (int)Math.Round((boxWidth - width) / 2.0);
                    int top = props.rcDestination.Top + (int)Math.Round((boxHeight - height) / 2.0);

                    props.rcDestination = new NativeMethods.Rect(left, top, left + width, top + height);
                }

                if (this != null)
                    NativeMethods.DwmUpdateThumbnailProperties(_thumbHandle, ref props);
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_renderingHandler != null)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                _renderingHandler = null;
            }

            if (_thumbHandle != IntPtr.Zero)
            {
                NativeMethods.DwmUnregisterThumbnail(_thumbHandle);
                _thumbHandle = IntPtr.Zero;
            }

            _toolTipTimer.Stop();
            if (ToolTip is ToolTip tip)
            {
                tip.IsOpen = false;
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            DpiScale = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.M11;

            // No NativeMethods.DwmIsCompositionEnabled() pre-check: composition has been permanently on
            // since Windows 8, and that call was observed to (incorrectly) report false on a plain
            // local Windows 11 session, which silently disabled every thumbnail. DwmRegisterThumbnail
            // itself fails (non-zero) if composition genuinely isn't available, and that is checked.
            if (SourceWindowHandle != IntPtr.Zero && Handle != IntPtr.Zero && NativeMethods.DwmRegisterThumbnail(Handle, SourceWindowHandle, out _thumbHandle) == 0)
            {
                Refresh();
                // once loaded, we need to refresh the thumbnail...
                _renderingHandler = (s, a) => Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Refresh));
                CompositionTarget.Rendering += _renderingHandler;
            }

            // (No title tooltip any more: the preview has its own title row, and the extra tooltip faded
            // in a second after the preview itself, on top of it.)
        }

        private void ToolTipTimer_Tick(object sender, EventArgs e)
        {
            if (ToolTip is ToolTip tip)
            {
                tip.PlacementTarget = this;
                tip.IsOpen = true;
            }
        }
    }
}