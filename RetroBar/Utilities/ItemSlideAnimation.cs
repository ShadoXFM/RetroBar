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
        private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(150));

        // A From -> To animation whose value is always a whole number of device pixels.
        private sealed class PixelSnappedAnimation : DoubleAnimationBase
        {
            private double _from;
            private double _to;
            private double _dpi = 1;
            private IEasingFunction _ease;
            private bool _stallProof;
            private double _nominalMs;
            private double _playedMs;
            private long _lastTicks;

            public PixelSnappedAnimation()
            {
            }

            // stallProof: the progress is what has been drawn, not the time on the clock - a stretch of more than 60 ms between two evaluations
            // (the thread was busy, with a window that is opening) is not counted, so the slide is seen from its start whenever it was begun.
            // The clock then runs longer than the animation, which stays at its end.
            public PixelSnappedAnimation(double from, double to, double dpi, Duration duration, IEasingFunction ease, bool stallProof = false)
            {
                _from = from;
                _to = to;
                _dpi = dpi > 0 ? dpi : 1;
                _ease = ease;
                _stallProof = stallProof;
                _nominalMs = duration.HasTimeSpan ? duration.TimeSpan.TotalMilliseconds : 0;
                Duration = stallProof && duration.HasTimeSpan ? new Duration(duration.TimeSpan + TimeSpan.FromSeconds(3)) : duration;
            }

            protected override Freezable CreateInstanceCore() => new PixelSnappedAnimation(_from, _to, _dpi,
                _stallProof ? new Duration(TimeSpan.FromMilliseconds(_nominalMs)) : Duration, _ease, _stallProof);

            protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
            {
                double progress = animationClock.CurrentProgress ?? 1;
                if (_stallProof && _nominalMs > 0)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (_lastTicks != 0)
                    {
                        double step = (now - _lastTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (step <= 60)
                        {
                            _playedMs += step;
                        }
                    }

                    _lastTicks = now;
                    progress = Math.Min(1, _playedMs / _nominalMs);
                }

                double eased = _ease?.Ease(progress) ?? progress;
                double value = _from + (_to - _from) * eased;
                return Math.Round(value * _dpi) / _dpi;
            }
        }

        // Where the places are measured from: the window, so that a list that moves as a whole (the centered tabs of the Windows 11
        // style, when one is added) has its items slide too, not only the ones that moved inside it.
        private static Visual Reference(ItemsControl list) => Window.GetWindow(list) ?? (Visual)list;

        /// <summary>
        /// Makes something drawn in the list (the ghost of a tab that has closed, in the list's adorner layer) go with the list when it moves as a whole
        /// (the centered tabs of the Windows 11 style, when one closes), at the pace the tabs slide to their new places, instead of being put
        /// where the list is now at once.
        /// </summary>
        public static void Follow(UIElement list, UIElement element)
        {
            Visual reference = Window.GetWindow(list) ?? (Visual)list;
            Point before;
            try
            {
                before = list.TransformToAncestor(reference).Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                return;
            }

            bool done = false;
            int passes = 0;
            EventHandler handler = null;
            handler = (s, e) =>
            {
                if (done)
                {
                    return;
                }

                passes++;
                try
                {
                    Point now = list.TransformToAncestor(reference).Transform(new Point(0, 0));
                    double dx = before.X - now.X, dy = before.Y - now.Y;
                    if (Math.Abs(dx) >= 0.5 || Math.Abs(dy) >= 0.5)
                    {
                        double dpi = VisualTreeHelper.GetDpi(list).DpiScaleX;
                        dx = Math.Round(dx * dpi) / dpi;
                        dy = Math.Round(dy * dpi) / dpi;
                        var transform = new TranslateTransform(dx, dy);
                        element.RenderTransform = transform;
                        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                        transform.BeginAnimation(TranslateTransform.XProperty, new PixelSnappedAnimation(dx, 0, dpi, SlideDuration, ease, stallProof: true));
                        transform.BeginAnimation(TranslateTransform.YProperty, new PixelSnappedAnimation(dy, 0, dpi, SlideDuration, ease, stallProof: true));
                        done = true;
                    }
                    else if (passes >= 4)
                    {
                        done = true;
                    }
                }
                catch (InvalidOperationException)
                {
                    done = true;
                }

                if (done)
                {
                    list.LayoutUpdated -= handler;
                }
            };
            list.LayoutUpdated += handler;
        }

        /// <summary>A From -> To animation that is not shortened by a stall of the thread (see PixelSnappedAnimation: the same progress as the slide's,
        /// frame by frame, so a tab that grows in goes with the others that slide). Its clock runs a few seconds longer than the animation.</summary>
        public static DoubleAnimationBase StallProof(double from, double to, Duration duration, IEasingFunction ease) =>
            new PixelSnappedAnimation(from, to, 1e6, duration, ease, stallProof: true);

        /// <summary>Takes a tab's transform to this offset from where it is, in whole device pixels at every step.</summary>
        public static void Glide(TranslateTransform transform, double to, double dpi, Action completed = null)
        {
            double scale = dpi > 0 ? dpi : 1;
            var animation = new PixelSnappedAnimation(transform.X, Math.Round(to * scale) / scale, scale, new Duration(TimeSpan.FromMilliseconds(130)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut });
            if (completed != null)
            {
                animation.Completed += (s, e) => completed();
            }

            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        public static Dictionary<object, Point> Capture(ItemsControl list)
        {
            var positions = new Dictionary<object, Point>();
            Visual reference = Reference(list);

            foreach (object item in list.Items)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container && container.IsLoaded)
                {
                    positions[item] = container.TransformToAncestor(reference).Transform(new Point(0, 0));
                }
            }

            return positions;
        }

        /// <summary>Schedules the slide for the layout pass that follows: it is set up as that pass ends, before the frame with the items
        /// at their new places is drawn (set up later, that frame showed them there for an instant and they then jumped back).</summary>
        public static void Play(ItemsControl list, Dictionary<object, Point> before)
        {
            bool done = false;
            int passes = 0;
            EventHandler handler = null;
            handler = (s, e) =>
            {
                if (done)
                {
                    return;
                }

                passes++;
                if (Slide(list, before) || passes >= 4)
                {
                    done = true;
                    list.LayoutUpdated -= handler;
                }
            };
            list.LayoutUpdated += handler;

            // If no layout comes (nothing changed its size), the slide is tried once after all.
            list.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!done)
                {
                    done = true;
                    list.LayoutUpdated -= handler;
                    Slide(list, before);
                }
            }), DispatcherPriority.Loaded);
        }

        private static bool Slide(ItemsControl list, Dictionary<object, Point> before)
        {
            bool moved = false;
            Visual reference = Reference(list);
            foreach (object item in list.Items)
            {
                if (!before.TryGetValue(item, out Point oldPosition) ||
                    list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                    !container.IsLoaded)
                {
                    continue;
                }

                Point newPosition = container.TransformToAncestor(reference).Transform(new Point(0, 0));
                double dx = oldPosition.X - newPosition.X;
                double dy = oldPosition.Y - newPosition.Y;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                {
                    continue;
                }

                moved = true;
                // Whole device pixels at every step: an icon drawn between two pixels is blurred by the
                // resampling, which made the toolbar's icons smear while they slid into place.
                double dpi = VisualTreeHelper.GetDpi(container).DpiScaleX;

                // And in the frame before the first step of the animation too (the place the transform starts at is drawn until the clock
                // ticks): the icons of the tabs, which are not snapped as the frames are, moved by a pixel against them for that frame.
                dx = Math.Round(dx * dpi) / dpi;
                dy = Math.Round(dy * dpi) / dpi;
                var transform = new TranslateTransform(dx, dy);
                container.RenderTransform = transform;
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                // (Stall-proof: a window that is slow to open keeps the thread busy for a while, and the slide used to begin part of the way.)
                transform.BeginAnimation(TranslateTransform.XProperty, new PixelSnappedAnimation(dx, 0, dpi, SlideDuration, ease, stallProof: true));
                transform.BeginAnimation(TranslateTransform.YProperty, new PixelSnappedAnimation(dy, 0, dpi, SlideDuration, ease, stallProof: true));
            }

            return moved;
        }
    }
}
