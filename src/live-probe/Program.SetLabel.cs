using System.Text;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    internal const string StockSetLabel = "Beginner's Set";

    internal const int MaxArmyNameChars = 32;

    internal static readonly byte[] SetLabelTag = "STRKSET\0"u8.ToArray();

    internal static byte[] SetLabelTagPattern()
    {
        return [0, .. SetLabelTag];
    }

    internal const string ArmyNameHexFlag = "--army-name-hex";

    internal static string? ArmyNameArg(string[] args)
    {
        if (ArgAfter(args, ArmyNameHexFlag) is { } hex)
        {
            if (hex.Length > MaxArmyNameChars * 8 || hex.Length % 2 != 0)
            {
                return null;
            }

            try
            {
                return CleanArmyName(Encoding.UTF8.GetString(Convert.FromHexString(hex)));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return CleanArmyName(ArgAfter(args, "--army-name"));
    }

    internal static string? CleanArmyName(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var kept = new StringBuilder();
        foreach (var rune in raw.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune == Rune.ReplacementChar)
            {
                continue;
            }

            kept.Append(rune.ToString());
        }

        var cleaned = FirstChars(kept.ToString().Trim(), MaxArmyNameChars).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string FirstChars(string text, int chars)
    {
        var kept = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == chars)
            {
                break;
            }

            kept.Append(rune.ToString());
            count++;
        }

        return kept.ToString();
    }

    internal static string CutToBytes(string text, int maxBytes, out int bytes)
    {
        var kept = new StringBuilder();
        bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                break;
            }

            kept.Append(rune.ToString());
            bytes += size;
        }

        return kept.ToString();
    }

    internal static (byte[] Bytes, int TextLength, bool Tagged, bool Cut) SetLabelBytes(string name, int room, int oldLength)
    {
        var whole = Encoding.UTF8.GetByteCount(name);
        var tagged = whole + SetLabelTag.Length <= room;
        var text = CutToBytes(name, tagged ? room - SetLabelTag.Length : room, out var textLength);
        var used = textLength + 1 + (tagged ? SetLabelTag.Length : 0);
        var bytes = new byte[Math.Max(used, Math.Min(oldLength + 1 + SetLabelTag.Length, room + 1))];
        Encoding.UTF8.GetBytes(text, bytes);
        if (tagged)
        {
            SetLabelTag.CopyTo(bytes, textLength + 1);
        }

        return (bytes, textLength, tagged, textLength < whole);
    }

    internal static int? TaggedTextLength(byte[] before)
    {
        for (var length = 0; length + 16 <= before.Length; length++)
        {
            var header = before[(before.Length - length - 16)..(before.Length - length)];
            if (RoomFromHeader(header, length) is not { } room || room < length + SetLabelTag.Length)
            {
                continue;
            }

            var text = before.AsSpan(before.Length - length);
            if (text.IndexOf((byte)0) >= 0)
            {
                continue;
            }

            return length;
        }

        return null;
    }

    internal static byte[] SetLabelRestoreBytes(int oursLength)
    {
        var stock = Terminated(StockSetLabel);
        var bytes = new byte[Math.Max(stock.Length, oursLength)];
        stock.CopyTo(bytes, 0);
        return bytes;
    }

    internal sealed record SetLabelSpot(ulong Text, int Length, int Room, bool Drawn = false);

    internal const int TextRunTextPointer = 0x30;
    internal const int TextRunFontSize = 0x50;

    internal static bool LooksLikeTextRun(byte[] run)
    {
        if (run.Length < TextRunFontSize + 4)
        {
            return false;
        }

        var alpha = BitConverter.ToSingle(run, 12);
        var size = BitConverter.ToSingle(run, TextRunFontSize);
        return alpha is > 0f and <= 1f && size is >= 6f and <= 200f;
    }

    internal static List<SetLabelSpot> MarkDrawn(IReadOnlyList<SetLabelSpot> spots, IReadOnlyList<List<ulong>> pointers,
                                                 Func<ulong, byte[]?> readRun)
    {
        var marked = new List<SetLabelSpot>();
        for (var i = 0; i < spots.Count; i++)
        {
            var drawn = i < pointers.Count &&
                        pointers[i].Any(p => readRun(p - TextRunTextPointer) is { } run && LooksLikeTextRun(run));
            marked.Add(spots[i] with { Drawn = drawn });
        }

        return marked;
    }

    internal static byte[] TerminatedUtf8(string text)
    {
        return [.. Encoding.UTF8.GetBytes(text), 0];
    }

    internal static bool SetLabelSweepDue(bool lost, TimeSpan lostFor, TimeSpan sinceSweep, bool sourceMoved, bool labelMissing)
    {
        if (sourceMoved)
        {
            return true;
        }

        if (!lost && labelMissing && sinceSweep >= NamesSlowRetry)
        {
            return true;
        }

        return NamesSweepDue(lost, lostFor, sinceSweep);
    }

    internal static bool SourceRefsMoved(IReadOnlyDictionary<ulong, uint> seen, Func<ulong, uint?> refcountAt)
    {
        return seen.Any(s => refcountAt(s.Key) is { } now && now != s.Value);
    }

    private static byte[]? ReadTextRun(ulong run)
    {
        return TryRead(run, TextRunFontSize + 4, out var bytes) ? bytes : null;
    }

    private const int TagLookBehind = 64;

    private static List<SetLabelSpot> FindSetLabels(string? ourSourceText = null, bool verbose = false)
    {
        var regions = ScanRegions(heapOnly: true);
        List<byte[]> patterns = [LabelPattern(StockSetLabel), SetLabelTagPattern()];
        if (ourSourceText is not null && ourSourceText != StockSetLabel)
        {
            patterns.Add(TerminatedUtf8(ourSourceText));
        }

        var scan = ScanForMany(patterns, 16, regions);
        var spots = new List<SetLabelSpot>();

        void AddByText(IEnumerable<ulong> hits, int length)
        {
            foreach (var at in hits)
            {
                if (TryRead(at - 16, 16, out var header) && LiveLabelHeader(header, length) &&
                    RoomFromHeader(header, length) is { } room && room >= StockSetLabel.Length &&
                    spots.All(s => s.Text != at))
                {
                    spots.Add(new SetLabelSpot(at, length, room));
                }
            }
        }

        AddByText(scan[0], StockSetLabel.Length);
        if (scan.Length > 2)
        {
            AddByText(scan[2], Encoding.UTF8.GetByteCount(ourSourceText!));
        }

        foreach (var end in scan[1])
        {
            if (!TryRead(end - TagLookBehind, TagLookBehind, out var before) || TaggedTextLength(before) is not { } length)
            {
                continue;
            }

            var header = before[(TagLookBehind - length - 16)..(TagLookBehind - length)];
            var text = end - (ulong)length;
            if (LiveLabelHeader(header, length) && RoomFromHeader(header, length) is { } room &&
                room >= StockSetLabel.Length && spots.All(s => s.Text != text))
            {
                spots.Add(new SetLabelSpot(text, length, room));
            }
        }

        if (spots.Count == 0)
        {
            return spots;
        }

        var pointers = ScanForMany([.. spots.Select(s => BitConverter.GetBytes(s.Text))], 4, regions);
        if (verbose)
        {
            for (var i = 0; i < spots.Count; i++)
            {
                var from = string.Join(", ", pointers[i].Select(p =>
                    $"0x{p:X}{(ReadTextRun(p - TextRunTextPointer) is { } run && LooksLikeTextRun(run) ? " (text run)" : "")}"));
                Console.WriteLine($"  candidate 0x{spots[i].Text:X}  length {spots[i].Length}  room {spots[i].Room}  " +
                                  $"pointed at from: {(from.Length == 0 ? "nothing" : from)}");
            }
        }

        return MarkDrawn(spots, pointers, ReadTextRun);
    }

    internal sealed record SetLabelWrite(ulong Text, byte[] Ours, int TextLength, bool Drawn, string Shown);

    internal static bool LiveLabelHeader(byte[]? header, int length)
    {
        return header is { Length: 16 } && BitConverter.ToUInt32(header, 4) == 0xFFFFFFFF &&
               RoomFromHeader(header, length) is not null;
    }

    private static bool LiveHeader(ulong text, int length)
    {
        return TryRead(text - 16, 16, out var header) && LiveLabelHeader(header, length);
    }

    private sealed class SetLabelHold(string name)
    {
        private readonly List<SetLabelWrite> held = [];
        private readonly Dictionary<ulong, uint> sourceRefs = [];
        private string? sourceText;
        private DateTime lastSweep = DateTime.MinValue;
        private DateTime lostAt = DateTime.UtcNow - NamesSettle;
        private bool toldDrawn;
        private bool toldSource;

        public void Tick(DateTime now)
        {
            var lost = held.Count == 0 || held.Any(h => !StillOurs(h));
            if (!lost)
            {
                lostAt = DateTime.MinValue;
            }
            else if (lostAt == DateTime.MinValue)
            {
                lostAt = now;
            }

            var moved = SourceRefsMoved(sourceRefs, RefcountAt);
            if (!SetLabelSweepDue(lost, now - lostAt, now - lastSweep, moved, !held.Any(h => h.Drawn)))
            {
                return;
            }

            lastSweep = now;
            held.RemoveAll(h => !StillOurs(h));
            foreach (var spot in FindSetLabels(sourceText))
            {
                if (held.Any(h => h.Text == spot.Text))
                {
                    continue;
                }

                if (WriteOne(spot) is not { } wrote)
                {
                    continue;
                }

                held.Add(wrote);
                if (wrote.Drawn)
                {
                    Console.WriteLine(toldDrawn
                        ? $"  your army's name written again on the set screen at 0x{spot.Text:X}"
                        : $"  your army's name is on the set screen (0x{spot.Text:X})");
                    toldDrawn = true;
                }
                else
                {
                    sourceText = wrote.Shown;
                    if (!toldSource)
                    {
                        Console.WriteLine($"  your army's name is ready for the set screen (0x{spot.Text:X})");
                        toldSource = true;
                    }
                }
            }

            sourceRefs.Clear();
            foreach (var h in held.Where(h => !h.Drawn))
            {
                if (RefcountAt(h.Text) is { } refs)
                {
                    sourceRefs[h.Text] = refs;
                }
            }
        }

        private static uint? RefcountAt(ulong text)
        {
            return TryReadU32(text - 16, out var refs) ? refs : null;
        }

        private SetLabelWrite? WriteOne(SetLabelSpot spot)
        {
            var (bytes, textLength, _, cut) = SetLabelBytes(name, spot.Room, spot.Length);
            if (cut && spot.Drawn)
            {
                Console.WriteLine($"  note: the set screen's label holds {spot.Room} bytes, so your army's name is cut to fit");
            }

            if (!LiveHeader(spot.Text, spot.Length) || !TryRead(spot.Text, bytes.Length, out var now))
            {
                return null;
            }

            if (!now.SequenceEqual(bytes) && !(Write(spot.Text, bytes) && Write(spot.Text - 8, BitConverter.GetBytes((uint)textLength))))
            {
                return null;
            }

            var shown = Encoding.UTF8.GetString(bytes, 0, textLength);
            return new SetLabelWrite(spot.Text, bytes, textLength, spot.Drawn, shown);
        }

        public void Restore()
        {
            var restored = 0;
            foreach (var h in held)
            {
                if (!StillOurs(h))
                {
                    continue;
                }

                if (Write(h.Text, SetLabelRestoreBytes(h.Ours.Length)) &&
                    Write(h.Text - 8, BitConverter.GetBytes((uint)StockSetLabel.Length)))
                {
                    restored++;
                }
            }

            held.Clear();
            if (restored > 0)
            {
                Console.WriteLine($"  the set screen's label is back to \"{StockSetLabel}\"");
            }
        }

        private static bool StillOurs(SetLabelWrite h)
        {
            return LiveHeader(h.Text, h.TextLength) && TryRead(h.Text, h.Ours.Length, out var now) &&
                   now.SequenceEqual(h.Ours);
        }
    }
}
