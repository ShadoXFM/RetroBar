using System;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// A small glyph drawn as whole device pixels: one cell of <see cref="Rows"/> ('X' filled, anything else empty, rows
    /// separated by '|') is exactly one pixel on every monitor, so it is the same crisp shape at 100%, 125% and so on (a font's
    /// glyph or a stroked line smooths into a lopsided one). The preview's close button draws its X the same way.
    /// </summary>
    public class PixelGlyph : FrameworkElement
    {
        public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
            nameof(Rows), typeof(string), typeof(PixelGlyph),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        // The text color of whatever it is in (a menu item's, which turns white when highlighted and grey when disabled), unless
        // given one.
        /// <summary>The cells to draw instead on a monitor scaled to 125% or more, where the same number of pixels is a smaller
        /// glyph.</summary>
        public static readonly DependencyProperty ScaledRowsProperty = DependencyProperty.Register(
            nameof(ScaledRows), typeof(string), typeof(PixelGlyph),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
            typeof(PixelGlyph),
            new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

        public PixelGlyph()
        {
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        public string Rows
        {
            get => (string)GetValue(RowsProperty);
            set => SetValue(RowsProperty, value);
        }

        public string ScaledRows
        {
            get => (string)GetValue(ScaledRowsProperty);
            set => SetValue(ScaledRowsProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        private string[] Cells
        {
            get
            {
                bool scaled = VisualTreeHelper.GetDpi(this).DpiScaleX >= 1.2 && !string.IsNullOrEmpty(ScaledRows);
                return (scaled ? ScaledRows : Rows ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            string[] cells = Cells;
            int columns = 0;
            foreach (string row in cells)
            {
                columns = Math.Max(columns, row.Length);
            }

            return new Size(columns / dpi.DpiScaleX, cells.Length / dpi.DpiScaleY);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double pixelWidth = 1 / dpi.DpiScaleX;
            double pixelHeight = 1 / dpi.DpiScaleY;
            string[] cells = Cells;

            for (int y = 0; y < cells.Length; y++)
            {
                for (int x = 0; x < cells[y].Length; x++)
                {
                    if (cells[y][x] == 'X')
                    {
                        drawingContext.DrawRectangle(Foreground, null, new Rect(x * pixelWidth, y * pixelHeight, pixelWidth, pixelHeight));
                    }
                }
            }
        }
    }
}
