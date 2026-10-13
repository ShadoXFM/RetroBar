using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The colour of a cover, picked as the Spicetify themes do (and Android's Palette, which node-vibrant follows): not the colour with the
    /// most pixels, which on most covers is a dark or grey background, but the "vibrant" one - a colour that is saturated and neither dark
    /// nor light, scored by how close it is to that and, a little, by how much of the picture it is. If there is none, the next kind that
    /// there is (light vibrant, dark vibrant, then the muted ones), and at last the most common colour.
    /// </summary>
    public static class CoverColor
    {
        // One kind of swatch: the saturation and lightness it aims at, and the range it has to be in.
        private readonly record struct Target(double Saturation, double MinSaturation, double MaxSaturation, double Lightness, double MinLightness, double MaxLightness);

        // The same targets as Palette's, in the order they are tried.
        private static readonly Target[] Targets =
        {
            new(1.0, 0.35, 1.0, 0.5, 0.3, 0.7),    // vibrant
            new(1.0, 0.35, 1.0, 0.74, 0.55, 1.0),  // light vibrant
            new(1.0, 0.35, 1.0, 0.26, 0.0, 0.45),  // dark vibrant
            new(0.3, 0.0, 0.4, 0.5, 0.3, 0.7),     // muted
            new(0.3, 0.0, 0.4, 0.74, 0.55, 1.0),   // light muted
            new(0.3, 0.0, 0.4, 0.26, 0.0, 0.45),   // dark muted
        };

        // The scores' weights, as Palette's: how close the lightness and the saturation are matter most, the share of the picture least.
        private const double SaturationWeight = 3;
        private const double LightnessWeight = 6;
        private const double PopulationWeight = 1;

        private readonly record struct Swatch(Color Color, double Saturation, double Lightness, int Population);

        public static Color? Vibrant(ImageSource source)
        {
            if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return null;
            }

            try
            {
                List<Swatch> swatches = Swatches(bitmap);
                if (swatches.Count == 0)
                {
                    return null;
                }

                int largest = 0;
                foreach (Swatch swatch in swatches)
                {
                    largest = Math.Max(largest, swatch.Population);
                }

                foreach (Target target in Targets)
                {
                    Swatch? best = null;
                    double bestScore = 0;
                    foreach (Swatch swatch in swatches)
                    {
                        if (swatch.Saturation < target.MinSaturation || swatch.Saturation > target.MaxSaturation ||
                            swatch.Lightness < target.MinLightness || swatch.Lightness > target.MaxLightness)
                        {
                            continue;
                        }

                        double score =
                            SaturationWeight * (1 - Math.Abs(swatch.Saturation - target.Saturation)) +
                            LightnessWeight * (1 - Math.Abs(swatch.Lightness - target.Lightness)) +
                            PopulationWeight * ((double)swatch.Population / largest);
                        if (best == null || score > bestScore)
                        {
                            best = swatch;
                            bestScore = score;
                        }
                    }

                    if (best != null)
                    {
                        return best.Value.Color;
                    }
                }

                // Nothing like any of them (a picture of greys, say): the colour with the most pixels.
                Swatch most = swatches[0];
                foreach (Swatch swatch in swatches)
                {
                    if (swatch.Population > most.Population)
                    {
                        most = swatch;
                    }
                }

                return most.Color;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.IOException)
            {
                return null;
            }
        }

        // The colours of the picture in buckets of 4 bits a channel (so a gradient of one colour is one swatch, as in Palette's quantizer),
        // each as the average of its pixels. The pixels are taken as they are in the picture, not blended by scaling it down; transparent
        // ones are left out.
        private static List<Swatch> Swatches(BitmapSource bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            // Every n-th pixel of every n-th row: about 40,000 of them for any size.
            int step = Math.Max(1, (int)Math.Sqrt(width * (double)height / 40000));

            var count = new int[4096];
            var sumR = new long[4096];
            var sumG = new long[4096];
            var sumB = new long[4096];
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
                    int bucket = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    count[bucket]++;
                    sumR[bucket] += r;
                    sumG[bucket] += g;
                    sumB[bucket] += b;
                }
            }

            var swatches = new List<Swatch>();
            for (int bucket = 0; bucket < count.Length; bucket++)
            {
                if (count[bucket] == 0)
                {
                    continue;
                }

                byte red = (byte)(sumR[bucket] / count[bucket]);
                byte green = (byte)(sumG[bucket] / count[bucket]);
                byte blue = (byte)(sumB[bucket] / count[bucket]);
                (double saturation, double lightness) = SaturationAndLightness(red, green, blue);
                swatches.Add(new Swatch(Color.FromRgb(red, green, blue), saturation, lightness, count[bucket]));
            }

            return swatches;
        }

        // HSL's saturation and lightness (what Palette uses).
        private static (double Saturation, double Lightness) SaturationAndLightness(byte red, byte green, byte blue)
        {
            double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double lightness = (max + min) / 2;
            if (max == min)
            {
                return (0, lightness);
            }

            double delta = max - min;
            double saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
            return (saturation, lightness);
        }
    }
}
