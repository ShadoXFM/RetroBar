using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Lets any element in a theme or control's XAML opt in to a per-monitor pixel nudge that
    /// the person can set from the Properties window's "Per-Monitor Adjustments" tab, instead
    /// of a hand-tuned XAML value that can only ever be correct on one monitor's DPI at a time.
    ///
    /// Usage: <Image utilities:MonitorOffset.Tag="TaskIcon" .../>
    ///
    /// "TaskIcon" here is just a label - it's the key this element's saved offset is stored
    /// and looked up under (see Settings.MonitorOffsets). Any string works, including ones
    /// made up later for an element that doesn't have this attached yet; whatever tag is
    /// entered in the Properties tab is what gets looked for.
    ///
    /// The offset is applied as a RenderTransform, not a Margin: it only ever changes where
    /// the element is painted, never its layout size or its neighbors' positions, so unlike a
    /// couple of past hand-tuned Margin fixes in this app, it cannot make a border overflow or
    /// clip the space its parent thinks it occupies.
    /// </summary>
    public static class MonitorOffset
    {
        public static readonly DependencyProperty TagProperty = DependencyProperty.RegisterAttached(
            "Tag", typeof(string), typeof(MonitorOffset), new PropertyMetadata(null, OnTagChanged));

        public static string GetTag(DependencyObject obj) => (string)obj.GetValue(TagProperty);
        public static void SetTag(DependencyObject obj, string value) => obj.SetValue(TagProperty, value);

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

            // Re-apply whenever any monitor's offsets change (cheaper checks aren't worth it -
            // this only runs when the person is actively editing the Properties tab).
            PropertyChangedEventHandler handler = null;
            handler = (s, args) =>
            {
                if (args.PropertyName != nameof(Settings.MonitorOffsets))
                {
                    return;
                }

                if (!element.Dispatcher.CheckAccess())
                {
                    element.Dispatcher.BeginInvoke(new Action(() => Apply(element)));
                }
                else
                {
                    Apply(element);
                }
            };

            Settings.Instance.PropertyChanged += handler;
            element.Unloaded += (s, args) => Settings.Instance.PropertyChanged -= handler;
        }

        private static void Apply(FrameworkElement element)
        {
            string tag = GetTag(element);
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            string deviceName = FindDeviceName(element);
            MonitorOffsetValue offset = Settings.Instance.GetMonitorOffset(deviceName, tag);

            element.RenderTransform = offset.IsEmpty ? null : new TranslateTransform(offset.X, offset.Y);
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
