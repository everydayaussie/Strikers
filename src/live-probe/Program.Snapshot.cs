namespace Strikers.LiveProbe;

internal static partial class Program
{
    private static int Snapshot()
    {
        var json = SnapshotJson(quiet: false);
        if (json is null)
        {
            return 2;
        }

        Console.WriteLine(json);
        return 0;
    }

    internal const string NoMatchLine = "{\"nomatch\":true}";
    private static readonly TimeSpan NoMatchAfter = TimeSpan.FromSeconds(1);

    internal static string SampleKey(string board, string placing, string match, string turn)
    {
        return board + placing + match + turn;
    }

    internal static string TurnJson(string controllerName, int aiSeat)
    {
        var seat = -1;
        if (aiSeat is 0 or 1)
        {
            if (controllerName.EndsWith("-> HumanBoardGamePlayingControllerInstance", StringComparison.Ordinal))
            {
                seat = 1 - aiSeat;
            }
            else if (controllerName.EndsWith("-> AIBoardGamePlayingControllerInstance", StringComparison.Ordinal))
            {
                seat = aiSeat;
            }
        }

        return $"\"turn\":{seat}";
    }

    internal static bool NoMatchDue(bool sawMatch, DateTime? goneSince, DateTime now, bool alreadySaid)
    {
        return sawMatch && !alreadySaid && goneSince is { } since && now - since >= NoMatchAfter;
    }

    private static int WatchSnapshot(int seconds)
    {
        var deadline = HoldDeadline(DateTime.UtcNow, seconds);
        string? last = null;
        var sawMatch = false;
        DateTime? goneSince = null;
        var noMatchSaid = false;

        while (DateTime.UtcNow < deadline && !_parentGone)
        {
            var parts = SnapshotParts(quiet: true);

            if (parts is not null)
            {
                sawMatch = true;
                goneSince = null;
                noMatchSaid = false;
            }
            else if (Walk(report: false).Logic == 0)
            {
                goneSince ??= DateTime.UtcNow;
                if (NoMatchDue(sawMatch, goneSince, DateTime.UtcNow, noMatchSaid))
                {
                    Console.WriteLine(NoMatchLine);
                    Console.Out.Flush();
                    noMatchSaid = true;
                    last = null;
                }
            }
            else
            {
                goneSince = null;
            }

            if (parts is not null &&
                SampleKey(parts.Value.Board, parts.Value.Placing, parts.Value.Match, parts.Value.Turn) != last)
            {
                Console.WriteLine(MergeAct(parts.Value.Board, parts.Value.Act, parts.Value.Match,
                                           parts.Value.Placing, parts.Value.Turn, parts.Value.Array,
                                           parts.Value.Count));
                Console.Out.Flush();
                last = SampleKey(parts.Value.Board, parts.Value.Placing, parts.Value.Match, parts.Value.Turn);
            }
            Thread.Sleep(40);
        }
        return 0;
    }

    private static string MergeAct(string board, string act, string match, string placing, string turn,
                                   ulong array, int count)
    {
        var stamp = $"\"t\":\"{DateTime.Now:HH:mm:ss.fff}\"";
        return board[..^1] + "," + stamp + "," + act + "," + match + "," + placing + "," + turn + "," +
               CommitsJson(array, count) + "}";
    }

    private const int TornReadAttempts = 4;

    internal static bool TornByDuplicateUnit(IReadOnlyList<ulong> units)
    {
        var seen = new HashSet<ulong>();
        foreach (var u in units)
        {
            if (!seen.Add(u))
            {
                return true;
            }
        }

        return false;
    }

    private static string? SnapshotJson(bool quiet)
    {
        var parts = SnapshotParts(quiet);
        if (parts is null)
        {
            return null;
        }

        return MergeAct(parts.Value.Board, parts.Value.Act, parts.Value.Match, parts.Value.Placing,
                        parts.Value.Turn, parts.Value.Array, parts.Value.Count);
    }

