using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Moves what is in a button by one device pixel down and to the right while the button is pressed, whatever the DPI. A margin in the
    /// template (1 DIP) is 1.25 pixels at 125%, which is drawn as 1 or 2 pixels, a button at a time: this is exactly one.
    /// Set it on the element in the button's template that holds the glyph:
    ///   <ContentPresenter utilities:PressShift.Enabled="True" ... />
    /// </summary>
    public static class PressShift
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(PressShift), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

        private static readonly DependencyPropertyDescriptor Pressed =
            DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
            {
                return;
            }

            element.Loaded -= Element_OnLoaded;
            element.Unloaded -= Element_OnUnloaded;
            if ((bool)e.NewValue)
            {
                element.Loaded += Element_OnLoaded;
                element.Unloaded += Element_OnUnloaded;
                if (element.IsLoaded)
                {
                    Element_OnLoaded(element, null);
                }
            }
        }

        // The handler each element has put on its button (to take it off again).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, EventHandler> Handlers = new();

        private static void Element_OnLoaded(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            if (element.TemplatedParent is not ButtonBase button)
            {
                return;
            }

            if (Handlers.TryGetValue(element, out EventHandler old))
            {
                Pressed.RemoveValueChanged(button, old);
                Handlers.Remove(element);
            }

            EventHandler handler = (s, args) => Apply(element, button);
            Handlers.Add(element, handler);
            Pressed.AddValueChanged(button, handler);
            Apply(element, button);
        }

        private static void Element_OnUnloaded(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            if (element.TemplatedParent is ButtonBase button && Handlers.TryGetValue(element, out EventHandler handler))
            {
                Pressed.RemoveValueChanged(button, handler);
                Handlers.Remove(element);
            }
        }

        private static void Apply(FrameworkElement element, ButtonBase button)
        {
            if (!button.IsPressed)
            {
                if (element.RenderTransform is TranslateTransform)
                {
                    element.ClearValue(UIElement.RenderTransformProperty);
                }

                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(element);
            element.RenderTransform = new TranslateTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
        }
    }
}
