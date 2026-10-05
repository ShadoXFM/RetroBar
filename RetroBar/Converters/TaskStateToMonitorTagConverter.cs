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
    ///
    /// The multi-value overload additionally takes whether the tab's context menu is open: that
    /// state shows the tab with the Active style (see TaskButtonStyleConverter) even though the
    /// window's State isn't Active, so it has to pick the Active tags too - otherwise the icon and
    /// label get the inactive offsets inside the active template, which sits them 1px up and
    /// left of where an active tab puts them.
    /// </summary>
    [ValueConversion(typeof(ApplicationWindow.WindowState), typeof(string))]
    public class TaskStateToMonitorTagConverter : IValueConverter, IMultiValueConverter
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

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (parameter is not string baseTag || string.IsNullOrEmpty(baseTag))
            {
                return null;
            }

            bool contextMenuOpen = values.Length > 1 && values[1] is true;
            bool active = values.Length > 0 && values[0] is ApplicationWindow.WindowState state && state == ApplicationWindow.WindowState.Active;

            return contextMenuOpen || active ? baseTag + "Active" : baseTag;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
