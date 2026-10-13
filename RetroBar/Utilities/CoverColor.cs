using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>The colour of a cover.</summary>
    public static class CoverColor
    {
        // The most common colour of the art: pixels counted in buckets of 5 bits a channel (taken as they are in the picture, not blended by
        // scaling it down), the colour that of the bucket with most of them - the average of the pixels in it, which differ by a few levels
        // at most. Near-black pixels are left out (it would not show on the popup's face), unless that is all there is.
        public static Color? MostCommon(ImageSource source)
        {
            if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return null;
            }

            try
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                // Every n-th pixel of every n-th row: about 40,000 of them for any size.
                int step = Math.Max(1, (int)Math.Sqrt(width * (double)height / 40000));

                var count = new int[32768];
                var sumR = new long[32768];
                var sumG = new long[32768];
                var sumB = new long[32768];
                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        int i = y * stride + x * 4;
                        if (pixels[i + 3] < 128)
                        {
                            continue;
                        }

                        byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                        int bucket = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                        count[bucket]++;
                        sumR[bucket] += r;
                        sumG[bucket] += g;
                        sumB[bucket] += b;
                    }
                }

                int best = -1;
                int bestAny = -1;
                for (int i = 0; i < count.Length; i++)
                {
                    if (count[i] == 0)
                    {
                        continue;
                    }

                    if (bestAny < 0 || count[i] > count[bestAny])
                    {
                        bestAny = i;
                    }

                    double brightest = Math.Max(sumR[i], Math.Max(sumG[i], sumB[i])) / (double)count[i];
                    if (brightest >= 40 && (best < 0 || count[i] > count[best]))
                    {
                        best = i;
                    }
                }

                int chosen = best >= 0 ? best : bestAny;
                if (chosen < 0)
                {
                    return null;
                }

                return Color.FromRgb((byte)(sumR[chosen] / count[chosen]), (byte)(sumG[chosen] / count[chosen]), (byte)(sumB[chosen] / count[chosen]));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.IOException)
            {
                return null;
            }
        }
    }
}
