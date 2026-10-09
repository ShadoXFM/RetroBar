using System.Windows;
using System.Windows.Controls;
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

        public static readonly DependencyProperty RoomProperty = DependencyProperty.Register(
            nameof(Room), typeof(Thickness), typeof(PopupShadow),
            new PropertyMetadata(new Thickness(10), (d, e) => ((PopupShadow)d).Apply()));

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
        }

        private void Apply()
        {
            // (Called by the property callbacks while the constructor is still running, before the template exists.)
            if (ShadowClip == null || ShadowShape == null)
            {
                return;
            }

            Thickness room = Room;
            ShadowClip.Margin = new Thickness(room.Left + ClipOverlap, room.Top + ClipOverlap, 0, 0);
            ShadowShape.Margin = new Thickness(Inset, Inset, room.Right, room.Bottom);

            if (ShadowShape.Effect is DropShadowEffect effect)
            {
                effect.Opacity = Strength;
            }
        }
    }
}
