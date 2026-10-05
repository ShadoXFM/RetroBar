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

                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, SlideDuration) { EasingFunction = ease });
                transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, SlideDuration) { EasingFunction = ease });
            }
        }
    }
}
