using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Gives every item of a context menu (and of the menus that open from its items) the one look of
    /// "RetroBarMenuItemStyle" (MenuStyles.xaml). An item that has a style of its own (the theme's Show Desktop item, say)
    /// keeps what that style sets - its text, its weight - and gets the look on top of it.
    /// </summary>
    public static class MenuItemStyle
    {
        private static readonly ConditionalWeakTable<MenuItem, object> Styled = new();
        private static readonly Dictionary<Style, Style> Derived = new();

        public static Style Style => (Style)Application.Current.FindResource("RetroBarMenuItemStyle");

        /// <summary>Styles the items of the menu, and those of the menus under them.</summary>
        public static void Apply(ItemsControl menu)
        {
            foreach (object child in menu.Items)
            {
                if (child is MenuItem item)
                {
                    Apply(item);
                    Apply((ItemsControl)item);
                }
            }
        }

        public static void Apply(MenuItem item)
        {
            if (Styled.TryGetValue(item, out _))
            {
                return;
            }

            Styled.Add(item, null);

            if (item.ReadLocalValue(FrameworkElement.StyleProperty) is Style own)
            {
                item.Style = Derive(own);
            }
            else
            {
                item.Style = Style;
            }
        }

        private static Style Derive(Style own)
        {
            lock (Derived)
            {
                if (!Derived.TryGetValue(own, out Style derived))
                {
                    derived = new Style(typeof(MenuItem), own);
                    foreach (SetterBase setter in Style.Setters)
                    {
                        derived.Setters.Add(setter);
                    }

                    Derived[own] = derived;
                }

                return derived;
            }
        }
    }
}
