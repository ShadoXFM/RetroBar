using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Controls
{
    /// <summary>
    /// A tiny graph of the last readings of a widget (the weather, the CPU temperature, the memory): a line on whole pixels, from the lowest
    /// to the highest reading (at least MinSpan apart, so that a steady reading is a flat line and not a noisy one), ending at now at the
    /// right. The colour is the clock's own, so it follows the theme.
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
            double Snap(double value) => Math.Round(value * dpi) / dpi;

            Brush brush = TryFindResource("ClockForeground") as Brush ?? Brushes.Black;
            var pen = new Pen(brush, pixel);
            pen.Freeze();

            // A baseline, so a flat graph still reads as one.
            dc.DrawRectangle(brush, null, new Rect(0, Snap(height - pixel), width, pixel));

            double min = _values.Min();
            double max = _values.Max();
            if (max - min < MinSpan)
            {
                double middle = (min + max) / 2;
                min = middle - MinSpan / 2;
                max = middle + MinSpan / 2;
            }

            double step = width / (_values.Length - 1);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                for (int i = 0; i < _values.Length; i++)
                {
                    double x = Snap(i * step);
                    double y = Snap(height - 2 * pixel - (_values[i] - min) / (max - min) * (height - 3 * pixel));
                    var point = new Point(x, y + pixel / 2);
                    if (i == 0)
                    {
                        ctx.BeginFigure(point, false, false);
                    }
                    else
                    {
                        ctx.LineTo(point, true, false);
                    }
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
