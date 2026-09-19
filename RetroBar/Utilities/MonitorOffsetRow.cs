using System.ComponentModel;
using ManagedShell.AppBar;

namespace RetroBar.Utilities
{
    /// <summary>
    /// One editable row in the Per-Monitor Adjustments tab: a single tagged element's saved
    /// X/Y nudge for one monitor. Edits to X/Y save immediately (see the setters below) rather
    /// than waiting for an explicit save action, matching how every other setting in this app
    /// persists as soon as it changes.
    /// </summary>
    public class MonitorOffsetRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string DeviceName { get; }
        public string Tag { get; }

        private double _x;
        public double X
        {
            get => _x;
            set
            {
                if (_x == value)
                {
                    return;
                }

                _x = value;
                Settings.Instance.SetMonitorOffset(DeviceName, Tag, _x, _y);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(X)));
            }
        }

        private double _y;
        public double Y
        {
            get => _y;
            set
            {
                if (_y == value)
                {
                    return;
                }

                _y = value;
                Settings.Instance.SetMonitorOffset(DeviceName, Tag, _x, _y);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Y)));
            }
        }

        public MonitorOffsetRow(string deviceName, string tag, double x, double y)
        {
            DeviceName = deviceName;
            Tag = tag;
            _x = x;
            _y = y;
        }
    }

    /// <summary>
    /// One entry in the Per-Monitor Adjustments tab's monitor picker.
    /// </summary>
    public class MonitorComboItem
    {
        public string DeviceName { get; }
        public string Label { get; }

        public MonitorComboItem(AppBarScreen screen)
        {
            DeviceName = screen.DeviceName;

            string cleanName = screen.DeviceName?.Replace(@"\\.\", "") ?? DeviceName;
            string primarySuffix = screen.Primary ? " (Primary)" : "";

            Label = $"{cleanName} — {screen.Bounds.Width}x{screen.Bounds.Height}{primarySuffix}";
        }
    }
}
