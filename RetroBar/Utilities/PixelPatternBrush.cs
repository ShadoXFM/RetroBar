using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Keeps a tiled checkerboard background (the one behind the active tab) the same size - and
    /// crisp - in *device pixels* on every monitor.
    ///
    /// The checkerboard resources are DrawingBrushes with a tile of 2x2 DIPs, which is exactly 2x2
    /// pixels at 100% but 2.5x2.5 at 125%: the tile gets resampled onto the pixel grid, so on a
    /// scaled monitor the pattern comes out smeared and visibly "bigger" than on a 100% monitor.
    /// (Simply shrinking the tile to 2 device pixels isn't enough either - WPF rasterizes and
    /// samples the vector tile with enough filtering that the two colors average back to a flat
    /// gray.) So this takes the color from the authored brush and re-creates the pattern as a real
    /// 2x2 pixel bitmap, tiled one-to-one with nearest-neighbor sampling.
    ///
    /// Usage (on the Border whose Background is the checkerboard DrawingBrush):
    /// <Border Background="{DynamicResource CheckeredBackgroundLight}" utilities:PixelPatternBrush.Enabled="True" />
    /// </summary>
    public static class PixelPatternBrush
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(PixelPatternBrush), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

        // The brush as authored, kept so a reload re-creates the pattern from it rather than from
        // the bitmap brush this class swapped in.
        private static readonly DependencyProperty SourceBrushProperty = DependencyProperty.RegisterAttached(
            "SourceBrush", typeof(DrawingBrush), typeof(PixelPatternBrush), new PropertyMetadata(null));

        private static readonly DependencyProperty AppliedBrushProperty = DependencyProperty.RegisterAttached(
            "AppliedBrush", typeof(Brush), typeof(PixelPatternBrush), new PropertyMetadata(null));

        // The Background of some of these borders is set later and changes over time - a button's face is
        // template-bound to its Background, which a trigger switches to the checkerboard while it is
        // checked - so the pattern has to be (re)built whenever a checkerboard brush shows up, not just
        // once at load.
        private static readonly DependencyPropertyDescriptor BackgroundDescriptor =
            DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border));

        private static readonly ConditionalWeakTable<Border, EventHandler> BackgroundWatchers = new();

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border border || e.NewValue is not true)
            {
                return;
            }

            // Re-applying on every Loaded is harmless, and needed: a template instance (the active
            // tab's) is rebuilt - and its Border reloaded - whenever the tab's style or theme changes.
            border.Loaded += (s, args) =>
            {
                Apply(border);
                Watch(border);
            };
            border.Unloaded += (s, args) => Unwatch(border);

            // The DPI the pattern is built for is the one the Border has when it loads, which is not always the
            // monitor's own: a taskbar window starts out with the DPI of the primary monitor and is only moved
            // to its monitor afterwards, so one on a monitor with another scale built its pattern for the wrong
            // one (the tile then got resampled, and showed as coarse diagonal stripes). Build it again when
            // the DPI changes, and when a layout pass finds it differs from what the pattern was built for.
            border.SizeChanged += (s, args) => ApplyIfDpiChanged(border);
            border.Loaded += (s, args) =>
            {
                if (Window.GetWindow(border) is Window window)
                {
                    window.DpiChanged -= OnWindowDpiChanged;
                    window.DpiChanged += OnWindowDpiChanged;
                }
            };

            if (border.IsLoaded)
            {
                Apply(border);
                Watch(border);
            }
        }

        private static readonly DependencyProperty BuiltForDpiProperty = DependencyProperty.RegisterAttached(
            "BuiltForDpi", typeof(double), typeof(PixelPatternBrush), new PropertyMetadata(0.0));

        private static void ApplyIfDpiChanged(Border border)
        {
            if (border.IsLoaded && border.Background is ImageBrush &&
                (double)border.GetValue(BuiltForDpiProperty) != VisualTreeHelper.GetDpi(border).DpiScaleX)
            {
                Apply(border);
            }
        }

        private static void OnWindowDpiChanged(object sender, DpiChangedEventArgs e)
        {
            if (sender is DependencyObject window)
            {
                ReapplyBelow(window);
            }
        }

        private static void ReapplyBelow(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border border && border.IsLoaded && border.GetValue(SourceBrushProperty) != null)
                {
                    Apply(border);
                }

                ReapplyBelow(child);
            }
        }

        private static void Watch(Border border)
        {
            if (BackgroundWatchers.TryGetValue(border, out _))
            {
                return;
            }

            EventHandler handler = (s, args) =>
            {
                // Our own ImageBrush (and any non-checkerboard background) is ignored, so applying the
                // pattern doesn't trigger itself.
                if (border.IsLoaded && border.Background is DrawingBrush)
                {
                    Apply(border);
                }
            };

            BackgroundWatchers.Add(border, handler);
            BackgroundDescriptor.AddValueChanged(border, handler);
        }

        private static void Unwatch(Border border)
        {
            if (BackgroundWatchers.TryGetValue(border, out EventHandler handler))
            {
                BackgroundDescriptor.RemoveValueChanged(border, handler);
                BackgroundWatchers.Remove(border);
            }
        }

        private static void Apply(Border border)
        {
            DrawingBrush source = (DrawingBrush)border.GetValue(SourceBrushProperty);

            if (source == null || !ReferenceEquals(border.Background, border.GetValue(AppliedBrushProperty)))
            {
                // First time, or the Background was replaced by something else since (a theme swap
                // re-resolving the resource) - take the current one as the new original.
                source = border.Background as DrawingBrush;
                border.SetValue(SourceBrushProperty, source);
            }

            if (source == null || !TryGetPattern(source, out Color color, out bool[,] cells))
            {
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(border);
            if (dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0)
            {
                return;
            }

            int width = cells.GetLength(0);
            int height = cells.GetLength(1);
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!cells[x, y])
                    {
                        continue;
                    }

                    int i = (y * width + x) * 4;
                    // Pbgra32 (premultiplied); the pattern colors are opaque.
                    pixels[i] = color.B;
                    pixels[i + 1] = color.G;
                    pixels[i + 2] = color.R;
                    pixels[i + 3] = 255;
                }
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                // One bitmap pixel per device pixel, whatever the monitor's scale.
                Viewport = new Rect(0, 0, width / dpi.DpiScaleX, height / dpi.DpiScaleY),
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);

            // The scaling mode the tile is sampled with is read from the element drawing it, so set it
            // there too (the brush's own setting alone isn't honored for a tiled brush).
            RenderOptions.SetBitmapScalingMode(border, BitmapScalingMode.NearestNeighbor);
            border.SnapsToDevicePixels = true;
            border.UseLayoutRounding = true;
            // SetCurrentValue, not assignment: assigning would replace a TemplateBinding / DynamicResource
            // on Background for good, so the button face could never go back to its normal color.
            border.SetCurrentValue(Border.BackgroundProperty, brush);
            border.SetValue(AppliedBrushProperty, brush);
            border.SetValue(BuiltForDpiProperty, dpi.DpiScaleX);
        }

        /// <summary>
        /// Reads the checkerboard's color and which cells of its 2x2 tile are filled, from the brush's
        /// own single GeometryDrawing (checked cell by cell, so it works for either phase of the
        /// pattern - CheckeredBackground vs CheckeredBackgroundAlt).
        /// </summary>
        private static bool TryGetPattern(DrawingBrush source, out Color color, out bool[,] cells)
        {
            color = default;
            cells = null;

            if (source.Drawing is not GeometryDrawing drawing || drawing.Geometry == null ||
                drawing.Brush is not SolidColorBrush solid)
            {
                return false;
            }

            Rect tile = source.Viewport;
            int width = Math.Max(1, (int)Math.Round(tile.Width));
            int height = Math.Max(1, (int)Math.Round(tile.Height));

            cells = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    cells[x, y] = drawing.Geometry.FillContains(new Point(x + 0.5, y + 0.5));
                }
            }

            color = solid.Color;
            return true;
        }
    }
}
