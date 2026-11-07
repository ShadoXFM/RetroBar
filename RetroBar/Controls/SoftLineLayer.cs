using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Controls
{
    /// <summary>
    /// The line and the area of a graph, drawn on a layer of their own. The borders of the app are drawn without anti-aliasing (App.xaml), and
    /// WPF passes that on to everything inside them, with no way back from a child: the layer is drawn to a picture of its own, which is
    /// anti-aliased as it is made, and the picture is shown as it is (one picture pixel to a screen pixel). The labels and the grid, on the graph
    /// under it, are not touched.
    /// </summary>
    internal sealed class SoftLineLayer : FrameworkElement
    {
        private Geometry _area;
        private Brush _areaBrush;
        private Geometry _line;
        private Pen _pen;

        public SoftLineLayer()
        {
            IsHitTestVisible = false;
        }

        public void Set(Geometry area, Brush areaBrush, Geometry line, Pen pen)
        {
            _area = area;
            _areaBrush = areaBrush;
            _line = line;
            _pen = pen;
            InvalidateVisual();
        }

        /// <summary>Draws what the callback draws to an anti-aliased picture of this many device pixels, and returns it.</summary>
        public static BitmapSource Picture(Action<DrawingContext> draw, int pixelWidth, int pixelHeight, double pixelsPerDip)
        {
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                draw(context);
            }

            var picture = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * pixelsPerDip, 96 * pixelsPerDip, PixelFormats.Pbgra32);
            picture.Render(visual);
            picture.Freeze();
            return picture;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if ((_area == null || _areaBrush == null) && (_line == null || _pen == null))
            {
                return;
            }

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int width = (int)Math.Ceiling(ActualWidth * dpi);
            int height = (int)Math.Ceiling(ActualHeight * dpi);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            BitmapSource picture = Picture(c =>
            {
                if (_area != null && _areaBrush != null)
                {
                    c.DrawGeometry(_areaBrush, null, _area);
                }

                if (_line != null && _pen != null)
                {
                    c.DrawGeometry(null, _pen, _line);
                }
            }, width, height, dpi);

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(picture, new Rect(0, 0, width / dpi, height / dpi));
        }
    }
}
