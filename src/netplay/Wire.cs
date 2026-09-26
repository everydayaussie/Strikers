using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strikers.Netplay;

public enum MsgKind
{
    Hello,
    Paired,
    Ident,
    Setup,
    Ready,
    Move,
    TurnEnd,
    Hash,
    Place,
    Rematch,
    Halt,
    Bye,
    KeyEx,
    Role,
    Confirmed,
    Left,

    Sealed,

    Recording,

    KeyCommit,

    RecordingStart,

    Log,
}

public sealed class Frame
{
    [JsonPropertyName("v")] public int Version { get; set; } = Protocol.Version;
    [JsonPropertyName("kind")] public MsgKind Kind { get; set; }
    [JsonPropertyName("seq")] public int Seq { get; set; }

    [JsonPropertyName("room")] public string? Room { get; set; }
    [JsonPropertyName("seat")] public int Seat { get; set; } = -1;
    [JsonPropertyName("resumeFrom")] public int ResumeFrom { get; set; } = -1;

    [JsonPropertyName("build")] public string? Build { get; set; }

    [JsonPropertyName("netplay")] public string? NetplayVersion { get; set; }
    [JsonPropertyName("probe")] public string? ProbeVersion { get; set; }

    [JsonPropertyName("part")] public int Part { get; set; } = -1;
    [JsonPropertyName("parts")] public int Parts { get; set; } = -1;
    [JsonPropertyName("data")] public string? Data { get; set; }

    [JsonPropertyName("challenge")] public string? Challenge { get; set; }
    [JsonPropertyName("army")] public List<string>? Army { get; set; }
    [JsonPropertyName("placements")] public List<Placement>? Placements { get; set; }

    [JsonPropertyName("board")] public List<int>? Board { get; set; }

    [JsonPropertyName("boardWidth")] public int BoardWidth { get; set; } = -1;
    [JsonPropertyName("boardHeight")] public int BoardHeight { get; set; } = -1;
    [JsonPropertyName("placementRows")] public int PlacementRows { get; set; } = -1;

    [JsonPropertyName("victoryPoints")] public int VictoryPoints { get; set; } = -1;
    [JsonPropertyName("draftPoints")] public int DraftPoints { get; set; } = -1;

    [JsonPropertyName("first")] public string? First { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("place")] public Placement? Place { get; set; }
    [JsonPropertyName("placeIdx")] public int PlaceIdx { get; set; } = -1;

    [JsonPropertyName("moves")] public List<Move>? Moves { get; set; }

    [JsonPropertyName("final")] public bool Final { get; set; }

    [JsonPropertyName("turn")] public int Turn { get; set; }
    [JsonPropertyName("pieces")] public string? PieceHash { get; set; }
    [JsonPropertyName("terrain")] public string? TerrainHash { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }

    [JsonPropertyName("pub")] public string? PublicKey { get; set; }

    [JsonPropertyName("commit")] public string? Commit { get; set; }

    [JsonPropertyName("bound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Bound { get; set; }

    [JsonPropertyName("role")] public SessionRole? Role { get; set; }

    [JsonPropertyName("box")] public string? Box { get; set; }

    [JsonPropertyName("setupDigest")] public string? SetupDigest { get; set; }
}

public sealed class Placement
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("dir")] public byte Dir { get; set; }

    public Placement Rotated(int width = 8, int height = 8)
    {
        return new()
        {
            X = width - 1 - X,
            Y = height - 1 - Y,
            Dir = (byte)((Dir + 2) & 3),
        };
    }
}

public sealed class Move
{
    [JsonPropertyName("srcX")] public int SrcX { get; set; }
    [JsonPropertyName("srcY")] public int SrcY { get; set; }
    [JsonPropertyName("dstX")] public int DstX { get; set; }
    [JsonPropertyName("dstY")] public int DstY { get; set; }
    [JsonPropertyName("facing")] public byte Facing { get; set; }
    [JsonPropertyName("attack")] public bool Attack { get; set; }
    [JsonPropertyName("targetX")] public int TargetX { get; set; } = -1;
    [JsonPropertyName("targetY")] public int TargetY { get; set; } = -1;
    [JsonPropertyName("burst")] public bool Burst { get; set; }

