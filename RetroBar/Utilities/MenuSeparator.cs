using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The separator line every context menu uses: the theme's menus draw a plain Separator as empty space (or in a color of
    /// the system's own), so this is a visible bevelled line - a shadow line over a highlight line, in the theme's own colors
    /// - the same in all the menus, the media player's included.
    /// </summary>
    public static class MenuSeparator
    {
        private static Style _style;

        public static Style Style => _style ??= Create();

        /// <summary>Has the menu's separators drawn this way (those already in it too).</summary>
        public static void Apply(ContextMenu menu)
        {
            if (!menu.Resources.Contains(typeof(Separator)))
            {
                menu.Resources[typeof(Separator)] = Style;
            }

            // A separator that has already been given its style (a menu written in XAML has them by now) is not restyled by the
            // resource above, so each is given the style itself.
            foreach (object child in menu.Items)
            {
                if (child is Separator separator && !ReferenceEquals(separator.Style, Style))
                {
                    separator.Style = Style;
                }
            }
        }

        /// <summary>
        /// Hides the separators that would stand alone now that the separator is a visible line: one at the top or the bottom
        /// of the menu, or next to another - the items between them are hidden, which used to leave only empty space.
        /// </summary>
        public static void Tidy(ContextMenu menu)
        {
            Separator pending = null;
            bool anyItem = false;

            foreach (object child in menu.Items)
            {
                if (child is not UIElement element)
                {
                    continue;
                }

                if (child is Separator separator)
                {
                    // Shown only if an item has come before it, and none of the separators since then has been shown.
                    separator.Visibility = Visibility.Collapsed;

                    if (anyItem && pending == null)
                    {
                        pending = separator;
                    }

                    continue;
                }

                if (element.Visibility != Visibility.Visible)
                {
                    continue;
                }

                anyItem = true;

                if (pending != null)
                {
                    pending.Visibility = Visibility.Visible;
                    pending = null;
                }
            }
        }

        private static Style Create()
        {
            var line = new FrameworkElementFactory(typeof(MenuSeparatorLine));
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 2.5, 0));

            var style = new Style(typeof(Separator));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Separator)) { VisualTree = line }));
            return style;
        }
    }

    /// <summary>
    /// The line itself: two lines each exactly one device pixel thick - the shadow over the highlight - with a pixel of room
    /// above and below, whatever the monitor's scale. Two 1 DIP rectangles came out one pixel thick in one menu and two in another at 125%, depending on where in the
    /// menu the separator fell.
    /// </summary>
    public class MenuSeparatorLine : FrameworkElement
    {
        private double PixelSize => 1 / System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(0, 4 * PixelSize);
        }

        protected override void OnRender(System.Windows.Media.DrawingContext drawingContext)
        {
            double pixel = PixelSize;
            drawingContext.DrawRectangle(TryFindResource("ButtonShadow") as System.Windows.Media.Brush, null, new Rect(0, pixel, ActualWidth, pixel));
            drawingContext.DrawRectangle(TryFindResource("ButtonHighlight") as System.Windows.Media.Brush, null, new Rect(0, 2 * pixel, ActualWidth, pixel));
        }
    }
}