    private static string ControllerActionJson((ulong Challenge, ulong Inst, ulong Logic) chain,
                                               ulong array, int count, int width, int height)
    {
        const string None = "\"act\":null";

        var (obj, name) = KnownChainController(chain);

        if (obj == 0 || !name.Contains("Human"))
        {
            return None;
        }

        var fx = (int)ReadU32(obj + 0xA0);
        var fy = (int)ReadU32(obj + 0xA4);
        var tx = (int)ReadU32(obj + 0xA8);
        var ty = (int)ReadU32(obj + 0xAC);

        if (fx < 0 || fx >= width || fy < 0 || fy >= height ||
            tx < 0 || tx >= width || ty < 0 || ty >= height)
        {
            return None;
        }

        var unit = ReadPtr(obj + 0x88);
        var idx = -1;
        for (var i = 0; i < count && i < 64; i++)
        {
            if (ReadPtr(array + (ulong)(i * 8)) == unit)
            {
                idx = i;
                break;
            }
        }

        var on = ReadByte(obj + 0x320) != 0;
        return $"\"act\":{{\"unit\":{idx},\"fx\":{fx},\"fy\":{fy},\"tx\":{tx},\"ty\":{ty}," +
               $"\"on\":{on.ToString().ToLowerInvariant()}}}";
    }

    private static sbyte[]? _lastTerrain;

    private const int MaxTerrainTilesAlteredPerRead = 2;

    private static bool _terrainAltered;

    private static bool _asymmetricBoard;

    private static int TerrainTilesChanged(sbyte[] before, sbyte[] after)
    {
        if (before.Length != after.Length)
        {
            return after.Length;
        }

        var changed = 0;
        for (var i = 0; i < after.Length; i++)
        {
            if (before[i] != after[i])
            {
                changed++;
            }
        }

        return changed;
    }

    private static (string Board, string Act, string Match, string Placing, string Turn, ulong Array, int Count)? SnapshotParts(bool quiet)
    {
        void Fail(string msg)
        {
            if (!quiet)
            {
                Console.Error.WriteLine(msg);
            }
        }

        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Fail("no match is live (logic pointer null). Load a Strike match and re-run.");
            return null;
        }

        var rows = ReadPtr(chain.Logic + 0x08);
        var height = (int)ReadU32(rows + 0x20);
        var width = (int)ReadU32(ReadPtr(ReadPtr(rows + 0x28)) + 0x20);
        if (width is <= 0 or > 16 || height is <= 0 or > 16)
        {
            Fail($"implausible board {width}x{height}, the layout is wrong for this build.");
            return null;
        }

        var tiles = ReadPtr(chain.Logic + 0x20);
        var terrain = new sbyte[width * height];
        for (var i = 0; i < terrain.Length; i++)
        {
            if (!TryReadByte(tiles + (ulong)(i * 0x48) + 0x40, out var t))
            {
                Fail($"tile {i} unreadable, refusing to emit a partial snapshot.");
                return null;
            }
            terrain[i] = (sbyte)t;
        }