    [JsonPropertyName("atkX")] public int AtkX { get; set; } = -1;
    [JsonPropertyName("atkY")] public int AtkY { get; set; } = -1;

    [JsonPropertyName("landX")] public int LandX { get; set; } = -1;
    [JsonPropertyName("landY")] public int LandY { get; set; } = -1;

    public bool Charged
    {
        get
        {
            return Attack && AtkX < 0 && AtkY < 0 && LandX >= 0 && LandY >= 0
                   && (LandX != DstX || LandY != DstY);
        }
    }

    public (int X, int Y) StrikeFrom
    {
        get
        {
            if (AtkX < 0 || AtkY < 0)
            {
                return (DstX, DstY);
            }

            return (AtkX, AtkY);
        }
    }

    public Move Rotated(int width = 8, int height = 8)
    {
        return new()
        {
            SrcX = width - 1 - SrcX,
            SrcY = height - 1 - SrcY,
            DstX = width - 1 - DstX,
            DstY = height - 1 - DstY,
            Facing = (byte)((Facing + 2) & 3),
            Attack = Attack,
            TargetX = TargetX < 0 ? -1 : width - 1 - TargetX,
            TargetY = TargetY < 0 ? -1 : height - 1 - TargetY,
            Burst = Burst,
            AtkX = AtkX < 0 ? -1 : width - 1 - AtkX,
            AtkY = AtkY < 0 ? -1 : height - 1 - AtkY,
            LandX = LandX < 0 ? -1 : width - 1 - LandX,
            LandY = LandY < 0 ? -1 : height - 1 - LandY,
        };
    }
}

public static class FrameLimits
{
    public const int MaxMoves = 32;

    public const int MaxPlacements = 16;

    public const int MaxBoard = 64;

    public const int MaxLine = 64 * 1024;

    public const int MaxHelloLine = 1024;

    public static int LineCapFor(bool saidHello)
    {
        return saidHello ? MaxLine : MaxHelloLine;
    }

    public const int MaxFramesPerWindow = 40;
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    public const int MaxPendingTurns = 8;

    public const int MaxRecordingParts = 64;

    public const int MaxRecordingStartParts = 6;

    public const int MaxRecordingTailParts = 50;

    public const int MaxLogParts = 8;

    public const int MaxHaltParts = 64;

    public const int MaxRecordingPartChars = 32 * 1024;

    public const int MaxRecordingBytes = 1536 * 1024;

    public const int MaxRecordingStartBytes = 128 * 1024;

    public const int MaxRecordingTailBytes = MaxRecordingTailParts * MaxRecordingPartChars / 4 * 3;

    public const int MaxLogBytes = 192 * 1024;

    public const int MaxReasonChars = 300;

