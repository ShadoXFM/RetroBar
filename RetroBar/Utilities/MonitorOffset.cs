using System;
using System.ComponentModel;
using System.Globalization;
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
    /// layout overrides (the same as setting them in XAML), text rendering/bitmap scaling mode
    /// are applied as the matching WPF rendering hints, and Margin - real layout space, unlike
    /// X/Y - is available too when a nudge genuinely needs to push neighboring content over
    /// rather than just visually shift; see MonitorOffsetValue for details on each.
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
        private static readonly DependencyProperty AppliedMarginProperty = DependencyProperty.RegisterAttached(
            "AppliedMargin", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));
        private static readonly DependencyProperty AppliedPaddingProperty = DependencyProperty.RegisterAttached(
            "AppliedPadding", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));
        private static readonly DependencyProperty AppliedBackgroundProperty = DependencyProperty.RegisterAttached(
            "AppliedBackground", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));
        private static readonly DependencyProperty AppliedGeometryProperty = DependencyProperty.RegisterAttached(
            "AppliedGeometry", typeof(bool), typeof(MonitorOffset), new PropertyMetadata(false));

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
            // Deliberately never unsubscribes itself: most elements only ever load once (the
            // main window loads at startup and never unloads), so that was harmless before, but
            // a Popup's content (e.g. the seek popup's buttons/slider/time text) genuinely
            // Loads/Unloads every time the popup opens/closes - unsubscribing after the first
            // firing left it permanently stuck on whatever monitor-adjustments.json said the
            // very first time it ever opened, since nothing was left to re-attach it on later
            // reopens. Attach() below already guards against double-subscribing its own
            // MonitorAdjustments.Changed handler, so calling it again on every reload is safe.
            Attach((FrameworkElement)sender);
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

            // What is worked out here depends on the DPI (the pixel sizes of a snapped image above all), and a window can
            // be given another DPI after its contents are loaded - a taskbar starts out with the DPI of the primary monitor
            // and is only then moved to its own - so apply again whenever its DPI changes.
            if (Window.GetWindow(element) is Window window)
            {
                DpiChangedEventHandler dpiHandler = (sender, args) =>
                    element.Dispatcher.BeginInvoke(new Action(() => Apply(element)), System.Windows.Threading.DispatcherPriority.Loaded);
                window.DpiChanged += dpiHandler;
                element.Unloaded += (sender, args) => window.DpiChanged -= dpiHandler;
            }

            if (element is System.Windows.Controls.Image)
            {
                // An Image's pixel-snapped transform depends on its laid-out size and position,
                // which don't exist yet when Apply first runs (an icon's source usually arrives
                // later), so re-run Apply once its size is known / changes. Deliberately not
                // LayoutUpdated: re-checking on every layout pass of a tagged Image kept the quick
                // launch icons from ever loading.
                SizeChangedEventHandler sizeHandler = (sender, args) => Apply(element);
                element.SizeChanged += sizeHandler;

                // The snapped transform also depends on where the image sits, which changes without its size
                // when the items around it move (icons of a rearranged toolbar shift by a slot of e.g. 27.5px,
                // so a stale transform leaves them half a pixel off the grid: blurry). Only the cheap position
                // key is compared on a layout pass; Apply itself runs, once and later, only when it changed.
                bool resnapQueued = false;
                EventHandler layoutHandler = (sender, args) =>
                {
                    if (resnapQueued || element.GetValue(SnapKeyProperty) is not string appliedKey ||
                        ComputeSnapKey(element, out _, out _) is not string currentKey || currentKey == appliedKey)
                    {
                        return;
                    }

                    resnapQueued = true;
                    element.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        resnapQueued = false;
                        Apply(element);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                };
                element.LayoutUpdated += layoutHandler;
                element.Unloaded += (sender, args) =>
                {
                    element.SizeChanged -= sizeHandler;
                    element.LayoutUpdated -= layoutHandler;
                };
            }

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

            if (element is not System.Windows.Controls.Image || !TryApplySnappedImageTransform(element, offset))
            {
                ApplyPlainTransform(element, offset);
            }

            // Width/Height: an explicit override behaves exactly like setting them in XAML. Only
            // touch the property at all when there's an override to apply or to remove - never
            // as a blanket "reset to NaN" - so an element whose Width/Height comes from a XAML
            // Binding (or a Style, or nothing at all) is left completely alone until someone
            // actually sets an adjustment for it.
            ApplySize(element, offset.Width, FrameworkElement.WidthProperty, AppliedWidthProperty);
            ApplySize(element, offset.Height, FrameworkElement.HeightProperty, AppliedHeightProperty);
            ApplyMargin(element, offset.Margin);
            ApplyPadding(element, offset.Padding);
            ApplyBackground(element, offset.Background);

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

            // RenderOptions.BitmapScalingMode doesn't carry WPF's AffectsRender metadata flag, so
            // changing it after the element's first render doesn't automatically trigger a
            // repaint - the property value updates correctly, but the screen keeps showing
            // whatever was already painted until something else forces a redraw.
            element.InvalidateVisual();

            ApplyGeometry(element, offset.Geometry);

            if (offset.Bold.HasValue)
            {
                element.SetValue(System.Windows.Documents.TextElement.FontWeightProperty,
                    offset.Bold.Value ? FontWeights.Bold : FontWeights.Normal);
            }
            else
            {
                element.ClearValue(System.Windows.Documents.TextElement.FontWeightProperty);
            }

            if (offset.FontSize.HasValue)
            {
                element.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, offset.FontSize.Value);
            }
            else
            {
                element.ClearValue(System.Windows.Documents.TextElement.FontSizeProperty);
            }

            if (!string.IsNullOrWhiteSpace(offset.FontFamily))
            {
                element.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,
                    new FontFamily(offset.FontFamily));
            }
            else
            {
                element.ClearValue(System.Windows.Documents.TextElement.FontFamilyProperty);
            }
        }

        private static void ApplyPlainTransform(FrameworkElement element, MonitorOffsetValue offset)
        {
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
        }

        private static readonly DependencyProperty SnapKeyProperty = DependencyProperty.RegisterAttached(
            "SnapKey", typeof(string), typeof(MonitorOffset), new PropertyMetadata(null));

        private static string ComputeSnapKey(FrameworkElement element, out Point layoutPositionPx, out Size layoutSizePx)
        {
            layoutPositionPx = default;
            layoutSizePx = default;

            // The root of whatever window the element is in: a Window, or the root of a Popup/ToolTip (which has no
            // Window above it, and was left unsnapped - and so softer than the same icon in the taskbar - before).
            Visual root = PresentationSource.FromVisual(element)?.RootVisual;
            if (root == null || element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
                VisualTreeHelper.GetParent(element) is not Visual parent)
            {
                return null;
            }

            try
            {
                // GetOffset is the element's layout position inside its parent, i.e. without its own
                // RenderTransform (which is what this class is about to set), so reading it never feeds
                // back into the value being computed. The parent's transform to the window does include
                // any transform an ancestor carries (e.g. TrayBox's own Y nudge), which does move pixels.
                Vector offsetInParent = VisualTreeHelper.GetOffset(element);
                Point layoutPosition = parent.TransformToAncestor(root).Transform(new Point(offsetInParent.X, offsetInParent.Y));
                DpiScale dpi = VisualTreeHelper.GetDpi(element);
                layoutPositionPx = new Point(layoutPosition.X * dpi.DpiScaleX, layoutPosition.Y * dpi.DpiScaleY);
                layoutSizePx = new Size(element.ActualWidth * dpi.DpiScaleX, element.ActualHeight * dpi.DpiScaleY);
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (!double.IsFinite(layoutPositionPx.X) || !double.IsFinite(layoutPositionPx.Y) ||
                !double.IsFinite(layoutSizePx.Width) || !double.IsFinite(layoutSizePx.Height))
            {
                return null;
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"{layoutPositionPx.X:F3},{layoutPositionPx.Y:F3},{layoutSizePx.Width:F3},{layoutSizePx.Height:F3}");
        }

        /// <summary>
        /// Position/scale for an Image, with its drawn edges rounded onto whole device pixels.
        ///
        /// Giving an element any RenderTransform switches off WPF's own pixel snapping for it, so a
        /// bitmap drawn through a plain scale/translate lands wherever the math puts it - e.g. a 20px
        /// icon at Scale 0.85 is 17px wide and centered, so its left edge sits half a pixel off the
        /// grid, and merely nudging X/Y leaves it wherever its (fractional) layout slot was. Every edge
        /// pixel then blends with its neighbor ("blurry") and it comes out differently on each
        /// monitor DPI. Here the scaled size is rounded to a whole number of device pixels and the
        /// translation is adjusted by the sub-pixel amount that puts the image's top-left corner
        /// exactly on a pixel boundary, so the bitmap is resampled once, cleanly, into an aligned box.
        /// Layout is untouched, exactly as before - only how the image is painted.
        /// </summary>
        private static bool TryApplySnappedImageTransform(FrameworkElement element, MonitorOffsetValue offset)
        {
            bool hasAnyAdjustment = offset.X != 0 || offset.Y != 0 || offset.Scale.HasValue || offset.Width.HasValue || offset.Height.HasValue;
            if (!hasAnyAdjustment)
            {
                element.RenderTransform = null;
                element.SetValue(SnapKeyProperty, null);
                return true;
            }

            string key = ComputeSnapKey(element, out Point position, out Size size);
            if (key == null)
            {
                // Not laid out / not in a window yet: the SizeChanged hook in Attach
                // re-run Apply once it is, so just use the plain transform for now.
                return false;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(element);
            double scale = offset.Scale ?? 1;

            double scaledWidth = scale == 1 ? size.Width : Math.Max(1, Math.Round(size.Width * scale));
            double scaledHeight = scale == 1 ? size.Height : Math.Max(1, Math.Round(size.Height * scale));

            // Left/top edge of the scaled image before snapping (scaling is about the element's center).
            double baseLeft = position.X + (size.Width - scaledWidth) / 2;
            double baseTop = position.Y + (size.Height - scaledHeight) / 2;
            double translateX = (Math.Round(baseLeft + offset.X * dpi.DpiScaleX) - baseLeft) / dpi.DpiScaleX;
            double translateY = (Math.Round(baseTop + offset.Y * dpi.DpiScaleY) - baseTop) / dpi.DpiScaleY;

            var group = new TransformGroup();
            if (scaledWidth != size.Width || scaledHeight != size.Height)
            {
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                group.Children.Add(new ScaleTransform(scaledWidth / size.Width, scaledHeight / size.Height));
            }

            group.Children.Add(new TranslateTransform(translateX, translateY));
            element.RenderTransform = group;
            element.SetValue(SnapKeyProperty, key);
            return true;
        }

        private static void ApplySize(FrameworkElement element, double? value, DependencyProperty sizeProperty, DependencyProperty appliedFlagProperty)
        {
            if (value.HasValue)
            {
                // Whole device pixels, for buttons and icons: a size that isn't (e.g. Width 22 on a
                // 125% monitor = 27.5px) leaves each one a fractional width, so a row of them
                // alternates between two whole-pixel widths and everything drawn to their bounds
                // (bevel lines, the hover tint) comes out 1px different from one to the next.
                // Other elements (a hand-tuned Grid height, say) keep exactly the value written.
                double size = value.Value;
                DpiScale dpi = VisualTreeHelper.GetDpi(element);
                double pixelsPerDip = sizeProperty == FrameworkElement.WidthProperty ? dpi.DpiScaleX : dpi.DpiScaleY;
                if (pixelsPerDip > 0 && size > 0 && element is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Image)
                {
                    size = Math.Max(1, Math.Round(size * pixelsPerDip, MidpointRounding.AwayFromZero)) / pixelsPerDip;
                }

                element.SetValue(sizeProperty, size);
                element.SetValue(appliedFlagProperty, true);
            }
            else if ((bool)element.GetValue(appliedFlagProperty))
            {
                element.ClearValue(sizeProperty);
                element.SetValue(appliedFlagProperty, false);
            }
        }

        private static readonly ThicknessConverter ThicknessConverter = new();

        private static void ApplyMargin(FrameworkElement element, string marginText)
        {
            if (!string.IsNullOrWhiteSpace(marginText))
            {
                try
                {
                    // Explicit InvariantCulture: the parameterless ConvertFromString(string)
                    // overload parses using CultureInfo.CurrentCulture, which on a system whose
                    // locale uses "," as the decimal separator (rather than a value separator)
                    // misparses a normal-looking Thickness string like "5,0,0,0" - this file is
                    // hand-edited JSON, not locale-sensitive user input, so it should always
                    // parse the same way regardless of the machine it runs on.
                    var thickness = (Thickness)ThicknessConverter.ConvertFromString(null, CultureInfo.InvariantCulture, marginText);
                    element.SetValue(FrameworkElement.MarginProperty, thickness);
                    element.SetValue(AppliedMarginProperty, true);
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException)
                {
                    ManagedShell.Common.Logging.ShellLogger.Warning(
                        $"MonitorOffset: Invalid Margin '{marginText}' for tag '{GetTag(element)}', ignoring it: {ex.Message}");
                }
            }
            else if ((bool)element.GetValue(AppliedMarginProperty))
            {
                element.ClearValue(FrameworkElement.MarginProperty);
                element.SetValue(AppliedMarginProperty, false);
            }
        }

        private static void ApplyPadding(FrameworkElement element, string paddingText)
        {
            // "Padding" isn't declared on FrameworkElement itself - Control and Border each
            // register their own independent DP of that name - so it's looked up per-instance,
            // same as ApplyBackground below.
            DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromName(
                "Padding", element.GetType(), element.GetType());

            if (descriptor == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(paddingText))
            {
                try
                {
                    var thickness = (Thickness)ThicknessConverter.ConvertFromString(null, CultureInfo.InvariantCulture, paddingText);
                    element.SetValue(descriptor.DependencyProperty, thickness);
                    element.SetValue(AppliedPaddingProperty, true);
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException)
                {
                    ManagedShell.Common.Logging.ShellLogger.Warning(
                        $"MonitorOffset: Invalid Padding '{paddingText}' for tag '{GetTag(element)}', ignoring it: {ex.Message}");
                }
            }
            else if ((bool)element.GetValue(AppliedPaddingProperty))
            {
                element.ClearValue(descriptor.DependencyProperty);
                element.SetValue(AppliedPaddingProperty, false);
            }
        }

        private static readonly BrushConverter BrushConverter = new();

        private static void ApplyBackground(FrameworkElement element, string backgroundText)
        {
            // "Background" isn't declared on FrameworkElement itself - Control, Panel and Border
            // each register their own independent DP of that name - so it's looked up
            // per-instance rather than hardcoded to one of those base types.
            DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromName(
                "Background", element.GetType(), element.GetType());

            if (descriptor == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(backgroundText))
            {
                try
                {
                    var brush = (Brush)BrushConverter.ConvertFromString(null, CultureInfo.InvariantCulture, backgroundText);
                    brush.Freeze();
                    element.SetValue(descriptor.DependencyProperty, brush);
                    element.SetValue(AppliedBackgroundProperty, true);
                }
                catch (Exception ex) when (ex is FormatException or NotSupportedException)
                {
                    ManagedShell.Common.Logging.ShellLogger.Warning(
                        $"MonitorOffset: Invalid Background '{backgroundText}' for tag '{GetTag(element)}', ignoring it: {ex.Message}");
                }
            }
            else if ((bool)element.GetValue(AppliedBackgroundProperty))
            {
                element.ClearValue(descriptor.DependencyProperty);
                element.SetValue(AppliedBackgroundProperty, false);
            }
        }

        private static void ApplyGeometry(FrameworkElement element, string figures)
        {
            if (element is not System.Windows.Shapes.Path path)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(figures))
            {
                try
                {
                    Geometry geometry = Geometry.Parse(figures);
                    geometry.Freeze();
                    path.Data = geometry;
                    path.SetValue(AppliedGeometryProperty, true);
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    ManagedShell.Common.Logging.ShellLogger.Warning(
                        $"MonitorOffset: Invalid Geometry '{figures}' for tag '{GetTag(element)}', ignoring it: {ex.Message}");
                }
            }
            else if ((bool)path.GetValue(AppliedGeometryProperty))
            {
                path.ClearValue(System.Windows.Shapes.Path.DataProperty);
                path.SetValue(AppliedGeometryProperty, false);
            }
        }

        private static string FindDeviceName(DependencyObject element)
        {
            if (element is not Visual visual)
            {
                return null;
            }

            if (Window.GetWindow(visual) is Taskbar taskbar)
            {
                return taskbar.Screen.DeviceName;
            }

            // Inside a tooltip (e.g. a tab's thumbnail preview), which lives in its own popup window
            // rather than the taskbar's: use the taskbar its tooltip belongs to.
            for (DependencyObject current = visual; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is System.Windows.Controls.ToolTip { PlacementTarget: Visual target })
                {
                    return (Window.GetWindow(target) as Taskbar)?.Screen.DeviceName;
                }
            }

            return null;
        }
    }
}