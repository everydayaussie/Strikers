using System.Text.Json;

namespace Strikers.Core;

public static class Terrain
{
    public const int Chasm = -2;
    public const int Marsh = -1;
    public const int Grassland = 0;
    public const int Forest = 1;
    public const int Hills = 2;
    public const int Mountains = 3;

    public static readonly int[] Cycle = [Chasm, Marsh, Grassland, Forest, Hills, Mountains];

    public static string Name(int value)
    {
        return value switch
        {
            Chasm => "Chasm",
            Marsh => "Marsh",
            Grassland => "Grassland",
            Forest => "Forest",
            Hills => "Hills",
            Mountains => "Mountains",
            _ => $"? {value}",
        };
    }

    public static Rgb Shade(int value)
    {
        return value switch
        {
            Chasm => new Rgb(70, 76, 100),
            Marsh => new Rgb(96, 104, 74),
            Grassland => new Rgb(126, 168, 90),
            Forest => new Rgb(58, 110, 62),
            Hills => new Rgb(158, 138, 96),
            Mountains => new Rgb(120, 120, 128),
            _ => new Rgb(255, 0, 255),
        };
    }

    public static Rgb Ink(int value)
    {
        return value is Chasm or Forest or Marsh ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
    }
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string ToHex()
    {
        return $"#{R:X2}{G:X2}{B:X2}";
    }
}

public sealed class StrikeBoard
{
    public const int Size = 8;
    public const int MaxSide = 8;

    public string Name { get; set; } = "Untitled";

    public int Width { get; init; } = Size;
    public int Height { get; init; } = Size;
    public int PlacementRows { get; set; } = 2;

    public static StrikeBoard Stock()
    {
        return new StrikeBoard
        {
            Name = Play.StockBoard,
            Width = Size,
            Height = Size,
            PlacementRows = 2,
            Cells =
            [
                1, 1, 1, 0, 1, 1, 1, 1,
                1, 1, 0, 0, 0, 1, 1, 1,
                2, 2, 0, 0, 0, 0, 0, 1,
                1, 0, 0, 0, 1, 0, 0, 0,
                0, 0, 0, 1, 0, 0, 0, 1,
                1, 0, 0, 0, 0, 0, 2, 2,
                1, 1, 1, 0, 0, 0, 1, 1,
                1, 1, 1, 1, 0, 1, 1, 1,
            ],
        };
    }

    private int[]? _cells;

    public int[] Cells
    {
        get
        {
            return _cells ??= new int[Width * Height];
        }
        init
        {
            _cells = value;
        }
    }

    public int At(int x, int y)
    {
        return Cells[(y * Width) + x];
    }

    public void Paint(int x, int y, int value, bool mirror = true)
    {
        Cells[(y * Width) + x] = value;
        if (mirror)
        {
            Cells[((Height - 1 - y) * Width) + (Width - 1 - x)] = value;
        }
    }

    public static bool CanPaint(int y, bool mirror, int height = Size)
    {
        if (!mirror)
        {
            return true;
        }

        return y >= height / 2;
    }

    public const string SharePrefix = "SB-";

    public const int MaxShareLength = 64;

    private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    private const int CheckSpan = 36 * 36 * 36;

    public string ToShareString()
    {
        var half = IsSymmetric();
        var written = half ? (Cells.Length + 1) / 2 : Cells.Length;

        var head = ((Width - 1) << 7) | ((Height - 1) << 4) | ((PlacementRows - 1) << 1) | (half ? 1 : 0);
        var body = new char[(written + 1) / 2];
        for (var i = 0; i < written; i += 2)
        {
            var first = Cells[i] + 2;

            var second = i + 1 < written ? Cells[i + 1] + 2 : 0;
            body[i / 2] = Base36[(first * 6) + second];
        }

        var payload = ToBase36(head, 2) + new string(body);
        return SharePrefix + payload + ToBase36((int)(Hash(payload) % CheckSpan), 3);
    }

    public bool IsSymmetric()
    {
        for (var i = 0; i < Cells.Length / 2; i++)
        {
            if (Cells[i] != Cells[Cells.Length - 1 - i])
            {
                return false;
            }
        }

        return true;
    }

