using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using RetroBar.Utilities;
using System;
using System.Windows.Data;

namespace RetroBar.Converters
{
    [ValueConversion(typeof(ApplicationWindow), typeof(string))]
    public class TaskLabelConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (!(values[0] is string title &&
                values[1] is int progressValue &&
                values[2] is NativeMethods.TBPFLAG progressState))
            {
                return Binding.DoNothing;
            }

            // Optionally the program's name rather than the title the window has at the moment.
            if (values.Length > 5 && values[4] is bool useProgramName && useProgramName &&
                values[5] is ApplicationWindow window)
            {
                title = ProgramName.Get(window) ?? title;
            }

            // A tab standing for several windows leads with how many: "(3) Title".
            if (values.Length > 3 && values[3] is int windowCount && windowCount > 1)
            {
                title = $"({windowCount}) {title}";
            }

            if (progressState == NativeMethods.TBPFLAG.TBPF_NOPROGRESS ||
                progressState == NativeMethods.TBPFLAG.TBPF_INDETERMINATE ||
                progressValue < 0)
            {
                return title;
            }

            if (title.Contains("%"))
            {
                // Window title may already contain progress percentage
                return title;
            }

            return $"[{Math.Floor(progressValue / 65534.0 * 100)}%] {title}";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
