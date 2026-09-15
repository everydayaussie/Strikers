using System.Runtime.InteropServices;
using System.Text;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private static bool Write(ulong address, byte[] bytes)
    {
        if (WriteProcessMemory(_handle, (nint)address, bytes, bytes.Length, out var written) &&
            written == bytes.Length)
        {
            return true;
        }

        if (VirtualProtectEx(_handle, (nint)address, bytes.Length, PageExecuteReadWrite, out var old))
        {
            var ok = WriteProcessMemory(_handle, (nint)address, bytes, bytes.Length, out var second) &&
                     second == bytes.Length;
            VirtualProtectEx(_handle, (nint)address, bytes.Length, old, out _);
            if (ok)
            {
                return true;
            }
        }

        Console.Error.WriteLine($"  WriteProcessMemory failed at 0x{address:X} ({Marshal.GetLastWin32Error()}). " +
                                "Run as administrator.");
        return false;
    }

    private static int Nibble(int v)
    {
        return ((v & 0xF) ^ 8) - 8;
    }

    private static bool Sane(ulong p)
    {
        return p >= 0x10000 && p < 0x7FFFFFFFFFFF && (p & 3) == 0;
    }

    private static int IndexOfArg(string[] args, string name)
    {
        return Array.IndexOf(args, name);
    }

    internal static int? SeatArg(string[] args)
    {
        var at = IndexOfArg(args, "--player");
        if (at < 0)
        {
            return null;
        }

        if (at + 1 < args.Length && args[at + 1] is "0" or "1")
        {
            return args[at + 1][0] - '0';
        }

        return -1;
    }

    internal static string? PlacingRowsProblem(int rows, int height)
    {
        if (rows <= 0 || rows > height)
        {
            return $"the placing row count reads {rows} on a board {height} rows deep, so no square can be judged";
        }

        return null;
    }

    private static uint PeTimeDateStamp(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadUInt32();
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550)
            {
                return 0;
            }

            br.ReadUInt16();
            br.ReadUInt16();
            return br.ReadUInt32();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static ulong ParseAddr(string[] args, int index)
    {
        return index >= args.Length ? 0 : ParseAddr(args[index]);
    }

    private static ulong ParseAddr(string raw)
    {
        var hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private static void DumpHex(ulong address, int length)
    {
        var buf = Read(address, length);
        for (var i = 0; i < length; i += 16)
        {
            var sb = new StringBuilder($"    0x{address + (ulong)i:X}  +0x{i:X2}  ");
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                sb.Append($"{buf[i + j]:X2} ");
            }

            Console.WriteLine(sb.ToString());
        }
    }

    private static bool TryRead(ulong address, int length, out byte[] buf)
    {
        buf = new byte[length];
        return ReadProcessMemory(_handle, (nint)address, buf, length, out var got) && got == length;
    }

    private static byte[] Read(ulong address, int length)
    {
        TryRead(address, length, out var buf);
        return buf;
    }

    private static bool TryReadU32(ulong a, out uint value)
    {
        var ok = TryRead(a, 4, out var buf);
        value = BitConverter.ToUInt32(buf);
        return ok;
    }

    private static bool TryReadU64(ulong a, out ulong value)
    {
        var ok = TryRead(a, 8, out var buf);
        value = BitConverter.ToUInt64(buf);
        return ok;
    }

    private static bool TryReadByte(ulong a, out byte value)
    {
        var ok = TryRead(a, 1, out var buf);
        value = buf[0];
        return ok;
    }

    private static byte ReadByte(ulong a)
    {
        return Read(a, 1)[0];
    }

    private static uint ReadU32(ulong a)
    {
        return BitConverter.ToUInt32(Read(a, 4));
    }

    private static ulong ReadPtr(ulong a)
    {
        return BitConverter.ToUInt64(Read(a, 8));
    }
}
