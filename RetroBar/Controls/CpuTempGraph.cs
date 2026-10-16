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
    /// A graph of the CPU temperature over the last five minutes (see CpuTemperatureHistory): the temperature up the side, in the unit of
    /// the weather's setting, and along the bottom the time, at every minute of the clock (8:00, 8:01, 8:02...), ending at now at the
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
        private const int TickMinutes = 1;

        private CpuTemperatureHistory.Sample[] _samples = Array.Empty<CpuTemperatureHistory.Sample>();

        public string CollectingText { get; set; } = "…";

        /// <summary>Draws the readings of the last five minutes again.</summary>
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
            // One device pixel thick, and on whole pixels whatever the DPI: at 125% a line of one DIP, or one at half a DIP, is smeared over two.
            var gridPen = new Pen(faint, 1 / dpi);
            gridPen.Freeze();

            double right = width - Pad;
            double top = FontSize / 2 + 1;
            double bottom = height - Pad - FontSize - 4;

            if (_samples.Length < 2)
            {
                FormattedText waiting = Text(CollectingText);
                dc.DrawText(waiting, new Point(Snap((width - waiting.Width) / 2), Snap((height - waiting.Height) / 2)));
                return;
            }

            // The range of the side: the readings with a little room, from a line to a line, so that the highest line is at the very top of
            // the graph (and the lowest at its bottom), with at least two spaces between lines.
            double min = _samples.Min(s => InUnit(s.Celsius));
            double max = _samples.Max(s => InUnit(s.Celsius));
            double step = (max - min + 2) <= 20 ? 5 : (max - min + 2) <= 40 ? 10 : 20;
            double low = Math.Floor((min - 1) / step) * step;
            double high = Math.Ceiling((max + 1) / step) * step;
            if (high - low < 2 * step)
            {
                high = low + 2 * step;
            }

            double Y(double value) => bottom - (value - low) / (high - low) * (bottom - top);

            // Lines across at every multiple of the step. The graph starts after the widest of their labels.
            var marks = new List<(double Value, FormattedText Label)>();
            for (double value = Math.Ceiling(low / step) * step; value <= high; value += step)
            {
                marks.Add((value, Text($"{value:0}°")));
            }

            double widest = marks.Count > 0 ? marks.Max(m => m.Label.Width) : 0;
            double left = Math.Ceiling((LabelMargin + widest + LabelGap) * dpi) / dpi;
            foreach ((double value, FormattedText label) in marks)
            {
                double y = (Math.Round(Y(value) * dpi) + 0.5) / dpi;
                dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
                dc.DrawText(label, new Point(Snap(LabelMargin + widest - label.Width), Snap(y - label.Height / 2)));
            }

            // The span is a whole number of spaces, each a whole number of device pixels (a mark is at the end of each), so every mark is the
            // same whole number of pixels from the next and the labels move together, not each by its own rounding.
            DateTime now = DateTime.UtcNow;
            int spaces = (int)Math.Round(CpuTemperatureHistory.Span.TotalMinutes / TickMinutes);
            int spacePixels = Math.Max(1, (int)Math.Floor((right - left) * dpi / spaces));
            double plot = spacePixels * spaces / dpi;
            double plotLeft = Math.Round(left * dpi) / dpi;
            double X(DateTime utc) => plotLeft + (1 - (now - utc).TotalSeconds / CpuTemperatureHistory.Span.TotalSeconds) * plot;

            // The time, at the minutes of the clock that are in the span (and the one before, whose label is still partly in, and the one
            // next, whose label comes in from the right edge before its time): the labels scroll with the time and are cut at the left edge
            // of the graph and at its right edge (now), not left out once they do not fit.
            DateTime nowLocal = now.ToLocalTime();
            DateTime tick = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, nowLocal.Minute / TickMinutes * TickMinutes, 0, DateTimeKind.Local)
                .AddMinutes(TickMinutes);
            double labelHalf = Text("00:00").Width / 2;
            dc.PushClip(new RectangleGeometry(new Rect(plotLeft, bottom, plot, height - bottom)));
            for (; (nowLocal - tick) <= CpuTemperatureHistory.Span + TimeSpan.FromMinutes(TickMinutes); tick = tick.AddMinutes(-TickMinutes))
            {
                double x = (Math.Round(X(tick.ToUniversalTime()) * dpi) + 0.5) / dpi;
                dc.DrawLine(gridPen, new Point(x, bottom), new Point(x, bottom + 3));
                dc.DrawText(Text(TimeText(tick)), new Point(Snap(x - labelHalf), Snap(bottom + 4)));
            }

            dc.Pop();

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
