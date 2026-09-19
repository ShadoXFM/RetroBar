using System.ComponentModel;
using System.Globalization;
using ManagedShell.AppBar;

namespace RetroBar.Utilities
{
    /// <summary>
    /// One editable row in the Per-Monitor Adjustments tab: everything MonitorOffsetValue can
    /// hold for a single tagged element on a single monitor. Width/Height/Scale are exposed as
    /// free text (blank = "not set") so a TextBox can bind to them directly with no value
    /// converter; TextRendering/BitmapScaling use the "(default)" sentinel the same way. Every
    /// setter saves immediately, matching how every other setting in this app persists as soon
    /// as it changes.
    /// </summary>
    public class MonitorOffsetRow : INotifyPropertyChanged
    {
        public const string DefaultOption = "(default)";
        public static readonly string[] TextRenderingOptions = { DefaultOption, "Auto", "Aliased", "Grayscale", "ClearType" };
        public static readonly string[] BitmapScalingOptions = { DefaultOption, "NearestNeighbor", "Linear", "HighQuality", "Fant" };

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
                Save();
                Raise(nameof(X));
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
                Save();
                Raise(nameof(Y));
            }
        }

        private string _widthText = "";
        public string WidthText
        {
            get => _widthText;
            set
            {
                if (_widthText == value)
                {
                    return;
                }

                _widthText = value;
                Save();
                Raise(nameof(WidthText));
            }
        }

        private string _heightText = "";
        public string HeightText
        {
            get => _heightText;
            set
            {
                if (_heightText == value)
                {
                    return;
                }

                _heightText = value;
                Save();
                Raise(nameof(HeightText));
            }
        }

        private string _scaleText = "";
        public string ScaleText
        {
            get => _scaleText;
            set
            {
                if (_scaleText == value)
                {
                    return;
                }

                _scaleText = value;
                Save();
                Raise(nameof(ScaleText));
            }
        }

        private string _textRendering = DefaultOption;
        public string TextRendering
        {
            get => _textRendering;
            set
            {
                if (_textRendering == value)
                {
                    return;
                }

                _textRendering = value;
                Save();
                Raise(nameof(TextRendering));
            }
        }

        private string _bitmapScaling = DefaultOption;
        public string BitmapScaling
        {
            get => _bitmapScaling;
            set
            {
                if (_bitmapScaling == value)
                {
                    return;
                }

                _bitmapScaling = value;
                Save();
                Raise(nameof(BitmapScaling));
            }
        }

        public MonitorOffsetRow(string deviceName, string tag, MonitorOffsetValue value)
        {
            DeviceName = deviceName;
            Tag = tag;

            value ??= new MonitorOffsetValue();

            _x = value.X;
            _y = value.Y;
            _widthText = value.Width?.ToString(CultureInfo.InvariantCulture) ?? "";
            _heightText = value.Height?.ToString(CultureInfo.InvariantCulture) ?? "";
            _scaleText = value.Scale?.ToString(CultureInfo.InvariantCulture) ?? "";
            _textRendering = value.TextRendering ?? DefaultOption;
            _bitmapScaling = value.BitmapScaling ?? DefaultOption;
        }

        private void Save()
        {
            Settings.Instance.SetMonitorOffset(DeviceName, Tag, new MonitorOffsetValue
            {
                X = _x,
                Y = _y,
                Width = ParseOrNull(_widthText),
                Height = ParseOrNull(_heightText),
                Scale = ParseOrNull(_scaleText),
                TextRendering = _textRendering == DefaultOption ? null : _textRendering,
                BitmapScaling = _bitmapScaling == DefaultOption ? null : _bitmapScaling
            });
        }

        private static double? ParseOrNull(string text)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : (double?)null;
        }

        private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
