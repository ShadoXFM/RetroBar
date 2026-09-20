using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Hosts a single UIElement inside an AdornerLayer, positioned so its local (0,0)
    /// lines up with the AdornedElement's own top-left by default. An Adorner renders
    /// above, and is never clipped by, any of the AdornedElement's own ancestors - that's
    /// the whole reason this exists (see MediaPlayer.xaml.cs's use of it for
    /// MediaAlbumArt, which otherwise gets clipped by the Tray GroupBox's Padding/
    /// BorderThickness when nudged upward via a per-monitor offset).
    /// </summary>
    public class UIElementAdorner : Adorner
    {
        private readonly UIElement _child;

        /// <param name="isHitTestVisible">false (the default) makes the adorner - and so the
        /// child too, since hit-test traversal stops at a non-hit-testable ancestor - purely
        /// visual, same as most adorners (resize handles being the usual exception). Pass true
        /// when the child needs to be clickable, e.g. MediaPlayer's album art.</param>
        public UIElementAdorner(UIElement adornedElement, UIElement child, bool isHitTestVisible = false) : base(adornedElement)
        {
            _child = child;
            AddVisualChild(_child);
            IsHitTestVisible = isHitTestVisible;
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _child;

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(constraint);
            return _child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Arrange the child at its own desired size, not stretched to finalSize -
            // otherwise an explicit Width/Height smaller than the AdornedElement's own
            // size (the common case) would get silently overridden.
            _child.Arrange(new Rect(new Point(0, 0), _child.DesiredSize));
            return finalSize;
        }
    }
}
