namespace Strikers.Netplay;

public readonly record struct CommitRecord(long Seq, string Kind, string Whose, int Unit, string Ptr,
                                           int Fx, int Fy, int Tx, int Ty, int Sx, int Sy)
{
    public bool IsHuman
    {
        get
        {
            return Whose == "human";
        }
    }
}

public sealed record CommitBatch(long Count, long Lost, IReadOnlyList<CommitRecord> Records);

public readonly record struct TranscriptAction(long Seq, TranscriptKind Kind, string Ptr, int Unit,
                                               int FromX, int FromY, int ToX, int ToY, bool Burst);

public enum TranscriptKind
{
    Move,
    Attack,
    Rotate,
}

internal static class CommitTranscript
{
    public const string OffBoardKind = "offboard";

    public static List<CommitRecord> HumanRecords(IReadOnlyList<BoardSnapshot> samples)
    {
        var seen = new HashSet<long>();
        var records = new List<CommitRecord>();
        foreach (var s in samples)
        {
            if (s.Commits is not { } batch)
            {
                continue;
            }

            foreach (var r in batch.Records)
            {
                if (r.IsHuman && seen.Add(r.Seq))
                {
                    records.Add(r);
                }
            }
        }

        records.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return records;
    }

    public static bool HasRing(IReadOnlyList<BoardSnapshot> samples)
    {
        foreach (var s in samples)
        {
            if (s.Commits is not null)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasRecords(IReadOnlyList<BoardSnapshot> samples)
    {
        foreach (var s in samples)
        {
            if (s.Commits is { } batch && batch.Records.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static long Lost(IReadOnlyList<BoardSnapshot> samples)
    {
        var lost = 0L;
        foreach (var s in samples)
        {
            if (s.Commits is { } batch)
            {
                lost += batch.Lost;
            }
        }

        return lost;
    }

    public sealed record Result(List<TranscriptAction> Actions, string? Refusal, string? NotCovered = null,
                                IReadOnlyList<long>? Cancelled = null);

    private static Result NotCoveredLateStart(string kind, long seq)
    {
        return new Result([], null,
                          $"the game committed {kind} (record {seq}) with no machine activated in this " +
                          "slice, so the reader started after the turn did");
    }

    public static Result Read(IReadOnlyList<BoardSnapshot> samples)
    {
        var lost = Lost(samples);
        if (lost > 0)
        {
            return new Result([], $"the game's commit ring dropped {lost} record(s) before they were read, " +
                                  "so this turn cannot be accounted for");
        }

        if (Unattributable(samples) is { } unknown)
        {
            return new Result([], $"the game's commit ring holds a record (record {unknown.Seq}) whose " +
                                  $"controller this build does not recognise (\"{unknown.Whose}\"), so the " +
                                  "turn cannot be told from the opponent's");
        }

        var records = HumanRecords(samples);
        var actions = new List<TranscriptAction>();
        var cancelled = new List<long>();
        var active = "";
        var burstPending = false;
        CommitRecord pendingBurst = default;

        foreach (var r in records)
        {
            switch (r.Kind)
            {
                case "activate":
                    if (burstPending)
                    {
                        if (NotCancelled(samples, records, pendingBurst, r) is { } activateWhy)
                        {
                            return new Result([], $"the game committed an Overcharge (record {pendingBurst.Seq}) " +
                                                  $"and then activated another machine (record {r.Seq}) before " +
                                                  $"the action it bought, and it was not a cancelled one " +
                                                  $"({activateWhy}), which this reading does not cover");
                        }

                        cancelled.Add(pendingBurst.Seq);
                        burstPending = false;
                    }

                    active = r.Ptr;
                    break;

                case "burst":
                    if (active.Length == 0)
                    {
                        return NotCoveredLateStart("an Overcharge", r.Seq);
                    }

                    if (burstPending)
                    {
                        if (NotCancelled(samples, records, pendingBurst, r) is { } burstWhy)
                        {
                            return new Result([], $"the game committed an Overcharge (record {pendingBurst.Seq}) " +
                                                  $"and then another (record {r.Seq}) before the action the first " +
                                                  $"bought, and the first was not a cancelled one ({burstWhy}), " +
                                                  "which this reading does not cover");
                        }

                        cancelled.Add(pendingBurst.Seq);
                    }

                    active = r.Ptr;
                    burstPending = true;
                    pendingBurst = r;
                    break;

                case "move":
                case "attack":
                    if (active.Length == 0)
                    {
                        return NotCoveredLateStart($"a {r.Kind}", r.Seq);
                    }

                    if (r.Ptr != active)
                    {
                        return new Result([], $"the game committed a {r.Kind} (record {r.Seq}) for a machine other " +
                                              "than the activated one, which this reading does not cover");
                    }

                    var kind = r.Kind == "attack" ? TranscriptKind.Attack
                             : r.Fx == r.Tx && r.Fy == r.Ty ? TranscriptKind.Rotate
                             : TranscriptKind.Move;
                    var bought = burstPending;
                    if (burstPending && NotCancelled(samples, records, pendingBurst, r) is null)
                    {
                        cancelled.Add(pendingBurst.Seq);
                        bought = false;
                    }

                    actions.Add(new TranscriptAction(r.Seq, kind, r.Ptr, r.Unit,
                                                     r.Fx, r.Fy, r.Tx, r.Ty, bought));
                    burstPending = false;
                    break;

                case OffBoardKind:
                    return new Result([], null,
                                      $"the game committed a record (record {r.Seq}) naming a square off the board, " +
                                      "so the turn is read from the board instead");

                default:
                    return new Result([], $"the game's commit ring holds a record of kind \"{r.Kind}\" (record " +
                                          $"{r.Seq}), which this reading does not cover");
            }
        }

        if (burstPending)
        {
            if (NotCancelled(samples, records, pendingBurst, null) is { } endWhy)
            {
                return new Result([], $"the turn ends on an Overcharge (record {pendingBurst.Seq}) with no action " +
                                      $"recorded after it, and it was not a cancelled one ({endWhy}), which this " +
                                      "reading does not cover");
            }

            cancelled.Add(pendingBurst.Seq);
        }

        return new Result(actions, null, null, cancelled);
    }

    private static string? NotCancelled(IReadOnlyList<BoardSnapshot> samples, IReadOnlyList<CommitRecord> records,
                                        CommitRecord burst, CommitRecord? next)
    {
        if (burst.Unit < 0)
        {
            return "the machine it names is not on the board";
        }

        var open = SampleCarrying(samples, burst.Seq);
        if (open < 0)
        {
            return "its record is in none of the turn's samples";
        }

        var last = samples.Count - 1;
        var healthEnd = last;
        if (next is { } n)
        {
            healthEnd = SampleCarrying(samples, n.Seq);
            if (healthEnd < 0)
            {
                return "the record after it is in none of the turn's samples";
            }
        }

        var counterEnd = last;
        foreach (var later in records)
        {
            if (later.Seq > burst.Seq && later.Kind == "burst" && later.Ptr == burst.Ptr)
            {
                var at = SampleCarrying(samples, later.Seq);
                if (at >= 0)
                {
                    counterEnd = at;
                }

                break;
            }
        }

        if (counterEnd < healthEnd)
        {
            counterEnd = healthEnd;
        }

        Piece? prev = null;
        var owner = -1;
        var health = 0;
        var bursts = 0;
        for (var k = open; k <= counterEnd; k++)
        {
            Piece? found = null;
            if (prev is not { } before)
            {
                found = BySlot(samples[k].Pieces, burst.Unit);
            }
            else if (samples[k].Pieces.Count == samples[k - 1].Pieces.Count)
            {
                found = BySlot(samples[k].Pieces, before.Idx);
            }
            else if (samples[k].Pieces.Count < samples[k - 1].Pieces.Count)
            {
                found = BySquare(samples[k].Pieces, before.Owner, before.X, before.Y);
            }
            else
            {
                return $"a machine appeared at sample {k + 1}";
            }

            if (found is not { } p)
            {
                return $"the machine it names cannot be followed at sample {k + 1}";
            }

            if (p.Bursts < 0)
            {
                return "the snapshot carries no Overcharge counter";
            }

            if (prev is null)
            {
                if (p.Burst)
                {
                    return "the machine already carried the Overcharge mark";
                }

                owner = p.Owner;
                health = p.Health;
                bursts = p.Bursts;
            }
            else if (p.Owner != owner)
            {
                return $"the slot it was followed by changed hands at sample {k + 1}";
            }

            if (p.Burst || p.Bursts > bursts)
            {
                return $"the machine shows the Overcharge taken at sample {k + 1}";
            }

            if (k <= healthEnd && p.Health < health)
            {
                return $"the machine lost health at sample {k + 1}";
            }

            prev = p;
        }

        return null;
    }

    private static int SampleCarrying(IReadOnlyList<BoardSnapshot> samples, long seq)
    {
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Commits is not { } batch)
            {
                continue;
            }

            foreach (var r in batch.Records)
            {
                if (r.Seq == seq)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static Piece? BySlot(IReadOnlyList<Piece> pieces, int slot)
    {
        foreach (var p in pieces)
        {
            if (p.Idx == slot)
            {
                return p;
            }
        }

        return null;
    }

    private static Piece? BySquare(IReadOnlyList<Piece> pieces, int owner, int x, int y)
    {
        Piece? match = null;
        foreach (var p in pieces)
        {
            if (p.Owner == owner && p.X == x && p.Y == y)
            {
                if (match is not null)
                {
                    return null;
                }

                match = p;
            }
        }

        return match;
    }

    private static CommitRecord? Unattributable(IReadOnlyList<BoardSnapshot> samples)
    {
        foreach (var s in samples)
        {
            if (s.Commits is not { } batch)
            {
                continue;
            }

            foreach (var r in batch.Records)
            {
                if (r.Whose is not ("human" or "ai"))
                {
                    return r;
                }
            }
        }

        return null;
    }

    internal sealed record Reading(List<Move> Moves, string? Refusal, string? NotCovered,
                                   IReadOnlyList<long>? Cancelled = null);

    internal static Reading ToMoves(IReadOnlyList<BoardSnapshot> samples, int localOwner)
    {
        if (localOwner is not (0 or 1))
        {
            return new Reading([], null, $"local owner is {localOwner}, the snapshot did not say which seat is the AI");
        }

        var read = Read(samples);
        if (read.Refusal is not null)
        {
            return new Reading([], read.Refusal, null);
        }

        if (read.NotCovered is not null)
        {
            return new Reading([], null, read.NotCovered);
        }

        if (read.Actions.Count == 0)
        {
            return new Reading([], null, "the game recorded no action of ours in this slice");
        }

        var opens = new int[read.Actions.Count];
        for (var a = 0; a < read.Actions.Count; a++)
        {
            opens[a] = -1;
            for (var i = 0; i < samples.Count && opens[a] < 0; i++)
            {
                if (samples[i].Commits is not { } batch)
                {
                    continue;
                }

                foreach (var r in batch.Records)
                {
                    if (r.Seq == read.Actions[a].Seq)
                    {
                        opens[a] = i;
                        break;
                    }
                }
            }

            if (opens[a] < 0)
            {
                return new Reading([], $"record {read.Actions[a].Seq} is in the turn but in none of its samples", null);
            }
        }

        var moves = new List<Move>(read.Actions.Count);
        var lastVictimPair = -1;
        for (var a = 0; a < read.Actions.Count; a++)
        {
            var act = read.Actions[a];

            var end = a + 1 < read.Actions.Count ? opens[a + 1] : samples.Count - 1;
            if (end < opens[a])
            {
                end = opens[a];
            }

            var landX = act.ToX;
            var landY = act.ToY;
            var landed = -1;
            for (var i = opens[a]; i <= end && landed < 0; i++)
            {
                foreach (var p in samples[i].Pieces)
                {
                    if (p.Owner == localOwner && p.X == act.ToX && p.Y == act.ToY)
                    {
                        landed = i;
                        break;
                    }
                }
            }

            var dive = false;
            var diveEnd = a + 1 < read.Actions.Count ? opens[a + 1] - 1 : end;
            if (landed < 0 && act.Kind == TranscriptKind.Attack &&
                DiveLanding(samples, localOwner, act, opens[a], diveEnd) is { } diver)
            {
                landed = diver.Sample;
                landX = diver.X;
                landY = diver.Y;
                dive = true;
            }

            byte? chargeFacing = null;
            (int X, int Y)? chargeStood = null;
            if (!dive && act.Kind == TranscriptKind.Attack &&
                ChargeLanding(samples, localOwner, act, opens[a], diveEnd) is { } charger)
            {
                landed = charger.Sample;
                chargeFacing = charger.Facing;
                chargeStood = (charger.X, charger.Y);
            }

            if (!dive && chargeStood is null && landed >= 0)
            {
                landed = SettledLanding(samples, localOwner, act.ToX, act.ToY, landed, end);
            }

            byte? fallenFacing = null;
            if (landed < 0 && act.Kind == TranscriptKind.Move &&
                DiedAtLanding(samples, localOwner, act, opens[a], end) is { } fallen)
            {
                landed = fallen.Sample;
                fallenFacing = fallen.Facing;
            }

            if (landed < 0)
            {
                return new Reading([], null,
                                   $"the game recorded a {act.Kind.ToString().ToLowerInvariant()} to " +
                                   $"({act.ToX},{act.ToY}) and no machine of ours stands there in the samples " +
                                   "that follow, which this reading does not cover yet");
            }

            byte facing = 0;
            var lander = new Piece(-1, -1, 0, 0, -1);
            foreach (var p in samples[landed].Pieces)
            {
                if (p.Owner == localOwner && p.X == landX && p.Y == landY)
                {
                    facing = p.Facing;
                    lander = p;
                    break;
                }
            }

            if (chargeFacing is { } charged)
            {
                facing = charged;
            }

            if (fallenFacing is { } fell)
            {
                facing = fell;
            }

            var move = new Move
            {
                SrcX = act.FromX,
                SrcY = act.FromY,
                DstX = landX,
                DstY = landY,
                Facing = facing,
                Burst = act.Burst,
                Attack = act.Kind == TranscriptKind.Attack,
                LandX = chargeStood?.X ?? landX,
                LandY = chargeStood?.Y ?? landY,
            };

            if (act.Kind == TranscriptKind.Attack)
            {
                var victimFrom = opens[a] > 0 && lastVictimPair != opens[a] - 1 ? opens[a] - 1 : opens[a];
                var victim = FirstVictim(samples, localOwner, victimFrom, end);
                if (victim is not { } v)
                {
                    return new Reading([], null,
                                       $"the game recorded an attack from ({act.ToX},{act.ToY}) and no machine of " +
                                       "theirs lost health after it, which this reading does not cover yet");
                }

                move.TargetX = v.X;
                move.TargetY = v.Y;
                lastVictimPair = v.Pair;

                if (!dive && chargeFacing is null
                    && FacingOf(samples[v.Pair + 1], localOwner, landX, landY, lander.Uuid) is { } struck)
                {
                    move.Facing = struck;
                }

                if (dive)
                {
                    var firing = new Piece(act.ToX, act.ToY, 1, (byte)(facing & 3), localOwner)
                    {
                        Range = lander.Range,
                        Uuid = lander.Uuid,
                    };
                    var target = new Piece(v.X, v.Y, 1, 0, 1 - localOwner);
                    var besideVictim = Math.Max(Math.Abs(landX - v.X), Math.Abs(landY - v.Y)) <= 1;
                    var reaches = MoveDetector.InStrikeLine(firing, target) || MoveDetector.InDiveArc(firing, target);
                    if (!besideVictim || !reaches)
                    {
                        return new Reading([], null,
                                           $"the game recorded a dive from ({act.ToX},{act.ToY}) landing on " +
                                           $"({landX},{landY}), and the first machine of theirs to lose health, at " +
                                           $"({v.X},{v.Y}), is not where that dive strikes, which this reading does " +
                                           "not cover yet");
                    }

                    move.AtkX = act.ToX;
                    move.AtkY = act.ToY;
                }
            }

            moves.Add(move);
        }

        return new Reading(moves, null, null, read.Cancelled);
    }

    private static (int Sample, int X, int Y)? DiveLanding(IReadOnlyList<BoardSnapshot> samples, int localOwner,
                                                         TranscriptAction act, int from, int to)
    {
        if (BySlot(samples[from].Pieces, act.Unit) is not { } first || first.Owner != localOwner ||
            Machines.Find(first.Uuid) is not { Pattern: "Dive" })
        {
            return null;
        }

        var count = samples[from].Pieces.Count;
        (int Sample, int X, int Y)? found = null;
        for (var i = from; i <= to && i < samples.Count; i++)
        {
            if (samples[i].Pieces.Count != count)
            {
                break;
            }

            if (BySlot(samples[i].Pieces, act.Unit) is not { } p || p.Owner != localOwner || p.Uuid != first.Uuid)
            {
                break;
            }

            found = (i, p.X, p.Y);
        }

        if (found is not { } f || (f.X == act.ToX && f.Y == act.ToY))
        {
            return null;
        }

        return f;
    }

    internal static byte? FacingOf(BoardSnapshot sample, int localOwner, int x, int y, string uuid)
    {
        foreach (var p in sample.Pieces)
        {
            if (p.Owner == localOwner && p.X == x && p.Y == y && (uuid.Length == 0 || p.Uuid == uuid))
            {
                return p.Facing;
            }
        }

        return null;
    }

    internal static int SettledLanding(IReadOnlyList<BoardSnapshot> samples, int localOwner, int x, int y,
                                       int first, int end)
    {
        Piece? arrived = null;
        foreach (var p in samples[first].Pieces)
        {
            if (p.Owner == localOwner && p.X == x && p.Y == y)
            {
                arrived = p;
                break;
            }
        }

        if (arrived is not { } was || was.Acts < 0 || was.Bursts < 0 || was.Uuid.Length == 0)
        {
            return first;
        }

        for (var i = first + 1; i <= end && i < samples.Count; i++)
        {
            foreach (var p in samples[i].Pieces)
            {
                if (p.Owner == localOwner && p.X == x && p.Y == y && p.Uuid == was.Uuid
                    && (p.Acts != was.Acts || p.Bursts != was.Bursts))
                {
                    return i;
                }
            }
        }

        return first;
    }

    internal static (int Sample, byte Facing)? DiedAtLanding(IReadOnlyList<BoardSnapshot> samples, int localOwner,
                                                              TranscriptAction act, int from, int to)
    {
        Piece? last = null;
        var lastAt = -1;
        for (var i = Math.Max(from - 1, 0); i <= to && i < samples.Count; i++)
        {
            var p = BySlot(samples[i].Pieces, act.Unit);
            if (p is { } here && here.Owner == localOwner && (last is null || here.Uuid == last.Value.Uuid))
            {
                last = here;
                lastAt = i;
                continue;
            }

            if (last is { } was && OursCount(samples[i], localOwner) < OursCount(samples[lastAt], localOwner))
            {
                return (lastAt, was.Facing);
            }

            return null;
        }

        return null;
    }

    private static int OursCount(BoardSnapshot s, int localOwner)
    {
        var count = 0;
        foreach (var p in s.Pieces)
        {
            if (p.Owner == localOwner)
            {
                count++;
            }
        }

        return count;
    }

    private static (int Sample, byte Facing, int X, int Y)? ChargeLanding(IReadOnlyList<BoardSnapshot> samples,
                                                                          int localOwner, TranscriptAction act,
                                                                          int from, int to)
    {
        if (BySlot(samples[from].Pieces, act.Unit) is not { } first || first.Owner != localOwner ||
            Machines.Find(first.Uuid) is not { Pattern: "Dash" } machine)
        {
            return null;
        }

        var count = samples[from].Pieces.Count;
        for (var i = from; i <= to && i < samples.Count; i++)
        {
            if (samples[i].Pieces.Count != count)
            {
                break;
            }

            if (BySlot(samples[i].Pieces, act.Unit) is not { } p || p.Owner != localOwner || p.Uuid != first.Uuid)
            {
                break;
            }

            var range = p.Range > 0 ? p.Range : machine.Range;
            var (dx, dy) = MoveDetector.Step(p.Facing);
            if (p.X == act.ToX + dx * range && p.Y == act.ToY + dy * range)
            {
                return (i, p.Facing, p.X, p.Y);
            }
        }

        return null;
    }

    private static (int X, int Y, int Pair)? FirstVictim(IReadOnlyList<BoardSnapshot> samples, int localOwner,
                                                         int from, int to)
    {
        for (var k = from; k < to && k + 1 < samples.Count; k++)
        {
            var before = samples[k];
            var after = samples[k + 1];
            var theirsBefore = 0;
            var theirsAfter = 0;
            foreach (var p in before.Pieces)
            {
                if (p.Owner != localOwner)
                {
                    theirsBefore++;
                }
            }

            foreach (var p in after.Pieces)
            {
                if (p.Owner != localOwner)
                {
                    theirsAfter++;
                }
            }

            foreach (var p in before.Pieces)
            {
                if (p.Owner == localOwner)
                {
                    continue;
                }

                var stillThere = false;
                foreach (var q in after.Pieces)
                {
                    if (q.Owner == p.Owner && q.X == p.X && q.Y == p.Y)
                    {
                        stillThere = true;
                        if (q.Health < p.Health)
                        {
                            return (p.X, p.Y, k);
                        }

                        break;
                    }
                }

                if (!stillThere && theirsAfter < theirsBefore)
                {
                    return (p.X, p.Y, k);
                }
            }
        }

        return null;
    }
}
