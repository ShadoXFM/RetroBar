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
            // Arrange the child at its own explicit Width/Height when it has one, not just
            // DesiredSize - an Image with Stretch="UniformToFill" can report a DesiredSize
            // larger than its own explicit Width/Height when the source's aspect ratio doesn't
            // match the box (e.g. a browser's widescreen video-frame thumbnail vs. square album
            // art), and that inflated DesiredSize propagates upward through any wrapping
            // container's own DesiredSize too (a Border just passes its child's DesiredSize
            // through), so trusting DesiredSize alone would arrange the child at the inflated
            // size regardless of what explicit size it was given. Falls back to DesiredSize for
            // children that don't set an explicit size (most adorners, e.g. resize handles).
            double width = _child.DesiredSize.Width;
            double height = _child.DesiredSize.Height;

            if (_child is FrameworkElement fe)
            {
                if (!double.IsNaN(fe.Width))
                {
                    width = fe.Width;
                }

                if (!double.IsNaN(fe.Height))
                {
                    height = fe.Height;
                }
            }

            _child.Arrange(new Rect(new Point(0, 0), new Size(width, height)));
            return finalSize;
        }
    }
}
