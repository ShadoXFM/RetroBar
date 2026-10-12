using System;
using System.Windows;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Small glyphs drawn as whole device pixels. A glyph is rows of cells ('X' filled), each cell exactly one pixel on every
    /// monitor: the same crisp shape at 100%, 125% and so on, where a stretched or stroked one comes out lopsided and soft.
    /// </summary>
    public static class PixelShape
    {
        /// <summary>The geometry of the rows, put offsetX / offsetY pixels in from the corner of its box and scaled so a cell
        /// is one device pixel at the given DPI.</summary>
        public static Geometry FromRows(string[] rows, int offsetX, int offsetY, DpiScale dpi)
        {
            var geometry = new StreamGeometry();

            using (StreamGeometryContext context = geometry.Open())
            {
                for (int y = 0; y < rows.Length; y++)
                {
                    int x = 0;
                    while (x < rows[y].Length)
                    {
                        if (rows[y][x] != 'X')
                        {
                            x++;
                            continue;
                        }

                        int start = x;
                        while (x < rows[y].Length && rows[y][x] == 'X')
                        {
                            x++;
                        }

                        context.BeginFigure(new Point(start + offsetX, y + offsetY), true, true);
                        context.LineTo(new Point(x + offsetX, y + offsetY), true, false);
                        context.LineTo(new Point(x + offsetX, y + offsetY + 1), true, false);
                        context.LineTo(new Point(start + offsetX, y + offsetY + 1), true, false);
                    }
                }
            }

            geometry.Transform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
            geometry.Freeze();
            return geometry;
        }

        // How many cells of a triangle (pointing to the side its widest row is on) are in row y of a shape height rows tall, the
        // triangle being width cells at its widest.
        private static int TriangleWidth(int y, int height, int width)
        {
            double distance = Math.Abs(y + 0.5 - height / 2.0);
            return Math.Max(1, (int)Math.Ceiling(width * (1 - distance / (height / 2.0))));
        }

        /// <summary>A play triangle, height rows tall (odd) and as wide as it is tall: 1, 3, 5 ... cells in a row, as the taskbar's own is
        /// drawn, with its point a single cell.</summary>
        public static string[] Play(int height)
        {
            var rows = new string[height];
            for (int y = 0; y < height; y++)
            {
                rows[y] = new string('X', Math.Min(height, 2 * Math.Min(y, height - 1 - y) + 1)).PadRight(height, '.');
            }

            return rows;
        }

        /// <summary>Two bars (each bar pixels wide, gap pixels apart).</summary>
        public static string[] Pause(int height, int bar, int gap)
        {
            string row = new string('X', bar) + new string('.', gap) + new string('X', bar);
            var rows = new string[height];
            Array.Fill(rows, row);
            return rows;
        }

        /// <summary>A triangle with a bar at its tip's side ("previous track": pointing left, bar on the left; "next track": pointing
        /// right, bar on the right), height rows tall (odd), the bar bar pixels wide and a pixel from the triangle.</summary>
        public static string[] Skip(int height, int bar, bool pointsRight)
        {
            int width = (height + 1) / 2;
            var rows = new string[height];

            for (int y = 0; y < height; y++)
            {
                string triangle = new string('X', TriangleWidth(y, height, width));
                triangle = pointsRight ? triangle.PadRight(width, '.') : triangle.PadLeft(width, '.');
                string stripe = new string('X', bar);
                rows[y] = pointsRight ? triangle + "." + stripe : stripe + "." + triangle;
            }

            return rows;
        }

        /// <summary>Two triangles, one after the other, pointing right ("fast forward") or left ("rewind").</summary>
        public static string[] DoubleTriangle(int height, bool pointsRight)
        {
            int width = (height + 1) / 2;
            var rows = new string[height];

            for (int y = 0; y < height; y++)
            {
                string slot = new string('X', TriangleWidth(y, height, width));
                slot = pointsRight ? slot.PadRight(width, '.') : slot.PadLeft(width, '.');
                rows[y] = slot + "." + slot;
            }

            return rows;
        }
    }
}
