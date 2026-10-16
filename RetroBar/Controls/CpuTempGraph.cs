using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Controls
{
    /// <summary>
    /// A graph of the CPU temperature over the last fifteen minutes (see CpuTemperatureHistory): the temperature up the side, in the unit of
    /// the weather's setting, and along the bottom the time, at every third minute of the clock (8:00, 8:03, 8:06...), ending at now at the
    /// right. Its text is Tahoma bold, hinted to the pixels (not smoothed).
    /// </summary>
    public class CpuTempGraph : FrameworkElement
    {
        // The temperatures at the left start this far from the edge (the header's text is as far), and have this much room before the graph.
        private const double LabelMargin = 2;
        private const double LabelGap = 4;
        private const double Pad = 4;
        private const double FontSize = 10;

        // A mark and a label every this many minutes of the clock.
        private const int TickMinutes = 3;

        private CpuTemperatureHistory.Sample[] _samples = Array.Empty<CpuTemperatureHistory.Sample>();

        public string CollectingText { get; set; } = "…";

        /// <summary>Draws the readings of the last fifteen minutes again.</summary>
        public void Refresh()
        {
            _samples = CpuTemperatureHistory.Last();
            InvalidateVisual();
        }

        private static double InUnit(double celsius) => Settings.Instance.WeatherUseFahrenheit ? celsius * 9 / 5 + 32 : celsius;

        // "8:03", or "08:03" where the clock has the hours in two digits.
        private static string TimeText(DateTime local)
        {
            string pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH:mm" : "h:mm";
            return local.ToString(pattern, CultureInfo.CurrentCulture);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 60 || height <= 2 * Pad + 2 * FontSize)
            {
                return;
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double Snap(double value) => Math.Round(value * dpi) / dpi;

            Brush foreground = TryFindResource("ButtonForeground") as Brush ?? Brushes.Black;
            var typeface = new Typeface(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            FormattedText Text(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground,
                new NumberSubstitution(), TextFormattingMode.Display, dpi);

            Brush faint = foreground.Clone();
            faint.Opacity = 0.3;
            var gridPen = new Pen(faint, 1);
            gridPen.Freeze();

            double right = width - Pad;
            double top = Pad + FontSize / 2;
            double bottom = height - Pad - FontSize - 4;

            if (_samples.Length < 2)
            {
                FormattedText waiting = Text(CollectingText);
                dc.DrawText(waiting, new Point(Snap((width - waiting.Width) / 2), Snap((height - waiting.Height) / 2)));
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

            // Lines across at every multiple of the step, which makes about four of them. The graph starts after the widest of their labels.
            double step = (high - low) <= 20 ? 5 : (high - low) <= 40 ? 10 : 20;
            var marks = new List<(double Value, FormattedText Label)>();
            for (double value = Math.Ceiling(low / step) * step; value <= high; value += step)
            {
                marks.Add((value, Text($"{value:0}°")));
            }

            double widest = marks.Count > 0 ? marks.Max(m => m.Label.Width) : 0;
            double left = Math.Ceiling((LabelMargin + widest + LabelGap) * dpi) / dpi;
            foreach ((double value, FormattedText label) in marks)
            {
                double y = Math.Round(Y(value)) + 0.5;
                dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
                dc.DrawText(label, new Point(Snap(LabelMargin + widest - label.Width), Snap(y - label.Height / 2)));
            }

            // Five whole device pixels' spaces for the fifteen minutes (a mark is at every third), so every mark is the same whole number of
            // pixels from the next and the labels move together, not each by its own rounding.
            DateTime now = DateTime.UtcNow;
            int spacePixels = Math.Max(1, (int)Math.Floor((right - left) * dpi / 5));
            double plot = spacePixels * 5 / dpi;
            double plotLeft = Math.Round(left * dpi) / dpi;
            double X(DateTime utc) => plotLeft + (1 - (now - utc).TotalSeconds / CpuTemperatureHistory.Span.TotalSeconds) * plot;

            // The time, at the third minutes of the clock that are in the span.
            DateTime nowLocal = now.ToLocalTime();
            DateTime tick = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, nowLocal.Minute / TickMinutes * TickMinutes, 0, DateTimeKind.Local);
            double labelHalf = Text("00:00").Width / 2;
            for (; (nowLocal - tick) <= CpuTemperatureHistory.Span; tick = tick.AddMinutes(-TickMinutes))
            {
                double x = (Math.Round(X(tick.ToUniversalTime()) * dpi) + 0.5) / dpi;
                if (x < plotLeft)
                {
                    continue;
                }

                FormattedText label = Text(TimeText(tick));
                dc.DrawLine(gridPen, new Point(x, bottom), new Point(x, bottom + 3));
                dc.DrawText(label, new Point(Math.Min(Snap(x - labelHalf), width - label.Width), Snap(bottom + 4)));
            }

            var line = new StreamGeometry();
            using (StreamGeometryContext context = line.Open())
            {
                context.BeginFigure(new Point(Math.Max(plotLeft, X(_samples[0].Utc)), Y(InUnit(_samples[0].Celsius))), false, false);
                foreach (CpuTemperatureHistory.Sample sample in _samples.Skip(1))
                {
                    context.LineTo(new Point(Math.Max(plotLeft, X(sample.Utc)), Y(InUnit(sample.Celsius))), true, false);
                }
            }

            line.Freeze();

            // The area under the line, faint, then the line.
            var area = new StreamGeometry();
            using (StreamGeometryContext context = area.Open())
            {
                context.BeginFigure(new Point(Math.Max(plotLeft, X(_samples[0].Utc)), bottom), true, true);
                foreach (CpuTemperatureHistory.Sample sample in _samples)
                {
                    context.LineTo(new Point(Math.Max(plotLeft, X(sample.Utc)), Y(InUnit(sample.Celsius))), false, false);
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
