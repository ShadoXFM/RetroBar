using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// DPI-snaps MediaPlayerButtonStyle's own template chrome (the nested Border Padding/
    /// BorderThickness values that build up its bevel - see that Style's ControlTemplate in
    /// System.xaml) to the button's actual monitor DPI, instead of leaving them as the fixed
    /// whole-DIP literals written in the template. Those literals are only pixel-grid-aligned at
    /// 100% scale; at any other scale (this app already has to deal with 125%), each nested
    /// Border's own Padding/BorderThickness rounds independently against the device-pixel grid,
    /// and the leftover remainder that produces doesn't divide evenly into an explicit per-monitor
    /// Height override - which was the actual cause behind a requested Height sometimes skipping
    /// straight past its target by 2px instead of landing on it exactly. Snapping every chrome
    /// value to an exact multiple of the monitor's own PixelStep up front means there's nothing
    /// left to round unevenly, on any monitor.
    ///
    /// Opt in via the Style itself (affects every Button using it, current and future, without
    /// touching each usage site):
    /// <Setter Property="utilities:ButtonChromeDpiSnap.Enabled" Value="True" />
    /// </summary>
    public static class ButtonChromeDpiSnap
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(ButtonChromeDpiSnap), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Control button || e.NewValue is not true)
            {
                return;
            }

            if (button.IsLoaded)
            {
                Apply(button);
            }

            // Deliberately never unsubscribes - a Button using this style either loads once (the
            // three real transport buttons, the hidden spacers) or Loads/Unloads each time a
            // Popup reopens (the seek popup's own buttons), and re-applying on every reload is
            // harmless (same pattern MonitorOffset's own Element_Loaded already relies on).
            button.Loaded += Button_Loaded;
        }

        private static void Button_Loaded(object sender, RoutedEventArgs e)
        {
            Apply((Control)sender);
        }

        private static void Apply(Control button)
        {
            button.ApplyTemplate();

            double dpiScale = VisualTreeHelper.GetDpi(button).DpiScaleY;
            double pixelStep = dpiScale > 0 ? 1.0 / dpiScale : 1.0;
            double Snap(double dip) => Math.Round(dip / pixelStep) * pixelStep;
            Thickness SnapThickness(Thickness t) => new(Snap(t.Left), Snap(t.Top), Snap(t.Right), Snap(t.Bottom));

            // Read each border's own template-authored Padding/BorderThickness as the nominal,
            // 100%-scale-correct value to snap, rather than a value hardcoded to one specific
            // style's own template - this element is a fresh template instance every time this
            // runs (a Button either loads once, or - for a Popup's own buttons - unloads/reloads
            // with a fresh template each time the popup reopens), so what's already on it here is
            // always the un-snapped XAML original, never a previous call's already-snapped result.
            if (button.Template?.FindName("ButtonBorder", button) is Border outerBorder)
            {
                outerBorder.Padding = SnapThickness(outerBorder.Padding);
            }

            if (button.Template?.FindName("ButtonRightBottomBorder", button) is Border rightBottomBorder)
            {
                rightBottomBorder.BorderThickness = SnapThickness(rightBottomBorder.BorderThickness);
            }

            if (button.Template?.FindName("ButtonLeftTopBorder", button) is Border leftTopBorder)
            {
                leftTopBorder.BorderThickness = SnapThickness(leftTopBorder.BorderThickness);
                leftTopBorder.Padding = SnapThickness(leftTopBorder.Padding);
            }

            // The thumbnail preview's frame (TaskButtonThumbnail): four nested bevel borders, each with
            // whichever of Margin / Padding / BorderThickness it uses.
            foreach (string name in new[] { "SnapBorder1", "SnapBorder2", "SnapBorder3", "SnapBorder4" })
            {
                if (button.Template?.FindName(name, button) is Border frameBorder)
                {
                    frameBorder.Margin = SnapThickness(frameBorder.Margin);
                    frameBorder.Padding = SnapThickness(frameBorder.Padding);
                    frameBorder.BorderThickness = SnapThickness(frameBorder.BorderThickness);
                }
            }

            // ToolbarThumb (the separator bar next to the quick launch / task list): its
            // template's vertical-orientation trigger overrides these same properties, and a
            // value set here in code would beat that trigger - so it's left alone when vertical.
            if (button is Thumb && !(Window.GetWindow(button) is ManagedShell.AppBar.AppBarWindow { Orientation: Orientation.Vertical }))
            {
                button.Margin = SnapThickness(button.Margin);

                foreach (string name in new[] { "OuterBorder1", "OuterBorder2", "OuterNubInner" })
                {
                    if (button.Template?.FindName(name, button) is Border border)
                    {
                        border.BorderThickness = SnapThickness(border.BorderThickness);
                    }
                }

                if (button.Template?.FindName("OuterNub", button) is Border nub)
                {
                    nub.BorderThickness = SnapThickness(nub.BorderThickness);
                    nub.Margin = SnapThickness(nub.Margin);
                    nub.Width = Snap(nub.Width);
                }
            }
        }
    }
}
