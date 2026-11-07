using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The glass frame of a tab that has just closed, left where it was and faded away, as the button of a program that closes goes in Windows 7
    /// (the tab itself is gone with its window): a copy of the frame, drawn with the same picture, over the list.
    /// </summary>
    internal sealed class FrameGhost : Adorner
    {
        private readonly NineSlice _slice;
        private readonly Rect _bounds;

        private FrameGhost(UIElement adorned, Rect bounds, NineSlice slice) : base(adorned)
        {
            IsHitTestVisible = false;
            _bounds = bounds;
            _slice = slice;
            AddVisualChild(_slice);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _slice;

        protected override Size MeasureOverride(Size constraint)
        {
            _slice.Measure(_bounds.Size);
            return _bounds.Size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _slice.Arrange(_bounds);
            return finalSize;
        }

        /// <summary>What it takes to draw a frame again: the picture and where it was, in the coordinates of the list.</summary>
        public sealed class Snapshot
        {
            public ImageSource Source;
            public Thickness Margins;
            public int StateCount;
            public int State;
            public Rect Bounds;
        }

        public static Snapshot Take(NineSlice slice, UIElement list)
        {
            if (slice == null || slice.Source == null || slice.ActualWidth <= 0 || slice.ActualHeight <= 0 || !slice.IsDescendantOf(list))
            {
                return null;
            }

            try
            {
                return new Snapshot
                {
                    Source = slice.Source,
                    Margins = slice.Margins,
                    StateCount = slice.StateCount,
                    State = slice.State,
                    Bounds = slice.TransformToAncestor(list).TransformBounds(new Rect(0, 0, slice.ActualWidth, slice.ActualHeight)),
                };
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Draws the frame where it was, and fades it away.</summary>
        public static void Fade(UIElement list, Snapshot snapshot, TimeSpan duration)
        {
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(list);
            if (layer == null || snapshot == null)
            {
                return;
            }

            var slice = new NineSlice { Source = snapshot.Source, Margins = snapshot.Margins, StateCount = snapshot.StateCount, State = snapshot.State };
            var ghost = new FrameGhost(list, snapshot.Bounds, slice);
            layer.Add(ghost);
            // (The list is where it is until its layout is done: if it moves then, the ghost goes with the tabs, not at once.)
            ItemSlideAnimation.Follow(list, slice);
            // The fade counts only the time that is drawn (see StallProofAnimation): closing a program keeps the thread busy for most of a second,
            // and a fade of 150 ms was over before it was seen. The ghost is taken away when it has faded (it is not seen from then, and a clock that
            // runs a few seconds more does not matter).
            var animation = new StallProofAnimation(new (double, double, IEasingFunction)[] { (0, 1, null), (duration.TotalMilliseconds, 0, null) });
            ghost.BeginAnimation(OpacityProperty, animation);
            var taker = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            taker.Tick += (s2, e2) =>
            {
                if (ghost.Opacity <= 0.001 || !ghost.IsVisible)
                {
                    taker.Stop();
                    layer.Remove(ghost);
                }
            };
            taker.Start();
        }
    }
}
