namespace Strikers.Core;

public static class Legibility
{
    public const int Ceiling = 100;
    public const double Knee = 0.25;

    public static (byte R, byte G, byte B) Cap(byte r, byte g, byte b)
    {
        var lum = ((r * 2126) + (g * 7152) + (b * 722)) / 10000;
        if (lum <= Ceiling)
        {
            return (r, g, b);
        }

        var target = Ceiling + ((lum - Ceiling) * Knee);
        var scale = target / lum;
        return ((byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
    }
}
