namespace Strikers.Core;

public static class IconArt
{
    private const int Super = 4;

    private static readonly int[] Cluster =
    [
        Terrain.Mountains, Terrain.Hills,
        Terrain.Forest, Terrain.Grassland,
    ];

    private const double BadgeRadius = 0.47;

    private const double ClusterFill = 0.90;

    private const double LiftPerLevel = 0.30;
    private const double BaseThickness = 0.22;

    private const double SideLeft = 1.00;
    private const double SideRight = 0.78;
    private const double TopLight = 1.70;

    private const double TextureScale = 1.3;

    public static byte[] Render(int size)
    {
        var n = size * Super;
        var pixels = new byte[n * n * 4];

        Badge(pixels, n);
        Blocks(pixels, n);

        return Downsample(pixels, n, Super);
    }

    private static void Badge(byte[] pixels, int n)
    {
        var centre = n / 2.0;
        var outer = n * BadgeRadius;

        var line = Math.Max(1, n / 48);
        var rim = Palette.SlateDark.Muted;
        var fill = Palette.SlateDark.Window;

        Disc(pixels, n, centre, centre, outer, rim, 0x8C);
        Disc(pixels, n, centre, centre, outer - line, fill, 0xFF);
    }

    private static void Disc(byte[] pixels, int n, double cx, double cy, double radius, Rgb colour, byte alpha)
    {
        for (var y = (int)(cy - radius) - 1; y <= (int)(cy + radius) + 1; y++)
        {
            for (var x = (int)(cx - radius) - 1; x <= (int)(cx + radius) + 1; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    Blend(pixels, n, x, y, colour, alpha);
                }
            }
        }
    }

    private static void Blocks(byte[] pixels, int n)
    {
        var board = new StrikeBoard
        {
            Width = 2,
            Height = 2,
            PlacementRows = 1,
            Cells = Cluster,
        };

        var span = board.Width + board.Height;
        var half = Fit(n, span);
        var perLevel = Math.Max(1, (int)(half * LiftPerLevel));
        var thickness = Math.Max(2, (int)(half * BaseThickness));
        var maxLift = perLevel * BoardArt.PlateLevels(Terrain.Mountains);

        var cx = n / 2.0;
        var cy = ((n - ((span * half / 2.0) + maxLift + thickness)) / 2.0) + maxLift;

        var texCell = Math.Max(8, (int)(half * TextureScale / Super));
        var texture = BoardArt.Tiles(board, texCell);
        var stride = board.Width * texCell;

        for (var step = 0; step <= span - 2; step++)
        {
            for (var ty = 0; ty < board.Height; ty++)
            {
                var tx = step - ty;
                if (tx < 0 || tx >= board.Width)
                {
                    continue;
                }

                var value = board.At(tx, ty);
                var lift = BoardArt.PlateLevels(value) * perLevel;
                var paint = BoardArt.MutedShade(value);

                (double X, double Y) Corner(double gx, double gy)
                {
                    return (cx + ((gx - gy) * half), cy + ((gx + gy) * half / 2.0) - lift);
                }

                var top = Corner(tx, ty);
                var right = Corner(tx + 1, ty);
                var bottom = Corner(tx + 1, ty + 1);
                var left = Corner(tx, ty + 1);
                var drop = lift + thickness;

                Quad(pixels, n, [left, bottom, (bottom.X, bottom.Y + drop), (left.X, left.Y + drop)],
                    Shade(paint, SideLeft));
                Quad(pixels, n, [bottom, right, (right.X, right.Y + drop), (bottom.X, bottom.Y + drop)],
                    Shade(paint, SideRight));
                Tops(pixels, n, [top, right, bottom, left], texture, stride, tx * texCell, ty * texCell, texCell);
            }
        }
    }

    private static int Fit(int n, int span)
    {
        var half = (int)(n * ClusterFill) / span;
        while (half > 3)
        {
            var perLevel = Math.Max(1, (int)(half * LiftPerLevel));
            var thickness = Math.Max(2, (int)(half * BaseThickness));
            var tall = (span * half / 2) + (perLevel * BoardArt.PlateLevels(Terrain.Mountains)) + thickness;
            if (tall <= n * ClusterFill)
            {
                break;
            }

            half--;
        }

        return half;
    }

    private static void Tops(byte[] pixels, int n, (double X, double Y)[] face, byte[] texture,
                             int stride, int tileX, int tileY, int cell)
    {
        var origin = face[0];
        var ex = (X: face[1].X - origin.X, Y: face[1].Y - origin.Y);
        var ey = (X: face[3].X - origin.X, Y: face[3].Y - origin.Y);
        var det = (ex.X * ey.Y) - (ex.Y * ey.X);
        if (Math.Abs(det) < 1e-9)
        {
            return;
        }

        var (minX, maxX, minY, maxY) = Bounds(face);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x + 0.5 - origin.X;
                var dy = y + 0.5 - origin.Y;
                var u = ((dx * ey.Y) - (dy * ey.X)) / det;
                var v = ((dy * ex.X) - (dx * ex.Y)) / det;
                if (u < 0 || v < 0 || u >= 1 || v >= 1)
                {
                    continue;
                }

                var sx = tileX + Math.Clamp((int)(u * cell), 0, cell - 1);
                var sy = tileY + Math.Clamp((int)(v * cell), 0, cell - 1);
                var at = ((sy * stride) + sx) * 4;
                var alpha = texture[at + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var colour = new Rgb(
                    (byte)Math.Clamp(texture[at + 2] * 255.0 / alpha * TopLight, 0, 255),
                    (byte)Math.Clamp(texture[at + 1] * 255.0 / alpha * TopLight, 0, 255),
                    (byte)Math.Clamp(texture[at + 0] * 255.0 / alpha * TopLight, 0, 255));
                Blend(pixels, n, x, y, colour, 0xFF);
            }
        }
    }

    private static void Quad(byte[] pixels, int n, (double X, double Y)[] corners, Rgb colour)
    {
        var (minX, maxX, minY, maxY) = Bounds(corners);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (Inside(corners, x + 0.5, y + 0.5))
                {
                    Blend(pixels, n, x, y, colour, 0xFF);
                }
            }
        }
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) Bounds((double X, double Y)[] corners)
    {
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        foreach (var (x, y) in corners)
        {
            minX = Math.Min(minX, (int)Math.Floor(x));
            maxX = Math.Max(maxX, (int)Math.Ceiling(x));
            minY = Math.Min(minY, (int)Math.Floor(y));
            maxY = Math.Max(maxY, (int)Math.Ceiling(y));
        }

        return (minX, maxX, minY, maxY);
    }

    private static bool Inside((double X, double Y)[] corners, double px, double py)
    {
        var positive = false;
        var negative = false;
        for (var i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            var cross = ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));
            if (cross > 0)
            {
                positive = true;
            }

            if (cross < 0)
            {
                negative = true;
            }
        }

        return !(positive && negative);
    }

    private static Rgb Shade(Rgb colour, double by)
    {
        return new Rgb(
            (byte)Math.Clamp(colour.R * by, 0, 255),
            (byte)Math.Clamp(colour.G * by, 0, 255),
            (byte)Math.Clamp(colour.B * by, 0, 255));
    }

    private static void Blend(byte[] pixels, int n, int x, int y, Rgb colour, byte alpha)
    {
        if (x < 0 || y < 0 || x >= n || y >= n || alpha == 0)
        {
            return;
        }

        var at = ((y * n) + x) * 4;
        var source = alpha / 255.0;
        var behind = pixels[at + 3] / 255.0;
        var result = source + (behind * (1 - source));
        if (result <= 0)
        {
            return;
        }

        pixels[at + 0] = (byte)Math.Clamp(((colour.R * source) + (pixels[at + 0] * behind * (1 - source))) / result, 0, 255);
        pixels[at + 1] = (byte)Math.Clamp(((colour.G * source) + (pixels[at + 1] * behind * (1 - source))) / result, 0, 255);
        pixels[at + 2] = (byte)Math.Clamp(((colour.B * source) + (pixels[at + 2] * behind * (1 - source))) / result, 0, 255);
        pixels[at + 3] = (byte)Math.Clamp(result * 255, 0, 255);
    }

    private static byte[] Downsample(byte[] pixels, int n, int factor)
    {
        var size = n / factor;
        var outPixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double r = 0;
                double g = 0;
                double b = 0;
                double a = 0;

                for (var sy = 0; sy < factor; sy++)
                {
                    for (var sx = 0; sx < factor; sx++)
                    {
                        var at = ((((y * factor) + sy) * n) + (x * factor) + sx) * 4;
                        var alpha = pixels[at + 3] / 255.0;
                        r += pixels[at + 0] * alpha;
                        g += pixels[at + 1] * alpha;
                        b += pixels[at + 2] * alpha;
                        a += alpha;
                    }
                }

                if (a <= 0)
                {
                    continue;
                }

                var to = ((y * size) + x) * 4;
                outPixels[to + 0] = (byte)Math.Clamp(r / a, 0, 255);
                outPixels[to + 1] = (byte)Math.Clamp(g / a, 0, 255);
                outPixels[to + 2] = (byte)Math.Clamp(b / a, 0, 255);
                outPixels[to + 3] = (byte)Math.Clamp(a / (factor * factor) * 255, 0, 255);
            }
        }

        return outPixels;
    }
}
