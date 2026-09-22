using System;
using System.Globalization;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// Subtracts a fixed amount (the ConverterParameter) from a bound double, clamped to 0 -
    /// e.g. binding an element's Width to another element's ActualWidth minus a margin, without
    /// needing that margin to also grow the container the way an actual Margin would (see
    /// SeekAlbumArtImage in MediaPlayer.xaml, sized to the seek popup's controls row width
    /// this way so it doesn't force the row - and so the whole popup - wider to fit around it).
    /// </summary>
    [ValueConversion(typeof(double), typeof(double))]
    public class SubtractDoubleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double amount
                || parameter is not string marginText
                || !double.TryParse(marginText, NumberStyles.Float, CultureInfo.InvariantCulture, out double margin))
            {
                return value;
            }

            return Math.Max(0, amount - margin);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
