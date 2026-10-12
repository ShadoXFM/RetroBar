using System;
using System.Globalization;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// Builds a utilities:MonitorOffset.Tag value from Taskbar.IsMediaPlaying, IsWeatherShown and IsCpuTempShown (in that
    /// order) and a base tag name (the ConverterParameter): the base name while any of them is on, the base name + "Alone"
    /// when none is, so what is tuned for the tray box with nothing but the tray icons and the clock in it leaves the others
    /// as they are.
    /// </summary>
    public class TrayAloneToMonitorTagConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter is not string baseTag || string.IsNullOrEmpty(baseTag))
            {
                return null;
            }

            foreach (object value in values)
            {
                if (value is true)
                {
                    return baseTag;
                }
            }

            return baseTag + "Alone";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