    public static string Safe(string? s, int max = MaxReasonChars)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        var kept = new System.Text.StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s)
        {
            if (kept.Length >= max)
            {
                kept.Append("...");
                break;
            }

            if (c is >= ' ' and <= '~')
            {
                kept.Append(c);
            }
            else if (!char.IsControl(c))
            {
                kept.Append('?');
            }
        }

        return kept.ToString();
    }

    public static string? SquaresOff(IReadOnlyList<Move> moves, int width, int height)
    {
        for (var i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            if (Outside(m.SrcX, width) || Outside(m.SrcY, height) || Outside(m.DstX, width) || Outside(m.DstY, height)
                || m.TargetX >= width || m.TargetY >= height || m.AtkX >= width || m.AtkY >= height
                || m.LandX >= width || m.LandY >= height || m.LandX < -1 || m.LandY < -1)
            {
                return $"action {i + 1} names a square off this {width}x{height} board";
            }
        }

        return null;
    }

    public static string? SquareOff(Placement p, int width, int height)
    {
        if (Outside(p.X, width) || Outside(p.Y, height))
        {
            return $"a placement at ({p.X},{p.Y}), off this {width}x{height} board";
        }

        return null;
    }

    private static bool Outside(int value, int size)
    {
        return value < 0 || value >= size;
    }

    public const int HashLength = 16;

    public static bool IsHash(string? s)
    {
        if (s is not { Length: HashLength })
        {
            return false;
        }

        foreach (var c in s)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsCommitment(string? s)
    {
        if (s is not { Length: <= 64 })
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(s).Length == KeyExchange.CommitmentLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string? Refuse(Frame f)
    {
        if ((f.Commit is not null || f.Kind == MsgKind.KeyCommit) && !IsCommitment(f.Commit))
        {
            return "a key commitment that is not 32 bytes";
        }

        if ((f.PieceHash is not null && !IsHash(f.PieceHash)) || (f.TerrainHash is not null && !IsHash(f.TerrainHash))
            || (f.Kind == MsgKind.Hash && (f.PieceHash is null || f.TerrainHash is null)))
        {
            return $"a board hash that is not {HashLength} hex digits";
        }

        if (f.Moves is { Count: > MaxMoves })
        {
            return $"a turn of {f.Moves.Count} actions, over the {MaxMoves} a legal turn can hold";
        }

        if (f.Placements is { Count: > MaxPlacements })
        {
            return $"a setup of {f.Placements.Count} placements, over the {MaxPlacements} a seat can hold";
        }

        if (f.Army is { Count: > MaxPlacements })
        {
            return $"an army of {f.Army.Count} machines, over the {MaxPlacements} a seat can hold";
        }

        if (f.Board is { Count: > MaxBoard })
        {
            return $"a board of {f.Board.Count} squares, over the {MaxBoard} this game's boards hold";
        }

        return null;
    }
}

public sealed class RateLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _seen = new();

    public RateLimiter(int max, TimeSpan window)
    {
        _max = max;
        _window = window;
    }

    public bool Allow(DateTime now)
    {
        while (_seen.Count > 0 && now - _seen.Peek() > _window)
        {
            _seen.Dequeue();
        }

        if (_seen.Count >= _max)
        {
            return false;
        }

        _seen.Enqueue(now);
        return true;
    }
}

public enum SessionRole
{
    Lobby,
    Play,
}

public enum SessionPhase
{
    NoMatch,
    Placing,
    Playing,
    Over,
}

public static class FrameGate
{
    public static string? Refuse(SessionPhase phase, Frame f, IReadOnlySet<int> writtenSlots,
                                 IReadOnlySet<(int X, int Y)> writtenSquares)
    {
        switch (f.Kind)
        {
            case MsgKind.Rematch when phase is SessionPhase.Placing or SessionPhase.Playing:
                return "a rematch while a match is still running";

            case MsgKind.Place when f.PlaceIdx >= 0 && writtenSlots.Contains(f.PlaceIdx):
                return $"a second placement for machine {f.PlaceIdx}, which is already written";

            case MsgKind.Place when f.Place is { } square && writtenSquares.Contains((square.X, square.Y)):
                return $"a placement on ({square.X},{square.Y}), where one of their machines is already placed";

            case MsgKind.Place when phase is SessionPhase.Playing or SessionPhase.Over:
                return "a placement after the placement phase has finished";

            case MsgKind.Move when phase is SessionPhase.Over:
                return "a turn after the match has already ended";

            default:
                return null;
        }
    }
}

public static class Protocol
{
    public const int Version = 30;

    public static string Encode(Frame f)
    {
        return JsonSerializer.Serialize(f, WireJson.Default.Frame);
    }

    public static Frame? Decode(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, WireJson.Default.Frame);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }
}
