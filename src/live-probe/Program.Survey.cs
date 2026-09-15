using System.Runtime.InteropServices;
using System.Text;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private static int Survey(ulong moduleEnd, bool grids)
    {
        var names = new (string Name, ulong Rva)[]
        {
            ("BoardGame", BoardGameVtableRva), ("BoardGameSettings", SettingsVtableRva),
            ("BoardGameTile", TileVtableRva), ("BoardGameUnitAbility", AbilityVtableRva),
            ("BoardGameBoard", BoardVtableRva), ("BoardGameBoardTileRow", TileRowVtableRva),
            ("BoardGameDraftPreset", DraftPresetVtableRva), ("BoardGameUnit", BoardGameUnitVtableRva),
            ("BoardGameUnitCollectionComponentResource", CollectionResourceVtableRva),
            ("BoardGameUnitCollectionSave", CollectionSaveVtableRva),
            ("BoardGameUnitCollectionSave(2)", CollectionSaveVtable2Rva),
        };

        Console.WriteLine("\n  surveying loaded BoardGame resources (one pass, all types)");
        var hits = ScanForAll(names.Select(n => _base + n.Rva), moduleEnd);

        Console.WriteLine();
        foreach (var (name, rva) in names)
        {
            Console.WriteLine($"    {name,-24} {hits[_base + rva].Count,4}   (vftable RVA 0x{rva:X})");
        }

        var settings = hits[_base + SettingsVtableRva];
        var tiles = hits[_base + TileVtableRva];
        var abilities = hits[_base + AbilityVtableRva];
        var boardGames = hits[_base + BoardGameVtableRva];
        var boards = hits[_base + BoardVtableRva];
        var presets = hits[_base + DraftPresetVtableRva];

        foreach (var s in settings)
        {
            Console.WriteLine($"\n  === BoardGameSettings @0x{s:X} ===");
            Console.WriteLine($"    +0x28 BurstHealthCost {(TryReadU32(s + 0x28, out var bh) ? (int)bh : -1)}" +
                              $"   +0x2C MaxDraftPoints {(TryReadU32(s + 0x2C, out var mp) ? (int)mp : -1)}" +
                              $"   +0x30 MaxDuplicateDraftUnits {(TryReadU32(s + 0x30, out var md) ? (int)md : -1)}");

            var (tc, td) = Array_(s + 0xF8);
            Console.WriteLine($"    AllTileTypes {tc} @0x{td:X}");
            for (var i = 0; i < tc && i < 32 && Sane(td); i++)
            {
                var t = ReadPtr(td + (ulong)(i * 8));
                var type = Sane(t) && TryRead(t + 0x50, 1, out var tb) ? (sbyte)tb[0] : int.MinValue;
                Console.WriteLine($"      [{i,2}] 0x{t:X}  {NameAt(t + 0x28),-18} type {type,3} {TileTypeName(type)}");
            }

            var (ac, ad) = Array_(s + 0x108);
            Console.WriteLine($"    AllAbilities {ac} @0x{ad:X}");
            for (var i = 0; i < ac && i < 64 && Sane(ad); i++)
            {
                var a = ReadPtr(ad + (ulong)(i * 8));
                var type = Sane(a) && TryRead(a + 0x38, 1, out var ab) ? ab[0] : int.MinValue;
                Console.WriteLine($"      [{i,2}] 0x{a:X}  {NameAt(a + 0x20),-18} type {type,3} {SkillName(type)}");
            }
        }

        Console.WriteLine($"\n  === all loaded BoardGameTile resources ({tiles.Count}) ===");
        foreach (var t in tiles)
        {
            var type = TryRead(t + 0x50, 1, out var tb) ? (sbyte)tb[0] : int.MinValue;
            Console.WriteLine($"    0x{t:X}  {NameAt(t + 0x28),-18} type {type,3} {TileTypeName(type)}");
        }

        Console.WriteLine($"\n  === all loaded BoardGameUnitAbility resources ({abilities.Count}) ===");
        foreach (var a in abilities)
        {
            var type = TryRead(a + 0x38, 1, out var ab) ? ab[0] : int.MinValue;
            Console.WriteLine($"    0x{a:X}  {NameAt(a + 0x20),-18} type {type,3} {SkillName(type)}");
        }

        var units = hits[_base + BoardGameUnitVtableRva].Where(h =>
        {
            if (!TryRead(h, 0x88, out var b))
            {
                return false;
            }

            for (var off = 0x50; off <= 0x60; off += 4)
            {
                if (BitConverter.ToInt32(b, off) is < 0 or > 64)
                {
                    return false;
                }
            }

            return true;
        }).ToList();

        Console.WriteLine($"\n  === machine roster ({units.Count} BoardGameUnit resources) ===");
        foreach (var u in units)
        {
            PrintUnitStats("  ", u);
        }

        var usedSkills = units
            .Select(u => ReadPtr(u + 0x48))
            .Where(Sane)
            .Select(a => TryRead(a + 0x38, 1, out var sb) ? sb[0] : -1)
            .Where(s => s >= 0).Distinct().ToHashSet();
        var usedPatterns = units
            .Select(u => TryRead(u + 0x70, 1, out var pb) ? pb[0] : -1)
            .Where(p => p >= 0).Distinct().ToHashSet();

        Console.WriteLine($"\n    skills used by a machine   : {string.Join(", ", usedSkills.OrderBy(s => s).Select(SkillName))}");
        Console.WriteLine($"    skills with NO machine     : {string.Join(", ", Enumerable.Range(0, SkillNames.Length).Except(usedSkills).Select(SkillName))}");
        Console.WriteLine($"    attack patterns used       : {string.Join(", ", usedPatterns.OrderBy(p => p).Select(PatternName))}");
        Console.WriteLine($"    patterns with NO machine   : {string.Join(", ", Enumerable.Range(0, PatternNames.Length).Except(usedPatterns).Select(PatternName))}");

        Console.WriteLine($"\n  === challenges ({boardGames.Count} BoardGame resources) ===");
        foreach (var g in boardGames)
        {
            Console.WriteLine($"\n    BoardGame @0x{g:X}   uuid {ResourceUuid(g)}   settings 0x{ReadPtr(g + 0x40):X}");
            for (var seat = 0; seat < 2; seat++)
            {
                var p = ReadPtr(g + 0x20 + (ulong)(seat * 0x10));
                var draft = ReadPtr(g + 0x28 + (ulong)(seat * 0x10));
                var vt = Sane(p) ? ReadPtr(p) : 0;
                var kind = vt == _base + AiPlayerVtableRva ? "AI"
                         : vt == _base + HumanPlayerVtableRva ? "human"
                         : $"vt 0x{vt:X}";
                var diff = "";
                if (vt == _base + AiPlayerVtableRva && TryRead(p + 0x48, 12, out var d))
                {
                    diff = $"  low {BitConverter.ToSingle(d, 0):F2} med {BitConverter.ToSingle(d, 4):F2} " +
                           $"high {BitConverter.ToSingle(d, 8):F2}";
                }

                var flags = "";
                if (IsDraft(draft))
                {
                    var placeNow = TryReadByte(draft + 0x30, out var pi) && pi != 0;
                    var canStart = TryReadByte(draft + 0x31, out var st2) && st2 != 0;
                    var customOk = TryReadByte(draft + 0x32, out var cd) && cd != 0;
                    flags = $"  PlaceInstantly {(placeNow ? 1 : 0)}" +
                            $" IsAllowedToStart {(canStart ? 1 : 0)}" +
                            $" IsAllowedCustomDraft {(customOk ? 1 : 0)}";
                }

                Console.WriteLine($"      player{seat + 1} 0x{p:X} {kind,-6} draft 0x{draft:X}{diff}{flags}");
                if (IsDraft(draft))
                {
                    ReportDraft(draft);
                }
            }

            var (bc, bd) = Array_(g + 0x48);
            Console.WriteLine($"      Boards {bc} @0x{bd:X}");
            for (var i = 0; i < bc && i < 16 && Sane(bd); i++)
            {
                var b = ReadPtr(bd + (ulong)(i * 8));
                var blight = TryReadByte(b + 0x30, out var eb) ? eb : (byte)0;
                var startTurn = TryReadU32(b + 0x34, out var st) ? (int)st : -1;
                var grid = ReadBoardGrid(b);
                var used = grid.SelectMany(r => r).Where(v => v != int.MinValue).Distinct().OrderBy(v => v);
                Console.WriteLine($"        board[{i}] 0x{b:X}  {grid.Count}x{(grid.Count > 0 ? grid[0].Count : 0)}" +
                                  $"  blight {(blight != 0 ? "ON" : "off")} from turn {startTurn}" +
                                  $"  types: {string.Join(",", used.Select(TileTypeName))}");
                if (!grids)
                {
                    continue;
                }

                foreach (var row in grid)
                {
                    Console.WriteLine($"          {string.Join(" ", row.Select(v => v == int.MinValue ? " ?" : $"{v,2}"))}");
                }
            }
        }

        var linked = new HashSet<ulong>();
        foreach (var g in boardGames)
        {
            var (bc, bd) = Array_(g + 0x48);
            for (var i = 0; i < bc && i < 16 && Sane(bd); i++)
            {
                linked.Add(ReadPtr(bd + (ulong)(i * 8)));
            }
        }
        var orphans = boards.Where(b => !linked.Contains(b)).ToList();
        Console.WriteLine($"\n  {boards.Count} board resource(s) loaded, {linked.Count} referenced by a challenge, " +
                          $"{orphans.Count} referenced by none");
        foreach (var b in orphans.Take(20))
        {
            var grid = ReadBoardGrid(b);
            var used = grid.SelectMany(r => r).Where(v => v != int.MinValue).Distinct().OrderBy(v => v);
            Console.WriteLine($"    orphan 0x{b:X}  {grid.Count}x{(grid.Count > 0 ? grid[0].Count : 0)}" +
                              $"  types: {string.Join(",", used.Select(TileTypeName))}");
        }

        var collections = hits[_base + CollectionResourceVtableRva];
        Console.WriteLine($"\n  === BoardGameUnitCollectionComponentResource ({collections.Count}) ===");
        foreach (var c in collections)
        {
            Console.WriteLine($"\n    @0x{c:X}");
            Console.WriteLine($"      +0x30 MaxCountPerUnitType {(TryReadU32(c + 0x30, out var mc) ? (int)mc : -1)}" +
                              $"   +0x34 MaxDraftPointCount {(TryReadU32(c + 0x34, out var dp) ? (int)dp : -1)}" +
                              $"   +0x38 MinPointsRequired {(TryReadU32(c + 0x38, out var mr) ? (int)mr : -1)}");

            var count = TryReadU32(c + 0x20, out var ac) ? (int)ac : 0;
            var cap = TryReadU32(c + 0x24, out var acap) ? (int)acap : 0;
            var data = ReadPtr(c + 0x28);
            Console.WriteLine($"      +0x20 AllCollectableUnitTypes  count {count}  capacity {cap}  data 0x{data:X}");

            if (count <= 0 || !Sane(data))
            {
                continue;
            }

            Console.WriteLine("      raw (work the stride out from the repeat):");
            DumpHex(data, Math.Min(count * 32, 0x180));

            foreach (var stride in new[] { 16, 24, 32 })
            {
                Console.WriteLine($"      as stride {stride}:");
                for (var i = 0; i < count && i < 6; i++)
                {
                    if (TryRead(data + (ulong)(i * stride), 16, out var u))
                    {
                        Console.WriteLine($"        [{i,2}] {Convert.ToHexString(u)}");
                    }
                }
            }
        }

        var saves = hits[_base + CollectionSaveVtableRva].Concat(hits[_base + CollectionSaveVtable2Rva]).ToList();
        Console.WriteLine($"\n  === BoardGameUnitCollectionSave ({saves.Count}), the player's own collection ===");
        foreach (var s in saves)
        {
            var (pc, pd) = Array_(s + 0x28);
            Console.WriteLine($"    @0x{s:X}  DraftPresets {pc} @0x{pd:X}  " +
                              $"SelectedPreset {(TryReadU32(s + 0x38, out var sp) ? (int)sp : -1)}");
        }

        Console.WriteLine($"\n  === BoardGameDraftPreset ({presets.Count}), the player's saved collections ===");
        foreach (var p in presets)
        {
            var (dc, dd) = Array_(p + 0x28);
            Console.WriteLine($"    preset 0x{p:X}  DraftedUnits {dc} @0x{dd:X}");
            for (var i = 0; i < dc && i < 32 && Sane(dd); i++)
            {
                if (TryRead(dd + (ulong)(i * 0x20), 0x20, out var rec))
                {
                    Console.WriteLine($"      [{i,2}] uuid {Convert.ToHexString(rec, 8, 16)}  " +
                                      $"amount {BitConverter.ToInt32(rec, 0x18)}");
                }
            }
        }

        return 0;
    }

    private static string UnitName(ulong unit)
    {
        return NameAt(unit + 0x20);
    }

    private static string NameAt(ulong nameField)
    {
        var text = ReadPtr(nameField);
        if (!Sane(text))
        {
            return "";
        }

        var str = ReadPtr(text + 0x20);
        if (!Sane(str) || !TryRead(text + 0x28, 2, out var lenBuf))
        {
            return "";
        }

        var len = BitConverter.ToUInt16(lenBuf);
        if (len is 0 or > 64 || !TryRead(str, len, out var b))
        {
            return "";
        }

        var s = Encoding.UTF8.GetString(b);
        return s.All(c => c is >= ' ' and <= '~') ? s : "";
    }

    private static void PrintUnitStats(string label, ulong unit)
    {
        if (!TryRead(unit, 0x88, out var b))
        {
            Console.WriteLine($"    {label} 0x{unit:X}  <unreadable>");
            return;
        }
        var ability = BitConverter.ToUInt64(b, 0x48);
        var skill = Sane(ability) && TryRead(ability + 0x38, 1, out var sb) ? SkillName(sb[0]) : "-";

        Console.WriteLine($"    {label} 0x{unit:X}  {UnitName(unit),-18}  hp {BitConverter.ToInt32(b, 0x50),2}  " +
                          $"cost {BitConverter.ToInt32(b, 0x54),2}  move {BitConverter.ToInt32(b, 0x58),2}  " +
                          $"range {BitConverter.ToInt32(b, 0x5C),2}  power {BitConverter.ToInt32(b, 0x60),2}  " +
                          $"armour F{(sbyte)b[0x80],2} B{(sbyte)b[0x81],2} L{(sbyte)b[0x82],2} R{(sbyte)b[0x83],2}  " +
                          $"{PatternName(b[0x70]),-6} {skill,-10} uuid {Convert.ToHexString(b, 0x08, 16)}");
    }

    private static int SetStats(string[] args, int at, ulong moduleEnd)
    {
        if (at >= args.Length)
        {
            Console.Error.WriteLine("--set-stats <machine uuid> [--hp N] [--cost N] [--move N] " +
                                    "[--range N] [--power N] [--yes]");
            return 1;
        }

        var uuid = NormaliseUuid(args[at]);
        if (uuid.Length != 32)
        {
            Console.Error.WriteLine($"'{args[at]}' is not a machine uuid (32 hex characters).");
            return 1;
        }

        (string Flag, int Offset, int Low, int High)[] fields =
        [
            ("--hp", 0x50, 1, 50),
            ("--cost", 0x54, 0, 99),
            ("--move", 0x58, 0, 12),
            ("--range", 0x5C, 0, 12),
            ("--power", 0x60, 0, 12),
        ];

        var wanted = new List<(int Offset, string Flag, int Value)>();
        foreach (var (flag, offset, low, high) in fields)
        {
            var i = IndexOfArg(args, flag);
            if (i < 0)
            {
                continue;
            }

            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var value))
            {
                Console.Error.WriteLine($"{flag} takes a number.");
                return 1;
            }

            if (value < low || value > high)
            {
                Console.Error.WriteLine($"{flag} {value} is outside {low}..{high}.");
                return 1;
            }

            wanted.Add((offset, flag, value));
        }

        if (wanted.Count == 0)
        {
            Console.Error.WriteLine("nothing to write: name at least one of --hp --cost --move --range --power.");
            return 1;
        }

        var unitVtable = _base + BoardGameUnitVtableRva;
        Console.WriteLine($"\n  looking for machine {uuid}");
        var hits = ScanForAll([unitVtable], moduleEnd);

        var unit = hits[unitVtable].FirstOrDefault(u => ResourceUuid(u) == uuid);
        if (unit == 0)
        {
            Console.Error.WriteLine($"  no loaded machine carries that uuid ({hits[unitVtable].Count} found). " +
                                    "Stand at the Machine Strike menu so the roster is loaded.");
            return 2;
        }

        Console.WriteLine($"  found @0x{unit:X}  {UnitName(unit)}");
        foreach (var (offset, flag, value) in wanted)
        {
            var was = TryRead(unit + (ulong)offset, 4, out var b) ? BitConverter.ToInt32(b, 0) : -1;
            if (!args.Contains("--yes"))
            {
                Console.WriteLine($"    would write {flag[2..],-5} {was} -> {value}  @0x{unit + (ulong)offset:X}");
                continue;
            }

            if (!Write(unit + (ulong)offset, BitConverter.GetBytes(value)))
            {
                Console.Error.WriteLine($"    write to 0x{unit + (ulong)offset:X} failed.");
                return 2;
            }

            var now = TryRead(unit + (ulong)offset, 4, out var after) ? BitConverter.ToInt32(after, 0) : -1;
            Console.WriteLine($"    {flag[2..],-5} {was} -> {now}");
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("  add --yes to apply.");
        }

        return 0;
    }

    private static int ListUnits(ulong moduleEnd)
    {
        var vtable = _base + BoardGameUnitVtableRva;
        Console.WriteLine($"\n  BoardGameUnit::vftable 0x{vtable:X}, scanning (about 80 s)");

        var hits = new List<ulong>();
        FindPtr(vtable, moduleEnd, hits);

        var units = hits.Where(h =>
        {
            if (!TryRead(h, 0x88, out var b))
            {
                return false;
            }

            for (var off = 0x50; off <= 0x60; off += 4)
            {
                var v = BitConverter.ToInt32(b, off);
                if (v is < 0 or > 64)
                {
                    return false;
                }
            }
            return true;
        }).ToList();

        Console.WriteLine($"\n  {units.Count} BoardGameUnit resource(s) (of {hits.Count} raw hits):");
        foreach (var u in units)
        {
            PrintUnitStats("  ", u);
        }

        return 0;
    }

    private const ulong BoardGameDraftVtableRva = 0x190E400;

    private static bool IsDraft(ulong p)
    {
        return Sane(p) && TryRead(p, 8, out var vt) && BitConverter.ToUInt64(vt) == _base + BoardGameDraftVtableRva;
    }

    private static string ResourceUuid(ulong r)
    {
        return TryRead(r + 0x08, 16, out var u) ? Convert.ToHexString(u) : "";
    }

    private static string NormaliseUuid(string s)
    {
        return new(s.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private static List<ulong> FindBoardGames(ulong moduleEnd)
    {
        return ScanForAll(new[] { _base + BoardGameVtableRva }, moduleEnd)[_base + BoardGameVtableRva];
    }

    private static (int Seat, ulong Player, ulong Draft) SeatOf(ulong boardGame, bool wantAi)
    {
        for (var seat = 0; seat < 2; seat++)
        {
            var p = ReadPtr(boardGame + 0x20 + (ulong)(seat * 0x10));
            if (!Sane(p))
            {
                continue;
            }

            var vt = ReadPtr(p);
            var isAi = vt == _base + AiPlayerVtableRva;
            var isHuman = vt == _base + HumanPlayerVtableRva;
            if (!isAi && !isHuman)
            {
                continue;
            }

            if (isAi == wantAi)
            {
                return (seat, p, ReadPtr(boardGame + 0x28 + (ulong)(seat * 0x10)));
            }
        }
        return (-1, 0, 0);
    }

    private static int ListBoardGames(ulong moduleEnd)
    {
        Console.WriteLine("\n  looking for BoardGame resources (region-filtered scan)");
        var games = FindBoardGames(moduleEnd);
        if (games.Count == 0)
        {
            Console.WriteLine("  none, no challenge is loaded. Stand in a Machine Strike menu.");
            return 2;
        }

        foreach (var g in games)
        {
            Console.WriteLine($"\n  BoardGame @0x{g:X}  uuid {ResourceUuid(g)}");
            foreach (var wantAi in new[] { false, true })
            {
                var (seat, player, draft) = SeatOf(g, wantAi);
                if (seat < 0)
                {
                    Console.WriteLine($"    {(wantAi ? "ai" : "human"),-5}, seat not found");
                    continue;
                }
                var (uc, ud) = (0, 0UL);
                if (IsDraft(draft))
                {
                    var setup = ReadPtr(draft + 0x28);
                    if (Sane(setup))
                    {
                        (uc, ud) = (TryReadU32(setup, out var c) ? (int)c : 0, ReadPtr(setup + 0x08));
                    }
                }
                Console.WriteLine($"    {(wantAi ? "ai" : "human"),-5} seat {seat}  player 0x{player:X}  draft 0x{draft:X}  units {uc}");
                for (var i = 0; i < uc && i < 8 && Sane(ud); i++)
                {
                    PrintUnitStats($"      [{i}]", ReadPtr(ud + (ulong)(i * 8)));
                }
            }
        }
        return 0;
    }

    private static List<ulong> FindDrafts(bool verbose)
    {
        var found = new List<ulong>();

        var chain = Walk(report: false);
        if (Sane(chain.Inst))
        {
            for (var seat = 0; seat < 2; seat++)
            {
                var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
                if (!Sane(player))
                {
                    continue;
                }

                var d = ReadPtr(player + 0x28);
                if (IsDraft(d) && !found.Contains(d))
                {
                    found.Add(d);
                }
            }
        }

        if (found.Count > 0)
        {
            if (verbose)
            {
                Console.WriteLine($"  found {found.Count} draft(s) via the players");
            }

            return found;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var needle = _base + BoardGameDraftVtableRva;
        var buf = new byte[0x100000];
        ulong address = 0x10000, scanned = 0;

        while (address < 0x7FFFFFFFFFFF)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            var readable = info.State == 0x1000 &&
                           (info.Protect & 0x100) == 0 &&
                           (info.Protect & 0xFF) is not (0 or 0x01);

            if (readable && info.RegionSize <= 0x4000000)
            {
                for (ulong off = 0; off < info.RegionSize; off += (ulong)buf.Length)
                {
                    var want = (int)Math.Min((ulong)buf.Length, info.RegionSize - off);
                    if (!ReadProcessMemory(_handle, (nint)(info.BaseAddress + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    scanned += (ulong)(int)got;
                    for (var i = 0; i + 8 <= (int)got; i += 8)
                    {
                        if (BitConverter.ToUInt64(buf, i) == needle)
                        {
                            found.Add(info.BaseAddress + off + (ulong)i);
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

        if (verbose)
        {
            Console.WriteLine($"  scanned {scanned / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms " +
                              $"-- {found.Count} draft(s)");
        }

        return found;
    }

    private static void ReportDraft(ulong draft)
    {
        Console.WriteLine($"\n  BoardGameDraft @0x{draft:X}");
        Console.WriteLine($"    +0x30 PlaceInstantly {(TryReadByte(draft + 0x30, out var pi) ? pi.ToString() : "?")}" +
                          "   (0 = Auto placer walks the placements, non-zero = Instant)");

        if (!TryReadU32(draft + 0x20, out var setups))
        {
            return;
        }

        var data = ReadPtr(draft + 0x28);
        Console.WriteLine($"    DraftSetups {setups} @0x{data:X}");
        if (!Sane(data))
        {
            return;
        }

        for (var s = 0; s < (int)setups && s < 4; s++)
        {
            var setup = data + (ulong)(s * 0x50);
            var uCount = TryReadU32(setup + 0x00, out var uc) ? (int)uc : 0;
            var uData = ReadPtr(setup + 0x08);
            var pCount = TryReadU32(setup + 0x10, out var pc) ? (int)pc : 0;
            var pData = ReadPtr(setup + 0x18);

            Console.WriteLine($"    [{s}] @0x{setup:X}  units {uCount} @0x{uData:X}   placements {pCount} @0x{pData:X}");
            for (var i = 0; i < uCount && i < 8 && Sane(uData); i++)
            {
                PrintUnitStats($"      unit[{i}]", ReadPtr(uData + (ulong)(i * 8)));
            }

            for (var i = 0; i < pCount && i < 8 && Sane(pData); i++)
            {
                if (TryRead(pData + (ulong)(i * PlacementStride), PlacementStride, out var rec))
                {
                    Console.WriteLine($"      place[{i}]  x {BitConverter.ToInt32(rec, 0)}  " +
                                      $"y {BitConverter.ToInt32(rec, 4)}  dir {rec[8]}");
                }
            }
        }
    }

    private static int FindDraft()
    {
        Console.WriteLine("\n  looking for BoardGameDraft resources");
        var drafts = FindDrafts(verbose: true);
        if (drafts.Count == 0)
        {
            Console.WriteLine("  none, no challenge is loaded.");
            return 2;
        }
        foreach (var d in drafts)
        {
            ReportDraft(d);
        }

        return 0;
    }

    private static int SetActed(string[] args, int at)
    {
        if (at + 1 >= args.Length)
        {
            Console.Error.WriteLine("--set-acted <x> <y> [--burst] [--yes]   (game coordinates)");
            return 1;
        }

        var x = (int)ParseAddr(args, at);
        var y = (int)ParseAddr(args, at + 1);
        if (!int.TryParse(args[at], out x) || !int.TryParse(args[at + 1], out y))
        {
            return 1;
        }

        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live.");
            return 2;
        }

        var count = (int)ReadU32(chain.Logic + 0x38);
        var array = ReadPtr(chain.Logic + 0x40);
        ulong unit = 0;
        for (var i = 0; i < count && i < 64; i++)
        {
            var u = ReadPtr(array + (ulong)(i * 8));
            if (!Sane(u) || !TryReadByte(u + 0x38, out var packed))
            {
                continue;
            }

            if (Nibble(packed) == x && Nibble(packed >> 4) == y)
            {
                unit = u;
                break;
            }
        }

        if (unit == 0)
        {
            Console.Error.WriteLine($"  no piece on ({x},{y}).");
            return 2;
        }
        if (!TryReadByte(unit + 0x3B, out var b))
        {
            Console.Error.WriteLine("  read failed.");
            return 2;
        }

        var burst = args.Contains("--burst");
        var after = burst
            ? (byte)((((b + 0x10) ^ b) & 0x30 ^ b) | 0x40)
            : (byte)(((((b + 4) ^ b) & 0x0c) ^ b) & 0xbf);

        Console.WriteLine($"\n  piece ({x},{y}) @0x{unit:X}");
        Console.WriteLine($"    +0x3B  0x{b:X2} -> 0x{after:X2}   " +
                          $"(facing {b & 3}, action count {(b >> 2) & 3} -> {(after >> 2) & 3})");

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("  dry run, pass --yes to write.");
            return 0;
        }

        if (!Write(unit + 0x3B, [after]))
        {
            return 1;
        }

        if (TryReadByte(unit + 0x68, out var f))
        {
            Write(unit + 0x68, [(byte)(burst ? f & 0xFE : f | 1)]);
        }

        Console.WriteLine("    written.");
        return 0;
    }

    private static bool UnitsStillValid(List<ulong> units, ulong vtable)
    {
        foreach (var u in units)
        {
            if (TryRead(u, 8, out var vt) && BitConverter.ToUInt64(vt) == vtable)
            {
                continue;
            }

            Console.Error.WriteLine($"  REJECT: 0x{u:X} no longer carries BoardGameUnit::vftable, " +
                                    "the resource was freed, most likely by a match reload. " +
                                    "Re-run --survey against the match you are writing to.");
            return false;
        }
        return true;
    }

    private static int SetUnits(string[] args, int at, ulong moduleEnd)
    {
        var positional = TakeValues(args, at);

        var seats = new List<(bool WantAi, List<string> Specs)>();
        var humanAt = IndexOfArg(args, "--human");
        var aiAt = IndexOfArg(args, "--ai");
        if (humanAt >= 0)
        {
            seats.Add((false, TakeValues(args, humanAt + 1)));
        }

        if (aiAt >= 0)
        {
            seats.Add((true, TakeValues(args, aiAt + 1)));
        }

        var uuidAt = IndexOfArg(args, "--board-game");
        var gameUuid = uuidAt >= 0 && uuidAt + 1 < args.Length ? NormaliseUuid(args[uuidAt + 1]) : "";

        if (seats.Count == 0 && positional.Count > 0 && gameUuid.Length > 0)
        {
            var seatArg = IndexOfArg(args, "--seat");
            var seatName = seatArg >= 0 && seatArg + 1 < args.Length ? args[seatArg + 1].ToLowerInvariant() : "ai";
            if (seatName is not ("ai" or "human"))
            {
                Console.Error.WriteLine($"  --seat must be 'ai' or 'human', not '{seatName}'.");
                return 1;
            }
            seats.Add((seatName == "ai", positional));
        }

        if (seats.Count > 0)
        {
            if (gameUuid.Length != 32)
            {
                Console.Error.WriteLine("  --human/--ai need --board-game <uuid>, at the menu the draft is only");
                Console.Error.WriteLine("  reachable from the BoardGame resource. --list-board-games prints them.");
                return 1;
            }
            foreach (var (wantAi, specs) in seats)
            {
                if (specs.Count == 0)
                {
                    Console.Error.WriteLine($"  --{(wantAi ? "ai" : "human")} was given no machines.");
                    return 1;
                }
            }

            return SetUnitsFromMenu(args, seats, gameUuid, moduleEnd);
        }

        if (positional.Count == 0)
        {
            Console.Error.WriteLine("--set-units --board-game <uuid> --human <machine> ... --ai <machine> ... [--force] [--yes]");
            Console.Error.WriteLine("  --set-units <machine> ... --board-game <uuid> [--seat ai|human]   one seat");
            Console.Error.WriteLine("  --set-units <unitAddr> ... [--player N] [--secs N]                waits for a match (too late for setup)");
            Console.Error.WriteLine("  --allocate            size the seat to the army given, instead of refusing any count");
            Console.Error.WriteLine("                        but its own. A seat with room is resized in place; one without");
            Console.Error.WriteLine("                        (a free-draft human seat) is granted a setup of ours.");
            Console.Error.WriteLine("  --restore-when-live   with --allocate, wait for the match to spawn and hand the granted");
            Console.Error.WriteLine("                        seats back. Warning: WITHOUT this, leaving the challenge list");
            Console.Error.WriteLine("                        crashes the game, which frees a granted setup it never allocated.");
            Console.Error.WriteLine("  a <machine> is a BoardGameUnit uuid (32 hex, what the lobby sends) or a --survey address.");
            Console.Error.WriteLine("  the challenge uuid comes from --list-board-games.");
            Console.Error.WriteLine("  --scan-threads N   how many threads the scan reads with (default: half the cores,");
            Console.Error.WriteLine("                     capped at 8). 1 is the old single-threaded pass, about 7x slower.");
            return 1;
        }

        var want = new List<ulong>();
        foreach (var s in positional)
        {
            var spec = ParseUnitSpec(s);
            if (spec.Uuid.Length == 32)
            {
                Console.Error.WriteLine($"  '{s}' is a uuid, and resolving one needs the scan that only the");
                Console.Error.WriteLine("  --board-game route runs. Pass --board-game <uuid>, or give an address.");
                return 1;
            }
            if (spec.Address == 0)
            {
                Console.Error.WriteLine($"  '{s}' is neither a unit address nor a 32-hex uuid.");
                return 1;
            }
            want.Add(spec.Address);
        }

        var vtable = _base + BoardGameUnitVtableRva;
        if (!UnitsStillValid(want, vtable))
        {
            return 1;
        }

        var secsAt = IndexOfArg(args, "--secs");
        var secs = secsAt >= 0 && secsAt + 1 < args.Length && int.TryParse(args[secsAt + 1], out var sv) ? sv : 300;
        var playerArg = SeatArg(args);
        if (playerArg is -1)
        {
            Console.Error.WriteLine("REFUSED: --player must be 0 or 1.");
            return 3;
        }

        var forcedSeat = playerArg ?? -1;
        var force = args.Contains("--force");
        var yes = args.Contains("--yes");

        Console.WriteLine($"\n  waiting for a match (up to {secs}s). Units to field:");
        foreach (var u in want)
        {
            PrintUnitStats("  ", u);
        }

        var deadline = DateTime.UtcNow.AddSeconds(secs);
        ulong armed = 0;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);

            var chain = Walk(report: false);
            if (!Sane(chain.Inst))
            {
                continue;
            }

            var seat = forcedSeat >= 0 ? forcedSeat : FindAiSeat(chain.Inst);
            if (seat < 0)
            {
                continue;
            }

            var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
            if (!Sane(player))
            {
                continue;
            }

            var draft = ReadPtr(player + 0x30);
            if (!Sane(draft))
            {
                continue;
            }

            if (!TryReadU32(draft + 0x00, out var count) || count == 0)
            {
                continue;
            }

            var data = ReadPtr(draft + 0x08);
            if (!Sane(data))
            {
                continue;
            }

            var already = true;
            for (var i = 0; i < want.Count && already; i++)
            {
                if (ReadPtr(data + (ulong)(i * 8)) != want[i])
                {
                    already = false;
                }
            }

            if (data == armed && already)
            {
                continue;
            }

            Console.WriteLine($"\n  draft found. seat {seat}  player 0x{player:X}  draft 0x{draft:X}");
            Console.WriteLine($"  logic {(chain.Logic == 0 ? "not yet live (good, armed before setup)" : "already live")}" +
                              $", units still to place: {(TryReadU32(player + 0x40, out var r) ? r.ToString() : "?")}");
            Console.WriteLine($"  units: {count} @0x{data:X}");
            for (var i = 0; i < (int)count; i++)
            {
                PrintUnitStats($"existing[{i}]", ReadPtr(data + (ulong)(i * 8)));
            }

            if (want.Count != (int)count && !force)
            {
                Console.Error.WriteLine($"  REJECT: {want.Count} unit(s) given but the array holds {count}. " +
                                        "Growing it needs the game's allocator, and the Placements data sits " +
                                        "immediately after it. Pass --force to write only the ones given.");
                return 1;
            }

            if (!yes)
            {
                Console.WriteLine("  dry run, pass --yes to write.");
                return 0;
            }

            if (!UnitsStillValid(want, vtable))
            {
                return 1;
            }

            for (var i = 0; i < want.Count && i < (int)count; i++)
            {
                if (!Write(data + (ulong)(i * 8), BitConverter.GetBytes(want[i])))
                {
                    return 1;
                }
            }

            for (var i = 0; i < (int)count; i++)
            {
                PrintUnitStats($"wrote[{i}]   ", ReadPtr(data + (ulong)(i * 8)));
            }

            armed = data;
            Console.WriteLine("  READY, the army is read at match setup, so this must be armed before the match loads.");
        }

        Console.WriteLine("\n  done.");
        return armed == 0 ? 2 : 0;
    }

    private static int SetUnitsFromMenu(string[] args, List<(bool WantAi, List<string> Specs)> seats,
                                        string uuid, ulong moduleEnd)
    {
        var unitVtable = _base + BoardGameUnitVtableRva;
        var gameVtable = _base + BoardGameVtableRva;
        var force = args.Contains("--force");
        var allocate = args.Contains("--allocate");

        if (allocate && Walk(report: false).Logic != 0)
        {
            Console.Error.WriteLine("  REFUSED: a match is live (or its victory screen is still up), and a page " +
                                    "granted now would be torn down with it. Leave to the challenge list first.");
            return 4;
        }

        Console.WriteLine($"\n  looking for BoardGame {uuid}, seats: " +
                          $"{string.Join(" and ", seats.Select(s => s.WantAi ? "ai" : "human"))}");

        var hits = ScanForAll(new[] { gameVtable, unitVtable }, moduleEnd);

        var game = hits[gameVtable].FirstOrDefault(g => ResourceUuid(g) == uuid);
        if (game == 0)
        {
            Console.Error.WriteLine($"  no loaded BoardGame carries that uuid ({hits[gameVtable].Count} found). " +
                                    "Are you in the right menu, on the agreed challenge?");
            return 2;
        }

        var byUuid = new Dictionary<string, ulong>();
        var loaded = 0;
        foreach (var u in hits[unitVtable])
        {
            if (!TryRead(u, 0x88, out var b))
            {
                continue;
            }

            var plausible = true;
            for (var off = 0x50; off <= 0x60 && plausible; off += 4)
            {
                if (BitConverter.ToInt32(b, off) is < 0 or > 64)
                {
                    plausible = false;
                }
            }

            if (!plausible)
            {
                continue;
            }

            loaded++;
            var id = Convert.ToHexString(b, 0x08, 16);
            if (!byUuid.TryAdd(id, u))
            {
                Console.WriteLine($"    note: uuid {id} is loaded twice (0x{byUuid[id]:X}, 0x{u:X}); using the first.");
            }
        }
        Console.WriteLine($"  {loaded} BoardGameUnit resources loaded, {byUuid.Count} distinct uuids");

        var plans = new List<(string Name, int Seat, ulong Data, uint Count, List<ulong> Want)>();
        var grants = new List<(string Name, int Seat, ulong Draft, ulong Setup, uint Room,
                               List<ulong> Want, bool WantAi)>();

        foreach (var (wantAi, specs) in seats)
        {
            var name = wantAi ? "ai" : "human";
            var want = new List<ulong>();

            foreach (var s in specs)
            {
                var spec = ParseUnitSpec(s);
                if (spec.Uuid.Length == 32)
                {
                    if (!byUuid.TryGetValue(spec.Uuid, out var resolved))
                    {
                        Console.Error.WriteLine($"  REJECT: no loaded BoardGameUnit carries uuid {spec.Uuid} " +
                                                $"({byUuid.Count} loaded). The peer named a machine this PC has " +
                                                "not loaded, check both are in the Machine Strike menu.");
                        return 2;
                    }
                    want.Add(resolved);
                }
                else if (spec.Address == 0)
                {
                    Console.Error.WriteLine($"  REJECT: '{s}' is neither a unit address nor a 32-hex uuid.");
                    return 1;
                }
                else
                {
                    want.Add(spec.Address);
                }
            }

            if (!UnitsStillValid(want, unitVtable))
            {
                return 1;
            }

            var (seat, player, draft) = SeatOf(game, wantAi);
            if (seat < 0 || !IsDraft(draft))
            {
                Console.Error.WriteLine($"  BoardGame @0x{game:X} has no readable {name} seat. " +
                                        "Refusing rather than guessing which draft is which.");
                return 2;
            }

            if (allocate)
            {
                var have = ReadPtr(draft + DraftSetupsData);
                uint room = 0;
                if (Sane(have) && !GrantedSetups(have) &&
                    TryReadU32(draft + DraftSetupsCount, out var setups) && setups > 0 &&
                    TryReadU32(have + 0x04, out var cap))
                {
                    room = cap;
                }

                var fits = room >= want.Count;

                grants.Add((name, seat, draft, fits ? have : 0, room, want, wantAi));

                Console.WriteLine();
                Console.WriteLine($"  BoardGame @0x{game:X}  {name} is seat {seat}  " +
                                  $"player 0x{player:X}  draft 0x{draft:X}");
                Console.WriteLine(fits
                    ? $"  resizing the seat's own setup in place, {room} slot(s) to hold {want.Count}"
                    : $"  granting a {want.Count}-machine setup (the seat has room for {room})");
                foreach (var u in want)
                {
                    PrintUnitStats("  to field ", u);
                }

                continue;
            }

            var setup = ReadPtr(draft + DraftSetupsData);
            if (!Sane(setup))
            {
                Console.Error.WriteLine($"  {name}: DraftSetups unreadable.");
                return 2;
            }

            if (!TryReadU32(setup, out var count) || count == 0)
            {
                Console.Error.WriteLine($"  {name}: the Units array is empty, nothing to overwrite.");
                return 2;
            }

            var data = ReadPtr(setup + 0x08);
            if (!Sane(data))
            {
                Console.Error.WriteLine($"  {name}: Units data pointer unreadable.");
                return 2;
            }

            Console.WriteLine($"\n  BoardGame @0x{game:X}  {name} is seat {seat}  " +
                              $"player 0x{player:X}  draft 0x{draft:X}");
            Console.WriteLine($"  units: {count} @0x{data:X}");
            for (var i = 0; i < (int)count; i++)
            {
                PrintUnitStats($"existing[{i}]", ReadPtr(data + (ulong)(i * 8)));
            }

            foreach (var u in want)
            {
                PrintUnitStats("  to field ", u);
            }

            if (want.Count != (int)count && !force)
            {
                Console.Error.WriteLine($"  REJECT: {want.Count} unit(s) given for the {name} seat but the array " +
                                        $"holds {count}. Growing it needs the game's allocator, and the Placements " +
                                        "data sits immediately after it. Pass --force to write only the ones given.");
                return 1;
            }

            plans.Add((name, seat, data, count, want));
        }

        if (grants.Select(g => g.Draft).Distinct().Count() != grants.Count)
        {
            Console.Error.WriteLine("  REJECT: both seats resolved to the same draft. Refusing to write.");
            return 2;
        }

        if (plans.Select(p => p.Data).Distinct().Count() != plans.Count)
        {
            Console.Error.WriteLine("  REJECT: both seats resolved to the same Units array. Refusing to write.");
            return 2;
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, pass --yes to write.");
            return 0;
        }

        foreach (var p in plans)
        {
            if (!UnitsStillValid(p.Want, unitVtable))
            {
                return 1;
            }
        }

        foreach (var g in grants)
        {
            if (!UnitsStillValid(g.Want, unitVtable))
            {
                return 1;
            }
        }

        using var stop = args.Contains("--restore-when-live") ? OpenDraftStopEvent() : null;

        var granted = new List<(string Name, ulong Draft, byte[] WasHeader, byte WasCustom, ulong Page)>();
        foreach (var g in grants)
        {
            if (g.Setup != 0)
            {
                if (!ResizeSeatInPlace(g.Name, g.Setup, g.Want, g.WantAi))
                {
                    return 1;
                }

                continue;
            }

            var undo = GrantDraftSetup(g.Name, g.Draft, g.Want, g.WantAi);
            if (undo is null)
            {
                return 1;
            }

            granted.Add(undo.Value);
        }

        if (granted.Count > 0 && stop is not null)
        {
            return RestoreWhenLive(granted, args, stop);
        }

        foreach (var p in plans)
        {
            for (var i = 0; i < p.Want.Count && i < (int)p.Count; i++)
            {
                if (!Write(p.Data + (ulong)(i * 8), BitConverter.GetBytes(p.Want[i])))
                {
                    return 1;
                }
            }

            Console.WriteLine($"\n  {p.Name} seat {p.Seat}:");
            for (var i = 0; i < (int)p.Count; i++)
            {
                PrintUnitStats($"wrote[{i}]   ", ReadPtr(p.Data + (ulong)(i * 8)));
            }
        }

        Console.WriteLine("\n  READY, start the match. The army is read at setup, so do not reload the menu.");
        return 0;
    }

    internal const string DraftStopEvent = @"Local\Strikers-draft-guard-stop";

    private static EventWaitHandle OpenDraftStopEvent()
    {
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, DraftStopEvent, out var createdNew);
        if (!createdNew)
        {
            stop.Reset();
        }

        return stop;
    }

    private static int StopDraftGuard()
    {
        if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(DraftStopEvent, out var stop))
        {
            using (stop)
            {
                stop.Set();
            }

            Console.WriteLine("  stop signalled to the draft guard");
            return 0;
        }

        Console.WriteLine("  no draft guard is running");
        return 0;
    }

    private static bool OwnPage(ulong ptr)
    {
        return (ptr & 0xFFF) == 0;
    }

    private const ulong GrantMagic = 0x544E52474B525453;
    private const ulong GrantMagicOffset = 0x20;
    private const int GrantSetupsSize = 0x30;

    private static bool GrantedSetups(ulong data)
    {
        if (data == 0)
        {
            return false;
        }

        if (OwnPage(data))
        {
            return true;
        }

        return TryReadU64(data + GrantMagicOffset, out var magic) && magic == GrantMagic;
    }

    private const int MaxGrantArmy = 24;

    private static (string Name, ulong Draft, byte[] WasHeader, byte WasCustom, ulong Page)?
        GrantDraftSetup(string name, ulong draft, List<ulong> want, bool wantAi)
    {
        if (want.Count < 1 || want.Count > MaxGrantArmy)
        {
            Console.Error.WriteLine($"  {name}: refusing to grant {want.Count} machine(s); one page " +
                                    $"holds at most {MaxGrantArmy}.");
            return null;
        }

        var places = wantAi ? want.Count : 0;
        var records = new byte[places * PlacementStride];
        var units = new byte[want.Count * 8];
        for (var i = 0; i < want.Count; i++)
        {
            BitConverter.GetBytes(want[i]).CopyTo(units, i * 8);
        }

        if (!TryGameAllocate(GrantSetupsSize, out var page, out var whySetups))
        {
            Console.Error.WriteLine($"  {name}: the game would not allocate the seat's setup: {whySetups}.");
            return null;
        }

        if (!TryGameAllocate((uint)units.Length, out var unitsAt, out var whyUnits))
        {
            Console.Error.WriteLine($"  {name}: the game would not allocate the seat's units: {whyUnits}.");
            return null;
        }

        var placesAt = 0UL;
        if (records.Length > 0 && !TryGameAllocate((uint)records.Length, out placesAt, out var whyPlaces))
        {
            Console.Error.WriteLine($"  {name}: the game would not allocate the seat's placements: " +
                                    $"{whyPlaces}.");
            return null;
        }

        if (!TryRead(draft + DraftSetupsCount, 16, out var wasHeader) ||
            !TryReadByte(draft + AllowCustomDraft, out var wasCustom))
        {
            Console.Error.WriteLine($"  {name}: cannot read the seat's draft fields, refusing to write.");
            return null;
        }

        var priorData = BitConverter.ToUInt64(wasHeader, 8);
        if (GrantedSetups(priorData))
        {
            wasHeader = new byte[16];
            wasCustom = 1;
        }

        var setup = new byte[GrantSetupsSize];
        BitConverter.GetBytes(want.Count).CopyTo(setup, 0x00);
        BitConverter.GetBytes(want.Count).CopyTo(setup, 0x04);
        BitConverter.GetBytes(unitsAt).CopyTo(setup, 0x08);
        BitConverter.GetBytes(places).CopyTo(setup, 0x10);
        BitConverter.GetBytes(places).CopyTo(setup, 0x14);
        BitConverter.GetBytes(placesAt).CopyTo(setup, 0x18);
        BitConverter.GetBytes(GrantMagic).CopyTo(setup, (int)GrantMagicOffset);

        if (!Write(page, setup) || !Write(unitsAt, units) ||
            (records.Length > 0 && !Write(placesAt, records)))
        {
            return null;
        }

        var header = new byte[16];
        BitConverter.GetBytes(1).CopyTo(header, 0);
        BitConverter.GetBytes(1).CopyTo(header, 4);
        BitConverter.GetBytes(page).CopyTo(header, 8);
        if (!Write(draft + DraftSetupsCount, header))
        {
            return null;
        }

        if (!Write(draft + AllowCustomDraft, [0]))
        {
            return null;
        }

        Console.WriteLine();
        Console.WriteLine($"  {name} seat: {want.Count} machine(s) and {places} placement record(s), " +
                          $"draft 0x{draft:X} now points at it, custom draft off");
        Console.WriteLine($"  the GAME allocated all three: setup 0x{page:X}, units 0x{unitsAt:X}, " +
                          $"placements 0x{placesAt:X}; its own teardown frees them, so no hand-back is " +
                          "needed for safety");
        Console.WriteLine($"  undo {name}: --poke 0x{draft + DraftSetupsCount:X} " +
                          $"{Convert.ToHexString(wasHeader)} --yes  and  " +
                          $"--poke 0x{draft + AllowCustomDraft:X} {wasCustom:X2} --yes");
        for (var i = 0; i < want.Count; i++)
        {
            PrintUnitStats($"    granted[{i}]", ReadPtr(unitsAt + (ulong)(i * 8)));
        }

        return (name, draft, wasHeader, wasCustom, page);
    }

    private static bool ResizeSeatInPlace(string name, ulong setup, List<ulong> want, bool wantAi)
    {
        var units = ReadPtr(setup + 0x08);
        if (!Sane(units))
        {
            Console.Error.WriteLine($"  {name}: Units data pointer unreadable.");
            return false;
        }

        for (var i = 0; i < want.Count; i++)
        {
            if (!Write(units + (ulong)(i * 8), BitConverter.GetBytes(want[i])))
            {
                return false;
            }
        }

        if (!Write(setup + 0x00, BitConverter.GetBytes(want.Count)))
        {
            return false;
        }

        var places = wantAi ? want.Count : 0;
        if (!Write(setup + 0x10, BitConverter.GetBytes(places)))
        {
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"  {name} seat: resized in place to {want.Count} machine(s) and " +
                          $"{places} placement record(s), setup 0x{setup:X}, nothing to undo");
        for (var i = 0; i < want.Count; i++)
        {
            PrintUnitStats($"    fielded[{i}]", ReadPtr(units + (ulong)(i * 8)));
        }

        return true;
    }

    private static int RestoreWhenLive(List<(string Name, ulong Draft, byte[] WasHeader, byte WasCustom, ulong Page)> granted,
                                       string[] args, EventWaitHandle stop)
    {
        var secsAt = IndexOfArg(args, "--secs");
        var secs = secsAt >= 0 && secsAt + 1 < args.Length && int.TryParse(args[secsAt + 1], out var sv) ? sv : 600;
        var deadline = DateTime.UtcNow.AddSeconds(secs);

        Console.WriteLine();
        Console.WriteLine($"  holding {granted.Count} granted seat(s) (up to {secs} s)");

        var placed = false;
        var seenPlacing = false;
        var gone = false;
        var stopped = false;
        var goneReads = 0;
        var placedReads = 0;
        while (!_parentGone && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);

            if (stop.WaitOne(0))
            {
                stopped = true;
                break;
            }

            var chain = Walk(report: false);
            var instSane = Sane(chain.Inst);
            var placingLive = instSane && FindPlacingPhase(chain.Inst) != 0;
            seenPlacing |= placingLive;
            var step = GuardStep(instSane, placingLive, seenPlacing, goneReads, placedReads);
            goneReads = step.GoneReads;
            placedReads = step.PlacedReads;
            if (step.Verdict == GuardVerdict.Gone)
            {
                gone = true;
                break;
            }

            if (step.Verdict == GuardVerdict.Placed)
            {
                placed = true;
                break;
            }
        }

        var why = placed ? "every machine is placed"
                : gone ? "the match ended or was quit"
                : stopped ? "the launcher stopped the match"
                : _ctrlPressed ? "asked to stop"
                : _parentGone ? "the parent exited"
                : "timed out";
        Console.WriteLine($"  {why}; handing the seats back");

        var ok = true;
        foreach (var g in granted)
        {
            if (ReadPtr(g.Draft + DraftSetupsData) != g.Page)
            {
                Console.WriteLine($"  {g.Name}: draft 0x{g.Draft:X} no longer points at the granted page, so the " +
                                  "game rebuilt or freed it; nothing to hand back.");
                continue;
            }

            if (!Write(g.Draft + DraftSetupsCount, g.WasHeader) ||
                !Write(g.Draft + AllowCustomDraft, [g.WasCustom]))
            {
                Console.Error.WriteLine($"  {g.Name}: COULD NOT hand the seat back. Leaving the challenge " +
                                        "list will crash the game; the poke to undo it is printed above.");
                ok = false;
                continue;
            }

            Console.WriteLine($"  {g.Name}: handed back, draft 0x{g.Draft:X} points at its own data again");
        }

        return ok ? 0 : 1;
    }

    internal enum GuardVerdict
    {
        Waiting,
        Placed,
        Gone,
    }

    internal const int GuardEdgeReads = 2;

    internal static (GuardVerdict Verdict, int GoneReads, int PlacedReads) GuardStep(
        bool instSane, bool placingLive, bool seenPlacing, int goneReads, int placedReads)
    {
        if (placingLive || !seenPlacing)
        {
            return (GuardVerdict.Waiting, 0, 0);
        }

        if (!instSane)
        {
            var gone = goneReads + 1;
            return (gone >= GuardEdgeReads ? GuardVerdict.Gone : GuardVerdict.Waiting, gone, 0);
        }

        var placed = placedReads + 1;
        return (placed >= GuardEdgeReads ? GuardVerdict.Placed : GuardVerdict.Waiting, 0, placed);
    }

    private readonly record struct UnitSpec(string Uuid, ulong Address);

    private static UnitSpec ParseUnitSpec(string raw)
    {
        var hex = NormaliseUuid(raw);
        return hex.Length == 32 ? new UnitSpec(hex, 0) : new UnitSpec("", ParseAddr(raw));
    }

    private static List<string> TakeValues(string[] args, int at)
    {
        var vals = new List<string>();
        for (var i = at; i < args.Length && !args[i].StartsWith("--"); i++)
        {
            vals.Add(args[i]);
        }

        return vals;
    }

}
