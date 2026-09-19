namespace RetroBar.Utilities
{
    /// <summary>
    /// A small X/Y render-position nudge, in device-independent pixels, applied to a single
    /// tagged element on a single monitor. Stored inside Settings.MonitorOffsets, keyed first
    /// by the monitor's AppBarScreen.DeviceName and then by the element's MonitorOffset.Tag.
    ///
    /// This is deliberately just a render offset (see MonitorOffset.cs) rather than a Margin
    /// override: a couple of past fixes in this app used a hand-tuned Margin to compensate for
    /// a DPI-rounding difference between monitors, and because a negative Margin can make an
    /// element overflow the space its parent thinks it occupies, that repeatedly caused borders
    /// to clip or detach on whichever monitor the margin wasn't tuned for. A render offset only
    /// moves where the element is painted - it can't affect layout - so it can't reintroduce
    /// that failure mode.
    /// </summary>
    public class MonitorOffsetValue
    {
        public double X { get; set; }
        public double Y { get; set; }

        public bool IsEmpty => X == 0 && Y == 0;
    }
}
