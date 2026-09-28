using System;
using System.Globalization;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// Computes how much width is left for one flexible element after its fixed-width siblings
    /// (and a fixed deduction for spacing ActualWidth alone doesn't capture, e.g. their Margins)
    /// are subtracted from a total available width - clamped to a minimum so it never shrinks
    /// below a usable size. Values[0] is the total available width, every other value is a
    /// sibling's ActualWidth to subtract. ConverterParameter is "extraDeduction,minWidth" (two
    /// comma-separated doubles).
    ///
    /// Used to size SeekSlider off SeekAlbumArtImage's own ActualWidth (see MediaPlayer.xaml) -
    /// the art's own Height, in turn, is sized off its OTHER siblings only (via
    /// SumWidthsConverter, deliberately excluding the slider) specifically so this can be a
    /// one-way, non-circular dependency: the slider reacts to the art's size, the art never
    /// reacts to the slider's.
    /// </summary>
    public class RemainingWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double total = values != null && values.Length > 0 && values[0] is double t ? t : 0;

            double sum = 0;
            if (values != null)
            {
                for (int i = 1; i < values.Length; i++)
                {
                    if (values[i] is double d)
                    {
                        sum += d;
                    }
                }
            }

            double extraDeduction = 0;
            double minWidth = 0;
            if (parameter is string paramText)
            {
                string[] parts = paramText.Split(',');
                if (parts.Length > 0)
                {
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out extraDeduction);
                }
                if (parts.Length > 1)
                {
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minWidth);
                }
            }

            return Math.Max(minWidth, total - sum - extraDeduction);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
