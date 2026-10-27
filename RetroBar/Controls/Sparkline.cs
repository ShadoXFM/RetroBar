using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Controls
{
    /// <summary>
    /// A tiny graph of the last readings of a widget (the CPU temperature, the memory): a smooth line from the lowest to the highest reading
    /// (at least MinSpan apart, so that a steady reading is a flat line and not a noisy one), ending at now at the right, with a faint area
    /// under it. The readings are averaged into a point for every device pixel and a little more (so the line does not follow every
    /// reading), and the line is a curve through them, drawn anti-aliased. The colour is the clock's own, so it follows the theme.
    /// </summary>
    public class Sparkline : FrameworkElement
    {
        private double[] _values = Array.Empty<double>();

        /// <summary>The least range the graph is drawn over, in the unit of the readings.</summary>
        public double MinSpan { get; set; } = 4;

        public void SetValues(double[] values)
        {
            _values = values;
            InvalidateVisual();
        }

        // The readings in as many parts as the graph has points (the average of each), then each point with its neighbours.
        private static double[] Smooth(double[] values, int points)
        {
            if (values.Length <= points)
            {
                return values;
            }

            var parts = new double[points];
            for (int i = 0; i < points; i++)
            {
                int from = (int)Math.Round((double)i * values.Length / points);
                int to = Math.Max(from + 1, (int)Math.Round((double)(i + 1) * values.Length / points));
                parts[i] = values.Skip(from).Take(to - from).Average();
            }

            var smooth = new double[points];
            for (int i = 0; i < points; i++)
            {
                double sum = parts[i] * 2;
                double weight = 2;
                if (i > 0)
                {
                    sum += parts[i - 1];
                    weight++;
                }

                if (i < points - 1)
                {
                    sum += parts[i + 1];
                    weight++;
                }

                smooth[i] = sum / weight;
            }

            return smooth;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 4 || height < 4 || _values.Length < 2)
            {
                return;
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double pixel = 1 / dpi;

            Brush brush = TryFindResource("ClockForeground") as Brush ?? Brushes.Black;
            Brush faint = brush.Clone();
            faint.Opacity = 0.3;
            faint.Freeze();
            Brush area = brush.Clone();
            area.Opacity = 0.1;
            area.Freeze();

            // The baseline, faint: a flat graph still reads as one.
            double baseline = Math.Round((height - pixel) * dpi) / dpi;
            dc.DrawRectangle(faint, null, new Rect(0, baseline, width, pixel));

            double[] data = Smooth(_values, Math.Max(4, (int)Math.Round(width * dpi / 2)));
            double min = data.Min();
            double max = data.Max();
            if (max - min < MinSpan)
            {
                double middle = (min + max) / 2;
                min = middle - MinSpan / 2;
                max = middle + MinSpan / 2;
            }

            double top = pixel * 1.5;
            double bottom = baseline - pixel;
            double step = width / (data.Length - 1);
            Point[] points = data.Select((v, i) => new Point(i * step, bottom - (v - min) / (max - min) * (bottom - top))).ToArray();

            // A curve through the points (Catmull-Rom, as beziers).
            var line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                for (int i = 0; i < points.Length - 1; i++)
                {
                    Point p0 = points[Math.Max(i - 1, 0)];
                    Point p1 = points[i];
                    Point p2 = points[i + 1];
                    Point p3 = points[Math.Min(i + 2, points.Length - 1)];
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Min(bottom, Math.Max(top, p1.Y + (p2.Y - p0.Y) / 6)));
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Min(bottom, Math.Max(top, p2.Y - (p3.Y - p1.Y) / 6)));
                    ctx.BezierTo(c1, c2, p2, true, true);
                }
            }

            line.Freeze();

            var fill = new StreamGeometry();
            using (StreamGeometryContext ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, baseline), true, true);
                ctx.LineTo(points[0], false, false);
                for (int i = 0; i < points.Length - 1; i++)
                {
                    Point p0 = points[Math.Max(i - 1, 0)];
                    Point p1 = points[i];
                    Point p2 = points[i + 1];
                    Point p3 = points[Math.Min(i + 2, points.Length - 1)];
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Min(bottom, Math.Max(top, p1.Y + (p2.Y - p0.Y) / 6)));
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Min(bottom, Math.Max(top, p2.Y - (p3.Y - p1.Y) / 6)));
                    ctx.BezierTo(c1, c2, p2, false, false);
                }

                ctx.LineTo(new Point(points[^1].X, baseline), false, false);
            }

            fill.Freeze();

            var pen = new Pen(brush, pixel * 1.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
    }
}
