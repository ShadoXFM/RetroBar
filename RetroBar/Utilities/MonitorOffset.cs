using System;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Lets any element in a theme or control's XAML opt in to a per-monitor adjustment read
    /// from MonitorAdjustments (a hand-edited JSON file - see that class), instead of a
    /// hand-tuned XAML value that can only ever be correct on one monitor's DPI at a time.
    ///
    /// Usage: <Image utilities:MonitorOffset.Tag="TaskIcon" .../>
    ///
    /// "TaskIcon" here is just a label - it's the key this element's saved adjustment is stored
    /// and looked up under in monitor-adjustments.json (see MonitorAdjustments). Any string
    /// works, including ones made up later for an element that doesn't have this attached yet.
    ///
    /// Position and scale are applied as a RenderTransform, not a Margin: they only ever change
    /// how the element is painted, never its layout size or its neighbors' positions, so unlike
    /// a couple of past hand-tuned Margin fixes in this app, they cannot make a border overflow
    /// or clip the space its parent thinks it occupies. Width/Height are applied as ordinary
    /// layout overrides (the same as setting them in XAML), and text rendering/bitmap scaling
    /// mode are applied as the matching WPF rendering hints - see MonitorOffsetValue for details
    /// on each.
    /// </summary>
    public static class MonitorOffset
    {
        public static readonly DependencyProperty TagProperty = DependencyProperty.RegisterAttached(
            "Tag", typeof(string), typeof(MonitorOffset), new PropertyMetadata(null, OnTagChanged));

        public static string GetTag(DependencyObject obj) => (string)obj.GetValue(TagProperty);
        public static void SetTag(DependencyObject obj, string value) => obj.SetValue(TagProperty, value);

        // Tracks whether we ourselves last set an explicit Width/Height on this element, so we
        // know it's ours to clear (via ClearValue, which restores whatever was there before -
        // including a Binding) when the adjustment is removed, and so we never touch Width or
        // Height at all on an element that never had an override. Some tagged elements (e.g.
        // MediaPlayer's TrackTextCanvas) have their Height driven by a Binding in XAML; directly
        // assigning FrameworkElement.Height/Width in code replaces a Binding outright, so doing
        // that unconditionally - even to "reset" to NaN - permanently destroys it.
        private static readonly DependencyProperty AppliedWidthProperty = DependencyProperty.RegisterAttached(
            "AppliedWidth", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));
        private static readonly DependencyProperty AppliedHeightProperty = DependencyProperty.RegisterAttached(
            "AppliedHeight", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));

        // Guards against subscribing a new MonitorAdjustments.Changed handler every time Tag
        // changes value on an already-loaded element (e.g. a tag that switches based on a
        // DataTrigger, like TaskButton's active-state icon) - every prior usage set Tag once
        // and never again, so this never mattered until that usage pattern existed.
        private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
            "IsAttached", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));

        private static void OnTagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element || e.NewValue is not string tag || string.IsNullOrEmpty(tag))
            {
                return;
            }

            if (element.IsLoaded)
            {
                Attach(element);
            }
            else
            {
                element.Loaded += Element_Loaded;
            }
        }

        private static void Element_Loaded(object sender, RoutedEventArgs e)
        {
            FrameworkElement element = (FrameworkElement)sender;
            element.Loaded -= Element_Loaded;
            Attach(element);
        }

        private static void Attach(FrameworkElement element)
        {
            Apply(element);

            if ((bool)element.GetValue(IsAttachedProperty))
            {
                // Already subscribed below from an earlier Tag value on this same element -
                // that handler re-reads GetTag(element) on every invocation, so it already
                // picks up whatever the tag is now without needing a second subscription.
                return;
            }

            element.SetValue(IsAttachedProperty, true);

            // Re-apply whenever the monitor-adjustments.json file changes on disk. Changed
            // fires on a background (file-watcher) thread, so this always has to hop back to
            // the element's own dispatcher before touching it.
            EventHandler handler = null;
            handler = (s, args) =>
            {
                if (!element.Dispatcher.CheckAccess())
                {
                    element.Dispatcher.BeginInvoke(new Action(() => Apply(element)));
                }
                else
                {
                    Apply(element);
                }
            };

            MonitorAdjustments.Changed += handler;
            element.Unloaded += (s, args) =>
            {
                MonitorAdjustments.Changed -= handler;
                element.SetValue(IsAttachedProperty, false);
            };
        }

        private static void Apply(FrameworkElement element)
        {
            string tag = GetTag(element);
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            string deviceName = FindDeviceName(element);
            MonitorOffsetValue offset = MonitorAdjustments.Get(deviceName, tag);

            bool hasScale = offset.Scale.HasValue && offset.Scale.Value != 1;

            if (offset.X != 0 || offset.Y != 0 || hasScale)
            {
                var group = new TransformGroup();

                if (hasScale)
                {
                    // Scale from the element's own center rather than its top-left corner, so a
                    // size nudge doesn't also shove the element sideways.
                    element.RenderTransformOrigin = new Point(0.5, 0.5);
                    group.Children.Add(new ScaleTransform(offset.Scale.Value, offset.Scale.Value));
                }

                group.Children.Add(new TranslateTransform(offset.X, offset.Y));
                element.RenderTransform = group;
            }
            else
            {
                element.RenderTransform = null;
            }

            // Width/Height: an explicit override behaves exactly like setting them in XAML. Only
            // touch the property at all when there's an override to apply or to remove - never
            // as a blanket "reset to NaN" - so an element whose Width/Height comes from a XAML
            // Binding (or a Style, or nothing at all) is left completely alone until someone
            // actually sets an adjustment for it.
            ApplySize(element, offset.Width, FrameworkElement.WidthProperty, AppliedWidthProperty);
            ApplySize(element, offset.Height, FrameworkElement.HeightProperty, AppliedHeightProperty);

            if (offset.TextRendering != null && Enum.TryParse(offset.TextRendering, out TextRenderingMode renderingMode))
            {
                TextOptions.SetTextRenderingMode(element, renderingMode);
            }
            else
            {
                element.ClearValue(TextOptions.TextRenderingModeProperty);
            }

            if (offset.BitmapScaling != null && Enum.TryParse(offset.BitmapScaling, out BitmapScalingMode scalingMode))
            {
                RenderOptions.SetBitmapScalingMode(element, scalingMode);
            }
            else
            {
                element.ClearValue(RenderOptions.BitmapScalingModeProperty);
            }
        }

        private static void ApplySize(FrameworkElement element, double? value, DependencyProperty sizeProperty, DependencyProperty appliedFlagProperty)
        {
            if (value.HasValue)
            {
                element.SetValue(sizeProperty, value.Value);
                element.SetValue(appliedFlagProperty, true);
            }
            else if ((bool)element.GetValue(appliedFlagProperty))
            {
                element.ClearValue(sizeProperty);
                element.SetValue(appliedFlagProperty, false);
            }
        }

        private static string FindDeviceName(DependencyObject element)
        {
            if (element is not Visual visual)
            {
                return null;
            }

            return (Window.GetWindow(visual) as Taskbar)?.Screen.DeviceName;
        }
    }
}