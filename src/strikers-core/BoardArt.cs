namespace Strikers.Core;

public static class BoardArt
{
    private const double Desaturate = 0.45;
    private const double Darken = 0.72;
    private const double JitterScale = 0.6;
    private const byte Alpha = 244;

    private const double LightPerLevel = 0.06;

    private const double MarshFloor = 0.85;

    private const double ToneAmp = 0.03;

    private const double SeamDrop = 0.35;

    private const double BevelLight = 0.16;
    private const double BevelDark = 0.24;

    public static int PlateLevels(int value)
    {
        return Math.Clamp(value - Terrain.Chasm, 0, Terrain.Mountains - Terrain.Chasm);
    }

    public static Rgb MutedShade(int value)
    {
        var shade = Terrain.Shade(value);
        var (r, g, b) = Lens(shade);
        return new Rgb((byte)r, (byte)g, (byte)b);
    }

    private static (double R, double G, double B) Lens(Rgb shade)
    {
        var luma = (0.2126 * shade.R) + (0.7152 * shade.G) + (0.0722 * shade.B);
        var r = (shade.R + ((luma - shade.R) * Desaturate)) * Darken;
        var g = (shade.G + ((luma - shade.G) * Desaturate)) * Darken;
        var b = (shade.B + ((luma - shade.B) * Desaturate)) * Darken;
        return (r, g, b);
    }

    public static byte[] Tiles(StrikeBoard board, int cell, int frostRows = 0)
    {
        var width = board.Width * cell;
        var height = board.Height * cell;
        var pixels = new byte[width * height * 4];

        var tileCount = board.Width * board.Height;
        var baseR = new double[tileCount];
        var baseG = new double[tileCount];
        var baseB = new double[tileCount];
        var lifts = new double[tileCount];
        var drops = new double[tileCount];
        var values = new int[tileCount];

        for (var ty = 0; ty < board.Height; ty++)
        {
            for (var tx = 0; tx < board.Width; tx++)
            {
                var value = board.At(tx, ty);
                var jitter = Grunge.TileJitter(tx, ty) * JitterScale;

                var (lr, lg, lb) = Lens(Terrain.Shade(value));
                var r = Clamp((int)(lr + jitter));
                var g = Clamp((int)(lg + jitter));
                var b = Clamp((int)(lb + jitter));

                if (value == Terrain.Marsh)
                {
                    r *= MarshFloor;
                    g *= MarshFloor;
                    b *= MarshFloor;
                }

                var light = LightPerLevel * Math.Clamp(value, Terrain.Chasm, Terrain.Mountains);

                var at = (ty * board.Width) + tx;
                baseR[at] = r;
                baseG[at] = g;
                baseB[at] = b;
                lifts[at] = Math.Max(0, light);
                drops[at] = Math.Max(0, -light);
                values[at] = value;
            }
        }

        var bevel = Math.Max(2, cell / 12);

        for (var py = 0; py < height; py++)
        {
            var ty = py / cell;
            var ry = py - (ty * cell);

            for (var px = 0; px < width; px++)
            {
                var tx = px / cell;
                var rx = px - (tx * cell);
                var tile = (ty * board.Width) + tx;

                var r = baseR[tile];
                var g = baseG[tile];
                var b = baseB[tile];
                var lift = lifts[tile];
                var drop = drops[tile];
                var value = values[tile];

                var tone = 1.0 + (Grunge.Tone(px, py) * ToneAmp);
                r = Math.Min(255, r * tone);
                g = Math.Min(255, g * tone);
                b = Math.Min(255, b * tone);

                if (lift > 0)
                {
                    r += (255 - r) * lift;
                    g += (255 - g) * lift;
                    b += (255 - b) * lift;
                }

                var lit = rx < bevel || ry < bevel;
                var shadowed = rx >= cell - bevel || ry >= cell - bevel;
                if (shadowed)
                {
                    drop = Stack(drop, BevelDark);
                }
                else if (lit)
                {
                    r += (255 - r) * BevelLight;
                    g += (255 - g) * BevelLight;
                    b += (255 - b) * BevelLight;
                }

                if (rx == cell - 1 || ry == cell - 1)
                {
                    drop = Stack(drop, SeamDrop);
                }

                r *= 1.0 - drop;
                g *= 1.0 - drop;
                b *= 1.0 - drop;

                var at = ((py * width) + px) * 4;
                pixels[at] = (byte)(b * Alpha / 255);
                pixels[at + 1] = (byte)(g * Alpha / 255);
                pixels[at + 2] = (byte)(r * Alpha / 255);
                pixels[at + 3] = Alpha;
            }
        }

        if (frostRows > 0)
        {
            FrostTop(pixels, width, Math.Min(frostRows, height));
        }

        return pixels;
    }

    private const int FrostRadius = 2;
    private const double FrostDesaturate = 0.65;
    private const double FrostDarken = 0.8;
    private const double FrostFade = 0.72;

    public static void FrostTop(byte[] pixels, int width, int rows)
    {
        BlurPass(pixels, width, rows, horizontal: true);
        BlurPass(pixels, width, rows, horizontal: false);

        for (var py = 0; py < rows; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var at = ((py * width) + px) * 4;
                double b = pixels[at];
                double g = pixels[at + 1];
                double r = pixels[at + 2];

                var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                r = (r + ((luma - r) * FrostDesaturate)) * FrostDarken * FrostFade;
                g = (g + ((luma - g) * FrostDesaturate)) * FrostDarken * FrostFade;
                b = (b + ((luma - b) * FrostDesaturate)) * FrostDarken * FrostFade;

                pixels[at] = (byte)b;
                pixels[at + 1] = (byte)g;
                pixels[at + 2] = (byte)r;
                pixels[at + 3] = (byte)(pixels[at + 3] * FrostFade);
            }
        }
    }

    private static void BlurPass(byte[] pixels, int width, int rows, bool horizontal)
    {
        var span = rows * width * 4;
        var source = new byte[span];
        Array.Copy(pixels, source, span);

        for (var py = 0; py < rows; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var sumB = 0;
                var sumG = 0;
                var sumR = 0;
                var count = 0;
                for (var o = -FrostRadius; o <= FrostRadius; o++)
                {
                    var sx = horizontal ? px + o : px;
                    var sy = horizontal ? py : py + o;
                    if (sx < 0 || sx >= width || sy < 0 || sy >= rows)
                    {
                        continue;
                    }

                    var from = ((sy * width) + sx) * 4;
                    sumB += source[from];
                    sumG += source[from + 1];
                    sumR += source[from + 2];
                    count++;
                }

                var at = ((py * width) + px) * 4;
                pixels[at] = (byte)(sumB / count);
                pixels[at + 1] = (byte)(sumG / count);
                pixels[at + 2] = (byte)(sumR / count);
            }
        }
    }

    private static double Stack(double first, double second)
    {
        return 1.0 - ((1.0 - first) * (1.0 - second));
    }

    private static double Clamp(int channel)
    {
        return Math.Min(255, Math.Max(0, channel));
    }
}
