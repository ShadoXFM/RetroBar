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
        /// <summary>The classic 8x7 close glyph, as on the preview's close button.</summary>
        public const string Close = "XX....XX|.XX..XX.|..XXXX..|...XX...|..XXXX..|.XX..XX.|XX....XX";

        /// <summary>A check mark of the same 8x7 size.</summary>
        public const string Check = ".......X|......XX|.....XX.|X...XX..|XX.XX...|.XXX....|..X.....";

        public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
            nameof(Rows), typeof(string), typeof(PixelGlyph),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        // The text color of whatever it is in (a menu item's, which turns white when highlighted and grey when disabled), unless
        // given one.
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

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        private string[] Cells => (Rows ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);

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
