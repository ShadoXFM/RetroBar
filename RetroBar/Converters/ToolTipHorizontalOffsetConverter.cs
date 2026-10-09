using ManagedShell.AppBar;
using RetroBar.Utilities;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace RetroBar.Converters
{
    [ValueConversion(typeof(double), typeof(double))]
    public class ToolTipHorizontalOffsetConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.FirstOrDefault(v => v == DependencyProperty.UnsetValue) != null)
            {
                return double.NaN;
            }

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                // Closer to the tab by the margin the preview frame has for its shadow.
                return Settings.Instance.Edge == AppBarEdge.Left ? -RetroBar.Controls.TaskButton.PreviewShadowRoom : RetroBar.Controls.TaskButton.PreviewShadowRoom;
            }
            else
            {
                double placementTargetWidth = (double)values[0];
                double toolTipWidth = (double)values[1];
                return (placementTargetWidth / 2.0) - (toolTipWidth / 2.0 / (double)values[2]);
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
