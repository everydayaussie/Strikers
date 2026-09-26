using System.Runtime.InteropServices;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private const ulong CommitActivateRva = 0xE404F0;
    private const ulong CommitMoveRva = 0xE40800;
    private const ulong CommitAttackRva = 0xE40BD0;
    private const ulong CommitBurstRva = 0xE405D0;

    private const int CommitCountSlot = 0x000;
    private const int CommitRecordsSlot = 0x040;
    private const int CommitRecordSize = 0x40;
    private const int CommitRecordCount = 32;

    private const int CommitMagicSlot = 0x00;
    private const int CommitRingSlot = 0x08;
    private const int CommitStubsAt = 0x10;
    private const int CommitStubStride = 0x100;

    private const ulong CommitMagic = 0x474E49524D4F4353;

    private const ulong AiControllerVtableRva = 0x190D708;
    private const int ActivateSlot = 0x10;
    private const ulong BaseActivateRva = 0xE3F3B0;
    private const ulong LightSelectedRva = 0xE41790;

    internal static int CursorStubAt()
    {
        return CommitStubsAt + CommitSites.Length * CommitStubStride;
    }

    internal static byte[] AiCursorStub(ulong activate, ulong lightSelected)
    {
        var code = new List<byte> { 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xD9, 0x48, 0xB8 };
        code.AddRange(BitConverter.GetBytes(activate));
        code.AddRange([0xFF, 0xD0, 0x48, 0x8B, 0xCB, 0x33, 0xD2, 0x48, 0xB8]);
        code.AddRange(BitConverter.GetBytes(lightSelected));
        code.AddRange([0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3]);
        return [.. code];
    }

    internal enum AiActivateState
    {
        Stock,
        Ours,
        Foreign,
        Unreadable,
    }

    internal static AiActivateState AiActivateVerdict(byte[]? have, ulong stock, ulong moduleBase, ulong moduleEnd)
    {
        if (have is null || have.Length != 8)
        {
            return AiActivateState.Unreadable;
        }

        var target = BitConverter.ToUInt64(have, 0);
        if (target == stock)
        {
            return AiActivateState.Stock;
        }

        if (target >= moduleBase && target < moduleEnd)
        {
            return AiActivateState.Foreign;
        }

        return AiActivateState.Ours;
    }

    private static void PatchAiActivate(ulong page)
    {
        var slot = _base + AiControllerVtableRva + ActivateSlot;
        var stubAt = page + (ulong)CursorStubAt();
        var have = TryRead(slot, 8, out var read) ? read : null;
        var verdict = AiActivateVerdict(have, _base + BaseActivateRva, _base, _moduleEnd);
        if (verdict == AiActivateState.Ours && BitConverter.ToUInt64(have!, 0) == stubAt)
        {
            Console.WriteLine("  AI cursor: the activate slot already points at this page's stub");
            return;
        }

        if (verdict != AiActivateState.Stock)
        {
            Console.Error.WriteLine($"  AI cursor: the AI controller's activate slot 0x{slot:X} does not read the stock " +
                                    $"0x{_base + BaseActivateRva:X}, so it is left alone and the idle cursor stays");
            return;
        }

        if (!WriteCode(slot, BitConverter.GetBytes(stubAt)))
        {
            Console.Error.WriteLine("  AI cursor: the activate slot could not be written, so the idle cursor stays");
            return;
        }

        Console.WriteLine($"  AI cursor: activate slot 0x{slot:X} -> stub @0x{stubAt:X}, the AI seat starts each turn " +
                          "with no tile lit");
    }

    private static int ClearAiActivate()
    {
        var slot = _base + AiControllerVtableRva + ActivateSlot;
        var stock = _base + BaseActivateRva;
        var have = TryRead(slot, 8, out var read) ? read : null;
        switch (AiActivateVerdict(have, stock, _base, _moduleEnd))
        {
            case AiActivateState.Stock:
                return 0;

            case AiActivateState.Ours:
                if (!WriteCode(slot, BitConverter.GetBytes(stock)))
                {
                    Console.Error.WriteLine($"  AI cursor: the activate slot 0x{slot:X} could not be put back");
                    return -1;
                }

                Console.WriteLine("  AI cursor: the activate slot put back");
                return 1;

            case AiActivateState.Foreign:
                Console.Error.WriteLine($"  AI cursor: the activate slot 0x{slot:X} points inside the game and not at " +
                                        "the stock activate: a different build, left alone");
                return 0;

            default:
                Console.Error.WriteLine($"  AI cursor: the activate slot 0x{slot:X} could not be read");
                return -1;
        }
    }

    private static readonly (string Name, ulong Rva, byte Kind, byte[] Stock)[] CommitSites =
    [
        ("activate", CommitActivateRva, 1, [0x48, 0x89, 0x5C, 0x24, 0x08]),
        ("move", CommitMoveRva, 2, [0x48, 0x89, 0x5C, 0x24, 0x10]),
        ("attack", CommitAttackRva, 3, [0x48, 0x89, 0x5C, 0x24, 0x10]),
        ("burst", CommitBurstRva, 4, [0x48, 0x89, 0x5C, 0x24, 0x08]),
    ];

    internal static byte[] CommitStub(ulong stubAt, ulong ring, byte kind, byte[] displaced, ulong backTo)
    {
        var code = new List<byte>();
        code.AddRange([0x4C, 0x8D, 0x15]);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 3, ring, 7));
        code.AddRange([0x41, 0x8B, 0x82]); code.AddRange(BitConverter.GetBytes(CommitCountSlot));
        code.AddRange([0x41, 0x89, 0xC3]);
        code.AddRange([0x41, 0x83, 0xE3, (byte)(CommitRecordCount - 1)]);
        code.AddRange([0x41, 0xC1, 0xE3, 0x06]);
        code.AddRange([0x4D, 0x03, 0xDA]);
        code.AddRange([0x49, 0x81, 0xC3]); code.AddRange(BitConverter.GetBytes(CommitRecordsSlot));
        code.AddRange([0x49, 0x89, 0x4B, 0x08]);
        code.AddRange([0x48, 0x8B, 0x81, 0x88, 0x00, 0x00, 0x00]);
        code.AddRange([0x49, 0x89, 0x43, 0x10]);
        code.AddRange([0x8B, 0x81, 0xA0, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x89, 0x43, 0x18]);
        code.AddRange([0x8B, 0x81, 0xA4, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x89, 0x43, 0x1C]);
        code.AddRange([0x8B, 0x81, 0xA8, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x89, 0x43, 0x20]);
        code.AddRange([0x8B, 0x81, 0xAC, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x89, 0x43, 0x24]);
        code.AddRange([0x49, 0x89, 0x53, 0x28]);
        code.AddRange([0x48, 0x8B, 0x01]);
        code.AddRange([0x49, 0x89, 0x43, 0x30]);
        code.AddRange([0x8B, 0x81, 0x80, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x89, 0x43, 0x38]);
        code.AddRange([0x8B, 0x81, 0x84, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x89, 0x43, 0x3C]);
        code.AddRange([0x41, 0xC7, 0x03, kind, 0x00, 0x00, 0x00]);
        code.AddRange([0x41, 0x8B, 0x82]); code.AddRange(BitConverter.GetBytes(CommitCountSlot));
        code.AddRange([0xFF, 0xC0]);
        code.AddRange([0x41, 0x89, 0x43, 0x04]);
        code.AddRange([0x41, 0x89, 0x82]); code.AddRange(BitConverter.GetBytes(CommitCountSlot));
        code.AddRange(displaced);
        code.Add(0xE9);
        code.AddRange(Rel32(stubAt + (ulong)code.Count - 1, backTo, 5));
        return [.. code];
    }

    private static int CommitSitesPatched()
    {
        var patched = 0;
        foreach (var s in CommitSites)
        {
            if (ReadByte(_base + s.Rva) == 0xE9)
            {
                patched++;
            }
        }

        return patched;
    }

    internal static ulong FindCommitRing(out bool unreadable)
    {
        unreadable = false;
        var entries = new List<byte[]>();
        foreach (var s in CommitSites)
        {
            if (!TryRead(_base + s.Rva, 5, out var jump))
            {
                unreadable = true;
                return 0;
            }

            entries.Add(jump);
        }

        var page = CommitRingPageFromEntries(_base, entries);
        if (page == 0)
        {
            return 0;
        }

        if (!Sane(page) || !TryRead(page, CommitStubsAt, out var header))
        {
            unreadable = true;
            return 0;
        }

        if (BitConverter.ToUInt64(header, CommitMagicSlot) != CommitMagic)
        {
            return 0;
        }

        var ring = BitConverter.ToUInt64(header, CommitRingSlot);
        if (!Sane(ring))
        {
            unreadable = true;
            return 0;
        }

        return ring;
    }

    internal enum CommitRingStep
    {
        Emit,
        Wait,
        Reset,
    }

    internal static CommitRingStep StepCommitRing(ulong current, ulong found, bool unreadable)
    {
        if (unreadable)
        {
            return CommitRingStep.Wait;
        }

        if (found == 0 || found != current)
        {
            return CommitRingStep.Reset;
        }

        return CommitRingStep.Emit;
    }

    internal static ulong CommitPageFromJump(ulong entry, byte[] jump)
    {
        if (jump.Length < 5 || jump[0] != 0xE9)
        {
            return 0;
        }

        var stubAt = (ulong)((long)entry + 5 + BitConverter.ToInt32(jump, 1));
        if (stubAt < CommitStubsAt)
        {
            return 0;
        }

        return stubAt - CommitStubsAt;
    }

    internal static ulong CommitRingPageFromEntries(ulong @base, IReadOnlyList<byte[]> entries)
    {
        if (entries.Count != CommitSites.Length)
        {
            return 0;
        }

        ulong page = 0;
        for (var i = 0; i < CommitSites.Length; i++)
        {
            var slot = CommitPageFromJump(@base + CommitSites[i].Rva, entries[i]);
            var offset = (ulong)(i * CommitStubStride);
            if (slot == 0 || slot < offset)
            {
                return 0;
            }

            var here = slot - offset;
            if (i > 0 && here != page)
            {
                return 0;
            }

            page = here;
        }

        return page;
    }

    private static int InstallCommitRing(out ulong page, out ulong ring)
    {
        page = 0;
        ring = 0;

        foreach (var s in CommitSites)
        {
            var have = Read(_base + s.Rva, s.Stock.Length);
            if (!have.SequenceEqual(s.Stock))
            {
                Console.Error.WriteLine($"  {s.Name} 0x{s.Rva:X} reads {BitConverter.ToString(have)}, not the stock " +
                                        $"{BitConverter.ToString(s.Stock)}: a different build, or already patched " +
                                        "(--commit-ring --clear puts it back).");
                return 3;
            }
        }

        page = AllocNear(_base, 0x1000);
        ring = page == 0 ? 0 : AllocNear(_base, 0x1000);
        if (page == 0 || ring == 0)
        {
            Console.Error.WriteLine($"  VirtualAllocEx failed ({Marshal.GetLastWin32Error()}).");
            return 1;
        }

        Console.WriteLine($"  stub page @0x{page:X}, ring page @0x{ring:X}");

        var stubs = new List<(ulong At, byte[] Code, (string Name, ulong Rva, byte Kind, byte[] Stock) Site)>();
        try
        {
            for (var i = 0; i < CommitSites.Length; i++)
            {
                var s = CommitSites[i];
                var at = page + (ulong)(CommitStubsAt + (i * CommitStubStride));
                var entry = _base + s.Rva;
                _ = Rel32(entry, at, 5);
                var code = CommitStub(at, ring, s.Kind, s.Stock, entry + (ulong)s.Stock.Length);
                stubs.Add((at, code, s));
            }
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"  refusing to patch: {e.Message}");
            Console.Error.WriteLine($"  the stub page landed at 0x{page:X}, too far from the module at 0x{_base:X}.");
            return 1;
        }

        foreach (var (at, code, site) in stubs)
        {
            if (!Write(at, code))
            {
                return 1;
            }

            Console.WriteLine($"  {site.Name}: stub {code.Length} bytes @0x{at:X}");
        }

        var cursorAt = page + (ulong)CursorStubAt();
        var cursor = AiCursorStub(_base + BaseActivateRva, _base + LightSelectedRva);
        if (!Write(cursorAt, cursor))
        {
            return 1;
        }

        Console.WriteLine($"  AI cursor: stub {cursor.Length} bytes @0x{cursorAt:X}");

        var head = new byte[CommitStubsAt];
        BitConverter.GetBytes(CommitMagic).CopyTo(head, CommitMagicSlot);
        BitConverter.GetBytes(ring).CopyTo(head, CommitRingSlot);
        if (!Write(page, head) || !Write(ring, new byte[0x1000]) || !MakeExecutable(page, 0x1000))
        {
            return 1;
        }

        var jumps = new List<(ulong At, byte[] Patch, byte[] Stock)>();
        foreach (var (at, _, site) in stubs)
        {
            var entry = _base + site.Rva;
            var patch = new List<byte> { 0xE9 };
            patch.AddRange(Rel32(entry, at, 5));
            jumps.Add((entry, [.. patch], site.Stock));
        }

        var set = PatchAllOrNone(jumps, WriteCode);
        if (set != PatchSet.Whole)
        {
            Console.Error.WriteLine(set == PatchSet.RolledBack
                ? "  could not patch every commit entry; each one already written is put back."
                : "  could not patch every commit entry, and putting one back failed too; run --commit-ring --clear.");
            return 1;
        }

        foreach (var (at, _, site) in stubs)
        {
            Console.WriteLine($"  {site.Name} 0x{site.Rva:X}: jmp -> 0x{at:X}");
        }

        PatchAiActivate(page);
        return 0;
    }

    private static int ClearCommitRing()
    {
        var restored = 0;
        var failed = 0;
        var cursor = ClearAiActivate();
        if (cursor < 0)
        {
            failed++;
        }

        foreach (var s in CommitSites)
        {
            switch (ClearSite($"{s.Name} 0x{s.Rva:X}", s.Rva, s.Stock, sayStock: true))
            {
                case > 0:
                    restored++;
                    break;

                case < 0:
                    failed++;
                    break;
            }
        }

        Console.WriteLine($"  {restored} entr{(restored == 1 ? "y" : "ies")} put back; the pages stay mapped on purpose.");
        return failed == 0 ? 0 : 1;
    }

    internal const string CommitRingHeldLine = "  commit ring installed, holding until --play exits";

    private static int CommitRing(bool confirmed, bool clear, bool keep)
    {
        if (clear)
        {
            return ClearCommitRing();
        }

        var patched = CommitSitesPatched();
        if (patched == CommitSites.Length)
        {
            Console.WriteLine("  already installed, nothing to do.");
            var slot = _base + AiControllerVtableRva + ActivateSlot;
            var have = TryRead(slot, 8, out var read) ? read : null;
            var cursorPatched = AiActivateVerdict(have, _base + BaseActivateRva, _base, _moduleEnd) == AiActivateState.Ours;
            Console.WriteLine(cursorPatched
                ? "  AI cursor: the activate slot is patched"
                : "  AI cursor: the activate slot is not patched, so the idle cursor stays this run");
            return keep ? HoldCommitRing() : 0;
        }

        if (patched != 0)
        {
            Console.Error.WriteLine($"  {patched} of the {CommitSites.Length} entries carry a jump and the rest are " +
                                    "stock; run --commit-ring --clear first. Refusing.");
            return 2;
        }

        Console.WriteLine($"  all {CommitSites.Length} commit entries hold their stock bytes.");
        if (!confirmed)
        {
            Console.WriteLine("  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        var installed = InstallCommitRing(out _, out _);
        if (installed != 0)
        {
            return installed;
        }

        if (!keep)
        {
            Console.WriteLine("  installed. Release with: live-probe --commit-ring --clear");
            return 0;
        }

        return HoldCommitRing();
    }

    private static int HoldCommitRing()
    {
        Console.WriteLine(CommitRingHeldLine);
        while (!_parentGone)
        {
            Thread.Sleep(250);
        }

        Console.WriteLine("  --play is gone; putting the commit entries back");
        return ClearCommitRing();
    }

    private static int CommitLog(string[] args)
    {
        var clear = args.Contains("--clear");
        var seconds = 600;
        var flagAt = IndexOfArg(args, "--commit-log");
        if (flagAt >= 0 && flagAt + 1 < args.Length && int.TryParse(args[flagAt + 1], out var asked) && asked > 0)
        {
            seconds = asked;
        }

        if (clear)
        {
            return ClearCommitRing();
        }

        var installed = InstallCommitRing(out _, out var ring);
        if (installed != 0)
        {
            return installed;
        }

        var chain = Walk(report: false);
        if (chain is { } c)
        {
            var (obj, name) = KnownChainController(c);
            Console.WriteLine($"  the playing controller now: 0x{obj:X} {name}");
        }

        Console.WriteLine($"  logging commits for {seconds} s; Ctrl+C or --commit-log --clear restores the entries.");
        Console.WriteLine("  kind      seq  controller       unit             from    to      arg             whose  selected  unit: facing square health");

        var seen = 0u;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var names = new Dictionary<byte, string> { [1] = "activate", [2] = "move", [3] = "attack", [4] = "burst" };
        var later = new List<(DateTime Due, uint Seq, ulong Who)>();
        while (!_parentGone && DateTime.UtcNow < deadline)
        {
            var now = DateTime.UtcNow;
            foreach (var (_, dueSeq, who) in later.Where(l => l.Due <= now).ToList())
            {
                Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}   after {dueSeq,4}: unit 0x{who:X12} now {UnitState(who)}");
            }

            later.RemoveAll(l => l.Due <= now);

            var count = ReadU32(ring + CommitCountSlot);
            if (count > seen + CommitRecordCount)
            {
                Console.WriteLine($"  {count - seen - CommitRecordCount} record(s) overwritten before they were read");
                seen = count - CommitRecordCount;
            }

            while (seen < count)
            {
                var rec = ring + CommitRecordsSlot + (ulong)((seen % CommitRecordCount) * CommitRecordSize);
                var bytes = Read(rec, CommitRecordSize);
                if (!CommitRecordCurrent(bytes, seen))
                {
                    break;
                }

                var kind = BitConverter.ToUInt32(bytes, 0);
                var seq = BitConverter.ToUInt32(bytes, 4);
                var ctrl = BitConverter.ToUInt64(bytes, 8);
                var unit = BitConverter.ToUInt64(bytes, 0x10);
                var fx = BitConverter.ToInt32(bytes, 0x18);
                var fy = BitConverter.ToInt32(bytes, 0x1C);
                var tx = BitConverter.ToInt32(bytes, 0x20);
                var ty = BitConverter.ToInt32(bytes, 0x24);
                var arg = BitConverter.ToUInt64(bytes, 0x28);
                var vtable = BitConverter.ToUInt64(bytes, 0x30);
                var selX = BitConverter.ToInt32(bytes, 0x38);
                var selY = BitConverter.ToInt32(bytes, 0x3C);
                var who = kind is 1 or 4 ? arg : unit;
                var state = who == 0 ? "-" : UnitState(who);
                var kindName = names.TryGetValue((byte)kind, out var n) ? n : $"kind{kind}";
                Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} {kindName,-9}{seq,4}  0x{ctrl:X12}  0x{unit:X12}  ({fx},{fy})  ({tx},{ty})  0x{arg:X12}  {ControllerVtableName(vtable),-5}  ({selX},{selY})  {state}");
                if (who != 0)
                {
                    later.Add((DateTime.UtcNow.AddSeconds(1), seq, who));
                }

                seen++;
            }

            Thread.Sleep(40);
        }

        ClearCommitRing();
        Console.WriteLine($"  {seen} record(s) read; the four entries are stock again.");
        return 0;
    }

    internal static string CommitKindName(uint kind)
    {
        return kind switch
        {
            1 => "activate",
            2 => "move",
            3 => "attack",
            4 => "burst",
            _ => "?",
        };
    }

    private static uint _commitsEmitted;
    private static bool _commitsStarted;

    internal static uint CommitsLostSince(uint seen, bool started, uint written)
    {
        if (!started || written <= seen + CommitRecordCount)
        {
            return 0;
        }

        return written - seen - CommitRecordCount;
    }

    private static ulong _commitRing;

    private static string CommitsJson(ulong array, int count)
    {
        var live = FindCommitRing(out var unreadable);
        switch (StepCommitRing(_commitRing, live, unreadable))
        {
            case CommitRingStep.Wait:
                return "\"commits\":null";

            case CommitRingStep.Reset:
                if (live == 0 && _commitRing != 0)
                {
                    Console.Error.WriteLine("  the game's commit ring is no longer installed; turns fall back to the board reader.");
                }

                _commitRing = live;
                _commitsStarted = false;
                return "\"commits\":null";
        }

        if (!TryReadU32(_commitRing + CommitCountSlot, out var written))
        {
            return "\"commits\":null";
        }

        if (!_commitsStarted)
        {
            _commitsStarted = true;
            _commitsEmitted = written;
        }

        var lost = CommitsLostSince(_commitsEmitted, started: true, written);
        if (lost > 0)
        {
            _commitsEmitted = written - CommitRecordCount;
        }

        var records = new List<string>();
        while (_commitsEmitted < written)
        {
            var at = _commitRing + CommitRecordsSlot +
                     (ulong)((_commitsEmitted % CommitRecordCount) * CommitRecordSize);
            if (!TryRead(at, CommitRecordSize, out var bytes) || !CommitRecordCurrent(bytes, _commitsEmitted))
            {
                break;
            }

            var who = CommitRecordMachine(bytes);
            var idx = -1;
            for (var i = 0; i < count && i < 64; i++)
            {
                if (ReadPtr(array + (ulong)(i * 8)) == who)
                {
                    idx = i;
                    break;
                }
            }

            records.Add(CommitRecordJson(bytes, idx));
            _commitsEmitted++;
        }

        return $"\"commits\":{{\"count\":{written},\"lost\":{lost},\"records\":[{string.Join(",", records)}]}}";
    }

    internal static bool CommitRecordCurrent(byte[] record, uint index)
    {
        return record.Length >= 8 && BitConverter.ToUInt32(record, 0x04) == index + 1;
    }

    internal static ulong CommitRecordMachine(byte[] bytes)
    {
        var kind = BitConverter.ToUInt32(bytes, 0x00);
        return kind is 1 or 4 ? BitConverter.ToUInt64(bytes, 0x28) : BitConverter.ToUInt64(bytes, 0x10);
    }

    internal static string CommitRecordJson(byte[] bytes, int idx)
    {
        var kind = BitConverter.ToUInt32(bytes, 0x00);
        var seq = BitConverter.ToUInt32(bytes, 0x04);
        var vtable = BitConverter.ToUInt64(bytes, 0x30);
        return $"{{\"seq\":{seq},\"kind\":\"{CommitKindName(kind)}\"," +
               $"\"whose\":\"{ControllerVtableName(vtable)}\"," +
               $"\"unit\":{idx},\"ptr\":\"{CommitRecordMachine(bytes):X}\"," +
               $"\"fx\":{BitConverter.ToInt32(bytes, 0x18)},\"fy\":{BitConverter.ToInt32(bytes, 0x1C)}," +
               $"\"tx\":{BitConverter.ToInt32(bytes, 0x20)},\"ty\":{BitConverter.ToInt32(bytes, 0x24)}," +
               $"\"sx\":{BitConverter.ToInt32(bytes, 0x38)},\"sy\":{BitConverter.ToInt32(bytes, 0x3C)}}}";
    }

    private static string UnitState(ulong unit)
    {
        var packed = ReadByte(unit + 0x38);
        var facing = ReadByte(unit + 0x3B) & 3;
        var health = ReadByte(unit + 0x3A);
        return $"{facing} ({packed & 0xF},{packed >> 4}) {health}";
    }

    internal static string ControllerVtableName(ulong vtable)
    {
        if (vtable == 0)
        {
            return "?";
        }

        foreach (var (name, rva) in ControllerVtables)
        {
            if (vtable == _base + rva)
            {
                return name.StartsWith("Human", StringComparison.Ordinal) ? "human"
                     : name.StartsWith("AI", StringComparison.Ordinal) ? "ai"
                     : "base";
            }
        }

        return "?";
    }
}
