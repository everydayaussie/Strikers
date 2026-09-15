using System.Security.Cryptography;
using System.Text;

namespace Strikers.Netplay;

public readonly record struct Piece(int X, int Y, byte Health, byte Facing, int Owner, bool Burst = false,
                                    int Idx = -1, bool Acted = false, int Acts = -1, int Bursts = -1,
                                    int Skill = -1, int Range = -1, string Uuid = "");

public readonly record struct ActRecord(int Unit, int Fx, int Fy, int Tx, int Ty, bool On);

public readonly record struct MatchState(bool Over, int Winner, int Vp0, int Vp1, int VpMax);

public readonly record struct PlacingState(int Left0, int Left1, int Active)
{
    public int LeftFor(int seat)
    {
        return seat == 0 ? Left0 : Left1;
    }
}

public sealed record BoardSnapshot(int Width, int Height, IReadOnlyList<Piece> Pieces, IReadOnlyList<sbyte> Terrain)
{
    public int AiSeat { get; init; } = -1;
    public string? Stamp { get; init; }
    public ActRecord? Act { get; init; }
    public MatchState? Match { get; init; }
    public PlacingState? Placing { get; init; }
    public int Turn { get; init; } = -1;

    public bool PlacingUnreadable { get; init; }

    public CommitBatch? Commits { get; init; }
    public int LocalOwner
    {
        get
        {
            return AiSeat is 0 or 1 ? 1 - AiSeat : -1;
        }
    }

    public bool IsTornAfter(BoardSnapshot prev)
    {
        var n = Pieces.Count;
        if (n < 2 || n != prev.Pieces.Count)
        {
            return false;
        }

        if (!SameUnit(Pieces[n - 1], Pieces[n - 2]))
        {
            return false;
        }

        foreach (var dead in prev.Pieces)
        {
            if (dead.Health != 0)
            {
                continue;
            }

            var survives = false;
            foreach (var p in Pieces)
            {
                if (SameUnit(p, dead))
                {
                    survives = true;
                    break;
                }
            }

            if (!survives)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameUnit(Piece a, Piece b)
    {
        return (a with { Idx = b.Idx }) == b;
    }
}

public static class BoardHash
{
    public static BoardSnapshot Canonicalise(BoardSnapshot s, int seat)
    {
        if (seat is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "seat must be 0 or 1; hash before pairing is meaningless");
        }

        if (seat == 0)
        {
            return s;
        }

        var pieces = s.Pieces
            .Select(p => new Piece(s.Width - 1 - p.X, s.Height - 1 - p.Y, p.Health,
                                   (byte)((p.Facing + 2) & 3), 1 - p.Owner, p.Burst, p.Idx, p.Acted, p.Acts, p.Bursts,
                                   p.Skill, p.Range))
            .ToList();

        var terrain = new sbyte[s.Terrain.Count];
        for (var y = 0; y < s.Height; y++)
        {
            for (var x = 0; x < s.Width; x++)
            {
                terrain[(s.Height - 1 - y) * s.Width + (s.Width - 1 - x)] = s.Terrain[y * s.Width + x];
            }
        }

        return s with { Pieces = pieces, Terrain = terrain };
    }

    public static string Pieces(BoardSnapshot s, int seat)
    {
        var c = Canonicalise(s, seat);
        var sb = new StringBuilder();
        sb.Append(c.Width).Append('x').Append(c.Height).Append(';');

        foreach (var p in c.Pieces.Where(p => p.Health > 0).OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            sb.Append(p.Owner).Append(':').Append(p.X).Append(',').Append(p.Y)
              .Append(':').Append(p.Health).Append(':').Append(p.Facing & 3).Append(';');
        }

        return Digest(sb.ToString());
    }

    public static string Terrain(BoardSnapshot s, int seat)
    {
        var c = Canonicalise(s, seat);
        var sb = new StringBuilder();
        foreach (var t in c.Terrain)
        {
            sb.Append(t).Append(',');
        }

        return Digest(sb.ToString());
    }

    private static string Digest(string s)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant()[..16];
    }
}