    private static string ToBase36(int value, int width)
    {
        var text = new char[width];
        for (var i = width - 1; i >= 0; i--)
        {
            text[i] = Base36[value % 36];
            value /= 36;
        }

        return new string(text);
    }

    private static bool FromBase36(string text, out int value)
    {
        value = 0;
        foreach (var c in text)
        {
            var digit = Base36.IndexOf(c);
            if (digit < 0)
            {
                value = 0;
                return false;
            }

            value = (value * 36) + digit;
        }

        return true;
    }

    public static StrikeBoard? FromShareString(string? text, out string? problem)
    {
        var trimmed = (text ?? "").Trim();

        if (trimmed.Length > MaxShareLength)
        {
            problem = $"that is longer than any shared board, which is at most {MaxShareLength} characters";
            return null;
        }

        if (!trimmed.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"that does not look like a shared board, which starts with {SharePrefix}";
            return null;
        }

        return ReadPayload(trimmed[SharePrefix.Length..], out problem);
    }

    private static StrikeBoard? ReadPayload(string rest, out string? problem)
    {
        var text = rest.ToLowerInvariant();

        if (text.Length < 5 || !FromBase36(text[..2], out var head))
        {
            problem = "that is not shaped like a shared board. Copy the whole line and paste it again.";
            return null;
        }

        if (head > 1023)
        {
            if (FromBase36(text[^3..], out var futureCheck) &&
                futureCheck == (int)(Hash(text[..^3]) % CheckSpan))
            {
                problem = "that board is from a newer Strikers than this one. Update both PCs to " +
                          "the same Strikers version and paste it again.";
            }
            else
            {
                problem = "that is not shaped like a shared board. Copy the whole line and paste it again.";
            }

            return null;
        }

        var width = ((head >> 7) & 7) + 1;
        var height = ((head >> 4) & 7) + 1;
        var depth = ((head >> 1) & 7) + 1;
        var half = (head & 1) == 1;

        var count = width * height;
        var written = half ? (count + 1) / 2 : count;
        var expected = 2 + ((written + 1) / 2) + 3;
        if (text.Length != expected)
        {
            problem = $"that board says it is {width} by {height}, which takes {expected} characters, " +
                      $"but it carries {text.Length}. Copy the whole line and paste it again.";
            return null;
        }

        if (!FromBase36(text[^3..], out var given) || given != (int)(Hash(text[..^3]) % CheckSpan))
        {
            problem = "that board did not survive the trip: its check digits do not match. Copy it again.";
            return null;
        }

        foreach (var c in text)
        {
            if (!Base36.Contains(c))
            {
                problem = "that shared board carries a character that does not belong in one. " +
                          "Copy the whole line and paste it again.";
                return null;
            }
        }

        if (depth * 2 > height)
        {
            problem = $"that board asks for a placing depth of {depth} on {height} rows, which would " +
                      "make the two players' placing zones overlap.";
            return null;
        }

        var cells = new int[count];
        for (var i = 0; i < written; i += 2)
        {
            var pair = Base36.IndexOf(text[2 + (i / 2)]);
            cells[i] = (pair / 6) - 2;
            if (i + 1 < written)
            {
                cells[i + 1] = (pair % 6) - 2;
            }
        }

        for (var i = written; i < count; i++)
        {
            cells[i] = cells[count - 1 - i];
        }

        problem = null;
        return new StrikeBoard
        {
            Width = width,
            Height = height,
            PlacementRows = depth,
            Cells = cells,
        };
    }

