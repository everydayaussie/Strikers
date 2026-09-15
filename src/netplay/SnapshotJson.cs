using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strikers.Netplay;

public sealed class SnapshotDto
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    [JsonPropertyName("aiSeat")] public int AiSeat { get; set; } = -1;

    [JsonPropertyName("t")] public string? T { get; set; }
    [JsonPropertyName("pieces")] public List<PieceDto> Pieces { get; set; } = [];
    [JsonPropertyName("terrain")] public List<int> Terrain { get; set; } = [];

    [JsonPropertyName("act")] public ActDto? Act { get; set; }

    [JsonPropertyName("match")] public MatchDto? Match { get; set; }

    [JsonPropertyName("placing")] public PlacingDto? Placing { get; set; }

    [JsonPropertyName("turn")] public int Turn { get; set; } = -1;

    [JsonPropertyName("commits")] public CommitsDto? Commits { get; set; }
}

public sealed class CommitsDto
{
    [JsonPropertyName("count")] public long Count { get; set; }
    [JsonPropertyName("lost")] public long Lost { get; set; }
    [JsonPropertyName("records")] public List<CommitDto> Records { get; set; } = [];
}

public sealed class CommitDto
{
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("whose")] public string Whose { get; set; } = "";
    [JsonPropertyName("unit")] public int Unit { get; set; } = -1;
    [JsonPropertyName("ptr")] public string Ptr { get; set; } = "";
    [JsonPropertyName("fx")] public int Fx { get; set; }
    [JsonPropertyName("fy")] public int Fy { get; set; }
    [JsonPropertyName("tx")] public int Tx { get; set; }
    [JsonPropertyName("ty")] public int Ty { get; set; }
    [JsonPropertyName("sx")] public int Sx { get; set; }
    [JsonPropertyName("sy")] public int Sy { get; set; }
}

public sealed class PlacingDto
{
    [JsonPropertyName("left")] public List<int> Left { get; set; } = [];
    [JsonPropertyName("active")] public int Active { get; set; } = -1;
    [JsonPropertyName("unreadable")] public bool Unreadable { get; set; }
}

public sealed class MatchDto
{
    [JsonPropertyName("over")] public bool Over { get; set; }

    [JsonPropertyName("winner")] public int Winner { get; set; } = -1;

    [JsonPropertyName("vp")] public List<int> Vp { get; set; } = [];

    [JsonPropertyName("vpMax")] public int VpMax { get; set; } = -1;
}

public sealed class ActDto
{
    [JsonPropertyName("unit")] public int Unit { get; set; } = -1;

    [JsonPropertyName("fx")] public int Fx { get; set; } = -1;
    [JsonPropertyName("fy")] public int Fy { get; set; } = -1;
    [JsonPropertyName("tx")] public int Tx { get; set; } = -1;
    [JsonPropertyName("ty")] public int Ty { get; set; } = -1;

    [JsonPropertyName("on")] public bool On { get; set; }
}

public sealed class PieceDto
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("health")] public int Health { get; set; }
    [JsonPropertyName("facing")] public int Facing { get; set; }
    [JsonPropertyName("owner")] public int Owner { get; set; }

    [JsonPropertyName("burst")] public bool Burst { get; set; }

    [JsonPropertyName("idx")] public int Idx { get; set; } = -1;

    [JsonPropertyName("acted")] public bool Acted { get; set; }

    [JsonPropertyName("acts")] public int Acts { get; set; } = -1;
    [JsonPropertyName("bursts")] public int Bursts { get; set; } = -1;

    [JsonPropertyName("skill")] public int Skill { get; set; } = -1;
    [JsonPropertyName("range")] public int Range { get; set; } = -1;

    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
}

