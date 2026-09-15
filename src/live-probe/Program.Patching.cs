using System.Runtime.InteropServices;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private static readonly (string Name, ulong Rva)[] ControllerVtables =
    [
        ("AIBoardGamePlayingControllerInstance", 0x190D708),
        ("HumanBoardGamePlayingControllerInstance", 0x190D7C0),
        ("BoardGamePlayingControllerInstance", 0x190DD38),
    ];

    private static int FindController(int maxDepth, int maxNodes)
    {
        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live.");
            return 2;
        }

        Console.WriteLine($"\n  searching challenge @0x{chain.Challenge:X} and instance @0x{chain.Inst:X}");

        var (known, knownName) = KnownChainController(chain);
        if (known != 0)
        {
            Console.WriteLine($"\n  {knownName}");
            Console.WriteLine($"    the baked-in chain still holds, object 0x{known:X}, no search needed");
        }

        var found = ShallowFindControllers(chain);

        if (found.Count == 0)
        {
            Console.WriteLine("  not parked in either root's own fields. Widening to a pointer walk.");
            found = DeepFindControllers(chain, maxDepth, maxNodes);
        }

        if (found.Count == 0)
        {
            Console.WriteLine("\n  no playing controller reached. Fall back to a full pass:");
            foreach (var (name, rva) in ControllerVtables)
            {
                Console.WriteLine($"    live-probe --find-ptr {_base + rva:X}    # {name}");
            }

            return 3;
        }

        foreach (var (where, obj, name) in found)
        {
            Console.WriteLine($"\n  {name}");
            Console.WriteLine($"    slot 0x{where:X}  object 0x{obj:X}");

            var viaController = ReadPtr(ReadPtr(obj + 0x48) + 8);
            Console.WriteLine($"    [[+0x48]+8]  0x{viaController:X}  " +
                              $"{(viaController == chain.Inst ? "== BoardGameInstance, confirmed" : "MISMATCH, not our controller")}");

            Console.WriteLine($"    +0x030 actions   count {ReadU32(obj + 0x30)}  data 0x{ReadPtr(obj + 0x38):X}");
            Console.WriteLine($"    +0x050 player    0x{ReadPtr(obj + 0x50):X}");
            Console.WriteLine($"    +0x080 selected  ({(int)ReadU32(obj + 0x80)},{(int)ReadU32(obj + 0x84)})");
            Console.WriteLine($"    +0x088 active    0x{ReadPtr(obj + 0x88):X}");
            Console.WriteLine($"    +0x0A0 from-tile ({(int)ReadU32(obj + 0xA0)},{(int)ReadU32(obj + 0xA4)})");
            Console.WriteLine($"    +0x0B0 facing    {ReadByte(obj + 0xB0)}");

            var plan = ReadPtr(obj + 0xE8);
            Console.Write($"    +0x0E8 move plan 0x{plan:X}");
            if (plan == 0)
            {
                Console.WriteLine("   <-- NULL. The game's attack function would fault on this.");
            }
            else if (!Sane(plan))
            {
                Console.WriteLine("   <-- implausible pointer.");
            }
            else
            {
                Console.WriteLine($"   count {ReadU32(plan)}  data 0x{ReadPtr(plan + 8):X}");
            }

            Console.WriteLine($"    +0x320 activated {ReadByte(obj + 0x320)}   +0x348 latched {ReadByte(obj + 0x348)}");
            Console.WriteLine($"    +0x350 lathium   0x{ReadPtr(obj + 0x350):X}");
            Console.WriteLine($"    +0x440 queue     count {ReadU32(obj + 0x440)}  data 0x{ReadPtr(obj + 0x448):X}  index {ReadU32(obj + 0x450)}");
        }

        return 0;
    }

    private static readonly ulong[] ControllerChain = [0x68, 0x18];

    private static (ulong Obj, string Name) KnownChainController((ulong Challenge, ulong Inst, ulong Logic) chain)
    {
        var addr = chain.Inst;

        foreach (var off in ControllerChain)
        {
            if (!Sane(addr))
            {
                return (0, "");
            }

            addr = ReadPtr(addr + off);
        }

        if (!Sane(addr) || !TryRead(addr, 8, out var vt))
        {
            return (0, "");
        }

        var vtable = BitConverter.ToUInt64(vt);
        foreach (var (name, rva) in ControllerVtables)
        {
            if (vtable == _base + rva)
            {
                return (addr, $"instance{string.Join("", ControllerChain.Select(o => $"+0x{o:X}"))} -> {name}");
            }
        }

        return (0, "");
    }

    private static List<(ulong Where, ulong Obj, string Name)> ShallowFindControllers(
        (ulong Challenge, ulong Inst, ulong Logic) chain)
    {
        var found = new List<(ulong Where, ulong Obj, string Name)>();

        foreach (var (root, span, label) in new[]
                 {
                     (chain.Challenge, 0xC8, "challenge"),
                     (chain.Inst, 0x200, "instance"),
                 })
        {
            for (ulong off = 0; off < (ulong)span; off += 8)
            {
                var candidate = ReadPtr(root + off);
                if (!Sane(candidate))
                {
                    continue;
                }

                if (!TryRead(candidate, 8, out var vt))
                {
                    continue;
                }

                var vtable = BitConverter.ToUInt64(vt);

                foreach (var (name, rva) in ControllerVtables)
                {
                    if (vtable == _base + rva)
                    {
                        found.Add((root + off, candidate, $"{label}+0x{off:X} -> {name}"));
                    }
                }
            }
        }

        return found;
    }

    private static List<(ulong Where, ulong Obj, string Name)> DeepFindControllers(
        (ulong Challenge, ulong Inst, ulong Logic) chain, int maxDepth, int maxNodes)
    {
        const int Span = 0x400;

        var hits = new List<(ulong Where, ulong Obj, string Name)>();
        var seen = new HashSet<ulong>();
        var queue = new Queue<(ulong Addr, ulong Where, int Depth, string Path)>();

        void Push(ulong addr, ulong where, int depth, string path)
        {
            if (!Sane(addr) || !seen.Add(addr))
            {
                return;
            }

            queue.Enqueue((addr, where, depth, path));
        }

        Push(chain.Challenge, 0, 0, "challenge");
        Push(chain.Inst, 0, 0, "instance");
        Push(chain.Logic, 0, 0, "logic");

        for (var i = 0; i < 2; i++)
        {
            var slot = chain.Inst + 0x40 + (ulong)(i * 8);
            Push(ReadPtr(slot), slot, 0, $"instance+0x{0x40 + (i * 8):X}");
        }

        var visited = 0;
        var deepest = 0;
        var started = DateTime.UtcNow;

        while (queue.Count > 0 && visited < maxNodes)
        {
            var (addr, where, depth, path) = queue.Dequeue();

            var wide = TryRead(addr, Span, out var buf);
            if (!wide && !TryRead(addr, 0x40, out buf))
            {
                continue;
            }

            visited++;
            deepest = Math.Max(deepest, depth);

            var vtable = BitConverter.ToUInt64(buf, 0);
            foreach (var (name, rva) in ControllerVtables)
            {
                if (vtable == _base + rva)
                {
                    hits.Add((where, addr, $"{path} -> {name}"));
                }
            }

            if (hits.Count > 0 || depth >= maxDepth || !wide)
            {
                continue;
            }

            for (var off = 8; off + 8 <= Span; off += 8)
            {
                Push(BitConverter.ToUInt64(buf, off), addr + (ulong)off, depth + 1, $"{path}+0x{off:X}");
            }
        }

        var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        Console.WriteLine($"    walked {visited} objects to depth {deepest} in {ms} ms, {hits.Count} hit(s)" +
                          $"{(visited >= maxNodes ? "   <-- node cap reached, raise --nodes" : "")}");

        return hits;
    }

    private static (ulong Obj, string Name) LocateController(
        (ulong Challenge, ulong Inst, ulong Logic) chain, int maxDepth, int maxNodes, bool verbose)
    {
        var (known, knownName) = KnownChainController(chain);
        if (known != 0)
        {
            return (known, knownName);
        }

        var found = ShallowFindControllers(chain);
        if (found.Count == 0)
        {
            found = verbose ? DeepFindControllers(chain, maxDepth, maxNodes) : Quietly(chain, maxDepth, maxNodes);
        }

        if (found.Count == 0)
        {
            return (0, "");
        }

        foreach (var hit in found)
        {
            if (hit.Name.Contains("Human"))
            {
                return (hit.Obj, hit.Name);
            }
        }

        return (found[0].Obj, found[0].Name);
    }

    private static List<(ulong Where, ulong Obj, string Name)> Quietly(
        (ulong Challenge, ulong Inst, ulong Logic) chain, int maxDepth, int maxNodes)
    {
        var saved = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            return DeepFindControllers(chain, maxDepth, maxNodes);
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static int WatchController(int seconds, int maxDepth, int maxNodes)
    {
        Console.WriteLine($"\n  watching the playing controller for {seconds}s");
        Console.WriteLine("  READY");

        var until = DateTime.UtcNow.AddSeconds(seconds);
        var last = "";
        ulong obj = 0;
        var locates = 0;

        while (DateTime.UtcNow < until)
        {
            var chain = Walk(report: false);
            if (chain.Logic == 0)
            {
                Report("no match live", ref last);
                Thread.Sleep(250);
                continue;
            }

            if (obj != 0 && !IsController(obj))
            {
                Report($"controller 0x{obj:X} went stale", ref last);
                obj = 0;
            }

            if (obj == 0)
            {
                var started = DateTime.UtcNow;
                var (candidate, name) = LocateController(chain, maxDepth, maxNodes, verbose: false);
                var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                locates++;

                if (candidate == 0)
                {
                    Report($"not reachable (search {ms} ms)", ref last);
                    Thread.Sleep(250);
                    continue;
                }

                obj = candidate;
                Report($"located 0x{obj:X} in {ms} ms via {name}", ref last);
            }

            var unit = ReadPtr(obj + 0x88);
            var actor = Sane(unit)
                ? $"unit 0x{unit:X} at ({ReadByte(unit + 0x38) & 0xF},{ReadByte(unit + 0x38) >> 4}) hp {ReadByte(unit + 0x3A)}"
                : "unit none";

            var line = $"act {ReadByte(obj + 0x320)}  {actor}" +
                       $"  from ({(int)ReadU32(obj + 0xA0)},{(int)ReadU32(obj + 0xA4)})" +
                       $"  to ({(int)ReadU32(obj + 0xA8)},{(int)ReadU32(obj + 0xAC)})" +
                       $"  facing {ReadByte(obj + 0xB0)}" +
                       $"  sel ({(int)ReadU32(obj + 0x80)},{(int)ReadU32(obj + 0x84)})" +
                       $"  plan 0x{ReadPtr(obj + 0xE8):X}  latch {ReadByte(obj + 0x348)}";

            Report(line, ref last);
            Thread.Sleep(40);
        }

        Console.WriteLine($"\n  done, {locates} location(s)");
        return 0;
    }

    private static bool IsController(ulong obj)
    {
        if (!TryRead(obj, 8, out var vt))
        {
            return false;
        }

        var vtable = BitConverter.ToUInt64(vt);
        foreach (var (_, rva) in ControllerVtables)
        {
            if (vtable == _base + rva)
            {
                return true;
            }
        }

        return false;
    }

    private static void Report(string line, ref string last)
    {
        if (line == last)
        {
            return;
        }

        Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  {line}");
        last = line;
    }

    private static bool SetRejectBranch(byte want, bool confirmed)
    {
        var at = _base + RejectJzRva;
        var was = ReadByte(at);

        var verb = want == OpJmp ? "patch" : "restore";
        Console.WriteLine($"\n  {verb} the required-move rejection branch");
        Console.WriteLine($"  0x{at:X}  (RVA 0x{RejectJzRva:X}, inside the game's move-rejection function)");
        Console.WriteLine($"  reads 0x{was:X2}  ->  write 0x{want:X2}   " +
                          $"({(want == OpJmp ? "JZ becomes JMP: branch dead, no free" : "JMP becomes JZ: stock behaviour")})");

        if (was != OpJz && was != OpJmp)
        {
            Console.Error.WriteLine($"  REFUSING: expected 0x{OpJz:X2} (JZ) or 0x{OpJmp:X2} (JMP).");
            Console.Error.WriteLine("  This is not the instruction the offset was derived from, the game build");
            Console.Error.WriteLine("  has changed. Re-derive the offset for this build before writing anything here.");
            return false;
        }

        if (was == want)
        {
            Console.WriteLine("  already in that state, nothing to do.");
            return true;
        }

        if (!confirmed)
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return true;
        }

        if (!VirtualProtectEx(_handle, (nint)at, 1, PageExecuteReadWrite, out var old))
        {
            Console.Error.WriteLine($"  VirtualProtectEx failed ({Marshal.GetLastWin32Error()}).");
            return false;
        }

        var ok = Write(at, [want]);
        VirtualProtectEx(_handle, (nint)at, 1, old, out _);
        if (!ok)
        {
            return false;
        }

        var now = ReadByte(at);
        Console.WriteLine($"  wrote; reads back 0x{now:X2}  {(now == want ? "OK" : "MISMATCH")}");
        return now == want;
    }

    private const ulong MoveBoundsSite1Rva = 0xE45F8E;
    private const ulong MoveBoundsSite2Rva = 0xE46106;
    private const ulong MoveBoundsSite1Back = 0xE45FA2;
    private const ulong MoveBoundsSite1Reject = 0xE4626C;
    private const ulong MoveBoundsSite2Back = 0xE46122;
    private const ulong MoveBoundsSite2Reject = 0xE46145;

    private const ulong MoveBoundsSite3Rva = 0xE025A5;
    private const ulong MoveBoundsSite3Back = 0xE025BA;
    private const ulong MoveBoundsSite3Reject = 0xE02707;

    private const ulong MoveBoundsSite8Rva = 0xE3B8B9;
    private const ulong MoveBoundsSite8Back = 0xE3B8C4;

    private const ulong RetiredSite4Rva = 0xE02465;
    private const ulong RetiredSite4Back = 0xE0246C;
    private const ulong RetiredSite5Rva = 0xE024E8;
    private const ulong RetiredSite5Back = 0xE024ED;
    private const ulong RetiredSite6Rva = 0xE025D1;
    private const ulong RetiredSite6Back = 0xE025D7;

    private static readonly byte[] RetiredSite4Original =
    [
        0x42, 0x8D, 0x0C, 0xC2,
        0x48, 0x63, 0xD1,
    ];

    private static readonly byte[] RetiredSite5Original =
    [
        0x8D, 0x04, 0xC8,
        0x48, 0x98,
    ];

    private static readonly byte[] RetiredSite6Original =
    [
        0x8D, 0x04, 0xCA,
        0x48, 0x63, 0xF0,
    ];

    private static (ulong Rva, byte[] Original, string Name)[] RetiredSites()
    {
        return
        [
            (Rva: RetiredSite4Rva, Original: RetiredSite4Original, Name: "retired site 4 (0xE02465), the stride at the start square"),
            (Rva: RetiredSite5Rva, Original: RetiredSite5Original, Name: "retired site 5 (0xE024E8), the stride at the square expanded"),
            (Rva: RetiredSite6Rva, Original: RetiredSite6Original, Name: "retired site 6 (0xE025D1), the stride at the neighbour"),
        ];
    }

    private const ulong MoveBoundsSite7Rva = 0xE04FCD;
    private const ulong MoveBoundsSite7Back = 0xE04FE3;
    private const ulong MoveBoundsSite7Reject = 0xE04FEB;

    private static readonly byte[] MoveBoundsSite8Original =
    [
        0x48, 0x63, 0x8A, 0xB0, 0x00, 0x00, 0x00,
        0x88, 0x44, 0x11, 0x70,
    ];

    private static readonly byte[] MoveBoundsSite7Original =
    [
        0x8B, 0x53, 0x10,
        0x0F, 0xBE, 0xC0,
        0xC1, 0xF8, 0x04,
        0x3B, 0xC2,
        0x7D, 0x11,
        0x8B, 0xC1,
        0xC1, 0xF8, 0x04,
        0x3B, 0xC2,
        0x7D, 0x08,
    ];

    private static readonly byte[] MoveBoundsSite1Original =
    [
        0x41, 0x8B, 0x49, 0x58,
        0x0F, 0xBE, 0xD0,
        0x8B, 0xC2,
        0xC1, 0xF8, 0x04,
        0x3B, 0xC1,
        0x0F, 0x8D, 0xCA, 0x02, 0x00, 0x00,
    ];

    private static readonly byte[] MoveBoundsSite2Original =
    [
        0x41, 0x8B, 0x49, 0x58,
        0x49, 0x8D, 0x79, 0x58,
        0x0F, 0xBE, 0xC0,
        0xC1, 0xF8, 0x04,
        0x3B, 0xC1,
        0x7D, 0x2D,
        0x0F, 0xBE, 0xC2,
        0xC1, 0xF8, 0x04,
        0x3B, 0xC1,
        0x7D, 0x23,
    ];

    private static readonly byte[] MoveBoundsSite3Original =
    [
        0x44, 0x8B, 0x46, 0x10,
        0x0F, 0xBE, 0xD0,
        0x8B, 0xC2,
        0xC1, 0xF8, 0x04,
        0x41, 0x3B, 0xC0,
        0x0F, 0x8D, 0x4D, 0x01, 0x00, 0x00,
    ];

    private const int MoveBoundsWidthSlot = 0;

    private const int MoveBoundsHeightSlot = 4;

    private static byte[] CmpEaxWidth(ulong stubAt, int offsetInStub, ulong page)
    {
        var code = new List<byte> { 0x3B, 0x05 };
        code.AddRange(Rel32(stubAt + (ulong)offsetInStub, page + MoveBoundsWidthSlot, 6));
        return [.. code];
    }

    private static byte[] CmpEaxHeight(ulong stubAt, int offsetInStub, ulong page)
    {
        var code = new List<byte> { 0x3B, 0x05 };
        code.AddRange(Rel32(stubAt + (ulong)offsetInStub, page + MoveBoundsHeightSlot, 6));
        return [.. code];
    }

    private static byte[] MovEcxHeight(ulong stubAt, int offsetInStub, ulong page)
    {
        var code = new List<byte> { 0x8B, 0x0D };
        code.AddRange(Rel32(stubAt + (ulong)offsetInStub, page + MoveBoundsHeightSlot, 6));
        return [.. code];
    }

    private static byte[] MovR8dHeight(ulong stubAt, int offsetInStub, ulong page)
    {
        var code = new List<byte> { 0x44, 0x8B, 0x05 };
        code.AddRange(Rel32(stubAt + (ulong)offsetInStub, page + MoveBoundsHeightSlot, 7));
        return [.. code];
    }

    private static bool Throws(Action act)
    {
        try
        {
            act();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int Count(byte[] haystack, byte[] needle)
    {
        var found = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle))
            {
                found++;
            }
        }

        return found;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        return Count(haystack, needle) > 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] Rel32(ulong from, ulong to, int instructionLength)
    {
        var delta = (long)to - (long)(from + (ulong)instructionLength);
        if (delta is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"rel32 out of range: 0x{from:X} -> 0x{to:X} is {delta} bytes, over 2 GB");
        }

        return BitConverter.GetBytes((int)delta);
    }

    private static ulong AllocNear(ulong near, int size)
    {
        const uint pageReadWrite = 0x04;
        const uint memFree = 0x10000;

        var at = near;
        while (at < near + 0x60000000)
        {
            if (VirtualQueryEx(_handle, (nint)at, out var info, Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }

            if (info.State == memFree && info.RegionSize >= (ulong)size)
            {
                var granular = (info.BaseAddress + 0xFFFF) & ~0xFFFFUL;
                if (granular + (ulong)size <= info.BaseAddress + info.RegionSize)
                {
                    var got = (ulong)VirtualAllocEx(_handle, (nint)granular, size, 0x1000 | 0x2000, pageReadWrite);
                    if (got != 0)
                    {
                        return got;
                    }
                }
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= at)
            {
                break;
            }

            at = next;
        }

        return (ulong)VirtualAllocEx(_handle, 0, size, 0x1000 | 0x2000, pageReadWrite);
    }

    private static bool MakeExecutable(ulong at, int size)
    {
        const uint pageExecuteRead = 0x20;
        if (!VirtualProtectEx(_handle, (nint)at, size, pageExecuteRead, out _))
        {
            Console.Error.WriteLine($"  could not make the stub page executable ({Marshal.GetLastWin32Error()}).");
            return false;
        }

        return true;
    }

    private static byte[] MoveBoundsStub1(ulong stubAt, ulong page)
    {
        var code = new List<byte>();
        code.AddRange([0x0F, 0xBE, 0xD0]);
        code.AddRange([0x8B, 0xC2]);
        code.AddRange([0xC1, 0xF8, 0x04]);
        code.AddRange(CmpEaxWidth(stubAt, code.Count, page));
        code.AddRange([0x0F, 0x8D]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 2, _base + MoveBoundsSite1Reject, 6));
        code.AddRange(MovEcxHeight(stubAt, code.Count, page));
        code.Add(0xE9);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 1, _base + MoveBoundsSite1Back, 5));
        return [.. code];
    }

    private static byte[] MoveBoundsStub2(ulong stubAt, ulong page)
    {
        var code = new List<byte>();
        code.AddRange([0x0F, 0xBE, 0xC0]);
        code.AddRange([0xC1, 0xF8, 0x04]);
        code.AddRange(CmpEaxWidth(stubAt, code.Count, page));
        code.AddRange([0x0F, 0x8D]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 2, _base + MoveBoundsSite2Reject, 6));
        code.AddRange(MovEcxHeight(stubAt, code.Count, page));
        code.AddRange([0x0F, 0xBE, 0xC2]);
        code.AddRange([0xC1, 0xF8, 0x04]);
        code.AddRange([0x3B, 0xC1]);
        code.AddRange([0x0F, 0x8D]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 2, _base + MoveBoundsSite2Reject, 6));
        code.AddRange([0x49, 0x8D, 0x79, 0x58]);
        code.Add(0xE9);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 1, _base + MoveBoundsSite2Back, 5));
        return [.. code];
    }

    private static byte[] MoveBoundsStub3(ulong stubAt, ulong page)
    {
        var code = new List<byte>
        {
            0x0F, 0xBE, 0xD0,
            0x8B, 0xC2,
            0xC1, 0xF8, 0x04,
        };
        code.AddRange(CmpEaxWidth(stubAt, code.Count, page));
        code.AddRange([0x0F, 0x8D]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 2, _base + MoveBoundsSite3Reject, 6));
        code.AddRange(MovR8dHeight(stubAt, code.Count, page));
        code.Add(0xE9);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 1, _base + MoveBoundsSite3Back, 5));
        return [.. code];
    }

    internal static byte[] MoveBoundsStub8(ulong stubAt, int width)
    {
        var pad = 8 - width;
        var code = new List<byte>();
        code.AddRange(MoveBoundsSite8Original);
        if (pad > 0)
        {
            code.AddRange([0x41, 0x89, 0xCA]);
            code.AddRange([0x41, 0x83, 0xE2, 0x07]);
            code.AddRange([0x41, 0x83, 0xFA, (byte)(width - 1)]);
            var rowEnd = new List<byte>();
            for (var k = 0; k < pad; k++)
            {
                rowEnd.AddRange([0xC6, 0x44, 0x11, (byte)(0x71 + k), 0x00]);
            }

            rowEnd.AddRange([0x83, 0x82, 0xB0, 0x00, 0x00, 0x00, (byte)pad]);
            code.AddRange([0x75, (byte)rowEnd.Count]);
            code.AddRange(rowEnd);
        }

        code.Add(0xE9);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 1, _base + MoveBoundsSite8Back, 5));
        return [.. code];
    }

    private static byte[] MoveBoundsStub7(ulong stubAt, ulong page)
    {
        var code = new List<byte>();
        code.AddRange([0x0F, 0xBE, 0xC0]);
        code.AddRange([0xC1, 0xF8, 0x04]);
        code.AddRange(CmpEaxWidth(stubAt, code.Count, page));
        code.AddRange([0x0F, 0x8D]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 2, _base + MoveBoundsSite7Reject, 6));
        code.AddRange([0x8B, 0xC1]);
        code.AddRange([0xC1, 0xF8, 0x04]);
        code.AddRange(CmpEaxHeight(stubAt, code.Count, page));
        code.AddRange([0x0F, 0x8D]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 2, _base + MoveBoundsSite7Reject, 6));
        code.Add(0xE9);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 1, _base + MoveBoundsSite7Back, 5));
        return [.. code];
    }

    internal const int MoveBoundsStock = 0;
    internal const int MoveBoundsPatched = 1;
    internal const int MoveBoundsMixed = 2;

    internal const string MoveBoundsHeldLine = "  move bounds patched, holding until the match or --play ends";

    internal static string? MoveBoundsShapeProblem(int width, int height)
    {
        if (ShapeProblem(width, "a width of") is { } wide)
        {
            return wide;
        }

        return ShapeProblem(height, "a height of");
    }

    internal static int MoveBoundsState(IReadOnlyList<bool> patched)
    {
        var any = false;
        var all = true;
        foreach (var p in patched)
        {
            any |= p;
            all &= p;
        }

        if (!any)
        {
            return MoveBoundsStock;
        }

        return all ? MoveBoundsPatched : MoveBoundsMixed;
    }

    private static int HoldMoveBounds(Func<int> clearAll, int width, int height)
    {
        Console.WriteLine(MoveBoundsHeldLine);
        var misses = 0;
        while (!_parentGone && misses < MoveBoundsGoneReads)
        {
            Thread.Sleep(250);
            var live = LiveBoardShape();
            misses = MoveBoundsMisses(misses, live.Logic, live.Width, live.Height, width, height);
        }

        Console.WriteLine(_parentGone
            ? "  --play is gone, or Ctrl+C was pressed; putting the move bounds back"
            : $"  no {width}x{height} match is live any more; putting the move bounds back");
        return clearAll();
    }

    internal const int MoveBoundsGoneReads = 3;

    internal static int MoveBoundsMisses(int misses, ulong liveLogic, int liveWidth, int liveHeight, int width, int height)
    {
        if (liveLogic != 0 && liveWidth == width && liveHeight == height)
        {
            return 0;
        }

        return misses + 1;
    }

    private static (ulong Logic, int Width, int Height) LiveBoardShape()
    {
        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            return (0, 0, 0);
        }

        var rowsObj = ReadPtr(chain.Logic + 0x08);
        var boardWidth = (int)ReadU32(ReadPtr(ReadPtr(rowsObj + 0x28)) + 0x20);
        var boardHeight = (int)ReadU32(rowsObj + 0x20);
        return (chain.Logic, boardWidth, boardHeight);
    }

    private static int PatchMoveBounds(bool confirmed, bool clear, bool keep, bool afterClear = false)
    {
        var sites = new[]
        {
            (Rva: MoveBoundsSite1Rva, Original: MoveBoundsSite1Original, Name: "site 1 (0xE45F8E)"),
            (Rva: MoveBoundsSite2Rva, Original: MoveBoundsSite2Original, Name: "site 2 (0xE46106)"),
            (Rva: MoveBoundsSite3Rva, Original: MoveBoundsSite3Original, Name: "site 3 (0xE025A5), the circles"),
            (Rva: MoveBoundsSite7Rva, Original: MoveBoundsSite7Original, Name: "site 7 (0xE04FCD), the attack stepper's bound"),
            (Rva: MoveBoundsSite8Rva, Original: MoveBoundsSite8Original, Name: "site 8 (0xE3B8B9), the table laid out eight wide"),
        };

        int ClearAll()
        {
            var restored = 0;
            var failed = 0;

            var all = sites.Select(s => (s.Rva, Stock: s.Original, s.Name, SayStock: true)).ToList();
            all.AddRange(RetiredSites().Select(s => (s.Rva, Stock: s.Original, Name: $"{s.Name}, left by an older build",
                                                     SayStock: false)));
            foreach (var s in all)
            {
                switch (ClearSite(s.Name, s.Rva, s.Stock, s.SayStock))
                {
                    case > 0:
                        restored++;
                        break;

                    case < 0:
                        failed++;
                        break;
                }
            }

            Console.WriteLine($"  {restored} site(s) put back. The stub page is left mapped on purpose.");
            return failed == 0 ? 0 : 1;
        }

        if (clear)
        {
            return ClearAll();
        }

        foreach (var s in RetiredSites())
        {
            if (ReadByte(_base + s.Rva) == 0xE9)
            {
                Console.Error.WriteLine($"  {s.Name} still carries an older build's detour; run " +
                                        "--patch-move-bounds --clear first. Refusing.");
                return 2;
            }
        }

        var patched = new List<bool>();
        var found = new List<byte[]>();
        foreach (var s in sites)
        {
            var at = _base + s.Rva;
            if (!TryRead(at, s.Original.Length, out var now))
            {
                Console.Error.WriteLine($"  {s.Name}: cannot read, refusing.");
                return 2;
            }

            patched.Add(now[0] == 0xE9);
            found.Add(now);
        }

        var state = MoveBoundsState(patched);
        if (state == MoveBoundsMixed)
        {
            Console.Error.WriteLine("  the sites are part patched and part stock; run --patch-move-bounds --clear first. Refusing.");
            return 2;
        }

        if (state == MoveBoundsPatched)
        {
            if (!confirmed || afterClear)
            {
                Console.WriteLine(afterClear
                    ? "  still patched after the clear, refusing."
                    : "  already patched; with --yes it is cleared and patched again for the live board.");
                return afterClear ? 2 : 0;
            }

            Console.WriteLine("  already patched, for a board this run did not read; clearing it first.");
            if (ClearAll() != 0)
            {
                Console.Error.WriteLine("  the old patch could not be cleared whole, refusing.");
                return 2;
            }

            return PatchMoveBounds(confirmed, clear: false, keep, afterClear: true);
        }

        for (var i = 0; i < sites.Length; i++)
        {
            if (!found[i].SequenceEqual(sites[i].Original))
            {
                Console.Error.WriteLine($"  {sites[i].Name}: bytes are not what this patch expects, refusing.");
                Console.Error.WriteLine($"    expected {Convert.ToHexString(sites[i].Original)}");
                Console.Error.WriteLine($"    found    {Convert.ToHexString(found[i])}");
                return 2;
            }
        }

        var (liveLogic, boardWidth, boardHeight) = LiveBoardShape();
        if (liveLogic == 0)
        {
            Console.Error.WriteLine("  no match is live, so the board width cannot be read. Enter a match first.");
            return 2;
        }

        if (MoveBoundsShapeProblem(boardWidth, boardHeight) is { } shapeBad)
        {
            Console.Error.WriteLine($"  {shapeBad}, refusing to patch.");
            return 2;
        }

        Console.WriteLine($"\n  all five sites hold their stock bytes.");
        Console.WriteLine($"  board is {boardWidth} wide by {boardHeight} deep; the bound the game uses " +
                          $"for BOTH axes reads {(int)ReadU32(liveLogic + 0x58)} (the smaller side).");
        Console.WriteLine("  patching so x is compared against the width and y against the height, " +
                          "and so the per-square table is laid out eight wide, the stride every reader assumes.");

        if (!confirmed)
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        var page = AllocNear(_base, 0x1000);
        if (page == 0)
        {
            Console.Error.WriteLine($"  VirtualAllocEx failed ({Marshal.GetLastWin32Error()}).");
            return 1;
        }

        byte[] stub1;
        byte[] stub2;
        byte[] stub3;
        byte[] stub7;
        byte[] stub8;
        var stub1At = page + 0x10;
        var stub2At = page;
        var stub3At = page;
        var stub7At = page;
        var stub8At = page;
        try
        {
            stub1 = MoveBoundsStub1(stub1At, page);
            stub2At = stub1At + (ulong)stub1.Length + 0x10;
            stub2 = MoveBoundsStub2(stub2At, page);
            stub3At = stub2At + (ulong)stub2.Length + 0x10;
            stub3 = MoveBoundsStub3(stub3At, page);
            stub7At = stub3At + (ulong)stub3.Length + 0x10;
            stub7 = MoveBoundsStub7(stub7At, page);
            stub8At = stub7At + (ulong)stub7.Length + 0x10;
            stub8 = MoveBoundsStub8(stub8At, boardWidth);

            _ = Rel32(_base + MoveBoundsSite1Rva, stub1At, 5);
            _ = Rel32(_base + MoveBoundsSite2Rva, stub2At, 5);
            _ = Rel32(_base + MoveBoundsSite3Rva, stub3At, 5);
            _ = Rel32(_base + MoveBoundsSite7Rva, stub7At, 5);
            _ = Rel32(_base + MoveBoundsSite8Rva, stub8At, 5);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"  refusing to patch: {e.Message}");
            Console.Error.WriteLine($"  the stub page landed at 0x{page:X}, too far from the module at 0x{_base:X}.");
            return 1;
        }

        if (!Write(page + MoveBoundsWidthSlot, BitConverter.GetBytes(boardWidth)) ||
            !Write(page + MoveBoundsHeightSlot, BitConverter.GetBytes(boardHeight)) ||
            !Write(stub1At, stub1) || !Write(stub2At, stub2) || !Write(stub3At, stub3) ||
            !Write(stub7At, stub7) || !Write(stub8At, stub8))
        {
            Console.Error.WriteLine("  could not write the stubs.");
            return 1;
        }

        if (!MakeExecutable(page, 0x1000))
        {
            return 1;
        }

        Console.WriteLine($"  stub page @0x{page:X}  width slot = {boardWidth}, height slot = {boardHeight}, stubs " +
                          $"{stub1.Length}/{stub2.Length}/{stub3.Length}/{stub7.Length}/{stub8.Length} bytes");

        var order = new[]
        {
            (Rva: MoveBoundsSite1Rva, StubAt: stub1At, Original: MoveBoundsSite1Original),
            (Rva: MoveBoundsSite2Rva, StubAt: stub2At, Original: MoveBoundsSite2Original),
            (Rva: MoveBoundsSite3Rva, StubAt: stub3At, Original: MoveBoundsSite3Original),
            (Rva: MoveBoundsSite7Rva, StubAt: stub7At, Original: MoveBoundsSite7Original),
            (Rva: MoveBoundsSite8Rva, StubAt: stub8At, Original: MoveBoundsSite8Original),
        };

        var jumps = new List<(ulong At, byte[] Patch, byte[] Stock)>();
        foreach (var (rva, stubAt, original) in order)
        {
            var at = _base + rva;
            var patch = new List<byte> { 0xE9 };
            patch.AddRange(Rel32(at, stubAt, 5));
            while (patch.Count < original.Length)
            {
                patch.Add(0x90);
            }

            jumps.Add((at, [.. patch], original));
        }

        var set = PatchAllOrNone(jumps, WriteCode);
        if (set != PatchSet.Whole)
        {
            Console.Error.WriteLine(set == PatchSet.RolledBack
                ? "  could not patch every site; each one already written is put back."
                : "  could not patch every site, and putting one back failed too; run --patch-move-bounds --clear.");
            return 1;
        }

        foreach (var (rva, stubAt, original) in order)
        {
            Console.WriteLine($"  0x{rva:X}: jmp -> 0x{stubAt:X}, {original.Length - 5} NOP(s)");
        }

        if (!keep)
        {
            Console.WriteLine("  patched. Release with: live-probe --patch-move-bounds --clear");
            return 0;
        }

        return HoldMoveBounds(ClearAll, boardWidth, boardHeight);
    }

    private static bool WriteCode(ulong at, byte[] bytes)
    {
        if (!VirtualProtectEx(_handle, (nint)at, bytes.Length, PageExecuteReadWrite, out var old))
        {
            Console.Error.WriteLine($"  VirtualProtectEx failed ({Marshal.GetLastWin32Error()}).");
            return false;
        }

        var ok = Write(at, bytes);
        VirtualProtectEx(_handle, (nint)at, bytes.Length, old, out _);
        return ok;
    }

    internal enum PatchSet
    {
        Whole,
        RolledBack,
        Partial,
    }

    internal static PatchSet PatchAllOrNone(IReadOnlyList<(ulong At, byte[] Patch, byte[] Stock)> sites,
                                            Func<ulong, byte[], bool> write)
    {
        for (var i = 0; i < sites.Count; i++)
        {
            if (write(sites[i].At, sites[i].Patch))
            {
                continue;
            }

            var clean = true;
            for (var j = i; j >= 0; j--)
            {
                clean &= write(sites[j].At, sites[j].Stock);
            }

            return clean ? PatchSet.RolledBack : PatchSet.Partial;
        }

        return PatchSet.Whole;
    }

    internal enum SiteFound
    {
        Stock,
        Ours,
        Foreign,
        Unreadable,
    }

    internal static SiteFound SiteClearVerdict(bool readOk, byte[] now, byte[] stock, ulong site,
                                               ulong imageStart, ulong imageEnd)
    {
        if (!readOk || now.Length < 5)
        {
            return SiteFound.Unreadable;
        }

        if (now.AsSpan().SequenceEqual(stock))
        {
            return SiteFound.Stock;
        }

        if (now[0] == 0xE9)
        {
            var target = (ulong)((long)site + 5 + BitConverter.ToInt32(now, 1));
            if (target < imageStart || target >= imageEnd)
            {
                return SiteFound.Ours;
            }
        }

        return SiteFound.Foreign;
    }

    private static int ClearSite(string name, ulong rva, byte[] stock, bool sayStock)
    {
        var at = _base + rva;
        var readOk = TryRead(at, stock.Length, out var now);
        switch (SiteClearVerdict(readOk, now, stock, at, _base, _moduleEnd))
        {
            case SiteFound.Stock:
                if (sayStock)
                {
                    Console.WriteLine($"  {name}: already stock");
                }

                return 0;

            case SiteFound.Ours:
                if (WriteCode(at, stock))
                {
                    Console.WriteLine($"  {name}: restored the stock {stock.Length} byte(s)");
                    return 1;
                }

                Console.Error.WriteLine($"  {name}: still patched, the write failed.");
                return -1;

            case SiteFound.Foreign:
                Console.Error.WriteLine($"  {name}: reads {Convert.ToHexString(now)}, neither the stock bytes nor " +
                                        "a jump of ours, so it is left alone.");
                return -1;

            default:
                Console.Error.WriteLine($"  {name}: cannot be read, so it is left alone.");
                return -1;
        }
    }

    private const ulong FlipBitRva = 0xE54F95;

    private static readonly byte[] FlipBitStock =
        [0x41, 0x81, 0xE0, 0x01, 0x00, 0x00, 0x80, 0x7D, 0x0A, 0x41, 0xFF, 0xC8,
         0x41, 0x83, 0xC8, 0xFE, 0x41, 0xFF, 0xC0];

    private static byte[] FlipBitForced(int index)
    {
        var patch = new byte[FlipBitStock.Length];
        Array.Fill(patch, (byte)0x90);
        patch[0] = 0x41; patch[1] = 0xB8;
        patch[2] = (byte)index; patch[3] = 0; patch[4] = 0; patch[5] = 0;
        return patch;
    }

    private static int ForceFirst(string[] args, int at)
    {
        var arg = at + 1 < args.Length ? args[at + 1].ToLowerInvariant() : "";
        var clear = args.Contains("--clear") || arg == "clear";

        var index = arg switch { "human" => 0, "ai" => 1, "0" => 0, "1" => 1, _ => -1 };
        if (!clear && index < 0)
        {
            Console.Error.WriteLine("  --force-first takes human, ai, 0, 1, or clear.");
            return 2;
        }

        var wait = ArgIntOrNull(args, "--wait") ?? 0;

        var site = _base + FlipBitRva;
        var was = Read(site, FlipBitStock.Length);
        var isStock = was.AsSpan().SequenceEqual(FlipBitStock);
        var want = clear ? FlipBitStock : FlipBitForced(index);

        var chatty = !Console.IsOutputRedirected;
        if (chatty)
        {
            Console.WriteLine($"\n  {(clear ? "restore" : "patch")} the coin flip's index computation");
            Console.WriteLine($"  0x{site:X}  (RVA 0x{FlipBitRva:X}, inside the game's coin-flip function)");
            Console.WriteLine($"  reads  {Convert.ToHexString(was)}");
            Console.WriteLine($"  write  {Convert.ToHexString(want)}");
            if (!clear)
            {
                Console.WriteLine($"         mov r8d, {index} + NOPs, first player is always player[{index}]");
            }
        }

        var isOurs = was.AsSpan().SequenceEqual(FlipBitForced(0)) || was.AsSpan().SequenceEqual(FlipBitForced(1));
        if (!isStock && !isOurs)
        {
            Console.Error.WriteLine("  REFUSING: these are not the bytes this offset was derived from.");
            Console.Error.WriteLine("  The game build has changed, re-derive the offset before writing here.");
            return 1;
        }

        if (was.AsSpan().SequenceEqual(want))
        {
            if (!ForceFirstAdopts(clear, wait, args.Contains("--yes")))
            {
                Console.WriteLine("  already in that state, nothing to do.");
                return 0;
            }

            Console.WriteLine($"  coin flip already forced to player[{index}]; holding it, and restoring it when the wait ends");
            return WaitThenRestoreFlip(site, index, wait);
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        if (!VirtualProtectEx(_handle, (nint)site, FlipBitStock.Length, PageExecuteReadWrite, out var old))
        {
            Console.Error.WriteLine($"  VirtualProtectEx failed ({Marshal.GetLastWin32Error()}).");
            return 1;
        }

        var ok = Write(site, want);
        VirtualProtectEx(_handle, (nint)site, FlipBitStock.Length, old, out _);
        if (!ok)
        {
            Console.Error.WriteLine("  write failed.");
            return 1;
        }

        var now = Read(site, FlipBitStock.Length);
        var good = now.AsSpan().SequenceEqual(want);
        if (chatty || !good)
        {
            Console.WriteLine($"  wrote; reads back {Convert.ToHexString(now)}  {(good ? "OK" : "MISMATCH")}");
        }
        else
        {
            Console.WriteLine(clear
                ? $"  coin flip restored to stock at 0x{site:X}"
                : $"  coin flip patched at 0x{site:X}: first player is always player[{index}]");
        }
        if (!good)
        {
            return 1;
        }

        if (clear)
        {
            return 0;
        }

        if (wait <= 0)
        {
            Console.WriteLine("  patch is IN PLACE. Enter a match, then run --force-first clear --yes.");
            return 0;
        }

        return WaitThenRestoreFlip(site, index, wait);
    }

    internal static bool ForceFirstAdopts(bool clear, int wait, bool confirmed)
    {
        return !clear && wait > 0 && confirmed;
    }

    internal static SiteFound FlipRestoreVerdict(bool readOk, byte[] now)
    {
        if (!readOk || now.Length != FlipBitStock.Length)
        {
            return SiteFound.Unreadable;
        }

        if (now.AsSpan().SequenceEqual(FlipBitStock))
        {
            return SiteFound.Stock;
        }

        if (now.AsSpan().SequenceEqual(FlipBitForced(0)) || now.AsSpan().SequenceEqual(FlipBitForced(1)))
        {
            return SiteFound.Ours;
        }

        return SiteFound.Foreign;
    }

    private static int WaitThenRestoreFlip(ulong site, int index, int wait)
    {
        Console.WriteLine($"\n  waiting for a match to verify against (up to {wait}s)");
        var until = DateTime.UtcNow.AddSeconds(wait);

        var seen = Walk(report: false).Inst;
        var seenFirst = Sane(seen) ? ReadPtr(seen + FirstPlayer) : 0;
        if (Sane(seen))
        {
            Console.WriteLine($"  (ignoring inst 0x{seen:X}, it was set up before the patch)");
        }

        while (DateTime.UtcNow < until && !_parentGone)
        {
            var live = Walk(report: false);
            if (Sane(live.Inst))
            {
                var cur = ReadPtr(live.Inst + FirstPlayer);
                var ai = FindAiSeat(live.Inst);
                if (cur != 0 && ai >= 0 && (live.Inst != seen || cur != seenFirst))
                {
                    seen = live.Inst;
                    seenFirst = cur;
                    var p = ReadPtr(live.Inst + 0x40 + (ulong)(index * 8));
                    var landed = cur == p;
                    var seatName = index == ai ? "the AI seat" : "OUR seat";

                    Console.WriteLine($"  inst 0x{live.Inst:X}  first player is " +
                                      $"player[{(cur == ReadPtr(live.Inst + 0x40) ? 0 : 1)}]  " +
                                      $"(AI is seat {ai}, so player[{index}] is {seatName})");
                    if (!landed)
                    {
                        Console.WriteLine("  Warning: +0x58 does NOT hold the forced index. The patch did not take.");
                    }
                    else if (!Console.IsOutputRedirected)
                    {
                        Console.WriteLine("  the patch decided it, +0x58 holds the index we forced.");
                    }
                }
            }
            Thread.Sleep(100);
        }

        Console.WriteLine("\n  restoring the stock bytes");
        var readOk = TryRead(site, FlipBitStock.Length, out var now);
        switch (FlipRestoreVerdict(readOk, now))
        {
            case SiteFound.Stock:
                Console.WriteLine("  already stock");
                return 0;

            case SiteFound.Foreign:
                Console.Error.WriteLine($"  reads {Convert.ToHexString(now)}, neither stock nor ours, so it is left alone.");
                return 1;

            case SiteFound.Unreadable:
                Console.Error.WriteLine("  cannot be read, so it is left alone.");
                return 1;
        }

        if (!VirtualProtectEx(_handle, (nint)site, FlipBitStock.Length, PageExecuteReadWrite, out var o2))
        {
            Console.Error.WriteLine($"  VirtualProtectEx failed ({Marshal.GetLastWin32Error()}); the coin flip is still forced.");
            return 1;
        }

        var wrote = Write(site, FlipBitStock);
        VirtualProtectEx(_handle, (nint)site, FlipBitStock.Length, o2, out _);
        var back = Read(site, FlipBitStock.Length);
        var stock = wrote && back.AsSpan().SequenceEqual(FlipBitStock);
        Console.WriteLine($"  reads back {Convert.ToHexString(back)}  {(stock ? "OK, stock" : "MISMATCH, the coin flip is still forced")}");
        return stock ? 0 : 1;
    }

    private static bool Validate(ulong logic, ulong inst, int seat, Act a)
    {
        int srcX = a.SrcX, srcY = a.SrcY, dstX = a.DstX, dstY = a.DstY;
        var attack = a.Attack;
        var facing = a.Facing;

        var rows = ReadPtr(logic + 0x08);
        var height = (int)ReadU32(rows + 0x20);
        var width = (int)ReadU32(ReadPtr(ReadPtr(rows + 0x28)) + 0x20);

        var ours = ReadPtr(inst + 0x40 + (ulong)(seat * 8));
        var theirs = ReadPtr(inst + 0x40 + (ulong)((1 - seat) * 8));

        ulong srcUnit = 0, dstUnit = 0, walkUnit = 0;
        var srcSeat = "?";
        var dstSeat = "?";

        var (walkX, walkY) = StandSquare(a);

        var count = (int)ReadU32(logic + 0x38);
        var array = ReadPtr(logic + 0x40);
        for (var i = 0; i < count && i < 64; i++)
        {
            var u = ReadPtr(array + (ulong)(i * 8));
            if (!Sane(u))
            {
                continue;
            }

            var packed = ReadByte(u + 0x38);
            int x = Nibble(packed), y = Nibble(packed >> 4);
            var owner = ReadPtr(u);
            var who = owner == ours ? "AI" : owner == theirs ? "opponent" : "?";

            if (x == srcX && y == srcY)
            {
                srcUnit = u;
                srcSeat = who;
            }
            if (x == dstX && y == dstY)
            {
                dstUnit = u;
                dstSeat = who;
            }
            if (x == walkX && y == walkY)
            {
                walkUnit = u;
            }
        }

        var problems = new List<string>();

        if (srcX < 0 || srcX >= width || srcY < 0 || srcY >= height)
        {
            problems.Add($"src ({srcX},{srcY}) is off a {width}x{height} board");
        }

        if (dstX < 0 || dstX >= width || dstY < 0 || dstY >= height)
        {
            problems.Add($"dst ({dstX},{dstY}) is off a {width}x{height} board");
        }

        if (srcX == dstX && srcY == dstY)
        {
            problems.Add("src and dst are the same tile");
        }

        if (srcUnit == 0)
        {
            problems.Add($"no piece stands on src ({srcX},{srcY})");
        }
        else if (srcSeat != "AI")
        {
            problems.Add($"the piece on src ({srcX},{srcY}) belongs to the {srcSeat}, not the AI");
        }

        if (attack)
        {
            if (dstUnit == 0)
            {
                problems.Add($"attack target ({dstX},{dstY}) is empty");
            }
            else if (dstSeat == "AI")
            {
                problems.Add($"attack target ({dstX},{dstY}) is the AI's own piece");
            }

            if (a.FromX >= 0 && a.FromY >= 0)
            {
                var aligned = (facing & 3) switch
                {
                    0 => dstX == walkX && dstY < walkY,
                    1 => dstY == walkY && dstX > walkX,
                    2 => dstX == walkX && dstY > walkY,
                    _ => dstY == walkY && dstX < walkX,
                };

                var (skill, srcRange) = srcUnit != 0 ? SkillAndRange(srcUnit) : (-1, -1);
                var swept = skill == SpreadSkill
                            && InSpreadReach(walkX, walkY, facing, srcRange, dstX, dstY);

                if (!aligned && !swept)
                {
                    problems.Add($"standing on ({walkX},{walkY}) facing {facing} does not put the victim " +
                                 $"({dstX},{dstY}) on the strike line, the attack would hit something else");
                }
            }

            if (walkX < 0 || walkX >= width || walkY < 0 || walkY >= height)
            {
                problems.Add($"striking ({dstX},{dstY}) facing {facing} would put the attacker on " +
                             $"({walkX},{walkY}), off a {width}x{height} board");
            }
            else if (walkUnit != 0 && walkUnit != srcUnit)
            {
                problems.Add($"the attacker must stand on ({walkX},{walkY}) to strike ({dstX},{dstY}) " +
                             $"facing {facing}, and that tile is occupied");
            }
        }
        else if (dstUnit != 0)
        {
            problems.Add($"move destination ({dstX},{dstY}) is occupied by the {dstSeat}");
        }

        Console.WriteLine($"\n  pre-flight  board {width}x{height}, {count} pieces");
        Console.WriteLine($"    src ({srcX},{srcY})  {(srcUnit == 0 ? "empty" : $"@0x{srcUnit:X} {srcSeat}, +0x3A 0x{ReadByte(srcUnit + 0x3A):X2} +0x3B 0x{ReadByte(srcUnit + 0x3B):X2}")}");
        Console.WriteLine($"    dst ({dstX},{dstY})  {(dstUnit == 0 ? "empty" : $"@0x{dstUnit:X} {dstSeat}, +0x3A 0x{ReadByte(dstUnit + 0x3A):X2} +0x3B 0x{ReadByte(dstUnit + 0x3B):X2}")}");

        if (problems.Count == 0)
        {
            Console.WriteLine("    checks passed");
            return true;
        }

        foreach (var p in problems)
        {
            Console.Error.WriteLine($"    REJECT: {p}");
        }

        Console.Error.WriteLine("    not arming. Pass --force to arm anyway.");
        return false;
    }

}
