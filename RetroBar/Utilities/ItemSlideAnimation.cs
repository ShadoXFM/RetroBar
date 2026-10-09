using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Makes the items of an ItemsControl glide to their new places when the list is rearranged, instead
    /// of jumping: <see cref="Capture"/> notes where every item is now, and after the rearrangement
    /// <see cref="Play"/> (once the new layout is in) animates each item from its old position to its
    /// new one.
    /// </summary>
    public static class ItemSlideAnimation
    {
        private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(180));

        // A From -> To animation whose value is always a whole number of device pixels.
        private sealed class PixelSnappedAnimation : DoubleAnimationBase
        {
            private double _from;
            private double _to;
            private double _dpi = 1;
            private IEasingFunction _ease;

            public PixelSnappedAnimation()
            {
            }

            public PixelSnappedAnimation(double from, double to, double dpi, Duration duration, IEasingFunction ease)
            {
                _from = from;
                _to = to;
                _dpi = dpi > 0 ? dpi : 1;
                _ease = ease;
                Duration = duration;
            }

            protected override Freezable CreateInstanceCore() => new PixelSnappedAnimation(_from, _to, _dpi, Duration, _ease);

            protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
            {
                double progress = animationClock.CurrentProgress ?? 1;
                double eased = _ease?.Ease(progress) ?? progress;
                double value = _from + (_to - _from) * eased;
                return Math.Round(value * _dpi) / _dpi;
            }
        }

        public static Dictionary<object, Point> Capture(ItemsControl list)
        {
            var positions = new Dictionary<object, Point>();

            foreach (object item in list.Items)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container && container.IsLoaded)
                {
                    positions[item] = container.TransformToAncestor(list).Transform(new Point(0, 0));
                }
            }

            return positions;
        }

        /// <summary>Schedules the slide for after the pending layout pass.</summary>
        public static void Play(ItemsControl list, Dictionary<object, Point> before)
        {
            list.Dispatcher.BeginInvoke(new Action(() => Slide(list, before)), DispatcherPriority.Loaded);
        }

        private static void Slide(ItemsControl list, Dictionary<object, Point> before)
        {
            foreach (object item in list.Items)
            {
                if (!before.TryGetValue(item, out Point oldPosition) ||
                    list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                    !container.IsLoaded)
                {
                    continue;
                }

                Point newPosition = container.TransformToAncestor(list).Transform(new Point(0, 0));
                double dx = oldPosition.X - newPosition.X;
                double dy = oldPosition.Y - newPosition.Y;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                {
                    continue;
                }

                var transform = new TranslateTransform(dx, dy);
                container.RenderTransform = transform;

                // Whole device pixels at every step: an icon drawn between two pixels is blurred by the
                // resampling, which made the toolbar's icons smear while they slid into place.
                double dpi = VisualTreeHelper.GetDpi(container).DpiScaleX;
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                transform.BeginAnimation(TranslateTransform.XProperty, new PixelSnappedAnimation(dx, 0, dpi, SlideDuration, ease));
                transform.BeginAnimation(TranslateTransform.YProperty, new PixelSnappedAnimation(dy, 0, dpi, SlideDuration, ease));
            }
        }
    }
}
