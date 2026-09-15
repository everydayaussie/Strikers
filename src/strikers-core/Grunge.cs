namespace Strikers.Core;

public static class Grunge
{
    public static int TileJitter(int tx, int ty)
    {
        return (int)(Hash(tx, ty, 11) % 19) - 9;
    }

    public static double Tone(int px, int py)
    {
        return (2.0 * Fractal(px, py, 0.006, octaves: 1, seed: 21)) - 1.0;
    }

    private static double Fractal(int x, int y, double frequency, int octaves, int seed)
    {
        double sum = 0;
        double amplitude = 1;
        double total = 0;
        for (var o = 0; o < octaves; o++)
        {
            sum += amplitude * ValueNoise(x * frequency, y * frequency, seed + o);
            total += amplitude;
            amplitude *= 0.5;
            frequency *= 2;
        }

        return sum / total;
    }

    private static double ValueNoise(double x, double y, int seed)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var u = Smooth(x - x0);
        var v = Smooth(y - y0);

        var a = Hash01(x0, y0, seed);
        var b = Hash01(x0 + 1, y0, seed);
        var c = Hash01(x0, y0 + 1, seed);
        var d = Hash01(x0 + 1, y0 + 1, seed);

        var top = a + ((b - a) * u);
        var bottom = c + ((d - c) * u);
        return top + ((bottom - top) * v);
    }

    private static double Smooth(double t)
    {
        return t * t * (3 - (2 * t));
    }

    private static double Hash01(int x, int y, int seed)
    {
        return Hash(x, y, seed) / (double)uint.MaxValue;
    }

    private static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = ((uint)x * 374761393u) + ((uint)y * 668265263u) + ((uint)seed * 974634599u);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
