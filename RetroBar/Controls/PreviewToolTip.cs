using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Controls
{
    /// <summary>
    /// The tab's preview. WPF closes every tooltip when the application stops being the active one (the popup raises PopupCouldClose
    /// whatever its StaysOpen), and the preview of a tab that was clicked first is up while RetroBar is the active application: closing
    /// a window from it hands the focus to another application and the preview went with it. While <see cref="KeepOpen"/> is set it is
    /// only closed by the code that opens and closes it (TaskButton.Preview.cs).
    /// </summary>
    public class PreviewToolTip : ToolTip
    {
        static PreviewToolTip()
        {
            IsOpenProperty.OverrideMetadata(typeof(PreviewToolTip), new FrameworkPropertyMetadata(false, null, CoerceIsOpen));
        }

        public bool KeepOpen { get; set; }

        private static object CoerceIsOpen(DependencyObject d, object value)
        {
            return value is false && d is PreviewToolTip { KeepOpen: true } ? true : value;
        }
    }
}
