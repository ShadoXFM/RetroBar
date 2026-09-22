namespace RetroBar.Utilities
{
    /// <summary>
    /// A per-monitor visual adjustment applied to a single tagged element, stored inside the
    /// hand-edited monitor-adjustments.json file (see MonitorAdjustments) and keyed first by
    /// the monitor's AppBarScreen.DeviceName and then by the element's MonitorOffset.Tag.
    ///
    /// X/Y and Scale are applied as a RenderTransform rather than a Margin or a layout resize:
    /// a couple of past fixes in this app used a hand-tuned Margin to compensate for a
    /// DPI-rounding difference between monitors, and because a negative Margin can make an
    /// element overflow the space its parent thinks it occupies, that repeatedly caused borders
    /// to clip or detach on whichever monitor the margin wasn't tuned for. A render transform
    /// only changes how the element is painted, never its layout footprint, so it can't
    /// reintroduce that failure mode.
    ///
    /// Width/Height are a plain layout override - the same thing as setting them in XAML - which
    /// is safe here because it only ever applies to an element that was deliberately tagged for
    /// per-monitor adjustment, not to arbitrary layout.
    /// </summary>
    public class MonitorOffsetValue
    {
        public double X { get; set; }
        public double Y { get; set; }

        public double? Width { get; set; }
        public double? Height { get; set; }

        /// <summary>Uniform render-time scale multiplier (1 = no change), applied around the element's center.</summary>
        public double? Scale { get; set; }

        /// <summary>A System.Windows.Media.TextRenderingMode name (Auto/Aliased/Grayscale/ClearType), or null to leave it inherited.</summary>
        public string TextRendering { get; set; }

        /// <summary>A System.Windows.Media.BitmapScalingMode name (NearestNeighbor/Linear/HighQuality/Fant), or null to leave it inherited.</summary>
        public string BitmapScaling { get; set; }

        /// <summary>true for bold text, false for normal, or null to leave it inherited. Applied
        /// via TextElement.FontWeightProperty, which is an inherited property - it doesn't need
        /// to target a TextBlock directly, tagging an ancestor (e.g. a hosting Canvas) works too.</summary>
        public bool? Bold { get; set; }

        /// <summary>A WPF Thickness string ("5", "5,0", or "5,0,0,0" - same syntax as a plain
        /// XAML Margin="..." attribute), or null to leave the element's own Style/XAML Margin
        /// alone. Unlike X/Y, this is real layout space - it pushes neighboring elements over
        /// and can make an element overflow the space its ancestors think it occupies, which is
        /// exactly the failure mode X/Y's RenderTransform was chosen to avoid (see the class
        /// remarks) - so a Margin override can reproduce the clipping issues that a plain X/Y
        /// nudge on the same element can't. Use X/Y instead whenever a visual nudge is enough;
        /// reach for this only when neighboring content genuinely needs to move too.</summary>
        public string Margin { get; set; }

        /// <summary>A WPF Thickness string (same syntax as Margin), applied to the element's own
        /// Padding property, or null to leave the element's own Style/XAML Padding alone. Only
        /// meaningful on an element that actually has a Padding property (Control, Border, ...) -
        /// like Background, it's looked up per-instance since it isn't declared on
        /// FrameworkElement itself. Unlike Margin, Padding is real layout space that's entirely
        /// internal to the element (it insets the element's own content, rather than pushing the
        /// element itself around relative to its siblings), so growing it genuinely grows the
        /// element's own rendered/auto-sized box without the overflow-past-the-parent failure
        /// mode Margin has, and without reflowing neighboring siblings the way growing an
        /// element's overall Width can. Growing Padding does push the element's own content
        /// inward though - pair it with an X nudge on the content itself (or a child tagged
        /// separately) to keep that content's own position fixed if that matters.</summary>
        public string Padding { get; set; }

        /// <summary>A WPF color string - "#RRGGBB", "#AARRGGBB", or a named color like "Red" -
        /// applied to the element's own Background property, or null to leave its Style/XAML
        /// background alone. Only meaningful on an element that actually has a Background
        /// property (Control, Panel, Border, ...).</summary>
        public string Background { get; set; }

        /// <summary>WPF path mini-language Figures (the same syntax as a PathGeometry's own
        /// Figures="..." - e.g. "M 0,0 L 6,3.5 L 0,7 Z"), applied to the element's own Data
        /// property to replace its vector shape entirely, or null to leave the element's own
        /// Style/XAML geometry alone. Only meaningful on a Path.</summary>
        public string Geometry { get; set; }

        public bool IsEmpty =>
            X == 0 && Y == 0 &&
            Width == null && Height == null && Scale == null &&
            TextRendering == null && BitmapScaling == null && Bold == null && Margin == null &&
            Padding == null && Background == null && Geometry == null;
    }
}
