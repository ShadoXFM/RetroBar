using System.Windows;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Whether the media player is in the tray box a box's tuned look follows (the per-monitor tags TrayBoxOuter, TrayBoxBevel and the like have a
    /// Playing variant for a tray box with the media player in it). Inherited down the box, so the box that has the media player in it (the tray
    /// box) says what the taskbar says, and any other box (the weather's, in the Windows 11 style) is as a tray box without it.
    /// </summary>
    public static class TrayBoxState
    {
        public static readonly DependencyProperty MediaPlayingProperty = DependencyProperty.RegisterAttached(
            "MediaPlaying", typeof(bool), typeof(TrayBoxState), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetMediaPlaying(DependencyObject obj) => (bool)obj.GetValue(MediaPlayingProperty);

        public static void SetMediaPlaying(DependencyObject obj, bool value) => obj.SetValue(MediaPlayingProperty, value);
    }
}
