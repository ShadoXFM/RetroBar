using System;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// Builds a utilities:MonitorOffset.Tag value from Taskbar.IsCpuTempShown and a base tag name
    /// (the ConverterParameter) - e.g. parameter "WeatherTemp" + IsCpuTempShown=false becomes
    /// "WeatherTempNoCpuTemp", true leaves the parameter unchanged. Suffixes on false (not true,
    /// unlike MediaPlayingToMonitorTagConverter) since the existing base tags already describe
    /// today's default layout - CPU temp shown - and the new, not-yet-configured case is CPU temp
    /// hidden. See TaskStateToMonitorTagConverter for the analogous per-state tag pattern.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(string))]
    public class CpuTempShownToMonitorTagConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (parameter is not string baseTag || string.IsNullOrEmpty(baseTag))
            {
                return null;
            }

            return value is false ? baseTag + "NoCpuTemp" : baseTag;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