    private static uint Hash(string body)
    {
        var hash = 2166136261u;
        foreach (var c in body)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    public string? Problem()
    {
        if (Width is < 1 or > MaxSide || Height is < 1 or > MaxSide)
        {
            return $"this board is {Width} by {Height}, and a board runs 1 to {MaxSide} each way, because " +
                   "the game has memory for no more than that";
        }

        if (Cells.Length != Width * Height)
        {
            return $"this board says it is {Width} by {Height} but carries {Cells.Length} squares";
        }

        for (var i = 0; i < Cells.Length; i++)
        {
            if (Cells[i] is < Terrain.Chasm or > Terrain.Mountains)
            {
                return $"the square at column {(i % Width) + 1}, row {Height - (i / Width)} holds " +
                       $"{Cells[i]}, which is not a terrain this game has. Repaint it or fix boards.json";
            }
        }

        if (PlacementRows < 1 || PlacementRows > MaxSide)
        {
            return $"a placing depth of {PlacementRows} is outside 1 to {MaxSide}, which is all the " +
                   "share format and the game's own rows can carry";
        }

        if (PlacementRows > Height)
        {
            return $"a placing depth of {PlacementRows} does not fit on {Height} rows, so the zone " +
                   "would run off the end of the board";
        }

        return null;
    }

    public StrikeBoard Reshaped(int width, int height, int placementRows)
    {
        var next = new StrikeBoard
        {
            Name = Name,
            Width = width,
            Height = height,
            PlacementRows = placementRows,
        };

        for (var y = 0; y < Math.Min(height, Height); y++)
        {
            for (var x = 0; x < Math.Min(width, Width); x++)
            {
                next.Cells[(y * width) + x] = At(x, y);
            }
        }

        return next;
    }

    public static StrikeBoard Random(int width, int height, int placementRows, bool symmetric, Random rng)
    {
        var board = new StrikeBoard { Width = width, Height = height, PlacementRows = placementRows };
        var cells = board.Cells;
        var count = width * height;
        var written = symmetric ? (count + 1) / 2 : count;

        for (var i = 0; i < written; i++)
        {
            var value = Terrain.Cycle[rng.Next(Terrain.Cycle.Length)];
            cells[i] = value;
            if (symmetric)
            {
                cells[count - 1 - i] = value;
            }
        }

        return board;
    }

    public const int MinRandomSide = 4;

    public static (int Width, int Height, int PlacementRows) RandomShape(Random rng)
    {
        var width = rng.Next(MinRandomSide, MaxSide + 1);
        var height = rng.Next(width, MaxSide + 1);
        var placementRows = rng.Next(1, Math.Max(1, height / 2) + 1);
        return (width, height, placementRows);
    }

    public string SetBoardCommand(string challengeUuid)
    {
        return "live-probe --set-board " + string.Join(' ', Cells) +
               $" --board-game {challengeUuid} --yes";
    }
}

public static class BoardStore
{
    public const int MaxBoards = 32;

    public static bool HasRoom(int saved)
    {
        return saved < MaxBoards;
    }

    public static string Path()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "boards.json");
    }

    public static List<StrikeBoard> ReadAll()
    {
        return ReadAll(Path(), out _);
    }

    public static List<StrikeBoard> ReadAll(out string? problem)
    {
        return ReadAll(Path(), out problem);
    }

    internal static List<StrikeBoard> ReadAll(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Normalise(JsonSerializer.Deserialize<List<StrikeBoard>>(File.ReadAllText(path)));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = Play.UnreadableFile("boards.json");
            return [];
        }
    }

    public static List<StrikeBoard> Normalise(List<StrikeBoard>? read)
    {
        var boards = new List<StrikeBoard>();
        foreach (var board in read ?? [])
        {
            if (board == null)
            {
                continue;
            }

            if (board.Width is < 1 or > StrikeBoard.MaxSide ||
                board.Height is < 1 or > StrikeBoard.MaxSide)
            {
                continue;
            }

            if (board.Cells.Length != board.Width * board.Height)
            {
                continue;
            }

            var deepest = Math.Max(1, board.Height / 2);
            board.PlacementRows = Math.Clamp(board.PlacementRows, 1, deepest);

            board.Name = Army.CleanName(board.Name, Army.MaxStoredName);
            if (board.Name.Length == 0)
            {
                board.Name = "Untitled";
            }

            boards.Add(board);

            if (boards.Count >= MaxBoards)
            {
                break;
            }
        }

        return boards;
    }

    public static string FreeName(string wanted, IEnumerable<StrikeBoard> existing)
    {
        return Names.Free(wanted, existing.Select(b => b.Name), Army.MaxStoredName);
    }

    public static string? WriteAll(List<StrikeBoard> boards)
    {
        return WriteAll(Path(), boards);
    }

    internal static string? WriteAll(string path, List<StrikeBoard> boards)
    {
        try
        {
            Play.WriteWhole(path, JsonSerializer.Serialize(boards, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Play.WithoutPath($"could not write {path}: {e.Message}", path);
        }
    }
}
