using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Makes a button's picture (the Background of a Border in its template) fade into the next one when the mouse enters or
    /// leaves it, is pressed or is checked, instead of being swapped at once (Vista's Start orb glows in and out). The Border's
    /// child is a Panel whose first child is a Border (the ghost): when the Background changes because of such a change, the
    /// ghost takes the previous picture and fades away over the new one.
    ///
    ///   <Border utilities:BackgroundCrossfade.Seconds="0.25"> <Grid> <Border IsHitTestVisible="False" /> ... </Grid> </Border>
    /// </summary>
    public static class BackgroundCrossfade
    {
        public static readonly DependencyProperty SecondsProperty = DependencyProperty.RegisterAttached(
            "Seconds", typeof(double), typeof(BackgroundCrossfade), new PropertyMetadata(0.0, OnSecondsChanged));

        public static double GetSeconds(DependencyObject obj) => (double)obj.GetValue(SecondsProperty);
        public static void SetSeconds(DependencyObject obj, double value) => obj.SetValue(SecondsProperty, value);

        private static void OnSecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border border || e.NewValue is not double seconds || seconds <= 0)
            {
                return;
            }

            Brush previous = border.Background;
            (bool, bool, bool) lastState = State(border);

            DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border)).AddValueChanged(border, (s, args) =>
            {
                Brush before = previous;
                previous = border.Background;

                (bool, bool, bool) state = State(border);
                bool stateChanged = state != lastState;
                lastState = state;

                // The same picture again (a button that is pressed and then checked shows the one image under two brushes): the fade
                // that is on its way goes on, and is not started again from a picture that looks like the new one.
                if (SameLook(before, border.Background))
                {
                    return;
                }

                if (!stateChanged || !border.IsLoaded || before == null ||
                    border.Child is not Panel { Children.Count: > 0 } panel || panel.Children[0] is not Border ghost)
                {
                    return;
                }

                ghost.Background = before;
                ghost.CornerRadius = border.CornerRadius;
                ghost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            });
        }

        private static bool SameLook(Brush a, Brush b)
        {
            return a is ImageBrush ia && b is ImageBrush ib && Equals(ia.ImageSource, ib.ImageSource) && ia.Viewbox == ib.Viewbox && ia.ViewboxUnits == ib.ViewboxUnits;
        }

        private static (bool, bool, bool) State(Border border)
        {
            return border.TemplatedParent is System.Windows.Controls.Primitives.ToggleButton t ? (t.IsMouseOver, t.IsPressed, t.IsChecked == true) : (false, false, false);
        }
    }
}
