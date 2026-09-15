using System.Runtime.InteropServices;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private const ulong GlobalAllocatorRva = 0x897A388;
    private const ulong AllocateVtableSlot = 0x88;
    private const ulong ArenaLowRva = 0x1FE0510;
    private const ulong ArenaHighRva = 0x1FE0518;

    private const int GameAllocResultSlot = 0x00;
    private const int GameAllocDoneSlot = 0x08;
    private const int GameAllocSizeSlot = 0x10;

    private static ulong _gameAllocCode;
    private static ulong _gameAllocResult;

    private static bool _gameAllocPoisoned;

    private const int GameAllocWaitMs = 5000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint handle, nint attributes, nint stackSize,
                                                 nint start, nint parameter, uint flags,
                                                 out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    internal static byte[] GameAllocStub(ulong allocatorSlot, ulong resultPage)
    {
        var code = new List<byte>();
        code.AddRange([0x48, 0x83, 0xEC, 0x28]);
        code.AddRange([0x48, 0xB9]);
        code.AddRange(BitConverter.GetBytes(allocatorSlot));
        code.AddRange([0x48, 0x8B, 0x09]);
        code.AddRange([0x31, 0xC0]);
        code.AddRange([0x48, 0x85, 0xC9]);
        code.AddRange([0x74, (byte)GameAllocStubJumpOver()]);
        code.AddRange([0x48, 0x8B, 0x01]);
        code.AddRange([0x48, 0xBA]);
        code.AddRange(BitConverter.GetBytes(resultPage + GameAllocSizeSlot));
        code.AddRange([0x8B, 0x12]);
        code.AddRange([0x41, 0xB8, 0x10, 0x00, 0x00, 0x00]);
        code.AddRange([0x45, 0x31, 0xC9]);
        code.AddRange([0xFF, 0x90]);
        code.AddRange(BitConverter.GetBytes((uint)AllocateVtableSlot));
        code.AddRange([0x48, 0xB9]);
        code.AddRange(BitConverter.GetBytes(resultPage));
        code.AddRange([0x48, 0x89, 0x01]);
        code.AddRange([0xC7, 0x41, 0x08, 0x01, 0x00, 0x00, 0x00]);
        code.AddRange([0x31, 0xC0]);
        code.AddRange([0x48, 0x83, 0xC4, 0x28]);
        code.AddRange([0xC3]);
        return [.. code];
    }

    internal static int GameAllocStubJumpOver()
    {
        return 3
             + 10
             + 2
             + 6
             + 3
             + 6;
    }

    internal static bool AllocatorCallable(ulong moduleBase, ulong moduleEnd, ulong vtable, ulong target)
    {
        if (moduleBase == 0 || moduleEnd <= moduleBase)
        {
            return false;
        }

        return vtable >= moduleBase && vtable < moduleEnd && target >= moduleBase && target < moduleEnd;
    }

    internal static bool InGameArena(ulong ptr, ulong low, ulong high)
    {
        if (ptr == 0 || low == 0 || high == 0 || low >= high)
        {
            return false;
        }

        return ptr >= low && ptr < high;
    }

    private static bool TryReadArena(out ulong low, out ulong high)
    {
        low = 0;
        high = 0;
        return TryReadU64(_base + ArenaLowRva, out low) && TryReadU64(_base + ArenaHighRva, out high)
               && low != 0 && high != 0 && low < high;
    }

    private static bool TryGameAllocate(uint size, out ulong pointer, out string why)
    {
        pointer = 0;
        why = "";

        if (size == 0 || size > 0x10000)
        {
            why = $"a size of {size} bytes is outside what a draft array can need (1 .. 0x10000)";
            return false;
        }

        if (!TryReadArena(out var low, out var high))
        {
            why = "the game's heap range bounds did not read, so a result could not be " +
                  "checked against it";
            return false;
        }

        if (!TryReadU64(_base + GlobalAllocatorRva, out var allocator))
        {
            why = "the game's allocator global did not read";
            return false;
        }

        if (allocator != 0 &&
            (!Sane(allocator) || !TryReadU64(allocator, out var allocatorVtable) ||
             !TryReadU64(allocatorVtable + AllocateVtableSlot, out var allocateAt) ||
             !AllocatorCallable(_base, _moduleEnd, allocatorVtable, allocateAt)))
        {
            why = "the game's allocator does not call into the game's own code, so this is not the game build " +
                  "the stub was made for, and nothing was called";
            return false;
        }

        if (_gameAllocPoisoned)
        {
            why = "an earlier allocator call timed out, so its thread may still store into these pages " +
                  "and they are not reused";
            return false;
        }

        if (_gameAllocCode == 0)
        {
            _gameAllocResult = (ulong)VirtualAllocEx(_handle, 0, 0x1000, 0x1000 | 0x2000, 0x04);
            var codePage = _gameAllocResult == 0
                ? 0
                : (ulong)VirtualAllocEx(_handle, 0, 0x1000, 0x1000 | 0x2000, 0x04);
            if (_gameAllocResult == 0 || codePage == 0)
            {
                why = $"VirtualAllocEx failed ({Marshal.GetLastWin32Error()})";
                return false;
            }

            var code = GameAllocStub(_base + GlobalAllocatorRva, _gameAllocResult);
            if (!Write(codePage, code) || !MakeExecutable(codePage, 0x1000))
            {
                why = "the stub could not be written into the game";
                return false;
            }

            _gameAllocCode = codePage;
        }

        if (!Write(_gameAllocResult, new byte[0x1000]) ||
            !Write(_gameAllocResult + GameAllocSizeSlot, BitConverter.GetBytes(size)))
        {
            why = "the request could not be written into the game";
            return false;
        }

        var thread = CreateRemoteThread(_handle, 0, 0, (nint)_gameAllocCode, 0, 0, out _);
        if (thread == 0)
        {
            why = $"CreateRemoteThread failed ({Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            const uint waitObject0 = 0;
            if (WaitForSingleObject(thread, GameAllocWaitMs) != waitObject0)
            {
                _gameAllocPoisoned = true;
                why = $"the allocator call did not finish inside {GameAllocWaitMs} ms; nothing was written " +
                      "and the pages are left mapped and unused because the thread may still run";
                return false;
            }
        }
        finally
        {
            CloseHandle(thread);
        }

        if (!TryReadU32(_gameAllocResult + GameAllocDoneSlot, out var done) || done != 1)
        {
            why = "the allocator call left no result, so the stub did not reach its last store";
            return false;
        }

        if (!TryReadU64(_gameAllocResult + GameAllocResultSlot, out var got) || got == 0)
        {
            why = "the game's allocator returned null";
            return false;
        }

        if (InGameArena(got, low, high))
        {
            why = $"the game's allocator returned 0x{got:X}, inside the arena 0x{low:X} .. 0x{high:X} " +
                  "that a different allocator owns, so the game's own free path would misroute it";
            return false;
        }

        pointer = got;
        return true;
    }

    private static int GameAlloc(string[] args, int sizeIndex)
    {
        if (sizeIndex >= args.Length)
        {
            Console.Error.WriteLine("--game-alloc <size in bytes, hex> [--yes]");
            return 1;
        }

        var size = (uint)ParseAddr(args, sizeIndex);
        Console.WriteLine($"\n  ask the GAME's own allocator for 0x{size:X} bytes, on a thread of ours " +
                          "that makes one virtual call and exits");
        if (!TryReadArena(out var low, out var high))
        {
            Console.Error.WriteLine("  the game's heap range bounds did not read.");
            return 1;
        }

        Console.WriteLine($"  heap range  0x{low:X} .. 0x{high:X}  ({(high - low) >> 30} GB reserved)");
        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, no thread created. Add --yes to apply.");
            return 0;
        }

        if (!TryGameAllocate(size, out var pointer, out var why))
        {
            Console.Error.WriteLine($"  refused: {why}.");
            return 1;
        }

        Console.WriteLine($"  pointer     0x{pointer:X}  outside the arena, so the game's own free path " +
                          "hands it back to the allocator that issued it");
        Console.WriteLine($"  first 0x20  {Convert.ToHexString(Read(pointer, 0x20))}");
        return 0;
    }
}
