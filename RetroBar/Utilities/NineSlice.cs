using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Draws a picture the way a visual style (msstyles) draws its parts: the corners are kept as they are, the edges and the middle
    /// are stretched. The picture can hold several states one over the other (a button's normal, hot, pressed ... images), of which
    /// State picks one.
    ///
    ///   <utilities:NineSlice Source="{StaticResource Image}" Margins="5,15,5,13" StateCount="6" State="1" />
    ///
    /// Margins are in pixels of the picture (left, top, right, bottom) and the pieces are placed on whole device pixels, so there is
    /// no seam between them at any scale.
    /// </summary>
    public class NineSlice : FrameworkElement
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(ImageSource), typeof(NineSlice),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPiecesChanged));

        public static readonly DependencyProperty MarginsProperty = DependencyProperty.Register(
            nameof(Margins), typeof(Thickness), typeof(NineSlice),
            new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsRender, OnPiecesChanged));

        public static readonly DependencyProperty StateCountProperty = DependencyProperty.Register(
            nameof(StateCount), typeof(int), typeof(NineSlice),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender, OnPiecesChanged));

        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(int), typeof(NineSlice),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnPiecesChanged));

        // The stretched pieces are not smoothed either: for a frame made of lines one pixel wide (the popups'), where smoothing along an edge
        // that is not on a whole pixel (half a pixel at 125%) spread the line over two rows next to the corner that is drawn as it is.
        public static readonly DependencyProperty CrispProperty = DependencyProperty.Register(
            nameof(Crisp), typeof(bool), typeof(NineSlice),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool Crisp { get => (bool)GetValue(CrispProperty); set => SetValue(CrispProperty, value); }

        public ImageSource Source { get => (ImageSource)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
        public Thickness Margins { get => (Thickness)GetValue(MarginsProperty); set => SetValue(MarginsProperty, value); }
        public int StateCount { get => (int)GetValue(StateCountProperty); set => SetValue(StateCountProperty, value); }
        public int State { get => (int)GetValue(StateProperty); set => SetValue(StateProperty, value); }

        private BitmapSource[,] _pieces;
        private int[] _cols;
        private int[] _rows;

        public NineSlice()
        {
            IsHitTestVisible = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
        }

        private static void OnPiecesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((NineSlice)d)._pieces = null;
        }

        private void BuildPieces()
        {
            _pieces = null;
            if (Source is not BitmapSource bmp || bmp.PixelWidth == 0)
            {
                return;
            }

            int states = Math.Max(1, StateCount);
            int stateHeight = bmp.PixelHeight / states;
            int state = Math.Max(0, Math.Min(states - 1, State));
            int left = (int)Margins.Left, top = (int)Margins.Top, right = (int)Margins.Right, bottom = (int)Margins.Bottom;

            _cols = new[] { 0, left, bmp.PixelWidth - right, bmp.PixelWidth };
            _rows = new[] { state * stateHeight, state * stateHeight + top, state * stateHeight + stateHeight - bottom, (state + 1) * stateHeight };

            var pieces = new BitmapSource[3, 3];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int w = _cols[c + 1] - _cols[c], h = _rows[r + 1] - _rows[r];
                    if (w > 0 && h > 0)
                    {
                        var piece = new CroppedBitmap(bmp, new Int32Rect(_cols[c], _rows[r], w, h));
                        piece.Freeze();
                        pieces[r, c] = piece;
                    }
                }
            }

            _pieces = pieces;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_pieces == null)
            {
                BuildPieces();
            }

            if (_pieces == null || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

            // The pieces are laid out in device pixels, and the corners and edges keep their own size in them: at a scale above 100% the
            // frame is not scaled up (that makes its 1px lines soft) but only gets a bigger middle, which is the part that is stretched.
            int totalW = (int)Math.Round(ActualWidth * sx), totalH = (int)Math.Round(ActualHeight * sy);
            int left = (int)Margins.Left, right = (int)Margins.Right, top = (int)Margins.Top, bottom = (int)Margins.Bottom;
            // Narrower or lower than its own frame (a button that is squeezed to a hotspot of one pixel, say): nothing is drawn, rather than
            // a sliver of its edge.
            if (left + right > totalW || top + bottom > totalH)
            {
                return;
            }

            int[] xs = { 0, left, totalW - right, totalW };
            int[] ys = { 0, top, totalH - bottom, totalH };

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    BitmapSource piece = _pieces[r, c];
                    int w = xs[c + 1] - xs[c], h = ys[r + 1] - ys[r];
                    if (piece == null || w <= 0 || h <= 0)
                    {
                        continue;
                    }

                    // a piece that is not stretched is drawn as it is; one that is stretched is smoothed along the way it is stretched
                    var group = new DrawingGroup();
                    RenderOptions.SetBitmapScalingMode(group, Crisp || (w == piece.PixelWidth && h == piece.PixelHeight) ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
                    using (DrawingContext gc = group.Open())
                    {
                        gc.DrawImage(piece, new Rect(xs[c] / sx, ys[r] / sy, w / sx, h / sy));
                    }

                    dc.DrawDrawing(group);
                }
            }
        }
    }
}
