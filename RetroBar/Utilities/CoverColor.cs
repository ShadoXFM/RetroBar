using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The colour of a cover: the colour the eye takes it to have, not the one with the most pixels (a dark or grey background on most
    /// covers) and not a swatch picked by saturation and lightness limits (which went wrong on covers with a little of a vivid colour and a
    /// lot of a dull one, or the other way round).
    ///
    /// It works in OKLab, where distances are what the eye sees. Every colourful pixel votes for its hue, with a weight that grows with how
    /// vivid it is (and is smaller for a very dark or very light one); the hues are counted in 10-degree bins, each helped by its two
    /// neighbours so that a colour that straddles two bins is not split; the winning hue's pixels are averaged. The result is then given
    /// the lightness and the vividness that read well on the dark popup, with the same hue. A cover with no colour in it (greys) gives
    /// a light grey.
    /// </summary>
    public static class CoverColor
    {
        private const int HueBins = 36;

        // The pixels that count: anything greyer than this is a background, not a colour.
        private const double MinChroma = 0.035;

        // What the colour is given for the popup: lightness within this, and at least this vivid.
        private const double MinLightness = 0.66;
        private const double MaxLightness = 0.82;
        private const double MinResultChroma = 0.11;

        public static Color? Vibrant(ImageSource source)
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

                // Every n-th pixel of every n-th row: about 40,000 of them for any size, as they are in the picture (not blended by scaling).
                int step = Math.Max(1, (int)Math.Sqrt(width * (double)height / 40000));

                var weight = new double[HueBins];
                var sumL = new double[HueBins];
                var sumA = new double[HueBins];
                var sumB = new double[HueBins];
                double lightnessTotal = 0;
                int counted = 0;

                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        int i = y * stride + x * 4;
                        if (pixels[i + 3] < 128)
                        {
                            continue;
                        }

                        (double l, double a, double b) = ToOkLab(pixels[i + 2], pixels[i + 1], pixels[i]);
                        lightnessTotal += l;
                        counted++;

                        double chroma = Math.Sqrt(a * a + b * b);
                        if (chroma < MinChroma)
                        {
                            continue;
                        }

                        // How much the pixel counts: more the more vivid (squared: a few vivid pixels outweigh a wash of pale ones), less
                        // when it is nearly black or white, where a colour is hard to tell.
                        double lightnessFactor = l < 0.25 ? 0.25 + (l / 0.25) * 0.75 : l > 0.92 ? 0.3 : 1;
                        double w = chroma * chroma * lightnessFactor;

                        int bin = (int)(((Math.Atan2(b, a) / (2 * Math.PI) + 1) % 1) * HueBins) % HueBins;
                        weight[bin] += w;
                        sumL[bin] += l * w;
                        sumA[bin] += a * w;
                        sumB[bin] += b * w;
                    }
                }

                if (counted == 0)
                {
                    return null;
                }

                // The best hue: its bin and its two neighbours' weights together.
                int best = -1;
                double bestScore = 0;
                for (int bin = 0; bin < HueBins; bin++)
                {
                    double score = weight[bin] + 0.5 * (weight[(bin + 1) % HueBins] + weight[(bin + HueBins - 1) % HueBins]);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = bin;
                    }
                }

                double mean = lightnessTotal / counted;
                if (best < 0 || bestScore <= 0)
                {
                    // Nothing with a colour in it: a light grey.
                    return FromOkLab(Math.Clamp(mean, MinLightness, MaxLightness), 0, 0);
                }

                double totalWeight = 0, l1 = 0, a1 = 0, b1 = 0;
                for (int offset = -1; offset <= 1; offset++)
                {
                    int bin = (best + offset + HueBins) % HueBins;
                    totalWeight += weight[bin];
                    l1 += sumL[bin];
                    a1 += sumA[bin];
                    b1 += sumB[bin];
                }

                double lightness = l1 / totalWeight;
                double ca = a1 / totalWeight;
                double cb = b1 / totalWeight;

                // Same hue, a lightness and a vividness that show on the dark popup.
                double chromaResult = Math.Sqrt(ca * ca + cb * cb);
                double scale = chromaResult > 1e-6 ? Math.Max(chromaResult, MinResultChroma) / chromaResult : 0;
                return FromOkLab(Math.Clamp(lightness, MinLightness, MaxLightness), ca * scale, cb * scale);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.IOException)
            {
                return null;
            }
        }

        private static double ToLinear(double channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static double FromLinear(double linear)
        {
            double c = Math.Clamp(linear, 0, 1);
            return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        }

        // sRGB (0-255) to OKLab (Björn Ottosson's).
        private static (double L, double A, double B) ToOkLab(byte red, byte green, byte blue)
        {
            double r = ToLinear(red), g = ToLinear(green), b = ToLinear(blue);
            double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            return (
                0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        // OKLab to sRGB; a colour outside what the screen can show is made less vivid (same hue and lightness) until it is inside.
        private static Color FromOkLab(double lightness, double a, double b)
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                double l_ = lightness + 0.3963377774 * a + 0.2158037573 * b;
                double m_ = lightness - 0.1055613458 * a - 0.0638541728 * b;
                double s_ = lightness - 0.0894841775 * a - 1.2914855480 * b;
                double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;

                double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
                double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
                double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

                const double eps = 0.0005;
                if ((r >= -eps && r <= 1 + eps && g >= -eps && g <= 1 + eps && bl >= -eps && bl <= 1 + eps) || attempt == 23)
                {
                    return Color.FromRgb(
                        (byte)Math.Round(FromLinear(r) * 255),
                        (byte)Math.Round(FromLinear(g) * 255),
                        (byte)Math.Round(FromLinear(bl) * 255));
                }

                a *= 0.92;
                b *= 0.92;
            }

            return Colors.Gray;
        }
    }
}
