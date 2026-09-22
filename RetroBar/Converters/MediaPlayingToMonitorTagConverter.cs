using System;
using System.Windows.Data;

namespace RetroBar.Converters
{
    /// <summary>
    /// Builds a utilities:MonitorOffset.Tag value from Taskbar.IsMediaPlaying and a base tag
    /// name (the ConverterParameter) - e.g. parameter "TrayBoxBevel" + IsMediaPlaying=true
    /// becomes "TrayBoxBevelPlaying", false leaves the parameter unchanged. See
    /// TaskStateToMonitorTagConverter for the analogous per-state tag pattern.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(string))]
    public class MediaPlayingToMonitorTagConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (parameter is not string baseTag || string.IsNullOrEmpty(baseTag))
            {
                return null;
            }

            return value is true ? baseTag + "Playing" : baseTag;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
