using System;
using System.Globalization;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// Sums several bound ActualWidths plus a fixed extra amount (ConverterParameter) - used to
    /// combine a row's fixed-width elements into one reference width for sizing something else
    /// off of (see SeekAlbumArtImage.Height in MediaPlayer.xaml, which deliberately excludes
    /// SeekSlider from this sum since the slider's own width is derived FROM the album art's
    /// size via RemainingWidthConverter - including it here too would make the two bindings
    /// circular).
    /// </summary>
    [ValueConversion(typeof(double[]), typeof(double))]
    public class SumWidthsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double sum = 0;

            if (values != null)
            {
                foreach (object value in values)
                {
                    if (value is double d)
                    {
                        sum += d;
                    }
                }
            }

            if (parameter is string extraText
                && double.TryParse(extraText, NumberStyles.Float, CultureInfo.InvariantCulture, out double extra))
            {
                sum += extra;
            }

            return Math.Max(0, sum);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
