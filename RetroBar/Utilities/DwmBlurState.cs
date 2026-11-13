namespace RetroBar.Utilities
{
    /// <summary>
    /// Whether the system draws the blur behind a popup itself (see PopupBlur.DirectAvailable), for a theme's templates to choose the glass of
    /// a frame by: with that blur the frame's fill is the see-through one, without it the darker one that stands in for the blur.
    /// </summary>
    public sealed class DwmBlurState
    {
        public static DwmBlurState Instance { get; } = new DwmBlurState();

        public bool Available => PopupBlur.DirectAvailable;
    }
}
