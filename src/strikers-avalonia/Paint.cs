using Avalonia.Media;

namespace Strikers.App;

internal static class Paint
{
    public static Color ToColor(this Rgb c)
    {
        return Color.FromRgb(c.R, c.G, c.B);
    }

    public static IBrush ToBrush(this Rgb c)
    {
        return new SolidColorBrush(c.ToColor());
    }
}
