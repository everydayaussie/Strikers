namespace Strikers.Netplay;

internal static class Names
{
    public const int MaxLength = 8;

    private static bool Allowed(char c)
    {
        return c is >= 'A' and <= 'Z' or >= '0' and <= '9';
    }

    public static string Hex(string name)
    {
        return Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(name));
    }

    public static string Clean(string? raw)
    {
        var kept = new System.Text.StringBuilder();
        foreach (var c in (raw ?? "").Trim().ToUpperInvariant())
        {
            if (Allowed(c) && kept.Length < MaxLength)
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }

    public static string? Problem(string? raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "a name cannot be empty";
        }

        if (trimmed.Length > MaxLength)
        {
            return $"that name is {trimmed.Length} characters; the game's own name box holds {MaxLength}";
        }

        foreach (var c in trimmed.ToUpperInvariant())
        {
            if (!Allowed(c))
            {
                return "a name can hold letters and numbers only";
            }
        }

        return null;
    }

    public static string SetLine(string? name)
    {
        return $"  your name is set, {(name ?? "").Length} characters";
    }

    public static string? FromPeer(string? raw)
    {
        var cleaned = Clean(FrameLimits.Safe(raw, MaxLength * 4));
        return cleaned.Length == 0 ? null : cleaned;
    }
}
