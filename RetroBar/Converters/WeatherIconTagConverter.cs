using System;
using System.Globalization;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// The utilities:MonitorOffset.Tag of the weather icon on the taskbar: the base tag (the ConverterParameter, "WeatherIcon")
    /// with "MoonSm" added while the small hand-drawn moon is the icon (values[1]; it is drawn at its own size, so it has its
    /// own adjustments), and then "NoCpuTemp" added while the CPU temperature isn't shown (values[0], as for
    /// CpuTempShownToMonitorTagConverter): WeatherIcon, WeatherIconNoCpuTemp, WeatherIconMoonSm, WeatherIconMoonSmNoCpuTemp.
    /// </summary>
    public class WeatherIconTagConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter is not string baseTag || string.IsNullOrEmpty(baseTag))
            {
                return null;
            }

            bool cpuTempShown = values.Length < 1 || values[0] is not false;
            bool smallMoon = values.Length > 1 && values[1] is true;

            return baseTag + (smallMoon ? "MoonSm" : "") + (cpuTempShown ? "" : "NoCpuTemp");
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
