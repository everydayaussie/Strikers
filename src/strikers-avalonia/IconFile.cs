using Avalonia.Controls;
using SkiaSharp;

namespace Strikers.App;

internal static class IconFile
{
    private const float Margin = 0f;

    public static string Target()
    {
        return Path.Combine(AppContext.BaseDirectory, IconSource.FileName);
    }

    public static bool Wanted()
    {
        return IconSource.Hosted() && !File.Exists(Target());
    }

    public static void Apply(Window window)
    {
        var path = Target();
        if (!IconSource.Pinned(path))
        {
            return;
        }

        try
        {
            using var png = new MemoryStream(Trimmed(path));
            window.Icon = new WindowIcon(png);
        }
        catch (Exception)
        {
        }
    }

    internal static byte[] Trimmed(string path)
    {
        using var source = SKBitmap.Decode(path) ?? throw new InvalidDataException("not an image");
        int x0 = source.Width, y0 = source.Height, x1 = -1, y1 = -1;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).Alpha > 24)
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        if (x1 < 0)
        {
            throw new InvalidDataException("the image is empty");
        }

        var width = x1 - x0 + 1;
        var height = y1 - y0 + 1;
        var side = (int)Math.Ceiling(Math.Max(width, height) * (1 + (2 * Margin)));
        using var square = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.Transparent);
            var left = (side - width) / 2f;
            var top = (side - height) / 2f;
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(source, new SKRect(x0, y0, x1 + 1, y1 + 1),
                              new SKRect(left, top, left + width, top + height), paint);
        }

        using var image = SKImage.FromBitmap(square);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
