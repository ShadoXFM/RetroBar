using System;
using System.Globalization;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>A number multiplied by the converter parameter (a font size made a little bigger, say).</summary>
    [ValueConversion(typeof(double), typeof(double))]
    public class ScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double factor = double.Parse(parameter as string ?? "1", CultureInfo.InvariantCulture);
            return value is double number ? number * factor : value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