        var symmetric = true;
        for (var y = 0; y < height && symmetric; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (terrain[y * width + x] != terrain[(height - 1 - y) * width + (width - 1 - x)])
                {
                    symmetric = false;
                    break;
                }
            }
        }

        if (!symmetric)
        {
            if (_lastTerrain is null)
            {
                if (!_terrainAltered && !_asymmetricBoard)
                {
                    Fail("terrain grid is NOT 180-degree symmetric on the first read, so the board was "
                         + "already wrong before anything could alter it: suspect the offset or stride. "
                         + "Not emitting. (Pass --terrain-altered for a one-shot read of a match where "
                         + "Growth or Alter Terrain has already fired, or --asymmetric-board if the "
                         + "board was authored that way.)");
                    return null;
                }
            }
            else
            {
                var changed = TerrainTilesChanged(_lastTerrain, terrain);
                if (changed > MaxTerrainTilesAlteredPerRead)
                {
                    Fail($"terrain grid is NOT 180-degree symmetric and {changed} tiles differ from the "
                         + $"last accepted read, more than the {MaxTerrainTilesAlteredPerRead} an ability "
                         + "can alter: suspect the offset or stride. Not emitting.");
                    return null;
                }
            }
        }

        _lastTerrain = terrain;

        var player0 = ReadPtr(chain.Inst + 0x40);
        var player1 = ReadPtr(chain.Inst + 0x48);
        var pieces = new List<string>();
        var units = new List<ulong>();
        var count = 0;
        var array = 0UL;
        for (var attempt = 0; ; attempt++)
        {
            pieces.Clear();
            units.Clear();
            count = (int)ReadU32(chain.Logic + 0x38);
            array = ReadPtr(chain.Logic + 0x40);

            for (var i = 0; i < count && i < 64; i++)
            {
                var u = ReadPtr(array + (ulong)(i * 8));
                if (!Sane(u) || !TryReadByte(u + 0x38, out var packed) ||
                    !TryReadByte(u + 0x3A, out var health) || !TryReadByte(u + 0x3B, out var flags) ||
                    !TryReadByte(u + 0x68, out var actedByte))
                {
                    Fail($"piece {i} unreadable, refusing to emit a partial snapshot.");
                    return null;
                }

                var resource = ReadPtr(u + 0x08);
                var range = -1;
                var skill = -1;

                var uuid = "";
                if (Sane(resource))
                {
                    if (TryRead(resource + 0x5C, 4, out var rangeBytes))
                    {
                        range = BitConverter.ToInt32(rangeBytes, 0);
                    }

                    var ability = ReadPtr(resource + 0x48);
                    if (Sane(ability) && TryRead(ability + 0x38, 1, out var skillBytes))
                    {
                        skill = skillBytes[0];
                    }

                    if (TryRead(resource + 0x08, 16, out var idBytes))
                    {
                        uuid = Convert.ToHexString(idBytes);
                    }
                }

                var owner = ReadPtr(u);
                var seat = owner == player0 ? 0 : owner == player1 ? 1 : -1;
                if (seat < 0)
                {
                    Fail($"piece {i} belongs to neither player (owner 0x{owner:X}), not emitting.");
                    return null;
                }

                units.Add(u);
                pieces.Add($"{{\"idx\":{i},\"x\":{Nibble(packed)},\"y\":{Nibble(packed >> 4)},\"health\":{health}," +
                           $"\"facing\":{flags & 3},\"owner\":{seat}," +
                           $"\"burst\":{((flags & 0x40) != 0).ToString().ToLowerInvariant()}," +
                           $"\"acts\":{(flags >> 2) & 3},\"bursts\":{(flags >> 4) & 3}," +
                           $"\"acted\":{((actedByte & 1) != 0).ToString().ToLowerInvariant()}," +
                           $"\"skill\":{skill},\"range\":{range},\"uuid\":\"{uuid}\"}}");
            }

            if (!TornByDuplicateUnit(units))
            {
                break;
            }

            if (attempt + 1 >= TornReadAttempts)
            {
                Fail("the unit array listed one machine twice on every read, a compaction in progress; " +
                     "not emitting.");
                return null;
            }

            Thread.Sleep(2);
        }

        var aiSeat = FindAiSeat(chain.Inst);
        var board = $"{{\"width\":{width},\"height\":{height},\"aiSeat\":{aiSeat}," +
                    $"\"pieces\":[{string.Join(",", pieces)}]," +
                    $"\"terrain\":[{string.Join(",", terrain)}]}}";

        return (board,
                ControllerActionJson(chain, array, count, width, height),
                MatchStateJson(chain.Inst, player0, player1),
                PlacingStateJson(chain.Inst),
                TurnJson(KnownChainController(chain).Name, aiSeat),
                array,
                count);
    }

    internal const string PlacingUnreadableJson = "\"placing\":{\"unreadable\":true}";

    private static string PlacingStateJson(ulong inst)
    {
        const string None = "\"placing\":null";

        if (!Sane(inst))
        {
            return None;
        }

        var phase = FindPlacingPhase(inst, out var phaseUnreadable);
        if (phaseUnreadable)
        {
            return PlacingUnreadableJson;
        }

        if (phase == 0)
        {
            return None;
        }

        if (!TryReadU64(phase + 0x10, out var activePlayer))
        {
            return PlacingUnreadableJson;
        }

        var active = -1;
        var left = new int[2];
        for (var i = 0; i < 2; i++)
        {
            if (!TryReadU64(inst + 0x40 + (ulong)(i * 8), out var p) || !Sane(p) || !TryReadU32(p + 0x40, out var count))
            {
                return PlacingUnreadableJson;
            }

            left[i] = (int)count;
            if (p == activePlayer)
            {
                active = i;
            }
        }

        return $"\"placing\":{{\"left\":[{left[0]},{left[1]}],\"active\":{active}}}";
    }

    private static ulong FindPlacingPhase(ulong inst)
    {
        return FindPlacingPhase(inst, out _);
    }

    private static ulong FindPlacingPhase(ulong inst, out bool unreadable)
    {
        unreadable = false;
        foreach (var slot in new ulong[] { 0x68, 0x60 })
        {
            if (!TryReadU64(inst + slot, out var phase))
            {
                unreadable = true;
                return 0;
            }

            if (!Sane(phase))
            {
                continue;
            }

            if (!TryReadU64(phase, out var vtable))
            {
                unreadable = true;
                return 0;
            }

            if (vtable == _base + UnitPlacingPhaseVtableRva)
            {
                return phase;
            }
        }

        return 0;
    }

    internal static int WinnerSeat(ulong winnerPtr, ulong player0, ulong player1)
    {
        if (winnerPtr == 0)
        {
            return -1;
        }

        return winnerPtr == player0 ? 0 : winnerPtr == player1 ? 1 : -1;
    }

    private static string MatchStateJson(ulong inst, ulong player0, ulong player1)
    {
        const string None = "\"match\":null";

        if (!Sane(inst))
        {
            return None;
        }

        if (!TryReadByte(inst + MatchOverFlag, out var overByte))
        {
            return None;
        }

        var winner = WinnerSeat(ReadPtr(inst + MatchWinner), player0, player1);

        var vp0 = TryReadU32(player0 + PlayerVictoryPoints, out var raw0) ? (int)raw0 : -1;
        var vp1 = TryReadU32(player1 + PlayerVictoryPoints, out var raw1) ? (int)raw1 : -1;

        var max = -1;
        var boardGame = ReadPtr(inst);
        if (Sane(boardGame))
        {
            var settings = ReadPtr(boardGame + SettingsFromBoardGame);
            if (Sane(settings) && TryReadU32(settings + MaxVictoryPointsOffset, out var rawMax))
            {
                max = (int)rawMax;
            }
        }

        var over = overByte != 0;
        return $"\"match\":{{\"over\":{over.ToString().ToLowerInvariant()},\"winner\":{winner}," +
               $"\"vp\":[{vp0},{vp1}],\"vpMax\":{max}}}";
    }

    private const ulong ChallengeManagerOffset = 0x190;

    private static readonly (string Name, ulong Rva)[] ChallengeTypes =
    {
        ("BoardGame", 0x190F750), ("FightingPit", 0x1911840),
        ("CombatArena", 0x190FCB8), ("HuntingGround", 0x19115E0),
    };

    private sealed record Challenge(
        string Type, ulong Ptr, int Parents, byte Unlocked,
        int ReqCount, ulong Condition, int CostCount, List<(ulong Item, int Amount)> Costs,
        List<(ulong Group, int Needed, ulong Specific)> Reqs);

    private static int Challenges()
    {
        var g = ReadPtr(_base + GlobalRva);
        var mgr = Sane(g) ? ReadPtr(g + ChallengeManagerOffset) : 0;
        if (!Sane(mgr) || !TryRead(mgr, 0x18, out var head))
        {
            Console.Error.WriteLine("ChallengeManager unreachable. Is the game past the main menu?");
            return 1;
        }

        var data = BitConverter.ToUInt64(head, 0x08);
        var count = BitConverter.ToInt32(head, 0x10);
        var capacity = BitConverter.ToInt32(head, 0x14);
        Console.WriteLine($"ChallengeManager @0x{mgr:X}  registered {count}, capacity {capacity}, map data 0x{data:X}");

        if (!Sane(data) || capacity <= 0 || capacity > 1 << 16)
        {
            Console.Error.WriteLine("  map header does not read plausibly, offsets are stale for this build.");
            return 1;
        }

        var found = new List<Challenge>();
        int unknown = 0, failed = 0;

        for (var i = 0; i < capacity; i++)
        {
            if (!TryRead(data + (ulong)i * 0x20, 0x20, out var e))
            {
                failed++;
                continue;
            }
            var hash = BitConverter.ToUInt32(e, 0x18);
            var ptr = BitConverter.ToUInt64(e, 0x10);
            if (hash == 0 || !Sane(ptr))
            {
                continue;
            }

            if (!TryRead(ptr, 0xA0, out var h))
            {
                failed++;
                continue;
            }
            var vt = BitConverter.ToUInt64(h, 0);
            var type = ChallengeTypes.FirstOrDefault(t => _base + t.Rva == vt).Name;
            if (type is null)
            {
                unknown++;
                continue;
            }

            var res = BitConverter.ToUInt64(h, 0x10);
            var req = BitConverter.ToUInt64(h, 0x28);

            var reqCount = 0;
            ulong condition = 0;
            var reqs = new List<(ulong, int, ulong)>();
            if (Sane(req) && TryRead(req, 0x38, out var rb))
            {
                reqCount = BitConverter.ToInt32(rb, 0x20);
                condition = BitConverter.ToUInt64(rb, 0x30);
                var rd = BitConverter.ToUInt64(rb, 0x28);
                for (var k = 0; k < reqCount && Sane(rd); k++)
                {
                    if (TryRead(rd + (ulong)k * 0x18, 0x18, out var q))
                    {
                        reqs.Add((BitConverter.ToUInt64(q, 0), BitConverter.ToInt32(q, 8), BitConverter.ToUInt64(q, 0x10)));
                    }
                }
            }

            var costCount = 0;
            var costs = new List<(ulong, int)>();
            if (Sane(res) && TryRead(res, 0x60, out var sb))
            {
                costCount = BitConverter.ToInt32(sb, 0x50);
                var cd = BitConverter.ToUInt64(sb, 0x58);
                for (var k = 0; k < costCount && Sane(cd); k++)
                {
                    if (!TryRead(cd + (ulong)k * 8, 8, out var cp))
                    {
                        continue;
                    }

                    var entry = BitConverter.ToUInt64(cp, 0);
                    if (Sane(entry) && TryRead(entry, 0x38, out var cb))
                    {
                        costs.Add((BitConverter.ToUInt64(cb, 0x28), BitConverter.ToInt32(cb, 0x30)));
                    }
                }
            }

            found.Add(new Challenge(type, ptr, BitConverter.ToInt32(h, 0x30), h[0x82],
                                    reqCount, condition, costCount, costs, reqs));
        }

        Console.WriteLine($"\n  walked {found.Count}" +
                          (unknown > 0 ? $", {unknown} of an unrecognised type" : "") +
                          (failed > 0 ? $", {failed} unreadable" : ""));

        Console.WriteLine("\n  type            n   reqs  cond   fee  parents  locked");
        foreach (var t in ChallengeTypes)
        {
            var g2 = found.Where(c => c.Type == t.Name).ToList();
            if (g2.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"  {t.Name,-14} {g2.Count,3}  {g2.Count(c => c.ReqCount > 0),4}  " +
                              $"{g2.Count(c => c.Condition != 0),4}  {g2.Count(c => c.CostCount > 0),4}  " +
                              $"{g2.Count(c => c.Parents > 0),7}  {g2.Count(c => c.Unlocked == 0),6}");
        }

        if (found.Count > 0 && found.All(c => c.ReqCount == 0 && c.CostCount == 0 && c.Condition == 0))
        {
            Console.WriteLine("\n  Warning: no challenge of ANY type carries a fee, requirement or condition.\n" +
                              "    That is what a stale offset produces. Treat it as a bad read, not a finding,\n" +
                              "    until one type reads non-zero as a control.");
        }

        var gated = found.Where(c => c.CostCount > 0 || c.ReqCount > 0 || c.Condition != 0 || c.Unlocked == 0)
                         .OrderBy(c => c.Type).ToList();
        Console.WriteLine($"\n  {gated.Count} carry a gate, {found.Count - gated.Count} are ungated:");
        foreach (var c in gated)
        {
            Console.WriteLine($"    {c.Type,-14} @0x{c.Ptr:X}  parents {c.Parents}" +
                              (c.Unlocked == 0 ? "  LOCKED" : "") +
                              (c.Condition != 0 ? $"  cond 0x{c.Condition:X}" : ""));
            foreach (var (item, amount) in c.Costs)
            {
                Console.WriteLine($"        fee  {amount} x item 0x{item:X}");
            }

            foreach (var (grp, needed, spec) in c.Reqs)
            {
                Console.WriteLine($"        need {needed} from group 0x{grp:X}" +
                                  (spec != 0 ? $", plus challenge 0x{spec:X}" : ""));
            }
        }

        return 0;
    }

    private static int UnlockChallenges(string[] args)
    {
        var typeAt = IndexOfArg(args, "--type");
        var wantType = typeAt >= 0 && typeAt + 1 < args.Length ? args[typeAt + 1] : "BoardGame";
        if (!ChallengeTypes.Any(t => t.Name == wantType))
        {
            Console.Error.WriteLine($"  --type must be one of: {string.Join(", ", ChallengeTypes.Select(t => t.Name))}");
            return 1;
        }

        var g = ReadPtr(_base + GlobalRva);
        var mgr = Sane(g) ? ReadPtr(g + ChallengeManagerOffset) : 0;
        if (!Sane(mgr) || !TryRead(mgr, 0x18, out var head))
        {
            Console.Error.WriteLine("ChallengeManager unreachable. Is the game past the main menu?");
            return 1;
        }

        var data = BitConverter.ToUInt64(head, 0x08);
        var capacity = BitConverter.ToInt32(head, 0x14);
        if (!Sane(data) || capacity <= 0 || capacity > 1 << 16)
        {
            Console.Error.WriteLine("  map header does not read plausibly, offsets are stale for this build.");
            return 1;
        }

        var targets = new List<(ulong Ptr, int Parents)>();
        for (var i = 0; i < capacity; i++)
        {
            if (!TryRead(data + (ulong)i * 0x20, 0x20, out var e))
            {
                continue;
            }

            if (BitConverter.ToUInt32(e, 0x18) == 0)
            {
                continue;
            }

            var ptr = BitConverter.ToUInt64(e, 0x10);
            if (!Sane(ptr) || !TryRead(ptr, 0x40, out var h))
            {
                continue;
            }

            if (BitConverter.ToUInt64(h, 0) != _base + ChallengeTypes.First(t => t.Name == wantType).Rva)
            {
                continue;
            }

            var parents = BitConverter.ToInt32(h, 0x30);
            if (parents > 0)
            {
                targets.Add((ptr, parents));
            }
        }

        Console.WriteLine($"\n  {wantType}: {targets.Count} challenge(s) carry a prerequisite");
        foreach (var (ptr, parents) in targets.Take(10))
        {
            Console.WriteLine($"    @0x{ptr:X}  parents {parents} -> 0");
        }

        if (targets.Count > 10)
        {
            Console.WriteLine($"    ... and {targets.Count - 10} more");
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("  nothing to do.");
            return 0;
        }
        if (!args.Contains("--yes"))
        {
            Console.WriteLine("  dry run, pass --yes to write.");
            return 0;
        }

        var wrote = 0;
        foreach (var (ptr, _) in targets)
        {
            if (Write(ptr + 0x30, BitConverter.GetBytes(0)))
            {
                wrote++;
            }
        }

        Console.WriteLine($"  cleared {wrote} of {targets.Count}. Re-open the challenge list to see it.");
        Console.WriteLine("  Warning: memory only, gone on restart. Completing one, though, writes to the save.");
        return wrote == targets.Count ? 0 : 1;
    }

}
