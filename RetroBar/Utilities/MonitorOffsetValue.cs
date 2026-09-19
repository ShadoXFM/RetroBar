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

        public bool IsEmpty =>
            X == 0 && Y == 0 &&
            Width == null && Height == null && Scale == null &&
            TextRendering == null && BitmapScaling == null;
    }
}
