using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// A StackPanel that is not clipped to its layout slot. WPF clips an element to the slot its parent
    /// gave it whenever the element wants to be larger (here the media player row has a fixed height that is
    /// a few pixels taller than the tray box's inner area on some taskbar heights), and that clip also cuts
    /// off everything drawn inside it - the VU meter, for one, could never be shown past the clip no matter
    /// how it was resized or moved.
    /// </summary>
    public class UnclippedStackPanel : StackPanel
    {
        protected override Geometry GetLayoutClip(Size layoutSlotSize) => null;
    }
}
