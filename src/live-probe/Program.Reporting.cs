using System.Runtime.InteropServices;
using System.Text;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private static bool Probe(int samples, bool allTiles)
    {
        ulong first = 0;
        var stable = true;

        for (var i = 0; i < samples; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(500);
            }

            var chain = Walk(report: i == 0);
            if (samples > 1)
            {
                Console.WriteLine($"sample {i + 1}/{samples}  inst 0x{chain.Inst:X}  logic 0x{chain.Logic:X}");
            }

            if (i == 0)
            {
                first = chain.Inst;
            }
            else if (chain.Inst != first)
            {
                stable = false;
            }

            if (i < samples - 1)
            {
                continue;
            }

            if (chain.Logic == 0)
            {
                Console.WriteLine("\nlogic pointer is null, no match is live. Start a Strike match and re-run.");
                return false;
            }

            ReportChallenge(chain.Challenge);
            ReportInstance(chain.Inst);
            ReportBoard(chain.Logic, allTiles, chain.Inst);
        }

        if (samples > 1)
        {
            Console.WriteLine(stable
                ? $"\ninstance pointer STABLE across {samples} samples"
                : $"\ninstance pointer CHANGED across {samples} samples, it is not a fixed singleton");
        }

        return true;
    }

    private static void ReportChallenge(ulong c)
    {
        if (!Sane(c))
        {
            return;
        }

        var vtable = ReadPtr(c);
        var inModule = vtable > _base && vtable - _base < 0x9400000;
        Console.WriteLine($"\nlink2 @0x{c:X}  (candidate BoardGameChallengeInstance, expected size 0xC8)");
        Console.WriteLine($"  [c]      0x{vtable:X}  {(inModule ? $"in module (+0x{vtable - _base:X}), looks like a vtable" : "not a module address")}");
        Console.WriteLine($"  [c+0x08] 0x{ReadPtr(c + 8):X}  (object the is-a check runs on)");
        DumpHex(c, 0xC8);
    }

    private static (ulong Challenge, ulong Inst, ulong Logic) Walk(bool report)
    {
        var links = new (string Label, ulong Offset)[]
        {
            ("global   [base+0x8983150]", GlobalRva),
            ("link1    [g+0x190]", 0x190),
            ("link2    [b+0x18]", 0x18),
            ("instance [c+0xA0]", 0xA0),
            ("logic    [inst+0x50]", 0x50),
        };

        var addr = _base;
        ulong challenge = 0;
        ulong inst = 0;

        foreach (var (label, offset) in links)
        {
            var slot = addr + offset;
            var value = ReadPtr(slot);

            if (report)
            {
                Console.WriteLine($"  {label,-28} @0x{slot:X} -> 0x{value:X}{(Sane(value) ? "" : "   <-- not a plausible pointer")}");
            }

            if (!Sane(value))
            {
                return (challenge, inst, 0);
            }

            if (label.StartsWith("link2"))
            {
                challenge = value;
            }

            if (label.StartsWith("instance"))
            {
                inst = value;
            }

            addr = value;
        }

        return (challenge, inst, addr);
    }

    private static void ReportInstance(ulong inst)
    {
        Console.WriteLine($"\nBoardGameInstance @0x{inst:X}");
        Console.WriteLine($"  +0x29 pause arg   {ReadByte(inst + 0x29)}");
        Console.WriteLine($"  +0x2A paused      {ReadByte(inst + 0x2A)}");

        for (var i = 0; i < 2; i++)
        {
            var p = ReadPtr(inst + 0x40 + (ulong)(i * 8));
            Console.WriteLine($"  player[{i}] @0x{p:X}{(Sane(p) ? "" : "   <-- implausible")}");
            if (!Sane(p))
            {
                continue;
            }

            var self = ReadPtr(p);
            Console.WriteLine($"      [p]-0x20 (move-record key) 0x{(self == 0 ? 0 : self - 0x20):X}");
            Console.WriteLine($"      required moves: {ReadU32(p + 0x68)} of {ReadU32(p + 0x6C)} @0x{ReadPtr(p + 0x70):X}");
        }
    }

    private static void ReportBoard(ulong logic, bool allTiles, ulong inst)
    {
        var player0 = ReadPtr(inst + 0x40);
        var player1 = ReadPtr(inst + 0x48);

        Console.WriteLine($"\nlogic/board @0x{logic:X}");

        var rows = ReadPtr(logic + 0x08);
        var height = (int)ReadU32(rows + 0x20);
        var rowArray = ReadPtr(rows + 0x28);
        var firstRow = ReadPtr(rowArray);
        var width = (int)ReadU32(firstRow + 0x20);

        Console.WriteLine($"  rows obj @0x{rows:X}  row array @0x{rowArray:X}  first row @0x{firstRow:X}");
        Console.WriteLine($"  WIDTH  {width}   (from [[rows+0x28]] + 0x20)");
        Console.WriteLine($"  HEIGHT {height}   (from rows+0x20)");

        var plausible = width is > 0 and <= 16 && height is > 0 and <= 16;
        Console.WriteLine(plausible
            ? "  dimensions plausible, compare against the visible board"
            : "  dimensions IMPLAUSIBLE, the layout is wrong, or this is not a board");

        var tiles = ReadPtr(logic + 0x20);
        Console.WriteLine($"\n  tile array @0x{tiles:X}  stride 0x48  index = x + width*y");

        if (plausible && Sane(tiles))
        {
            Console.WriteLine("  tile +0x40 (signed byte), row 0 at top, 0 Grassland, 1 Forest, 2 Hill:");
            var terrain = new int[height, width];
            for (var y = 0; y < height; y++)
            {
                var sb = new StringBuilder("    ");
                for (var x = 0; x < width; x++)
                {
                    terrain[y, x] = (sbyte)ReadByte(tiles + (ulong)((x + width * y) * 0x48) + 0x40);
                    sb.Append($"{terrain[y, x],3} ");
                }
                Console.WriteLine(sb.ToString());
            }

            var symmetric = true;
            for (var y = 0; y < height && symmetric; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (terrain[y, x] != terrain[height - 1 - y, width - 1 - x])
                    {
                        symmetric = false;
                        break;
                    }
                }
            }

            Console.WriteLine($"  180-degree symmetry: {(symmetric ? "yes, read looks correct" : "NO, suspect the offset or stride")}");

            if (allTiles)
            {
                for (var i = 0; i < width * height; i++)
                {
                    Console.WriteLine($"\n  tile[{i}] (x={i % width}, y={i / width})");
                    DumpHex(tiles + (ulong)(i * 0x48), 0x48);
                }
            }
            else
            {
                DumpHex(tiles, 0x48);
            }
        }

        var count = (int)ReadU32(logic + 0x38);
        var array = ReadPtr(logic + 0x40);
        Console.WriteLine($"\n  unit list: {count} entries @0x{array:X}");

        for (var i = 0; i < count && i < 64; i++)
        {
            var u = ReadPtr(array + (ulong)(i * 8));
            if (!Sane(u))
            {
                Console.WriteLine($"    [{i}] 0x{u:X}   <-- implausible");
                continue;
            }

            var packed = ReadByte(u + 0x38);
            var ownerPtr = ReadPtr(u);
            var seat = ownerPtr == player0 ? "0" : ownerPtr == player1 ? "1" : "?";
            Console.WriteLine($"    [{i}] @0x{u:X}  tile ({Nibble(packed)},{Nibble(packed >> 4)})  " +
                              $"player[{seat}] @0x{ownerPtr:X}  packed 0x{packed:X2}  " +
                              $"+0x3A 0x{ReadByte(u + 0x3A):X2}  +0x3B 0x{ReadByte(u + 0x3B):X2}");
        }

        if (count > 0 && Sane(ReadPtr(array)))
        {
            Console.WriteLine("\n  unit[0] bytes:");
            DumpHex(ReadPtr(array), 0x60);
        }
    }

    private static int AllocBytes(string[] args, int valueIndex)
    {
        if (valueIndex >= args.Length)
        {
            Console.Error.WriteLine("--alloc-bytes <hex bytes> [--yes]");
            return 1;
        }

        var hex = args[valueIndex].Replace("0x", "").Replace(" ", "");
        if (hex.Length % 2 != 0 || hex.Length == 0)
        {
            Console.Error.WriteLine("--alloc-bytes: byte string must have an even number of hex digits.");
            return 1;
        }

        var bytes = Convert.FromHexString(hex);
        Console.WriteLine($"\n  allocate a 4 KB read-write page and write {bytes.Length} bytes at its start");
        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing allocated. Add --yes to apply.");
            return 0;
        }

        var page = (ulong)VirtualAllocEx(_handle, 0, 0x1000, 0x1000 | 0x2000, 0x04);
        if (page == 0)
        {
            Console.Error.WriteLine($"  VirtualAllocEx failed ({Marshal.GetLastWin32Error()}).");
            return 1;
        }

        if (!Write(page, bytes))
        {
            return 1;
        }

        Console.WriteLine($"  page    0x{page:X}");
        Console.WriteLine($"  after   0x{page:X}  {Convert.ToHexString(Read(page, bytes.Length))}");
        return 0;
    }

    private static int Poke(ulong address, string[] args, int valueIndex)
    {
        if (address == 0 || valueIndex >= args.Length)
        {
            Console.Error.WriteLine("--poke <addr> <hex bytes, e.g. 01 or 0100000000>");
            return 1;
        }

        var hex = args[valueIndex].Replace("0x", "").Replace(" ", "");
        if (hex.Length % 2 != 0)
        {
            Console.Error.WriteLine("--poke: byte string must have an even number of hex digits.");
            return 1;
        }

        var bytes = Convert.FromHexString(hex);
        Console.WriteLine($"\n  before  0x{address:X}  {Convert.ToHexString(Read(address, bytes.Length))}");
        Console.WriteLine($"  write   0x{address:X}  {Convert.ToHexString(bytes)}");

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        if (!Write(address, bytes))
        {
            return 1;
        }

        Console.WriteLine($"  after   0x{address:X}  {Convert.ToHexString(Read(address, bytes.Length))}");
        return 0;
    }

    private static int InjectMove(string[] args, int firstArg)
    {
        if (firstArg + 5 >= args.Length)
        {
            Console.Error.WriteLine("--inject-move <player 0|1> <action> <x> <y> <p5> <p6> [--yes]");
            return 1;
        }

        var player = (int)ParseAddr(args, firstArg);
        var action = (byte)ParseAddr(args, firstArg + 1);
        var x = (int)ParseAddr(args, firstArg + 2);
        var y = (int)ParseAddr(args, firstArg + 3);
        var p5 = (byte)ParseAddr(args, firstArg + 4);
        var p6 = (byte)ParseAddr(args, firstArg + 5);

        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live.");
            return 2;
        }

        var p = ReadPtr(chain.Inst + 0x40 + (ulong)(player * 8));
        var count = ReadU32(p + MoveCount);
        var capacity = ReadU32(p + MoveCapacity);
        var data = ReadPtr(p + MoveData);

        Console.WriteLine($"\n  player[{player}] @0x{p:X}  required moves: {count} of {capacity} @0x{data:X}");

        if (data == 0 || count >= capacity)
        {
            Console.Error.WriteLine("  refusing: the array is full or unallocated, and growing it needs the " +
                                    "game's allocator. Wait for a state where capacity exceeds count.");
            return 3;
        }

        var record = data + count * (ulong)MoveStride;
        var buf = new byte[MoveStride];
        buf[0x00] = action;
        BitConverter.GetBytes(x).CopyTo(buf, 0x08);
        BitConverter.GetBytes(y).CopyTo(buf, 0x0C);
        buf[0x10] = p5;
        buf[0x11] = p6;

        Console.WriteLine($"  record  0x{record:X}  action 0x{action:X2}  ({x},{y})  p5 0x{p5:X2}  p6 0x{p6:X2}");
        Console.WriteLine($"  then    0x{p + MoveCount:X}  count {count} -> {count + 1}");

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        if (!Write(record, buf))
        {
            return 1;
        }

        if (!Write(p + MoveCount, BitConverter.GetBytes(count + 1)))
        {
            return 1;
        }

        Console.WriteLine($"\n  after   required moves: {ReadU32(p + MoveCount)} of {ReadU32(p + MoveCapacity)}");
        DumpHex(record, MoveStride);
        return 0;
    }

    private static int FindPtr(ulong needle, ulong moduleEnd)
    {
        return FindPtr(needle, moduleEnd, null);
    }

    private static int FindPtr(ulong needle, ulong moduleEnd, List<ulong>? hits)
    {
        if (hits is null)
        {
            Console.WriteLine($"\n  scanning for qword 0x{needle:X}");
        }

        var buf = new byte[0x100000];
        ulong address = 0x10000, scanned = 0;
        int inModule = 0, onHeap = 0;

        while (address < 0x7FFFFFFFFFFF)
        {
            if (VirtualQueryEx(_handle, (nint)address, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            var readable = info.State == 0x1000 &&
                           (info.Protect & 0x100) == 0 &&
                           (info.Protect & 0xFF) is not (0 or 0x01);

            if (readable && info.RegionSize <= 0x40000000)
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
                        if (BitConverter.ToUInt64(buf, i) != needle)
                        {
                            continue;
                        }

                        var at = info.BaseAddress + off + (ulong)i;
                        if (at >= _base && at < moduleEnd)
                        {
                            inModule++;
                            continue;
                        }

                        onHeap++;
                        if (hits is not null)
                        {
                            hits.Add(at);
                        }
                        else if (onHeap <= 40)
                        {
                            Console.WriteLine($"    heap  0x{at:X}");
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

        Console.WriteLine($"\n  scanned {scanned / (1024 * 1024)} MB, {onHeap} heap hit(s), {inModule} in-module hit(s)");
        if (hits is null && onHeap > 40)
        {
            Console.WriteLine("  (first 40 heap hits shown)");
        }

        return 0;
    }

    private const ulong BoardGameUnitVtableRva = 0x1911808;

    private const ulong BoardGameVtableRva = 0x190E780;
    private const ulong SettingsVtableRva = 0x190E8A0;
    private const ulong TileVtableRva = 0x190FC28;
    private const ulong AbilityVtableRva = 0x190FBD0;
    private const ulong BoardVtableRva = 0x190E3D8;
    private const ulong TileRowVtableRva = 0x190E468;
    private const ulong DraftPresetVtableRva = 0x190E848;

    private const ulong CollectionResourceVtableRva = 0x190E5F8;
    private const ulong CollectionSaveVtableRva = 0x190E230;
    private const ulong CollectionSaveVtable2Rva = 0x190E8C8;
    private const ulong AiPlayerVtableRva = 0x190E7A8;
    private const ulong HumanPlayerVtableRva = 0x1911A80;

    private const ulong UnitPlacingPhaseVtableRva = 0x190E638;
    private const ulong AutoPlacingCtrlVtableRva = 0x190E2F8;
    private const ulong AiPlacingCtrlVtableRva = 0x190E808;
    private const ulong HumanPlacingCtrlVtableRva = 0x190DCC0;

    private static string TileTypeName(int t)
    {
        return t switch
        {
            -3 => "Blight",
            -2 => "Void",
            -1 => "Water",
            0 => "Plains",
            1 => "Forest",
            2 => "Hills",
            3 => "Mountains",
            _ => $"?{t}"
        };
    }

    private static readonly string[] SkillNames =
    [
        "None", "Roam", "Stalk", "Scurry", "Climb", "Spread", "Shield", "Retaliate", "Burn",
        "Freeze", "Seed", "Unearth", "Blind", "Enpower", "Stun", "Spill", "Confuse"
    ];

    private static readonly string[] PatternNames = ["Strike", "Shot", "Dash", "Ram", "Dive", "Tow"];

    private static string SkillName(int s)
    {
        return s >= 0 && s < SkillNames.Length ? SkillNames[s] : $"?{s}";
    }

    private static string PatternName(int p)
    {
        return p >= 0 && p < PatternNames.Length ? PatternNames[p] : $"?{p}";
    }

    private static int _scanThreads;

    private static int ScanThreads()
    {
        if (_scanThreads > 0)
        {
            return _scanThreads;
        }

        return Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    }

    private static List<(ulong Base, ulong Size)> ScanRegions()
    {
        var regions = new List<(ulong Base, ulong Size)>();
        ulong address = 0x10000;

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
                regions.Add((info.BaseAddress, info.RegionSize));
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return regions;
    }

    private static Dictionary<ulong, List<ulong>> ScanForAll(IEnumerable<ulong> needles, ulong moduleEnd)
    {
        var wanted = new HashSet<ulong>(needles);
        var found = wanted.ToDictionary(n => n, _ => new List<ulong>());
        if (wanted.Count == 0)
        {
            return found;
        }

        var lo = wanted.Min();
        var hi = wanted.Max();
        var threads = ScanThreads();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var regions = ScanRegions();
        long scanned = 0;

        Parallel.ForEach(regions,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            () => new byte[0x100000],
            (region, _, buf) =>
            {
                var (regionBase, regionSize) = region;

                for (ulong off = 0; off < regionSize; off += (ulong)buf.Length)
                {
                    var want = (int)Math.Min((ulong)buf.Length, regionSize - off);
                    if (!ReadProcessMemory(_handle, (nint)(regionBase + off), buf, want, out var got) || got == 0)
                    {
                        continue;
                    }

                    Interlocked.Add(ref scanned, (int)got);

                    var words = MemoryMarshal.Cast<byte, ulong>(buf.AsSpan(0, (int)got & ~7));
                    for (var i = 0; i < words.Length; i++)
                    {
                        var v = words[i];
                        if (v < lo || v > hi)
                        {
                            continue;
                        }

                        if (!wanted.Contains(v))
                        {
                            continue;
                        }

                        var at = regionBase + off + (ulong)(i * 8);
                        if (at < _base || at >= moduleEnd)
                        {
                            lock (found)
                            {
                                found[v].Add(at);
                            }
                        }
                    }
                }

                return buf;
            },
            _ => { });

        foreach (var list in found.Values)
        {
            list.Sort();
        }

        Console.WriteLine($"  scanned {scanned / (1024 * 1024)} MB in {sw.ElapsedMilliseconds / 1000}s " +
                          $"({threads} threads)");
        return found;
    }

    private static (int Count, ulong Data) Array_(ulong at)
    {
        return (TryReadU32(at, out var c) ? (int)c : 0, ReadPtr(at + 8));
    }

    private static List<List<int>> ReadBoardGrid(ulong board)
    {
        var grid = new List<List<int>>();
        var (rows, rowData) = Array_(board + 0x20);
        for (var r = 0; r < rows && r < 16 && Sane(rowData); r++)
        {
            var row = ReadPtr(rowData + (ulong)(r * 8));
            var line = new List<int>();
            if (Sane(row))
            {
                var (tiles, tileData) = Array_(row + 0x20);
                for (var c = 0; c < tiles && c < 16 && Sane(tileData); c++)
                {
                    var tile = ReadPtr(tileData + (ulong)(c * 8));
                    line.Add(Sane(tile) && TryRead(tile + 0x50, 1, out var t)
                        ? (sbyte)t[0] : int.MinValue);
                }
            }
            grid.Add(line);
        }
        return grid;
    }

}
