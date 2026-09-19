using System;
using System.Windows.Data;
using ManagedShell.WindowsTasks;

namespace RetroBar.Converters
{
    /// <summary>
    /// Builds a utilities:MonitorOffset.Tag value from a task's State and a base tag name (the
    /// ConverterParameter) - e.g. parameter "TaskIcon" + State=Active becomes "TaskIconActive",
    /// any other state leaves the parameter unchanged. This lets the active/focused state have
    /// its own independent per-monitor adjustment (its border template nests one layer deeper,
    /// so on a non-100%-scale monitor layout rounding can land it a device pixel off from the
    /// resting/pressing state even when their un-rounded values are identical) while leaving the
    /// element's own Style bound via DynamicResource, since a Style.Trigger that swaps
    /// MonitorOffset.Tag would need a StaticResource/BasedOn Style, which stops following a live
    /// theme switch for any tag - like TaskLabel - that some theme overrides.
    /// </summary>
    [ValueConversion(typeof(ApplicationWindow.WindowState), typeof(string))]
    public class TaskStateToMonitorTagConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (parameter is not string baseTag || string.IsNullOrEmpty(baseTag))
            {
                return null;
            }

            if (value is ApplicationWindow.WindowState state && state == ApplicationWindow.WindowState.Active)
            {
                return baseTag + "Active";
            }

            return baseTag;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
