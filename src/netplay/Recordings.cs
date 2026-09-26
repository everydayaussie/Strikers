namespace Strikers.Netplay;

internal sealed class IncomingFile
{
    private readonly List<byte> _bytes = [];
    private readonly string _what;
    private readonly int _maxParts;
    private readonly int _maxBytes;
    private int _expected = -1;
    private int _next;

    public IncomingFile(string what, int maxParts, int maxBytes)
    {
        _what = what;
        _maxParts = Math.Min(maxParts, FrameLimits.MaxRecordingParts);
        _maxBytes = Math.Min(maxBytes, FrameLimits.MaxRecordingBytes);
    }

    public string What
    {
        get
        {
            return _what;
        }
    }

    public bool Done { get; private set; }

    public bool Refused { get; private set; }

    public byte[]? Taken { get; private set; }

    public string? Unfinished()
    {
        if (Done)
        {
            return null;
        }

        if (Refused)
        {
            return $"{_what} was refused";
        }

        if (_expected < 0)
        {
            return $"{_what} did not arrive";
        }

        return $"{_what} did not arrive whole, {_next} of {_expected} part(s)";
    }

    public string? Offer(Frame f, bool halted)
    {
        if (Done || Refused)
        {
            return $"{_what} arrived a second time and was dropped";
        }

        if (!halted)
        {
            Refused = true;
            return $"{_what} arrived while the match was live";
        }

        if (f.Parts < 1 || f.Parts > _maxParts)
        {
            Refused = true;
            return $"{_what} claimed {f.Parts} parts";
        }

        if (_expected < 0)
        {
            _expected = f.Parts;
        }

        if (f.Parts != _expected || f.Part != _next)
        {
            Refused = true;
            return $"{_what} arrived out of order";
        }

        var data = f.Data ?? "";
        if (data.Length > FrameLimits.MaxRecordingPartChars)
        {
            Refused = true;
            return $"a part of {_what} was over the size a part may be";
        }

        byte[] piece;
        try
        {
            piece = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            Refused = true;
            return $"a part of {_what} was not readable";
        }

        if (_bytes.Count + piece.Length > _maxBytes)
        {
            Refused = true;
            return $"{_what} was over the size a file may be";
        }

        _bytes.AddRange(piece);
        _next++;

        if (_next < _expected)
        {
            return null;
        }

        Done = true;
        Taken = [.. _bytes];
        return null;
    }
}

internal static class Recordings
{
    public const string OpponentPrefix = "opponent-";

    public const string LogName = "strikers.log";

    private const string RecordingSuffix = ".jsonl";

    public static List<Frame> Parts(byte[] bytes, MsgKind kind = MsgKind.Recording,
                                    int maxParts = FrameLimits.MaxRecordingParts)
    {
        var frames = new List<Frame>();
        if (bytes.Length == 0)
        {
            return frames;
        }

        var text = Convert.ToBase64String(bytes);
        var parts = (text.Length + FrameLimits.MaxRecordingPartChars - 1) / FrameLimits.MaxRecordingPartChars;
        if (parts > maxParts || parts > FrameLimits.MaxRecordingParts)
        {
            return frames;
        }

        for (var i = 0; i < parts; i++)
        {
            var at = i * FrameLimits.MaxRecordingPartChars;
            var take = Math.Min(FrameLimits.MaxRecordingPartChars, text.Length - at);
            frames.Add(new Frame
            {
                Kind = kind,
                Part = i,
                Parts = parts,
                Data = text.Substring(at, take),
            });
        }

        return frames;
    }

    public static byte[]? Head(string path, int maxBytes)
    {
        var read = Read(path, fromEnd: false, maxBytes, out var whole);
        if (read is null)
        {
            return null;
        }

        if (whole)
        {
            return read;
        }

        var last = Array.LastIndexOf(read, (byte)'\n');
        if (last < 0)
        {
            return null;
        }

        return read[..(last + 1)];
    }

    public static byte[]? Tail(string path, int maxBytes)
    {
        var read = Read(path, fromEnd: true, maxBytes, out var whole);
        if (read is null)
        {
            return null;
        }

        if (whole)
        {
            return read;
        }

        var first = Array.IndexOf(read, (byte)'\n');
        if (first < 0 || first + 1 >= read.Length)
        {
            return null;
        }

        return read[(first + 1)..];
    }

    public static byte[]? LogTail(string? folder = null)
    {
        var dir = folder ?? AppContext.BaseDirectory;
        return Tail(System.IO.Path.Combine(dir, LogName), FrameLimits.MaxLogBytes);
    }

    private static byte[]? Read(string path, bool fromEnd, int maxBytes, out bool whole)
    {
        whole = false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || maxBytes <= 0)
            {
                return null;
            }

            whole = info.Length <= maxBytes;
            long at = 0;
            if (fromEnd && !whole)
            {
                at = info.Length - maxBytes;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            file.Seek(at, SeekOrigin.Begin);
            var want = (int)Math.Min(info.Length - at, maxBytes);
            var bytes = new byte[want];
            var got = file.Read(bytes, 0, want);
            if (got < want)
            {
                return bytes[..got];
            }

            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            whole = false;
            return null;
        }
    }

    public static byte[] Scrubbed(byte[] bytes)
    {
        var kept = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is (>= 0x20 and <= 0x7E) or 0x09 or 0x0A or 0x0D)
            {
                kept[i] = b;
            }
            else
            {
                kept[i] = 0x3F;
            }
        }

        return kept;
    }

    public static string Name(DateTime when)
    {
        return $"{OpponentPrefix}{when:yyyyMMdd-HHmmss}{RecordingSuffix}";
    }

    public static string NameFor(string? ourCapture, DateTime when)
    {
        return $"{OpponentPrefix}{Stamp(ourCapture, when)}{RecordingSuffix}";
    }

    public static string StartNameFor(string? ourCapture, DateTime when)
    {
        return $"{OpponentPrefix}{Stamp(ourCapture, when)}-start{RecordingSuffix}";
    }

    public static string LogNameFor(string? ourCapture, DateTime when)
    {
        return $"{OpponentPrefix}{Stamp(ourCapture, when)}.log";
    }

    private static string Stamp(string? ourCapture, DateTime when)
    {
        var ours = ourCapture is null ? "" : System.IO.Path.GetFileName(ourCapture);
        if (ours.Length > Captures.Prefix.Length + RecordingSuffix.Length
            && ours.StartsWith(Captures.Prefix, StringComparison.Ordinal)
            && ours.EndsWith(RecordingSuffix, StringComparison.Ordinal))
        {
            return ours[Captures.Prefix.Length..^RecordingSuffix.Length];
        }

        return $"{when:yyyyMMdd-HHmmss}";
    }

    public static string? Write(byte[] bytes, string name)
    {
        try
        {
            Captures.Prune(Captures.Dir(), OpponentPrefix, Captures.Family(name, OpponentPrefix));
            var path = Captures.Path(name);
            File.WriteAllBytes(path, Scrubbed(bytes));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
