using System.Runtime.InteropServices;
using System.Text;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    internal static string? DepthRefusal(int askedDepth, int rowCount)
    {
        if (rowCount <= 0)
        {
            return "the board this challenge carries cannot be read, so a placing depth of " +
                   $"{askedDepth} cannot be checked against it.";
        }

        if (askedDepth > rowCount)
        {
            return $"a placing depth of {askedDepth} does not fit on a board {rowCount} rows deep; " +
                   "the zone would run off the end of the board.";
        }

        return null;
    }

    private static int SetRules(string[] args, ulong moduleEnd)
    {
        var uuidAt = IndexOfArg(args, "--board-game");
        var uuid = uuidAt >= 0 && uuidAt + 1 < args.Length ? NormaliseUuid(args[uuidAt + 1]) : null;

        var vp = ArgIntOrNull(args, "--victory-points");
        var draft = ArgIntOrNull(args, "--draft-points");

        var placeRows = ArgIntOrNull(args, "--placement-rows");

        if (uuid is null || uuid.Length != 32 || (vp is null && draft is null && placeRows is null))
        {
            Console.Error.WriteLine("--set-rules --board-game <uuid> [--victory-points N] [--draft-points N] " +
                                    "[--placement-rows N] [--yes]");
            Console.Error.WriteLine("  victory-points: the score a side wins at, and the clamp on each side's score.");
            Console.Error.WriteLine("  draft-points:   the cost budget an army must fit inside.");
            Console.Error.WriteLine("  placement-rows: how many rows deep each side's placing zone is.");
            return 1;
        }

        if (vp is < 1 or > 200 || draft is < 1 or > 200)
        {
            Console.Error.WriteLine("REFUSED: values outside 1..200 are not a rule change, they are a typo.");
            return 1;
        }

        if (placeRows is < 1 or > 8)
        {
            Console.Error.WriteLine("REFUSED: a placing depth outside 1..8 cannot describe a zone on any " +
                                    "board this game loads.");
            return 1;
        }

        var hits = ScanForAll([_base + BoardGameVtableRva], moduleEnd);
        var game = hits[_base + BoardGameVtableRva].FirstOrDefault(g => ResourceUuid(g) == uuid);
        var settings = game != 0 ? ReadPtr(game + SettingsFromBoardGame) : 0;

        if (!Sane(settings))
        {
            Console.Error.WriteLine($"no loaded BoardGame carries uuid {uuid}, or it has no settings.");
            return 2;
        }

        if (placeRows is { } askedDepth)
        {
            var (boardCount, boardData) = Array_(game + 0x48);
            var board = boardCount > 0 && Sane(boardData) ? ReadPtr(boardData) : 0;
            var rowCount = 0;
            if (Sane(board))
            {
                var (rows, _) = Array_(board + 0x20);
                rowCount = (int)rows;
            }

            if (DepthRefusal(askedDepth, rowCount) is { } why)
            {
                Console.Error.WriteLine($"REFUSED: {why}");
                return 1;
            }
        }

        var haveVp = (int)ReadU32(settings + MaxVictoryPointsOffset);
        var haveDraft = (int)ReadU32(settings + MaxDraftPointsOffset);
        var havePlaceRows = (int)ReadU32(settings + UnitPlacementRowCount);
        Console.WriteLine($"\n  BoardGameSettings @0x{settings:X}");
        Console.WriteLine($"    MaxVictoryPoints  {haveVp}" + (vp is { } v ? $"  ->  {v}" : "   (unchanged)"));
        Console.WriteLine($"    MaxDraftPoints    {haveDraft}" + (draft is { } d ? $"  ->  {d}" : "   (unchanged)"));
        Console.WriteLine($"    PlacementRows     {havePlaceRows}" +
                          (placeRows is { } p ? $"  ->  {p}" : "   (unchanged)"));

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        if (vp is { } wantVp && !Write(settings + MaxVictoryPointsOffset, BitConverter.GetBytes(wantVp)))
        {
            Console.Error.WriteLine("  write of MaxVictoryPoints failed.");
            return 2;
        }

        if (draft is { } wantDraft && !Write(settings + MaxDraftPointsOffset, BitConverter.GetBytes(wantDraft)))
        {
            Console.Error.WriteLine("  write of MaxDraftPoints failed.");
            return 2;
        }

        if (placeRows is { } wantPlaceRows && !Write(settings + UnitPlacementRowCount, BitConverter.GetBytes(wantPlaceRows)))
        {
            Console.Error.WriteLine("  write of UnitPlacementRowCount failed.");
            return 2;
        }

        Console.WriteLine($"    wrote, reads back {ReadU32(settings + MaxVictoryPointsOffset)} / " +
                          $"{ReadU32(settings + MaxDraftPointsOffset)} / " +
                          $"{ReadU32(settings + UnitPlacementRowCount)}");
        return 0;
    }

    private static int? ArgIntOrNull(string[] args, string name)
    {
        var at = IndexOfArg(args, name);
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out var v))
        {
            return null;
        }
        return v;
    }

    private const int MaxNameLength = 8;
    private const int OpponentCornerCap = 12;
    private const int BannerNameCap = 8;

    public static string? TurnBanner(string name)
    {
        if (NameProblem(name) is not null)
        {
            return null;
        }

        var text = $"{FitToRoom(name, BannerNameCap)}'S TURN";
        return text.Length > OpponentTurn.Length ? null : text.PadRight(OpponentTurn.Length);
    }

    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "a name cannot be empty";
        }

        if (name.Length > MaxNameLength)
        {
            return $"a name of {name.Length} characters is too long; the buffer holds {MaxNameLength}";
        }

        foreach (var c in name)
        {
            if (c is not (>= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return "a name has a character that is not a letter or a digit";
            }
        }

        return null;
    }

    public static int? RoomFromHeader(byte[] header, int labelLength)
    {
        if (header.Length != 16)
        {
            return null;
        }

        var refcount = BitConverter.ToUInt32(header, 0) & 0x7FFFFFFF;
        var length = BitConverter.ToUInt32(header, 8);
        var capacity = BitConverter.ToUInt32(header, 12);
        if (refcount == 0 || refcount > 0x10000 || length != (uint)labelLength)
        {
            return null;
        }

        if (capacity == length)
        {
            return labelLength;
        }

        if (capacity < 16 || capacity > 0x1000 || (capacity + 1) % 16 != 0)
        {
            return null;
        }

        return (int)capacity - 16;
    }

    public static string? FitToRoom(string? name, int room)
    {
        if (name is null || name.Length <= room)
        {
            return name;
        }

        return name[..room];
    }
    private const string AloyCorner = "ALOY";
    private const string OpponentCorner = "OPPONENT";
    private const string OpponentTurn = "Opponent's Turn";

    private static byte[] LabelPattern(string text)
    {
        var pattern = new byte[text.Length + 1];
        Encoding.ASCII.GetBytes(text).CopyTo(pattern, 0);
        pattern[text.Length] = 0;
        return pattern;
    }

    internal static List<int> MatchesIn(ReadOnlySpan<byte> span, byte[] pattern)
    {
        var found = new List<int>();
        var from = 0;
        while (from + pattern.Length <= span.Length)
        {
            var at = span[from..].IndexOf(pattern);
            if (at < 0)
            {
                break;
            }

            found.Add(from + at);
            from += at + 1;
        }

        return found;
    }

    private static List<ulong>[] ScanForMany(IReadOnlyList<byte[]> patterns, int maxPerPattern,
                                             IReadOnlyList<(ulong Base, ulong Size)> regions)
    {
        var hits = new List<ulong>[patterns.Count];
        for (var i = 0; i < patterns.Count; i++)
        {
            hits[i] = [];
        }

        var longest = patterns.Max(p => p.Length);
        var gate = new object();

        Parallel.ForEach(regions,
            new ParallelOptions { MaxDegreeOfParallelism = ScanThreads() },
            () => new byte[0x100000],
            (region, _, buf) =>
            {
                var (regionBase, regionSize) = region;

                var step = (ulong)(buf.Length - longest);
                for (ulong off = 0; off < regionSize; off += step)
                {
                    var want = (int)Math.Min((ulong)buf.Length, regionSize - off);
                    if (want < longest)
                    {
                        break;
                    }

                    if (!ReadProcessMemory(_handle, (nint)(regionBase + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    var span = new ReadOnlySpan<byte>(buf, 0, (int)got);
                    for (var p = 0; p < patterns.Count; p++)
                    {
                        foreach (var at in MatchesIn(span, patterns[p]))
                        {
                            lock (gate)
                            {
                                hits[p].Add(regionBase + off + (ulong)at);
                            }
                        }
                    }
                }

                return buf;
            },
            _ => { });

        foreach (var list in hits)
        {
            list.Sort();
            if (list.Count > maxPerPattern)
            {
                list.RemoveRange(maxPerPattern, list.Count - maxPerPattern);
            }
        }

        return hits;
    }

    private static int SetNames(string[] args)
    {
        var me = ArgAfter(args, "--me");
        var them = ArgAfter(args, "--them");
        var army = ArmyNameArg(args);
        if (me is null && them is null && army is null)
        {
            Console.Error.WriteLine("--set-names [--me <NAME>] [--them <NAME>] [--army-name <TEXT>] [--yes]");
            Console.Error.WriteLine($"  a name is 1 to {MaxNameLength} letters or digits, upper case.");
            Console.Error.WriteLine("  --me renames the left corner, --them the right corner AND the turn banner.");
            Console.Error.WriteLine($"  --army-name replaces \"{StockSetLabel}\" on the set screen before the match.");
            return 1;
        }

        foreach (var n in new[] { me, them })
        {
            if (n is not null && NameProblem(n) is { } bad)
            {
                Console.Error.WriteLine($"REFUSED: {bad}");
                return 1;
            }
        }

        var wait = ArgIntOrNull(args, "--wait") ?? 0;
        if (wait <= 0 && army is not null)
        {
            if (args.Contains("--yes"))
            {
                new SetLabelHold(army).Tick(DateTime.UtcNow);
            }
            else
            {
                Console.WriteLine($"  the set screen's label would read \"{army}\" at: " +
                                  string.Join(", ", FindSetLabels(verbose: true)
                                      .Select(s => $"0x{s.Text:X} ({(s.Drawn ? "on screen" : "its source")})")));
            }
        }

        var plan = wait > 0
            ? AwaitNames(me, them, TimeSpan.FromSeconds(wait), army is null ? null : new SetLabelHold(army))
            : PlanNames(me, them);
        if (plan is null)
        {
            Console.WriteLine("  no match was live in time, or --play is gone, so no names were written");
            return 2;
        }

        var holding = args.Contains(NamesHoldFlag) && args.Contains("--yes");
        if (plan.Count == 0 && !holding)
        {
            Console.WriteLine("  nothing to rename.");
            return 0;
        }

        foreach (var (what, addr, bytes) in plan)
        {
            Console.WriteLine(PlanLine(what, addr, bytes));
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run. Pass --yes to write.");
            return 0;
        }

        var wrote = WritePlan(plan);
        Console.WriteLine($"\n  done, {wrote} of {plan.Count} written.");

        if (holding)
        {
            return HoldNames(plan, me, them);
        }

        return 0;
    }

    internal static readonly TimeSpan NamesEntryPatience = TimeSpan.FromSeconds(20);

    internal static bool SetLabelTicks(DateTime liveSince)
    {
        return liveSince == DateTime.MinValue;
    }

    private static List<(string What, ulong Addr, byte[] Bytes)>? AwaitNames(string? me, string? them, TimeSpan wait,
                                                                          SetLabelHold? setLabel)
    {
        Console.WriteLine($"  waiting for a match to write the names on (up to {wait.TotalSeconds:0}s)");
        var deadline = DateTime.UtcNow + wait;
        var liveSince = DateTime.MinValue;
        while (!_parentGone && DateTime.UtcNow < deadline)
        {
            if (LiveBoardShape().Logic == 0)
            {
                if (SetLabelTicks(liveSince))
                {
                    setLabel?.Tick(DateTime.UtcNow);
                }

                Thread.Sleep(500);
                continue;
            }

            var now = DateTime.UtcNow;
            if (liveSince == DateTime.MinValue)
            {
                liveSince = now;
                setLabel?.Restore();
            }

            var plan = PlanNames(me, them, quiet: true);
            if (NamesEntryDone(plan, me, them, now - liveSince))
            {
                return plan;
            }

            Thread.Sleep(1000);
        }

        setLabel?.Restore();
        return null;
    }

    internal static bool NamesEntryDone(IReadOnlyList<(string What, ulong Addr, byte[] Bytes)> plan, string? me, string? them,
                                        TimeSpan liveFor)
    {
        return !CornerMissing(plan, me, them) || liveFor >= NamesEntryPatience;
    }

    internal const string NamesHoldFlag = "--hold-names";

    internal static bool IsAiHold(string[] args)
    {
        return args.Contains("--hold") && !args.Contains("--set-names");
    }

    internal const string NamesHeldLine = "  names written, holding until the match or --play ends";

    internal static readonly TimeSpan NamesSettle = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan NamesRetry = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan NamesPatience = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan NamesSlowRetry = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan NamesSafetySweep = TimeSpan.FromSeconds(30);

    private static int HoldNames(List<(string What, ulong Addr, byte[] Bytes)> plan, string? me, string? them)
    {
        Console.WriteLine(NamesHeldLine);
        var misses = 0;
        var lastSweep = DateTime.UtcNow;
        var lostAt = DateTime.MinValue;
        var toldMissing = false;
        while (!_parentGone)
        {
            Thread.Sleep(500);
            var live = LiveBoardShape();
            misses = live.Logic == 0 ? misses + 1 : 0;
            if (misses >= MoveBoundsGoneReads)
            {
                Console.WriteLine("  no match is live any more, the names hold ends");
                return 0;
            }

            if (live.Logic == 0)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            var lost = NamesNeedRewrite(plan, ReadBack) || CornerMissing(plan, me, them);
            if (!lost)
            {
                lostAt = DateTime.MinValue;
                toldMissing = false;
            }
            else if (lostAt == DateTime.MinValue)
            {
                lostAt = now;
            }

            if (!NamesSweepDue(lost, now - lostAt, now - lastSweep))
            {
                continue;
            }

            lastSweep = now;
            var kept = plan.Where(e => !NamesNeedRewrite([e], ReadBack)).ToList();
            var swept = System.Diagnostics.Stopwatch.StartNew();
            var (merged, fresh) = MergeNames(kept, PlanNames(me, them, quiet: true));
            Console.WriteLine(NamesSweepLine(swept.Elapsed, fresh.Count));
            plan = merged;
            if (fresh.Count > 0)
            {
                var rewrote = WritePlan(fresh);
                Console.WriteLine($"  names written again, {rewrote} of {fresh.Count}: the game rebuilt the corner labels");
            }
            else if (lost && !toldMissing)
            {
                Console.WriteLine("  the corner labels were rebuilt and are not found yet, trying again");
                toldMissing = true;
            }
        }

        Console.WriteLine("  --play is gone, or Ctrl+C was pressed, the names hold ends");
        return 0;
    }

    private static byte[]? ReadBack((ulong Addr, int Length) at)
    {
        return TryRead(at.Addr, at.Length, out var now) ? now : null;
    }

    internal static bool CornerMissing(IReadOnlyList<(string What, ulong Addr, byte[] Bytes)> plan, string? me, string? them)
    {
        var mine = me is null || plan.Any(e => e.What == "your corner");
        var theirs = them is null || plan.Any(e => e.What == "their corner");
        return !mine || !theirs;
    }

    internal static string NamesSweepLine(TimeSpan took, int toWrite)
    {
        var what = toWrite == 0 ? "nothing to write" : $"{toWrite} to write";
        return $"  names hold swept the game's memory in {(int)took.TotalMilliseconds} ms, {what}";
    }

    internal static bool NamesSweepDue(bool lost, TimeSpan lostFor, TimeSpan sinceSweep)
    {
        if (!lost)
        {
            return sinceSweep >= NamesSafetySweep;
        }

        if (lostFor < NamesSettle)
        {
            return false;
        }

        var every = lostFor < NamesPatience ? NamesRetry : NamesSlowRetry;
        return sinceSweep >= every;
    }

    internal static (List<(string What, ulong Addr, byte[] Bytes)> Merged, List<(string What, ulong Addr, byte[] Bytes)> Fresh)
        MergeNames(List<(string What, ulong Addr, byte[] Bytes)> kept, List<(string What, ulong Addr, byte[] Bytes)> found)
    {
        var merged = new List<(string What, ulong Addr, byte[] Bytes)>(kept);
        var fresh = new List<(string What, ulong Addr, byte[] Bytes)>();
        foreach (var entry in found)
        {
            var same = merged.FindIndex(e => e.Addr == entry.Addr);
            if (same >= 0 && merged[same].Bytes.SequenceEqual(entry.Bytes))
            {
                continue;
            }

            if (same >= 0)
            {
                merged[same] = entry;
            }
            else
            {
                merged.Add(entry);
            }

            fresh.Add(entry);
        }

        return (merged, fresh);
    }

    internal static bool NamesNeedRewrite(IReadOnlyList<(string What, ulong Addr, byte[] Bytes)> plan,
                                          Func<(ulong Addr, int Length), byte[]?> read)
    {
        foreach (var (_, addr, bytes) in plan)
        {
            var now = read((addr, bytes.Length));
            if (now is null || !now.SequenceEqual(bytes))
            {
                return true;
            }
        }

        return false;
    }

    internal static byte[]? StockAt(string what)
    {
        return what switch
        {
            "your corner" => LabelPattern(AloyCorner),
            "their corner" => LabelPattern(OpponentCorner),
            "your corner length" => BitConverter.GetBytes((uint)AloyCorner.Length),
            "their corner length" => BitConverter.GetBytes((uint)OpponentCorner.Length),
            "their turn banner" => Encoding.ASCII.GetBytes(OpponentTurn),
            _ => null,
        };
    }

    internal static bool StillStock(byte[]? now, byte[]? stock, bool labelLeftAlone)
    {
        return !labelLeftAlone && now is not null && stock is not null && now.AsSpan().SequenceEqual(stock);
    }

    private static int WritePlan(List<(string What, ulong Addr, byte[] Bytes)> plan)
    {
        var wrote = 0;
        var leftAlone = new HashSet<ulong>();
        foreach (var (what, addr, bytes) in plan)
        {
            var stock = StockAt(what);
            var now = stock is not null && TryRead(addr, stock.Length, out var read) ? read : null;
            var isLength = what.EndsWith(" length", StringComparison.Ordinal);
            if (!StillStock(now, stock, isLength && leftAlone.Contains(addr + 8)))
            {
                leftAlone.Add(addr);
                Console.Error.WriteLine($"  note: {what} at 0x{addr:X} no longer reads as the game's own label, left alone.");
                continue;
            }

            if (Write(addr, bytes))
            {
                wrote++;
            }
            else
            {
                Console.Error.WriteLine($"  note: could not write {what} at 0x{addr:X}, left alone.");
            }
        }

        return wrote;
    }

    private static List<(string What, ulong Addr, byte[] Bytes)> PlanNames(string? me, string? them, bool quiet = false)
    {
        var plan = new List<(string What, ulong Addr, byte[] Bytes)>();

        var sweep = System.Diagnostics.Stopwatch.StartNew();
        var scan = ScanForMany([LabelPattern(AloyCorner), LabelPattern(OpponentCorner),
                                Encoding.ASCII.GetBytes(OpponentTurn)], 8, ScanRegions(heapOnly: true));
        if (!quiet)
        {
            Console.WriteLine($"  scanned for the labels in {sweep.Elapsed.TotalSeconds:0.0}s " +
                              $"({ScanThreads()} threads)");
        }

        if (me is not null)
        {
            var found = scan[0];
            if (found.Count == 0 && !quiet)
            {
                Console.Error.WriteLine($"  note: the '{AloyCorner}' label was not found, so your name " +
                                        "was not written. Names may already be set, or the match is not loaded.");
            }

            foreach (var at in found)
            {
                PlanLabel(plan, "your corner", at, AloyCorner, me, MaxNameLength);
            }
        }

        if (them is not null)
        {
            var found = scan[1];
            if (found.Count == 0 && !quiet)
            {
                Console.Error.WriteLine($"  note: the '{OpponentCorner}' label was not found, so their " +
                                        "name was not written.");
            }

            foreach (var at in found)
            {
                PlanLabel(plan, "their corner", at, OpponentCorner, them, OpponentCornerCap);
            }

            if (TurnBanner(them) is { } shown && shown.TrimEnd() != $"{them}'S TURN" && !quiet)
            {
                Console.WriteLine($"  note: the turn banner holds {BannerNameCap} letters of a name, so it " +
                                  $"shows the first {BannerNameCap}.");
            }

            var bannerHits = scan[2];
            if (bannerHits.Count == 1 && TurnBanner(them) is { } banner)
            {
                plan.Add(("their turn banner", bannerHits[0], Encoding.ASCII.GetBytes(banner)));
            }
            else if (!quiet)
            {
                Console.Error.WriteLine($"  note: the '{OpponentTurn}' banner was not found, so it was " +
                                        "left alone.");
            }
        }

        return plan;
    }

    private static void PlanLabel(List<(string What, ulong Addr, byte[] Bytes)> plan, string what,
                                  ulong at, string label, string name, int cap)
    {
        var header = Read(at - 16, 16);
        var room = RoomFromHeader(header, label.Length);
        var fitted = FitToRoom(name, Math.Min(room ?? label.Length, cap))!;
        if (fitted != name)
        {
            Console.WriteLine(CutNote(what, at, headerRead: room is not null, fitted.Length));
        }

        plan.Add((what, at, Terminated(fitted)));
        if (room is not null && fitted.Length != label.Length)
        {
            plan.Add(($"{what} length", at - 8, BitConverter.GetBytes((uint)fitted.Length)));
        }
    }

    public static string PlanLine(string what, ulong addr, byte[] bytes)
    {
        var line = $"  {what,-18} 0x{addr:X}  {bytes.Length} bytes";
        if (what.EndsWith(" length"))
        {
            return $"{line}  {BitConverter.ToUInt32(bytes, 0)}";
        }

        return line;
    }

    public static string CutNote(string what, ulong at, bool headerRead, int kept)
    {
        var why = headerRead ? "holds" : "reads no header, so it keeps the label's";
        return $"  note: {what} at 0x{at:X} {why} {kept} letters, so it shows the first {kept}.";
    }

    private static byte[] Terminated(string name)
    {
        var bytes = new byte[name.Length + 1];
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        bytes[name.Length] = 0;
        return bytes;
    }

    private static string? ArgAfter(string[] args, string flag)
    {
        var i = IndexOfArg(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    private static int BoardShape(string[] args, ulong moduleEnd)
    {
        var uuidAt = IndexOfArg(args, "--board-game");
        var uuid = uuidAt >= 0 && uuidAt + 1 < args.Length ? NormaliseUuid(args[uuidAt + 1]) : null;
        if (uuid is null || uuid.Length != 32)
        {
            Console.Error.WriteLine("--board-shape --board-game <uuid>");
            return 1;
        }

        var hits = ScanForAll([_base + BoardGameVtableRva], moduleEnd);
        var game = hits[_base + BoardGameVtableRva].FirstOrDefault(g => ResourceUuid(g) == uuid);
        if (game == 0)
        {
            Console.Error.WriteLine($"  no loaded BoardGame carries uuid {uuid}.");
            return 2;
        }

        var (boardCount, boardData) = Array_(game + 0x48);
        if (boardCount <= 0 || !Sane(boardData))
        {
            Console.Error.WriteLine("  that challenge has no board.");
            return 2;
        }

        var board = ReadPtr(boardData);
        Console.WriteLine($"\n  BoardGame @0x{game:X}  board @0x{board:X}");
        Console.WriteLine($"  board+0x20 header: {Dump(board + 0x20, 16)}");

        var settings = ReadPtr(game + SettingsFromBoardGame);
        if (Sane(settings))
        {
            var placeRows = TryReadU32(settings + UnitPlacementRowCount, out var pr) ? (int)pr : -1;
            var (rowCount, _) = Array_(board + 0x20);
            Console.WriteLine($"\n  settings @0x{settings:X}  UnitPlacementRowCount (+0x120) {placeRows}");
            if (placeRows > 0 && rowCount > 0)
            {
                Console.WriteLine($"    seat 1 (AI) places on rows 0..{placeRows - 1}");
                Console.WriteLine($"    seat 0 (human) places on rows {rowCount - placeRows}..{rowCount - 1}" +
                                  $"  (height {rowCount} minus the depth)");
            }

            Console.WriteLine($"    settings+0x100: {Dump(settings + 0x100, 16)}");
            Console.WriteLine($"    settings+0x110: {Dump(settings + 0x110, 16)}");
            Console.WriteLine($"    settings+0x120: {Dump(settings + 0x120, 16)}");
            Console.WriteLine($"    settings+0x130: {Dump(settings + 0x130, 16)}");
        }

        var (rows, rowData) = Array_(board + 0x20);
        Console.WriteLine($"  rows {rows}, row pointer array @0x{rowData:X}");
        Console.WriteLine($"  the dword beside the row count (board+0x24) is {ReadU32(board + 0x24)}, " +
                          "a capacity there would be what a grow needs");

        ulong previousEnd = 0;
        for (var r = 0; r < rows; r++)
        {
            var row = ReadPtr(rowData + (ulong)(r * 8));
            if (!Sane(row))
            {
                Console.WriteLine($"    row {r}: unreadable @0x{row:X}");
                continue;
            }

            var (cols, tileData) = Array_(row + 0x20);
            var beside = ReadU32(row + 0x24);
            var contiguous = previousEnd != 0 && tileData == previousEnd ? "  contiguous with the row above" : "";
            Console.WriteLine($"    row {r} @0x{row:X}  cols {cols}  beside {beside}  " +
                              $"cells @0x{tileData:X}{contiguous}");
            Console.WriteLine($"      row+0x20 header: {Dump(row + 0x20, 16)}");
            previousEnd = tileData + (ulong)(cols * 8);
        }

        return 0;
    }

    private static string Dump(ulong at, int count)
    {
        return TryRead(at, count, out var b) ? Convert.ToHexString(b) : "unreadable";
    }

    private const int MaxBoardSide = 8;

    internal static int RowsToWalk(int rows)
    {
        return Math.Min(rows, MaxBoardSide);
    }

    internal static string? ShapeProblem(int value, string what)
    {
        if (value is < 1 or > MaxBoardSide)
        {
            return $"{what} {value} is outside 1..{MaxBoardSide}; the game allocates no more than " +
                   $"{MaxBoardSide} and a larger count reads memory it does not own";
        }

        return null;
    }

    internal static string? ResizeDepthProblem(int wantRows, bool depthRead, uint depthHeld)
    {
        if (wantRows < 0)
        {
            return null;
        }

        if (!depthRead)
        {
            return $"the placing depth this challenge holds cannot be read, so a board {wantRows} rows deep " +
                   "cannot be checked against it.";
        }

        if (depthHeld > (uint)wantRows)
        {
            return $"this challenge places {depthHeld} rows deep, which does not fit on a board {wantRows} rows " +
                   "deep. Write the depth first.";
        }

        return null;
    }

    private static int SetBoardSize(string[] args, ulong moduleEnd)
    {
        var uuidAt = IndexOfArg(args, "--board-game");
        var uuid = uuidAt >= 0 && uuidAt + 1 < args.Length ? NormaliseUuid(args[uuidAt + 1]) : null;
        var wantRows = IndexOfArg(args, "--rows") is var ra && ra >= 0 && ra + 1 < args.Length &&
                       int.TryParse(args[ra + 1], out var rv) ? rv : -1;
        var wantCols = IndexOfArg(args, "--cols") is var ca && ca >= 0 && ca + 1 < args.Length &&
                       int.TryParse(args[ca + 1], out var cv) ? cv : -1;

        var perRow = new Dictionary<int, int>();
        var prAt = IndexOfArg(args, "--row-cols");
        if (prAt >= 0 && prAt + 1 < args.Length)
        {
            foreach (var pair in args[prAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = pair.Split(':');
                if (bits.Length == 2 && int.TryParse(bits[0], out var r) && int.TryParse(bits[1], out var c))
                {
                    perRow[r] = c;
                }
            }
        }

        if (uuid is null || uuid.Length != 32 || (wantRows < 0 && wantCols < 0 && perRow.Count == 0))
        {
            Console.Error.WriteLine("--set-board-size --board-game <uuid> [--rows N] [--cols N] " +
                                    "[--row-cols r:N,r:N] [--yes]");
            Console.Error.WriteLine("  --rows is the height, --cols sets every row's width,");
            Console.Error.WriteLine("  --row-cols overrides single rows, which is how a ragged board is asked for.");
            return 1;
        }

        foreach (var asked in new[] { (wantRows, "--rows"), (wantCols, "--cols") })
        {
            if (asked.Item1 >= 0 && ShapeProblem(asked.Item1, asked.Item2) is { } shapeBad)
            {
                Console.Error.WriteLine($"REFUSED: {shapeBad}");
                return 1;
            }
        }

        foreach (var pair in perRow)
        {
            if (ShapeProblem(pair.Value, $"--row-cols row {pair.Key}") is { } rowBad)
            {
                Console.Error.WriteLine($"REFUSED: {rowBad}");
                return 1;
            }

            if (pair.Key is < 0 or >= MaxBoardSide)
            {
                Console.Error.WriteLine($"REFUSED: --row-cols names row {pair.Key}, outside 0..{MaxBoardSide - 1}");
                return 1;
            }
        }

        var hits = ScanForAll([_base + BoardGameVtableRva], moduleEnd);
        var game = hits[_base + BoardGameVtableRva].FirstOrDefault(g => ResourceUuid(g) == uuid);
        if (game == 0)
        {
            Console.Error.WriteLine($"  no loaded BoardGame carries uuid {uuid}.");
            return 2;
        }

        var settings = ReadPtr(game + SettingsFromBoardGame);
        uint depthHeld = 0;
        var depthRead = Sane(settings) && TryReadU32(settings + UnitPlacementRowCount, out depthHeld);
        if (ResizeDepthProblem(wantRows, depthRead, depthHeld) is { } depthBad)
        {
            Console.Error.WriteLine($"REFUSED: {depthBad}");
            return 1;
        }

        var (boardCount, boardData) = Array_(game + 0x48);
        if (boardCount <= 0 || !Sane(boardData))
        {
            Console.Error.WriteLine("  that challenge has no board.");
            return 2;
        }

        var board = ReadPtr(boardData);
        if (!Sane(board))
        {
            Console.Error.WriteLine("  board pointer unreadable.");
            return 2;
        }

        var (rows, rowData) = Array_(board + 0x20);
        if (!Sane(rowData))
        {
            Console.Error.WriteLine("  board has no rows.");
            return 2;
        }

        Console.WriteLine($"\n  BoardGame @0x{game:X}  board @0x{board:X}  currently {rows} rows");

        var writes = new List<(ulong At, int Value, string What)>();

        if (wantRows >= 0)
        {
            writes.Add((board + 0x20, wantRows, $"board row count {rows} -> {wantRows}"));
        }

        var walk = RowsToWalk(rows);
        if (rows > MaxBoardSide)
        {
            Console.Error.WriteLine($"  Warning: this board claims {rows} rows, over the {MaxBoardSide} " +
                                    "the game allocates. Only the real ones are touched.");
        }

        for (var r = 0; r < walk; r++)
        {
            var row = ReadPtr(rowData + (ulong)(r * 8));
            if (!Sane(row))
            {
                continue;
            }

            var want = perRow.TryGetValue(r, out var pr) ? pr : wantCols;
            if (want < 0)
            {
                continue;
            }

            var (cols, _) = Array_(row + 0x20);
            writes.Add((row + 0x20, want, $"row {r} width {cols} -> {want}"));
        }

        foreach (var w in writes)
        {
            Console.WriteLine($"    {w.What}   @0x{w.At:X}");
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("  dry run, pass --yes to write.");
            return 0;
        }

        foreach (var w in writes)
        {
            if (!Write(w.At, BitConverter.GetBytes(w.Value)))
            {
                Console.Error.WriteLine($"  write failed at 0x{w.At:X}");
                return 1;
            }
        }

        Console.WriteLine($"  written, {writes.Count} value(s). Start the match WITHOUT backing out of " +
                          "the challenge list; backing out rebuilds the resource and undoes this.");
        return 0;
    }

    private static int SetBoard(string[] args, int at, ulong moduleEnd)
    {
        var want = new List<int>();
        for (var i = at; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                break;
            }

            if (!int.TryParse(args[i], out var v))
            {
                break;
            }

            want.Add(v);
        }

        var uuidAt = IndexOfArg(args, "--board-game");
        var uuid = uuidAt >= 0 && uuidAt + 1 < args.Length ? NormaliseUuid(args[uuidAt + 1]) : null;
        if (want.Count == 0 || uuid is null || uuid.Length != 32)
        {
            Console.Error.WriteLine("--set-board <t0> <t1> ... --board-game <uuid> [--yes]");
            Console.Error.WriteLine("  one terrain value per cell, row-major from y=0 (the far edge).");
            Console.Error.WriteLine("  -2 Chasm, -1 Marsh, 0 Plains, 1 Forest, 2 Hills, 3 Mountains.");
            return 1;
        }

        var needles = new[] { _base + BoardGameVtableRva, _base + TileVtableRva };
        var hits = ScanForAll(needles, moduleEnd);

        var tiles = new Dictionary<int, ulong>();
        foreach (var t in hits[_base + TileVtableRva])
        {
            if (TryRead(t + 0x50, 1, out var tb))
            {
                tiles.TryAdd((sbyte)tb[0], t);
            }
        }

        Console.WriteLine($"\n  tile resources loaded: " +
                          string.Join(", ", tiles.OrderBy(k => k.Key).Select(k => $"{TileTypeName(k.Key)}={k.Key}")));

        var missing = want.Distinct().Where(v => !tiles.ContainsKey(v)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"  no loaded tile resource for type(s) {string.Join(", ", missing)}, " +
                                    "only the six above can be written.");
            return 1;
        }

        var game = hits[_base + BoardGameVtableRva].FirstOrDefault(g => ResourceUuid(g) == uuid);
        if (game == 0)
        {
            Console.Error.WriteLine($"  no loaded BoardGame carries uuid {uuid}.");
            return 2;
        }

        var (boardCount, boardData) = Array_(game + 0x48);
        if (boardCount <= 0 || !Sane(boardData))
        {
            Console.Error.WriteLine("  that challenge has no board.");
            return 2;
        }
        var board = ReadPtr(boardData);
        if (!Sane(board))
        {
            Console.Error.WriteLine("  board pointer unreadable.");
            return 2;
        }

        var (rows, rowData) = Array_(board + 0x20);
        if (rows <= 0 || !Sane(rowData))
        {
            Console.Error.WriteLine("  board has no rows.");
            return 2;
        }

        var cells = new List<ulong>();
        for (var r = 0; r < rows; r++)
        {
            var row = ReadPtr(rowData + (ulong)(r * 8));
            if (!Sane(row))
            {
                Console.Error.WriteLine($"  row {r} unreadable.");
                return 2;
            }
            var (cols, tileData) = Array_(row + 0x20);
            if (!Sane(tileData))
            {
                Console.Error.WriteLine($"  row {r} has no tiles.");
                return 2;
            }
            for (var c = 0; c < cols; c++)
            {
                cells.Add(tileData + (ulong)(c * 8));
            }
        }

        if (want.Count != cells.Count)
        {
            Console.Error.WriteLine($"  board is {cells.Count} cells but {want.Count} values were given.");
            return 1;
        }

        Console.WriteLine($"\n  BoardGame @0x{game:X}  board 0x{board:X}  {rows} rows, {cells.Count} cells");
        var before = ReadBoardGrid(board);
        Console.WriteLine("  before:");
        foreach (var line in before)
        {
            Console.WriteLine("    " + string.Join(" ", line.Select(v => $"{v,2}")));
        }

        Console.WriteLine("  after:");
        for (var r = 0; r < rows; r++)
        {
            Console.WriteLine("    " + string.Join(" ", want.Skip(r * (cells.Count / rows)).Take(cells.Count / rows).Select(v => $"{v,2}")));
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("  dry run, pass --yes to write.");
            return 0;
        }

        for (var i = 0; i < cells.Count; i++)
        {
            if (!Write(cells[i], BitConverter.GetBytes(tiles[want[i]])))
            {
                return 1;
            }
        }

        Console.WriteLine("  written. Start the match WITHOUT backing out of the challenge list.");
        return 0;
    }

    private static int FindAiPlayer(ulong moduleEnd)
    {
        var anchor = Walk(report: false).Inst;
        if (anchor == 0)
        {
            Console.Error.WriteLine("No match is live, needed to anchor the search arena.");
            return 2;
        }

        var arena = anchor >> 40;
        Console.WriteLine($"\n  scanning for AIBoardGamePlayer field signature in arena 0x{arena:X} (from instance 0x{anchor:X})");

        var buf = new byte[0x100000];
        ulong address = 0x10000, scanned = 0;
        var hits = 0;

        while (address < 0x7FFFFFFFFFFF)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            var readable = info.State == 0x1000 && (info.Protect & 0x100) == 0 &&
                           (info.Protect & 0xFF) is not (0 or 0x01);

            if (readable && info.RegionSize <= 0x40000000 && info.BaseAddress >> 40 == arena &&
                !(info.BaseAddress >= _base && info.BaseAddress < moduleEnd))
            {
                for (ulong off = 0; off < info.RegionSize; off += (ulong)buf.Length - 0x80)
                {
                    var want = (int)Math.Min((ulong)buf.Length, info.RegionSize - off);
                    if (!ReadProcessMemory(_handle, (nint)(info.BaseAddress + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    scanned += (ulong)(int)got;
                    for (var i = 0; i + 0x80 <= (int)got; i += 4)
                    {
                        var low = BitConverter.ToSingle(buf, i + 0x48);
                        var med = BitConverter.ToSingle(buf, i + 0x4C);
                        var high = BitConverter.ToSingle(buf, i + 0x50);

                        if (!Prob(low) || !Prob(med) || !Prob(high))
                        {
                            continue;
                        }

                        var sum = low + med + high;
                        if (sum is < 0.99f or > 1.01f)
                        {
                            continue;
                        }

                        if (!RangeAt(buf, i + 0x5C) || !RangeAt(buf, i + 0x64) || !RangeAt(buf, i + 0x6C))
                        {
                            continue;
                        }

                        var fact = BitConverter.ToUInt64(buf, i + 0x78);
                        if (fact != 0 && !Sane(fact))
                        {
                            continue;
                        }

                        var at = info.BaseAddress + off + (ulong)i;
                        hits++;
                        if (hits > 200)
                        {
                            Console.WriteLine("\n  over 200 candidates, signature still too loose");
                            return 0;
                        }
                        Console.WriteLine($"\n    candidate @0x{at:X}");
                        Console.WriteLine($"      difficulty  low {low:F2}  med {med:F2}  high {high:F2}");
                        Console.WriteLine($"      think {Rng(buf, i + 0x5C)}  attack {Rng(buf, i + 0x64)}  move {Rng(buf, i + 0x6C)}");
                        Console.WriteLine($"      +0x78 DisableAIMovesFact -> 0x{BitConverter.ToUInt64(buf, i + 0x78):X}");
                    }
                }
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        Console.WriteLine($"\n  scanned {scanned / (1024 * 1024)} MB, {hits} candidate(s)");
        return 0;

        static bool Prob(float f)
        {
            return f is >= 0f and <= 1f && !float.IsNaN(f);
        }

        static bool RangeAt(byte[] b, int i)
        {
            float lo = BitConverter.ToSingle(b, i), hi = BitConverter.ToSingle(b, i + 4);
            return lo is >= 0.01f and <= 60f && hi is >= 0.01f and <= 60f && lo <= hi;
        }

        static string Rng(byte[] b, int i)
        {
            return $"{BitConverter.ToSingle(b, i):F2}..{BitConverter.ToSingle(b, i + 4):F2}";
        }
    }

    private static ulong FindOne(ulong needle, ulong arena = 0)
    {
        var buf = new byte[0x100000];
        ulong address = 0x10000;

        while (address < 0x7FFFFFFFFFFF)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            var readable = info.State == 0x1000 && (info.Protect & 0x100) == 0 &&
                           (info.Protect & 0xFF) is not (0 or 0x01);

            if (readable && info.RegionSize <= 0x4000000 && info.BaseAddress < _base &&
                (arena == 0 || info.BaseAddress >> 40 == arena))
            {
                for (ulong off = 0; off < info.RegionSize; off += (ulong)buf.Length)
                {
                    var want = (int)Math.Min((ulong)buf.Length, info.RegionSize - off);
                    if (!ReadProcessMemory(_handle, (nint)(info.BaseAddress + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    for (var i = 0; i + 8 <= (int)got; i += 8)
                    {
                        if (BitConverter.ToUInt64(buf, i) == needle)
                        {
                            return info.BaseAddress + off + (ulong)i;
                        }
                    }
                }
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return 0;
    }

    private static int FindRange(ulong lo, ulong hi)
    {
        Console.WriteLine($"\n  scanning for heap pointers into 0x{lo:X}..0x{hi:X}");

        var counts = new Dictionary<ulong, int>();
        var buf = new byte[0x100000];
        ulong address = 0x10000;

        while (address < 0x7FFFFFFFFFFF)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            var readable = info.State == 0x1000 && (info.Protect & 0x100) == 0 &&
                           (info.Protect & 0xFF) is not (0 or 0x01);

            if (readable && info.RegionSize <= 0x40000000 && info.BaseAddress < _base)
            {
                for (ulong off = 0; off < info.RegionSize; off += (ulong)buf.Length)
                {
                    var want = (int)Math.Min((ulong)buf.Length, info.RegionSize - off);
                    if (!ReadProcessMemory(_handle, (nint)(info.BaseAddress + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    for (var i = 0; i + 8 <= (int)got; i += 8)
                    {
                        var v = BitConverter.ToUInt64(buf, i);
                        if (v < lo || v > hi)
                        {
                            continue;
                        }

                        counts.TryGetValue(v, out var c);
                        counts[v] = c + 1;
                    }
                }
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        foreach (var kv in counts.OrderBy(k => k.Key))
        {
            Console.WriteLine($"    0x{kv.Key:X}  {kv.Value} reference(s)");
        }

        Console.WriteLine($"\n  {counts.Count} distinct target(s)");
        return 0;
    }

    internal static ulong FreezeTarget(ulong inst, ulong challengeVtable, ulong moduleBase)
    {
        if (inst == 0)
        {
            return 0;
        }

        if (challengeVtable != moduleBase + BoardGameChallengeRva)
        {
            return 0;
        }

        return inst + PauseFlags;
    }

    private static int Freeze(bool on)
    {
        var chain = Walk(report: false);
        if (chain.Inst == 0)
        {
            Console.Error.WriteLine("No match is live.");
            return 2;
        }

        var target = FreezeTarget(chain.Inst, ReadPtr(chain.Challenge), _base);
        if (target == 0)
        {
            Console.Error.WriteLine("The challenge being played is not a Machine Strike one, so nothing was frozen.");
            return 2;
        }

        var value = (byte)(on ? 1 : 0);
        if (!Write(target, [value, value]))
        {
            return 1;
        }

        Console.WriteLine($"  match {(on ? "FROZEN, the board refuses input on this PC" : "released, play resumes")}");
        return 0;
    }

    private static int Highlight(bool on)
    {
        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live.");
            return 2;
        }

        var rows = ReadPtr(chain.Logic + 0x08);
        var height = (int)ReadU32(rows + 0x20);
        var width = (int)ReadU32(ReadPtr(ReadPtr(rows + 0x28)) + 0x20);
        if (width is <= 0 or > 16 || height is <= 0 or > 16)
        {
            Console.Error.WriteLine($"  implausible board {width}x{height}, refusing to write.");
            return 1;
        }

        var tiles = ReadPtr(chain.Logic + 0x20);
        var written = 0;

        for (var i = 0; i < width * height; i++)
        {
            var state = ReadPtr(tiles + (ulong)i * 0x48 + 0x20);
            if (!Sane(state))
            {
                continue;
            }
            if (Write(state + 0x3C, [(byte)(on ? 1 : 0), 0, 1]))
            {
                written++;
            }
        }

        Console.WriteLine($"  {(on ? "lit" : "cleared")} {written}/{width * height} tiles" +
                          $"{(on ? ", they draw as the player's cursor passes over them" : "")}");
        return written > 0 ? 0 : 1;
    }

    private static int FindBytes(byte[] pattern, bool heapOnly)
    {
        Console.WriteLine($"\n  scanning for {Convert.ToHexString(pattern)} ({pattern.Length} bytes)" +
                          (heapOnly ? ", private read-write memory only" : ""));
        var hits = heapOnly
            ? ScanForMany([pattern], 64, ScanRegions(heapOnly: true))[0]
            : ScanFor(pattern, 64);

        foreach (var h in hits)
        {
            Console.WriteLine($"    0x{h:X}");
        }

        Console.WriteLine($"\n  {hits.Count} hit(s)");
        return hits.Count > 0 ? 0 : 1;
    }

    private static List<ulong> ScanFor(byte[] pattern, int max)
    {
        var hits = new List<ulong>();
        var buf = new byte[0x100000];
        ulong address = 0x10000;

        while (address < 0x7FFFFFFFFFFF && hits.Count < max)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            var readable = info.State == 0x1000 && (info.Protect & 0x100) == 0 &&
                           (info.Protect & 0xFF) is not (0 or 0x01);

            if (readable && info.RegionSize <= 0x40000000)
            {
                for (ulong off = 0; off < info.RegionSize; off += (ulong)(buf.Length - pattern.Length))
                {
                    var want = (int)Math.Min((ulong)buf.Length, info.RegionSize - off);
                    if (want < pattern.Length)
                    {
                        break;
                    }

                    if (!ReadProcessMemory(_handle, (nint)(info.BaseAddress + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    for (var i = 0; i + pattern.Length <= (int)got; i++)
                    {
                        var match = true;
                        for (var j = 0; j < pattern.Length; j++)
                        {
                            if (buf[i + j] != pattern[j])
                            {
                                match = false;
                                break;
                            }
                        }

                        if (!match)
                        {
                            continue;
                        }

                        hits.Add(info.BaseAddress + off + (ulong)i);
                        if (hits.Count >= max)
                        {
                            break;
                        }
                    }
                }
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return hits;
    }

    private static float ReadF(ulong a)
    {
        return BitConverter.ToSingle(Read(a, 4));
    }

    private static int HuntAi(int seconds)
    {
        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live, needed to anchor the search arena.");
            return 2;
        }

        var arena = chain.Inst >> 40;
        var vtable = _base + 0x1906AD0;

        var t0 = DateTime.UtcNow;
        FindOne(vtable, arena);
        Console.WriteLine($"\n  hunting LathiumPlayerEasy (vtable 0x{vtable:X}) in arena 0x{arena:X}");
        Console.WriteLine($"  one pass takes {(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms, {seconds}s window");
        Console.WriteLine("  READY, end your turn now");

        var until = DateTime.UtcNow.AddSeconds(seconds);
        var passes = 0;

        while (DateTime.UtcNow < until)
        {
            var obj = FindOne(vtable, arena);
            passes++;
            if (obj == 0)
            {
                continue;
            }

            Console.WriteLine($"\n  FOUND at 0x{obj:X} after {passes} pass(es)");
            var last = "";

            while (DateTime.UtcNow < until)
            {
                if (ReadPtr(obj) != vtable)
                {
                    Console.WriteLine("    object gone");
                    break;
                }

                var line = $"+0xF0 {ReadU32(obj + 0xF0)}  +0xF4 0x{ReadByte(obj + 0xF4):X2}  " +
                           $"+0xFC 0x{ReadByte(obj + 0xFC):X2}  +0x100 {ReadU32(obj + 0x100)}";
                if (line != last)
                {
                    Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  {line}");
                    last = line;
                }

                Thread.Sleep(5);
            }

            Console.WriteLine($"\n  final dump of 0x{obj:X}:");
            DumpHex(obj + 0xE0, 0x40);
            return 0;
        }

        Console.WriteLine($"\n  never found, {passes} pass(es)");
        return 0;
    }

    private static int WatchAddr(ulong address, int length, int seconds)
    {
        Console.WriteLine($"\n  watching 0x{address:X} for {length} bytes, {seconds}s");
        Console.WriteLine("  READY");

        var until = DateTime.UtcNow.AddSeconds(seconds);
        var last = "";

        while (DateTime.UtcNow < until)
        {
            var now = Convert.ToHexString(Read(address, length));
            if (now != last)
            {
                Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  {now}");
                last = now;
            }

            Thread.Sleep(5);
        }

        Console.WriteLine("\n  done");
        return 0;
    }

    private static int WatchTerrain(int seconds)
    {
        Console.WriteLine($"\n  watching the terrain grid for {seconds}s (waiting for a match if none is live)");
        Console.WriteLine("  READY");

        var until = DateTime.UtcNow.AddSeconds(seconds);
        ulong logic = 0;
        sbyte[]? last = null;
        int w = 0, h = 0;

        while (DateTime.UtcNow < until)
        {
            var chain = Walk(report: false);
            if (chain.Logic == 0)
            {
                if (logic != 0)
                {
                    Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  (no match)");
                    logic = 0;
                    last = null;
                }
                Thread.Sleep(200);
                continue;
            }

            var rows = ReadPtr(chain.Logic + 0x08);
            var tiles = ReadPtr(chain.Logic + 0x20);
            if (!Sane(rows) || !Sane(tiles))
            {
                Thread.Sleep(100);
                continue;
            }

            if (chain.Logic != logic)
            {
                logic = chain.Logic;
                h = (int)ReadU32(rows + 0x20);

                var rowArray = ReadPtr(rows + 0x28);
                var firstRow = Sane(rowArray) ? ReadPtr(rowArray) : 0;
                w = Sane(firstRow) ? (int)ReadU32(firstRow + 0x20) : 0;

                if (w is <= 0 or > 16 || h is <= 0 or > 16)
                {
                    Console.Error.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  board reads {w}x{h}, implausible, " +
                                            "not sampling. The layout is stale for this build.");
                    logic = 0;
                    Thread.Sleep(500);
                    continue;
                }

                Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  (match live, board {w}x{h}, tiles 0x{tiles:X})");
                last = null;
            }

            if (w is <= 0 or > 16 || h is <= 0 or > 16)
            {
                Thread.Sleep(200);
                continue;
            }

            var grid = new sbyte[w * h];
            var ok = true;
            for (var i = 0; i < w * h && ok; i++)
            {
                if (TryRead(tiles + (ulong)i * 0x48 + 0x40, 1, out var b))
                {
                    grid[i] = (sbyte)b[0];
                }
                else
                {
                    ok = false;
                }
            }

            if (!ok)
            {
                Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  (read failed, process gone?)");
                break;
            }

            if (last is null || !grid.SequenceEqual(last))
            {
                var changed = new List<string>();
                if (last is not null)
                {
                    for (var i = 0; i < grid.Length; i++)
                    {
                        if (grid[i] != last[i])
                        {
                            changed.Add($"({i % w},{i / w}) {TileTypeName(last[i])}->{TileTypeName(grid[i])}");
                        }
                    }
                }

                Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  " +
                                  (changed.Count > 0 ? string.Join("  ", changed) : "(initial)"));
                for (var y = 0; y < h; y++)
                {
                    Console.WriteLine("        " + string.Join(" ",
                        Enumerable.Range(0, w).Select(x => $"{grid[x + w * y],2}")));
                }

                last = grid;
            }

            Thread.Sleep(20);
        }

        Console.WriteLine("\n  done");
        return 0;
    }

    private static int WatchBoard(int seconds)
    {
        Console.WriteLine($"\n  watching the piece list for {seconds}s (waiting for a match if none is live)");
        Console.WriteLine("  READY");

        var until = DateTime.UtcNow.AddSeconds(seconds);
        var last = "";
        ulong logic = 0, player0 = 0;

        while (DateTime.UtcNow < until)
        {
            var chain = Walk(report: false);
            if (chain.Logic == 0)
            {
                if (logic != 0)
                {
                    Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  (no match)");
                    logic = 0;
                    last = "";
                }
                Thread.Sleep(200);
                continue;
            }

            if (chain.Logic != logic)
            {
                logic = chain.Logic;
                player0 = ReadPtr(chain.Inst + 0x40);
                Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  (match live, inst 0x{chain.Inst:X} logic 0x{logic:X})");
                last = "";
            }

            var count = (int)ReadU32(chain.Logic + 0x38);
            var array = ReadPtr(chain.Logic + 0x40);
            var sb = new StringBuilder();

            for (var i = 0; i < count && i < 64; i++)
            {
                var u = ReadPtr(array + (ulong)(i * 8));
                if (!Sane(u))
                {
                    continue;
                }

                var packed = ReadByte(u + 0x38);
                var seat = ReadPtr(u) == player0 ? "us" : "ai";
                sb.Append($"[{seat} ({Nibble(packed)},{Nibble(packed >> 4)}) " +
                          $"3A:{ReadByte(u + 0x3A):X2} 3B:{ReadByte(u + 0x3B):X2}] ");
            }

            var now = sb.ToString();
            if (now != last)
            {
                Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  {now}");
                last = now;
            }

            Thread.Sleep(20);
        }

        Console.WriteLine("\n  done");
        return 0;
    }

}