public static class SnapshotJson
{
    public static bool TryParse(string json, out BoardSnapshot snapshot, out string error)
    {
        snapshot = null!;
        error = "";

        SnapshotDto? dto;
        try { dto = JsonSerializer.Deserialize<SnapshotDto>(json, Protocol.Json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { error = $"not valid snapshot JSON: {ex.Message}"; return false; }

        if (dto is null)
        {
            error = "snapshot JSON parsed to null";
            return false;
        }
        if (dto.Width is <= 0 or > 16 || dto.Height is <= 0 or > 16)
        { error = $"implausible board {dto.Width}x{dto.Height}"; return false; }
        if (dto.Terrain.Count != dto.Width * dto.Height)
        { error = $"terrain has {dto.Terrain.Count} entries, expected {dto.Width * dto.Height}"; return false; }

        foreach (var p in dto.Pieces)
        {
            if (p.X < 0 || p.X >= dto.Width || p.Y < 0 || p.Y >= dto.Height)
            { error = $"piece at ({p.X},{p.Y}) is off a {dto.Width}x{dto.Height} board"; return false; }
            if (p.Owner is not (0 or 1))
            {
                error = $"piece at ({p.X},{p.Y}) has owner {p.Owner}";
                return false;
            }
            if (p.Facing is < 0 or > 3)
            {
                error = $"piece at ({p.X},{p.Y}) has facing {p.Facing}";
                return false;
            }
        }

        ActRecord? act = null;
        if (dto.Act is { } a &&
            a.Fx >= 0 && a.Fx < dto.Width && a.Fy >= 0 && a.Fy < dto.Height &&
            a.Tx >= 0 && a.Tx < dto.Width && a.Ty >= 0 && a.Ty < dto.Height)
        {
            act = new ActRecord(a.Unit, a.Fx, a.Fy, a.Tx, a.Ty, a.On);
        }

        MatchState? match = null;
        if (dto.Match is { } m)
        {
            var vp0 = m.Vp.Count > 0 ? m.Vp[0] : -1;
            var vp1 = m.Vp.Count > 1 ? m.Vp[1] : -1;
            match = new MatchState(m.Over, m.Winner, vp0, vp1, m.VpMax);
        }

        PlacingState? placing = null;
        if (dto.Placing is { } pl && pl.Left.Count == 2)
        {
            placing = new PlacingState(pl.Left[0], pl.Left[1], pl.Active);
        }

        CommitBatch? commits = null;
        if (dto.Commits is { } c)
        {
            var kept = new List<CommitRecord>(c.Records.Count);
            foreach (var r in c.Records)
            {
                if (r.Kind is "move" or "attack")
                {
                    if (OffBoard(r.Fx, r.Fy, dto.Width, dto.Height) ||
                        OffBoard(r.Tx, r.Ty, dto.Width, dto.Height))
                    {
                        kept.Add(new CommitRecord(r.Seq, CommitTranscript.OffBoardKind, r.Whose, r.Unit, r.Ptr,
                                                  -1, -1, -1, -1, -1, -1));
                        continue;
                    }

                    kept.Add(new CommitRecord(r.Seq, r.Kind, r.Whose, r.Unit, r.Ptr,
                                              r.Fx, r.Fy, r.Tx, r.Ty, r.Sx, r.Sy));
                    continue;
                }

                if (r.Kind is "activate" or "burst")
                {
                    if (OffBoard(r.Sx, r.Sy, dto.Width, dto.Height))
                    {
                        kept.Add(new CommitRecord(r.Seq, CommitTranscript.OffBoardKind, r.Whose, r.Unit, r.Ptr,
                                                  -1, -1, -1, -1, -1, -1));
                        continue;
                    }

                    kept.Add(new CommitRecord(r.Seq, r.Kind, r.Whose, r.Unit, r.Ptr,
                                              -1, -1, -1, -1, r.Sx, r.Sy));
                    continue;
                }

                kept.Add(new CommitRecord(r.Seq, r.Kind, r.Whose, r.Unit, r.Ptr,
                                          r.Fx, r.Fy, r.Tx, r.Ty, r.Sx, r.Sy));
            }

            commits = new CommitBatch(c.Count, c.Lost, kept);
        }

        snapshot = new BoardSnapshot(dto.Width, dto.Height,
            dto.Pieces.Select(p => new Piece(p.X, p.Y, (byte)p.Health, (byte)p.Facing, p.Owner, p.Burst, p.Idx,
                                             p.Acted, p.Acts, p.Bursts, p.Skill, p.Range, p.Uuid)).ToList(),
            dto.Terrain.Select(t => (sbyte)t).ToList())
        {
            AiSeat = dto.AiSeat,
            Act = act,
            Match = match,
            Placing = placing,
            Turn = dto.Turn is 0 or 1 ? dto.Turn : -1,
            Stamp = dto.T,
            Commits = commits,
            PlacingUnreadable = dto.Placing is { Unreadable: true },
        };
        return true;
    }

    private static bool OffBoard(int x, int y, int width, int height)
    {
        return x < 0 || x >= width || y < 0 || y >= height;
    }
}
