using System;
using System.Windows.Data;

namespace RetroBar.Converters
{
    // Computes an individual task button width so a full row fills the taskbar exactly.
    // Every button gets the base (floored) width; the space lost to flooring is spread
    // evenly across the row's columns, each affected column getting one extra pixelStep
    // (not a flat 1 DIP - see TaskList.SetTaskButtonWidth for why the two aren't the same
    // on a monitor that isn't at 100% scale).
    //
    // Distribution is keyed on the column (index % buttonsPerRow) so widths line up in
    // columns across rows, and a partial last row keeps the same per-column widths.
    //
    // values[0] = item index (ItemsControl.AlternationIndex)
    // values[1] = base button width (double)
    // values[2] = number of columns that get an extra pixelStep (int)
    // values[3] = buttons per row (int)
    // values[4] = one physical device pixel, in DIPs (double)
    //
    // If the index isn't available the base width is returned, so the layout simply falls
    // back to uniform widths rather than breaking.
    public class TaskButtonWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length < 4 || !(values[1] is double baseWidth))
            {
                return Binding.DoNothing;
            }

            // values[5], when there is one: room the item leaves out of its slot (a launcher's gap), taken off its width.
            if (values.Length > 5 && values[5] is double gap)
            {
                baseWidth = Math.Max(1, baseWidth - gap);
            }

            if (values[0] is int index
                && values[2] is int extraCount
                && values[3] is int buttonsPerRow
                && extraCount > 0
                && buttonsPerRow > 0)
            {
                int column = index % buttonsPerRow;

                // Evenly spaced "+1"s: 1 when this column falls on a distribution boundary.
                int extraForColumn = (column + 1) * extraCount / buttonsPerRow - column * extraCount / buttonsPerRow;

                double pixelStep = values.Length > 4 && values[4] is double step ? step : 1d;

                return baseWidth + extraForColumn * pixelStep;
            }

            return baseWidth;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
