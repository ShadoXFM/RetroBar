using System;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Controls
{
    /// <summary>
    /// A tiny graph of the last readings of a widget (see SystemStats): a line on whole pixels, from 0 up to Maximum, ending at now at the
    /// right. The colour is the clock's own, so it follows the theme.
    /// </summary>
    public class Sparkline : FrameworkElement
    {
        private double[] _values = Array.Empty<double>();

        public double Maximum { get; set; } = 100;

        public void SetValues(double[] values)
        {
            _values = (double[])values.Clone();
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

            // A baseline, so an idle (flat) graph still reads as one.
            dc.DrawRectangle(brush, null, new Rect(0, Snap(height - pixel), width, pixel));

            double max = Math.Max(Maximum, 1e-9);
            double step = width / (_values.Length - 1);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                for (int i = 0; i < _values.Length; i++)
                {
                    double x = Snap(i * step);
                    double y = Snap(height - 2 * pixel - Math.Min(1, Math.Max(0, _values[i] / max)) * (height - 3 * pixel));
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
