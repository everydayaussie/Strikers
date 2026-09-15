using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Strikers.App;

internal sealed class BoardView : Control
{
    private const int LabelLeft = 22;
    private const int LabelBottom = 18;
    private const int SideRight = 22;
    private const int TopPad = 4;

    private const int MinCell = 20;
    private const int MaxCell = 84;

    private static readonly Cursor Cross = new(StandardCursorType.Cross);
    private static readonly Cursor Barred = new(StandardCursorType.No);
    private static readonly Cursor Plain = Cursor.Default;

    private StrikeBoard board = new();
    private WriteableBitmap? tiles;
    private bool tilesDirty = true;
    private int tileCell;
    private int cell = MinCell;

    private bool painting;
    private int hoverX = -1;
    private int hoverY = -1;

    public event Action? Changed;

    public BoardView()
    {
        Focusable = true;
    }

    public StrikeBoard Board
    {
        get
        {
            return board;
        }

        set
        {
            board = value;

            hoverX = -1;
            hoverY = -1;
            painting = false;
            tilesDirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private int brush = Terrain.Forest;

    public int Brush
    {
        get
        {
            return brush;
        }

        set
        {
            brush = value;
            InvalidateVisual();
        }
    }

    private bool mirror = true;

    public bool Mirror
    {
        get
        {
            return mirror;
        }

        set
        {
            mirror = value;
            tilesDirty = true;
            InvalidateVisual();
        }
    }

    private int LiftPerLevel()
    {
        return Math.Max(1, (int)(cell * 0.06));
    }

    private int PlateLift(int value)
    {
        return BoardArt.PlateLevels(value) * LiftPerLevel();
    }

    private int MaxLift()
    {
        return BoardArt.PlateLevels(Terrain.Mountains) * LiftPerLevel();
    }

    public double MinimumHeight
    {
        get
        {
            var rows = Math.Max(1, board.Height);
            var liftPerLevel = Math.Max(1, (int)(MinCell * 0.06));
            var lift = BoardArt.PlateLevels(Terrain.Mountains) * liftPerLevel;
            return (MinCell * rows) + LabelBottom + TopPad + lift;
        }
    }

    public static double FloorHeight
    {
        get
        {
            var liftPerLevel = Math.Max(1, (int)(MinCell * 0.06));
            var lift = BoardArt.PlateLevels(Terrain.Mountains) * liftPerLevel;
            return (MinCell * StrikeBoard.MaxSide) + LabelBottom + TopPad + lift;
        }
    }

    public static double CeilingHeight
    {
        get
        {
            var liftPerLevel = Math.Max(1, (int)(MaxCell * 0.06));
            var lift = BoardArt.PlateLevels(Terrain.Mountains) * liftPerLevel;
            return (MaxCell * StrikeBoard.MaxSide) + LabelBottom + TopPad + lift;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = Math.Max(1, board.Width);
        var h = Math.Max(1, board.Height);
        var availW = double.IsInfinity(availableSize.Width) ? 560 : availableSize.Width;
        var availH = double.IsInfinity(availableSize.Height) ? 560 : availableSize.Height;

        var fit = (int)Math.Min((availW - LabelLeft - SideRight) / StrikeBoard.MaxSide,
                                (availH - LabelBottom - TopPad) / (StrikeBoard.MaxSide + 0.35));
        cell = Math.Clamp(fit, MinCell, MaxCell);

        return new Size((cell * w) + LabelLeft + SideRight,
                        (cell * h) + LabelBottom + TopPad + MaxLift());
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (tiles is null || tilesDirty || tileCell != cell)
        {
            RebuildTiles();
        }

        double ox = LabelLeft;
        double oy = TopPad + MaxLift();
        var wpx = board.Width * cell;
        var hpx = board.Height * cell;

        var half = board.Height / 2;
        DrawPlates(context, ox, oy, 0, mirror ? half : board.Height, frosted: mirror);
        if (mirror)
        {
            DrawPlates(context, ox, oy, half, board.Height, frosted: false);
            DrawSeam(context, ox, oy);
        }

        DrawLedgeShadows(context, ox, oy);

        var muted = Palette.SlateDark.Muted.ToColor();
        DrawSilhouette(context, ox, oy, wpx, hpx, muted);

        DrawBrackets(context, ox, oy, muted);
        DrawCoordinates(context, ox, oy, muted);
        DrawGhost(context, ox, oy);
    }

    private static readonly Dictionary<int, SolidColorBrush> SideBrushes = [];
    private static readonly Dictionary<int, SolidColorBrush> FrostSideBrushes = [];

    private static SolidColorBrush SideBrush(int value, bool frosted)
    {
        var cache = frosted ? FrostSideBrushes : SideBrushes;
        if (!cache.TryGetValue(value, out var brush))
        {
            var shade = BoardArt.MutedShade(value);
            double r = shade.R * 0.45;
            double g = shade.G * 0.45;
            double b = shade.B * 0.45;
            var opacity = 1.0;
            if (frosted)
            {
                var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                r = (r + ((luma - r) * 0.65)) * 0.8;
                g = (g + ((luma - g) * 0.65)) * 0.8;
                b = (b + ((luma - b) * 0.65)) * 0.8;
                opacity = 0.72;
            }

            brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b), opacity);
            cache[value] = brush;
        }

        return brush;
    }

    private void DrawPlates(DrawingContext context, double ox, double oy, int fromRow, int toRow,
                            bool frosted)
    {
        for (var ty = fromRow; ty < toRow; ty++)
        {
            for (var tx = 0; tx < board.Width; tx++)
            {
                var value = board.At(tx, ty);
                var lift = PlateLift(value);
                var src = new Rect(tx * cell, ty * cell, cell, cell);
                var dest = new Rect(ox + (tx * cell), oy + (ty * cell) - lift, cell, cell);

                if (lift > 0)
                {
                    context.FillRectangle(SideBrush(value, frosted),
                        new Rect(ox + (tx * cell), oy + ((ty + 1) * cell) - lift, cell, lift));
                }

                context.DrawImage(tiles!, src, dest);
            }
        }
    }

    private static readonly LinearGradientBrush LedgeShadow = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x52, 0, 0, 0), 0),
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1),
        },
    };

    private void DrawLedgeShadows(DrawingContext context, double ox, double oy)
    {
        for (var ty = 1; ty < board.Height; ty++)
        {
            for (var tx = 0; tx < board.Width; tx++)
            {
                var own = PlateLift(board.At(tx, ty));
                var north = PlateLift(board.At(tx, ty - 1));
                if (north <= own)
                {
                    continue;
                }

                var depth = Math.Min(north - own, cell / 3);
                context.FillRectangle(LedgeShadow,
                    new Rect(ox + (tx * cell), oy + (ty * cell) - own, cell, depth));
            }
        }
    }

    private void RebuildTiles()
    {
        var frostRows = mirror ? (board.Height / 2) * cell : 0;
        var pixels = BoardArt.Tiles(board, cell, frostRows);
        var size = new PixelSize(board.Width * cell, board.Height * cell);

        tiles?.Dispose();
        tiles = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var frame = tiles.Lock())
        {
            var rowBytes = size.Width * 4;
            for (var y = 0; y < size.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels, y * rowBytes, frame.Address + (y * frame.RowBytes), rowBytes);
            }
        }

        tileCell = cell;
        tilesDirty = false;
    }

    private void DrawSeam(DrawingContext context, double ox, double oy)
    {
        var half = board.Height / 2;
        var core = new Pen(new SolidColorBrush(Colors.White, 0.55), 1);
        var breath = new Pen(new SolidColorBrush(Colors.White, 0.14), 1);

        for (var tx = 0; tx < board.Width; tx++)
        {
            var y = oy + (half * cell) - PlateLift(board.At(tx, half)) - 0.5;
            var x0 = ox + (tx * cell);
            var x1 = x0 + cell;
            context.DrawLine(breath, new Point(x0, y - 1), new Point(x1, y - 1));
            context.DrawLine(core, new Point(x0, y), new Point(x1, y));
            context.DrawLine(breath, new Point(x0, y + 1), new Point(x1, y + 1));
        }
    }

    private void DrawGhost(DrawingContext context, double ox, double oy)
    {
        if (hoverX < 0 || hoverX >= board.Width || hoverY >= board.Height ||
            !StrikeBoard.CanPaint(hoverY, mirror, board.Height))
        {
            return;
        }

        var lift = PlateLift(board.At(hoverX, hoverY));
        var at = new Rect(ox + (hoverX * cell) + 1.5, oy + (hoverY * cell) - lift + 1.5,
                          cell - 3, cell - 3);
        var fill = new SolidColorBrush(BoardArt.MutedShade(brush).ToColor(), 0.55);
        var dash = new Pen(new SolidColorBrush(Colors.White, 0.8), 1.5,
                           new DashStyle([2.5, 2.5], 0));
        context.DrawRectangle(fill, dash, at);

        if (mirror)
        {
            var ex = board.Width - 1 - hoverX;
            var ey = board.Height - 1 - hoverY;
            var echoLift = PlateLift(board.At(ex, ey));
            var echo = new Rect(ox + (ex * cell) + 1, oy + (ey * cell) - echoLift + 1,
                                cell - 2, cell - 2);
            var ring = new Pen(new SolidColorBrush(Colors.White, 0.5), 2);
            context.DrawRectangle(null, ring, echo);
        }
    }

    private (int X, int Y) CellAt(Point p)
    {
        double ox = LabelLeft;
        double oy = TopPad + MaxLift();

        var tx = (int)Math.Floor((p.X - ox) / cell);
        if (tx < 0 || tx >= board.Width)
        {
            return (-1, -1);
        }

        for (var ty = board.Height - 1; ty >= 0; ty--)
        {
            var lift = PlateLift(board.At(tx, ty));
            var top = oy + (ty * cell) - lift;
            var bottom = oy + ((ty + 1) * cell);
            if (p.Y >= top && p.Y < bottom)
            {
                return (tx, ty);
            }
        }

        return (-1, -1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var (tx, ty) = CellAt(e.GetPosition(this));
        if (tx < 0)
        {
            return;
        }

        Focus();

        e.Pointer.Capture(this);
        painting = true;
        PaintCell(tx, ty);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (tx, ty) = CellAt(e.GetPosition(this));

        if (tx != hoverX || ty != hoverY)
        {
            hoverX = tx;
            hoverY = ty;
            InvalidateVisual();
        }

        Cursor = tx < 0 ? Plain
            : StrikeBoard.CanPaint(ty, mirror, board.Height) ? Cross : Barred;

        if (painting && tx >= 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            PaintCell(tx, ty);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        painting = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        hoverX = -1;
        hoverY = -1;
        Cursor = Plain;
        InvalidateVisual();
    }

    private void PaintCell(int tx, int ty)
    {
        if (!StrikeBoard.CanPaint(ty, mirror, board.Height))
        {
            return;
        }

        if (board.At(tx, ty) == brush)
        {
            return;
        }

        board.Paint(tx, ty, brush, mirror);
        tilesDirty = true;
        InvalidateVisual();
        Changed?.Invoke();
    }

    private void DrawSilhouette(DrawingContext context, double ox, double oy, double wpx, double hpx, Color muted)
    {
        var pen = new Pen(new SolidColorBrush(muted, 0.5), 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(ox, oy + hpx), isFilled: false);
            ctx.LineTo(new Point(ox, oy - PlateLift(board.At(0, 0))));
            for (var tx = 0; tx < board.Width; tx++)
            {
                var top = oy - PlateLift(board.At(tx, 0));
                ctx.LineTo(new Point(ox + (tx * cell), top));
                ctx.LineTo(new Point(ox + ((tx + 1) * cell), top));
            }

            ctx.LineTo(new Point(ox + wpx, oy + hpx));
            ctx.EndFigure(isClosed: true);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawBrackets(DrawingContext context, double ox, double oy, Color muted)
    {
        var depth = Math.Clamp(board.PlacementRows, 0, board.Height / 2);
        if (depth < 1)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(muted, 0.6), 1);
        var leftX = ox - 6.5;
        var rightX = ox + (board.Width * cell) + 6.5;
        var lastCol = board.Width - 1;
        var backRow = board.Height - depth;

        DrawBracket(context, pen, leftX, oy - PlateLift(board.At(0, 0)), oy + (depth * cell), 5);
        DrawBracket(context, pen, rightX, oy - PlateLift(board.At(lastCol, 0)), oy + (depth * cell), -5);
        DrawBracket(context, pen, leftX,
            oy + (backRow * cell) - PlateLift(board.At(0, backRow)), oy + (board.Height * cell), 5);
        DrawBracket(context, pen, rightX,
            oy + (backRow * cell) - PlateLift(board.At(lastCol, backRow)), oy + (board.Height * cell), -5);
    }

    private static void DrawBracket(DrawingContext context, Pen pen, double x, double topY, double bottomY, double tick)
    {
        context.DrawLine(pen, new Point(x, topY + 0.5), new Point(x, bottomY - 0.5));
        context.DrawLine(pen, new Point(x, topY + 0.5), new Point(x + tick, topY + 0.5));
        context.DrawLine(pen, new Point(x, bottomY - 0.5), new Point(x + tick, bottomY - 0.5));
    }

    private static readonly Typeface CoordFace = new(new FontFamily("Consolas"));
    private static FormattedText[]? coordLabels;

    private static FormattedText CoordLabel(int number, Color muted)
    {
        if (coordLabels is null)
        {
            var ink = new SolidColorBrush(muted, 0.9);
            coordLabels = new FormattedText[StrikeBoard.MaxSide];
            for (var n = 1; n <= StrikeBoard.MaxSide; n++)
            {
                coordLabels[n - 1] = new FormattedText(
                    n.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, CoordFace, 11, ink);
            }
        }

        return coordLabels[number - 1];
    }

    private void DrawCoordinates(DrawingContext context, double ox, double oy, Color muted)
    {
        for (var x = 1; x <= board.Width; x++)
        {
            var text = CoordLabel(x, muted);
            context.DrawText(text, new Point(
                ox + ((x - 1) * cell) + ((cell - text.Width) / 2),
                oy + (board.Height * cell) + 3));
        }

        for (var r = 1; r <= board.Height; r++)
        {
            var text = CoordLabel(r, muted);
            context.DrawText(text, new Point(
                ox - 10 - text.Width,
                oy + ((board.Height - r) * cell) + ((cell - text.Height) / 2)));
        }
    }
}
