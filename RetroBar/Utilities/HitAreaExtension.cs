using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ManagedShell.AppBar;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Stretches a taskbar button's clickable area down to the bottom edge of the screen, without changing how it
    /// looks or how it is laid out.
    ///
    /// The buttons are a little shorter than the taskbar is tall (a per-monitor height override, or a taskbar made a
    /// pixel taller), which leaves a strip under them that does nothing - and the strip is exactly where the pointer
    /// ends up when it is pushed against the bottom of the screen, the easiest place to aim at.
    ///
    /// A button's template starts with a transparent border (that is what makes its padding clickable). This lets that
    /// border reach below the button by the size of the strip - a negative bottom margin - and adds the same amount
    /// to its bottom padding, so what is drawn inside it doesn't move or change size, and the button's own size and
    /// layout aren't touched at all. The strip is then simply part of the button: hover, press, click and the context
    /// menu all work there like anywhere else on it.
    ///
    /// How much room there is under the button is taken from the first thing around it that reaches lower (the task
    /// list's scroll viewer, the Start button's own control), in whole device pixels.
    ///
    /// Usage: <ToggleButton utilities:HitAreaExtension.Enabled="True" ... />
    /// </summary>
    public static class HitAreaExtension
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(HitAreaExtension), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

        private sealed class State
        {
            // The template's outer border, and its margin and padding as the template wrote them.
            public Border Root;
            public Thickness BaseMargin;
            public Thickness BasePadding;
            public double Applied;
        }

        private static readonly ConditionalWeakTable<ButtonBase, State> States = new();

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ButtonBase button || e.NewValue is not true)
            {
                return;
            }

            // A button's template - and with it the border that gets the margin and padding - is replaced whenever
            // its style changes (a tab turning active, a theme switch), so this checks on every layout pass; the work
            // is a few comparisons unless something actually changed.
            button.LayoutUpdated += (s, args) => Update(button);
            button.Loaded += (s, args) => Update(button);
        }

        // How far below the button (DIPs) the next thing around it reaches, 0 if nothing does.
        private static double RoomBelow(ButtonBase button)
        {
            double buttonBottom = button.ActualHeight;

            for (DependencyObject parent = VisualTreeHelper.GetParent(button); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is Window)
                {
                    break;
                }

                if (parent is FrameworkElement element)
                {
                    // Where the element ends, in the button's own coordinates.
                    double bottom = element.TransformToVisual(button).Transform(new Point(0, element.ActualHeight)).Y;

                    if (bottom - buttonBottom > 0.2)
                    {
                        return bottom - buttonBottom;
                    }

                    if (element is ScrollViewer)
                    {
                        break;
                    }
                }
            }

            return 0;
        }

        private static void Update(ButtonBase button)
        {
            if (!button.IsLoaded)
            {
                return;
            }

            State state = States.GetOrCreateValue(button);
            Border root = VisualTreeHelper.GetChildrenCount(button) > 0 ? VisualTreeHelper.GetChild(button, 0) as Border : null;

            if (!ReferenceEquals(state.Root, root))
            {
                // A replaced template has a fresh border, with nothing added.
                state.Root = root;
                state.BaseMargin = root?.Margin ?? default;
                state.BasePadding = root?.Padding ?? default;
                state.Applied = 0;
            }

            if (root == null)
            {
                return;
            }

            double gap = 0;

            // Only for a taskbar along the bottom edge of the screen.
            if (Settings.Instance.Edge == AppBarEdge.Bottom)
            {
                try
                {
                    double dpi = VisualTreeHelper.GetDpi(button).DpiScaleY;
                    gap = Math.Floor(RoomBelow(button) * dpi + 0.01) / dpi;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }

            if (gap < 0.2)
            {
                gap = 0;
            }

            if (Math.Abs(gap - state.Applied) < 0.01)
            {
                return;
            }

            root.Margin = new Thickness(state.BaseMargin.Left, state.BaseMargin.Top, state.BaseMargin.Right, state.BaseMargin.Bottom - gap);
            root.Padding = new Thickness(state.BasePadding.Left, state.BasePadding.Top, state.BasePadding.Right, state.BasePadding.Bottom + gap);
            state.Applied = gap;
        }
    }
}
