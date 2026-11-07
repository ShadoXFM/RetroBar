using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RetroBar.Controls
{
    /// <summary>
    /// The shadow behind a popup's frame - see PopupShadow.xaml. Put it first in the grid that holds the frame, filling
    /// the popup's whole window: <see cref="Room"/> is the margin the window has around the frame for the shadow to spread
    /// into, and <see cref="Strength"/> how dark it is.
    /// </summary>
    public partial class PopupShadow : UserControl
    {
        // How far inside the frame's top-left the shadow's rectangle starts (DIPs), and the clip, which is this much past
        // the frame's own edge so that none of the frame's edge pixels is covered by the black of the rectangle.
        private const double Inset = 3;
        private const double ClipOverlap = 1;

        // How far the rectangle's right and bottom are kept inside the frame's (DIPs): at a fractional scale (125%) the
        // offscreen bitmap the shadow effect draws the rectangle into is rounded outward, and without this its opaque black
        // stuck out a pixel or two past the frame's outline there (a hard black band; the shadow itself is soft).
        private const double Tuck = 2;

        public static readonly DependencyProperty RoomProperty = DependencyProperty.Register(
            nameof(Room), typeof(Thickness), typeof(PopupShadow),
            new PropertyMetadata(new Thickness(10), (d, e) => ((PopupShadow)d).Apply()));

        public static readonly DependencyProperty AllSidesProperty = DependencyProperty.Register(
            nameof(AllSides), typeof(bool), typeof(PopupShadow),
            new PropertyMetadata(false, (d, e) => ((PopupShadow)d).Apply()));

        public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
            nameof(Strength), typeof(double), typeof(PopupShadow),
            new PropertyMetadata(0.75, (d, e) => ((PopupShadow)d).Apply()));

        /// <summary>
        /// The room (DIPs) between the frame and the edge of this control on each side, for the shadow to spread into. A
        /// popup whose window has the same room on every side keeps the default of 10; a menu, whose frame is placed by the
        /// control itself, passes only the right and bottom (and gives the control a negative margin of the same size).
        /// </summary>
        public Thickness Room
        {
            get => (Thickness)GetValue(RoomProperty);
            set => SetValue(RoomProperty, value);
        }

        /// <summary>
        /// The shadow also spreads out above and to the left of the frame (a glow all round it, as Windows 7's thumbnails have), not only
        /// down and to the right. The room round the frame has to be there for it (the default is).
        /// </summary>
        public bool AllSides
        {
            get => (bool)GetValue(AllSidesProperty);
            set => SetValue(AllSidesProperty, value);
        }

        /// <summary>The shadow's opacity.</summary>
        public double Strength
        {
            get => (double)GetValue(StrengthProperty);
            set => SetValue(StrengthProperty, value);
        }

        public PopupShadow()
        {
            InitializeComponent();
            Apply();

            // The popup's window is made when it opens (again each time): the blur is asked for in it then.
            Loaded += (s, e) =>
            {
                PopupBlur.Update(this, Room);
                // Every frame while it is open: a popup that slides in moves its frame inside its window.
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
            };
            Unloaded += (s, e) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                PopupBlur.Remove(this);
            };
            PresentationSource.AddSourceChangedHandler(this, (s, e) => PopupBlur.Update(this, Room));
        }

        private Rect _frameSeen;

        private void OnRendering(object sender, System.EventArgs e)
        {
            PopupBlur.Update(this, Room);

            if (FrameRect() is Rect frame && frame != _frameSeen)
            {
                _frameSeen = frame;
                Apply();
            }
        }

        private Rect? FrameRect()
        {
            FrameworkElement frame = PopupBlur.FrameOf(this);
            if (frame == null || frame.ActualWidth <= 0 || frame.ActualHeight <= 0)
            {
                return null;
            }

            try
            {
                return frame.TransformToVisual(this).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private void Apply()
        {
            // (Called by the property callbacks while the constructor is still running, before the template exists.)
            if (ShadowClip == null || ShadowShape == null)
            {
                return;
            }

            // The room round the frame on whole device pixels (at 125% it is 12.5 of them: the frame is laid out on 13, and a shadow rectangle and
            // a cut-out that stayed on 12.5 left a row half black above the frame).
            DpiScale dpiScale = VisualTreeHelper.GetDpi(this);
            // Where the frame really is in this control, not where it is at rest in a window with all its room (a popup at the edge of the
            // screen has less of it on that side, and the shadow was cast from inside the frame there, with only its outline showing).
            Thickness roomDips = Room;
            if (FrameRect() is Rect seen && ActualWidth > 0 && ActualHeight > 0)
            {
                roomDips = new Thickness(seen.Left, seen.Top, System.Math.Max(0, ActualWidth - seen.Right), System.Math.Max(0, ActualHeight - seen.Bottom));
            }

            Thickness room = new Thickness(
                Snap(roomDips.Left, dpiScale.DpiScaleX), Snap(roomDips.Top, dpiScale.DpiScaleY),
                Snap(roomDips.Right, dpiScale.DpiScaleX), Snap(roomDips.Bottom, dpiScale.DpiScaleY));
            // A theme can ask for the shadow to fall all round the popups (PopupShadowAllSides) and for it to be stronger (PopupShadowBoost:
            // the strength of each popup is multiplied by it, up to 1), for the glass frames, whose shadow is all there is to set them off.
            bool allSides = AllSides || TryFindResource("PopupShadowAllSides") is true;
            double boost = TryFindResource("PopupShadowBoost") is double b ? b : 1;
            if (allSides)
            {
                ShadowClip.Margin = new Thickness(0);
                // (Flush with the frame, not past it: the rectangle itself is solid black, and a pixel of it outside the frame showed as a black
                // line along the top and left.)
                ShadowShape.Margin = new Thickness(room.Left + Tuck, room.Top + Tuck, room.Right + Tuck, room.Bottom + Tuck);
            }
            else
            {
                ShadowClip.Margin = new Thickness(room.Left + ClipOverlap, room.Top + ClipOverlap, 0, 0);
                ShadowShape.Margin = new Thickness(Inset, Inset, room.Right + Tuck, room.Bottom + Tuck);
            }

            if (ShadowShape.Effect is DropShadowEffect effect)
            {
                effect.Opacity = System.Math.Min(1, Strength * boost);
                // All round, it is not thrown as far down and to the right (so it reaches as much on the left and above).
                effect.ShadowDepth = allSides ? 0 : 3;
            }

            // The black rectangle that casts the shadow has the frame's rounded corners too (a square one showed as a black corner through the
            // frame's smoothed edge, over a light background).
            ShadowShape.CornerRadius = TryFindResource("PopupShadowOutsideOnly") is true ? new CornerRadius(FrameRadius()) : new CornerRadius(0);

            // A theme whose frames are see-through (PopupShadowOutsideOnly = True) must not have the black rectangle behind them: the shadow is
            // then only what spreads outside the frame, and the frame's own area is cut out of it.
            if (TryFindResource("PopupShadowOutsideOnly") is true && ActualWidth > 0 && ActualHeight > 0)
            {
                var whole = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
                // The frame's corners are rounded: cut out with square ones, the shadow was missing from the pixels between the curve and the
                // square (a white dot at each corner over a light background). A little less round than the frame, so the shadow runs under its
                // smoothed edge.
                double radius = FrameRadius();
                var frame = new RectangleGeometry(new Rect(room.Left, room.Top, System.Math.Max(0, ActualWidth - room.Left - room.Right), System.Math.Max(0, ActualHeight - room.Top - room.Bottom)), radius, radius);
                var cut = new CombinedGeometry(GeometryCombineMode.Exclude, whole, frame);
                cut.Freeze();
                Clip = cut;
            }
            else if (Clip != null)
            {
                ClearValue(ClipProperty);
            }

            PopupBlur.Update(this, Room);
        }

        private static double Snap(double dips, double scale) => System.Math.Round(dips * scale, MidpointRounding.AwayFromZero) / scale;

        // The radius of the frame's corners in DIPs (what the frame image draws, 6 pixels: a little less than the theme's blur corner radius,
        // which is for 6 DIPs).
        private double FrameRadius() => TryFindResource("PopupBlurCornerRadius") is double r ? r * 0.8 : 0;

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Apply();
        }
    }
}
