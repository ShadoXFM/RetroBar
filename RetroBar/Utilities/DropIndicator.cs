using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The mark that shows where a dragged tab will be dropped: a thin line between two tabs. One adorner per list, kept and moved (not
    /// made again on every mouse move, as the library's own insertion mark is, which made it flash and get in the way of the drag).
    /// </summary>
    public sealed class DropIndicator : Adorner
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemsControl, DropIndicator> Indicators = new();

        private Rect _line;
        private bool _shown;

        private DropIndicator(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
        }

        /// <summary>Shows the line before the tab at this index of the list (after the last one for the count).</summary>
        public static void Show(ItemsControl list, int insertIndex)
        {
            DropIndicator indicator = Get(list);
            if (indicator == null)
            {
                return;
            }

            int count = list.Items.Count;
            if (count == 0)
            {
                return;
            }

            bool after = insertIndex >= count;
            int index = Math.Clamp(after ? count - 1 : insertIndex, 0, count - 1);
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                return;
            }

            Rect bounds;
            try
            {
                bounds = container.TransformToAncestor(list).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return;
            }

            bool vertical = Settings.Instance.Edge is ManagedShell.AppBar.AppBarEdge.Left or ManagedShell.AppBar.AppBarEdge.Right;
            Rect line = vertical
                ? new Rect(bounds.Left, (after ? bounds.Bottom : bounds.Top) - 1, bounds.Width, 2)
                : new Rect((after ? bounds.Right : bounds.Left) - 1, bounds.Top, 2, bounds.Height);

            if (!indicator._shown || indicator._line != line)
            {
                indicator._line = line;
                indicator._shown = true;
                indicator.InvalidateVisual();
            }
        }

        public static void Hide(ItemsControl list)
        {
            if (Indicators.TryGetValue(list, out DropIndicator indicator) && indicator._shown)
            {
                indicator._shown = false;
                indicator.InvalidateVisual();
            }
        }

        private static DropIndicator Get(ItemsControl list)
        {
            if (Indicators.TryGetValue(list, out DropIndicator existing))
            {
                return existing;
            }

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(list);
            if (layer == null)
            {
                return null;
            }

            var indicator = new DropIndicator(list);
            layer.Add(indicator);
            Indicators.Add(list, indicator);
            return indicator;
        }

        protected override void OnRender(DrawingContext context)
        {
            if (!_shown)
            {
                return;
            }

            Brush brush = Application.Current?.TryFindResource("ButtonForeground") as Brush ?? Brushes.White;
            context.DrawRectangle(brush, null, _line);
        }
    }
}
