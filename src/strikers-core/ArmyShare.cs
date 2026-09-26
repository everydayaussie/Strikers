namespace Strikers.Core;

public static class ArmyShare
{
    public const string SharePrefix = "SA-";

    private const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    private const int CheckSpan = 36 * 36 * 36;

    private const int HeadSpan = 36 * 36;

    private const int FingerprintSpan = HeadSpan / Play.MaxArmy;

    public const int MaxRoster = 64;

    public static int MaxCodeLength
    {
        get
        {
            return SharePrefix.Length + 2 + BodyLength(Play.MaxArmy) + 3;
        }
    }

    public const int MaxShareLength = 64;

    public const string NotACode = "That is not an army code.";

    public const string Damaged = "That army code is damaged. Copy the whole code again.";

    public static IReadOnlyList<string> Canonical(IEnumerable<string> rosterNames)
    {
        return [.. rosterNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];
    }

    public static string? ToShareString(IEnumerable<string> machineNames, IEnumerable<string> rosterNames)
    {
        var roster = Canonical(rosterNames);
        if (roster.Count is 0 or > MaxRoster)
        {
            return null;
        }

        var picks = new List<int>();
        foreach (var name in machineNames)
        {
            var at = IndexOf(roster, name);
            if (at < 0)
            {
                return null;
            }

            picks.Add(at);
        }

        if (picks.Count < 1 || picks.Count > Play.MaxArmy)
        {
            return null;
        }

        var head = ((picks.Count - 1) * FingerprintSpan) + Fingerprint(roster);
        var body = Pack(picks);
        var payload = ToBase36(head, 2) + body;
        return SharePrefix + payload + ToBase36((int)(Hash(payload) % CheckSpan), 3);
    }

    public static IReadOnlyList<string>? FromShareString(string? text, IEnumerable<string> rosterNames,
                                                        out string? problem)
    {
        problem = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            problem = "Paste an army code first.";
            return null;
        }

        if (trimmed.Length > MaxShareLength)
        {
            problem = NotACode;
            return null;
        }

        if (!trimmed.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase))
        {
            problem = NotACode;
            return null;
        }

        var body = trimmed[SharePrefix.Length..].ToLowerInvariant();
        if (body.Length <= 5)
        {
            problem = Damaged;
            return null;
        }

        var payload = body[..^3];
        if (!string.Equals(body[^3..], ToBase36((int)(Hash(payload) % CheckSpan), 3), StringComparison.Ordinal))
        {
            problem = Damaged;
            return null;
        }

        if (!FromBase36(payload[..2], out var head))
        {
            problem = Damaged;
            return null;
        }

        var count = (head / FingerprintSpan) + 1;
        if (count < 1 || count > Play.MaxArmy)
        {
            problem = $"That army code has {count} machines. The most is {Play.MaxArmy}.";
            return null;
        }

        var roster = Canonical(rosterNames);
        if (roster.Count is 0 or > MaxRoster)
        {
            problem = "The machine list is still loading. Try again in a moment.";
            return null;
        }

        if (head % FingerprintSpan != Fingerprint(roster))
        {
            problem = "That army code comes from a save with different machines.";
            return null;
        }

        var digits = BodyLength(count);
        if (payload.Length != 2 + digits)
        {
            problem = Damaged;
            return null;
        }

        if (!Unpack(payload[2..], count, out var picks))
        {
            problem = Damaged;
            return null;
        }

        var names = new List<string>();
        foreach (var at in picks)
        {
            if (at >= roster.Count)
            {
                problem = "That army has a machine this save does not have.";
                return null;
            }

            names.Add(roster[at]);
        }

        return names;
    }

    public static int BodyLength(int count)
    {
        var span = System.Numerics.BigInteger.Pow(MaxRoster, count);
        var digits = 1;
        var reach = new System.Numerics.BigInteger(36);
        while (reach < span)
        {
            reach *= 36;
            digits++;
        }

        return digits;
    }

    private static int IndexOf(IReadOnlyList<string> roster, string name)
    {
        for (var i = 0; i < roster.Count; i++)
        {
            if (string.Equals(roster[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Pack(IReadOnlyList<int> picks)
    {
        var value = System.Numerics.BigInteger.Zero;
        foreach (var at in picks)
        {
            value = (value * MaxRoster) + at;
        }

        var digits = BodyLength(picks.Count);
        var text = new char[digits];
        for (var i = digits - 1; i >= 0; i--)
        {
            text[i] = Base36[(int)(value % 36)];
            value /= 36;
        }

        return new string(text);
    }

    private static bool Unpack(string text, int count, out IReadOnlyList<int> picks)
    {
        picks = [];
        var value = System.Numerics.BigInteger.Zero;
        foreach (var c in text)
        {
            var digit = Base36.IndexOf(c);
            if (digit < 0)
            {
                return false;
            }

            value = (value * 36) + digit;
        }

        var read = new int[count];
        for (var i = count - 1; i >= 0; i--)
        {
            read[i] = (int)(value % MaxRoster);
            value /= MaxRoster;
        }

        if (value != System.Numerics.BigInteger.Zero)
        {
            return false;
        }

        picks = read;
        return true;
    }

    private static int Fingerprint(IReadOnlyList<string> roster)
    {
        return (int)(Hash(string.Join('\n', roster)) % FingerprintSpan);
    }

    private static string ToBase36(int value, int width)
    {
        var text = new char[width];
        for (var i = width - 1; i >= 0; i--)
        {
            text[i] = Base36[value % 36];
            value /= 36;
        }

        return new string(text);
    }

    private static bool FromBase36(string text, out int value)
    {
        value = 0;
        foreach (var c in text)
        {
            var digit = Base36.IndexOf(c);
            if (digit < 0)
            {
                value = 0;
                return false;
            }

            value = (value * 36) + digit;
        }

        return true;
    }

    private static uint Hash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash;
    }
}
