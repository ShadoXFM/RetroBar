using RetroBar.Utilities;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Controls
{
    /// <summary>
    /// A graph of the CPU temperature over the last fifteen minutes (see CpuTemperatureHistory): the time along the bottom, ending at now at the
    /// right, the temperature up the side, in the unit of the weather's setting.
    /// </summary>
    public class CpuTempGraph : FrameworkElement
    {
        private const double LabelWidth = 30;
        private const double Pad = 4;

        private CpuTemperatureHistory.Sample[] _samples = Array.Empty<CpuTemperatureHistory.Sample>();

        public string CollectingText { get; set; } = "…";

        /// <summary>Draws the readings of the last fifteen minutes again.</summary>
        public void Refresh()
        {
            _samples = CpuTemperatureHistory.Last();
            InvalidateVisual();
        }

        private static double InUnit(double celsius) => Settings.Instance.WeatherUseFahrenheit ? celsius * 9 / 5 + 32 : celsius;

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= LabelWidth + 2 * Pad || height <= 2 * Pad)
            {
                return;
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush foreground = TryFindResource("ButtonForeground") as Brush ?? Brushes.Black;
            var typeface = new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double fontSize = 10;

            FormattedText Text(string text, Brush brush) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, brush, dpi);

            Brush faint = foreground.Clone();
            faint.Opacity = 0.3;
            var gridPen = new Pen(faint, 1);
            gridPen.Freeze();

            double left = LabelWidth;
            double right = width - Pad;
            double top = Pad + fontSize / 2;
            double bottom = height - Pad - fontSize / 2;

            if (_samples.Length < 2)
            {
                FormattedText waiting = Text(CollectingText, foreground);
                dc.DrawText(waiting, new Point((width - waiting.Width) / 2, (height - waiting.Height) / 2));
                return;
            }

            // The range of the side: the readings with a little room, at least ten degrees, between multiples of five.
            double min = _samples.Min(s => InUnit(s.Celsius));
            double max = _samples.Max(s => InUnit(s.Celsius));
            double low = Math.Floor((min - 1) / 5) * 5;
            double high = Math.Ceiling((max + 1) / 5) * 5;
            if (high - low < 10)
            {
                high = low + 10;
            }

            double Y(double value) => bottom - (value - low) / (high - low) * (bottom - top);

            // Lines across at every multiple of the step, which makes about four of them.
            double step = (high - low) <= 20 ? 5 : (high - low) <= 40 ? 10 : 20;
            for (double value = Math.Ceiling(low / step) * step; value <= high; value += step)
            {
                double y = Math.Round(Y(value)) + 0.5;
                dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
                FormattedText label = Text($"{value:0}°", foreground);
                dc.DrawText(label, new Point(left - 4 - label.Width, y - label.Height / 2));
            }

            DateTime now = DateTime.UtcNow;
            double X(DateTime utc) => left + (1 - (now - utc).TotalSeconds / CpuTemperatureHistory.Span.TotalSeconds) * (right - left);

            var line = new StreamGeometry();
            using (StreamGeometryContext context = line.Open())
            {
                context.BeginFigure(new Point(Math.Max(left, X(_samples[0].Utc)), Y(InUnit(_samples[0].Celsius))), false, false);
                foreach (CpuTemperatureHistory.Sample sample in _samples.Skip(1))
                {
                    context.LineTo(new Point(Math.Max(left, X(sample.Utc)), Y(InUnit(sample.Celsius))), true, false);
                }
            }

            line.Freeze();

            // The area under the line, faint, then the line.
            var area = new StreamGeometry();
            using (StreamGeometryContext context = area.Open())
            {
                double x0 = Math.Max(left, X(_samples[0].Utc));
                context.BeginFigure(new Point(x0, bottom), true, true);
                foreach (CpuTemperatureHistory.Sample sample in _samples)
                {
                    context.LineTo(new Point(Math.Max(left, X(sample.Utc)), Y(InUnit(sample.Celsius))), false, false);
                }

                context.LineTo(new Point(X(_samples[^1].Utc), bottom), false, false);
            }

            area.Freeze();
            Brush fill = foreground.Clone();
            fill.Opacity = 0.15;
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, new Pen(foreground, 1.5) { LineJoin = PenLineJoin.Round }, line);
        }
    }
}
