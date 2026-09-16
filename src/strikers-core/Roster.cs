using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strikers.Core;

public static class NetplayTool
{
    public static string? Run(string netplay, string[] args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(netplay)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEndAsync();

            var errors = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                p.Kill(entireProcessTree: false);
                return null;
            }

            _ = errors.Result;
            return output.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or AggregateException)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RepoBuildFolder = new(
        @"[\\/]src[\\/][A-Za-z0-9-]+[\\/]bin[\\/](Release|Debug)[\\/]net\d+\.\d+[\\/]?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool InRepoBuild(string folder)
    {
        return RepoBuildFolder.IsMatch(folder);
    }

    public static string Netplay()
    {
        return Netplay(AppContext.BaseDirectory, File.Exists);
    }

    internal static string Netplay(string folder, Func<string, bool> exists)
    {
        var local = Path.Combine(folder, "netplay.exe");
        if (exists(local) || !InRepoBuild(folder))
        {
            return local;
        }

        return Path.GetFullPath(Path.Combine(folder, @"..\..\..\..\netplay\bin\Release\net10.0\netplay.exe"));
    }

    public static string? LiveProbe()
    {
        return LiveProbe(AppContext.BaseDirectory, File.Exists);
    }

    internal static string? LiveProbe(string folder, Func<string, bool> exists)
    {
        var local = Path.Combine(folder, "live-probe.exe");
        if (exists(local))
        {
            return local;
        }

        if (!InRepoBuild(folder))
        {
            return null;
        }

        var built = Path.GetFullPath(Path.Combine(folder, @"..\..\..\..\live-probe\bin\Release\net10.0\live-probe.exe"));
        return exists(built) ? built : null;
    }

    public const string DraftStopEvent = @"Local\Strikers-draft-guard-stop";

    public static bool StopDraftGuard()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!EventWaitHandle.TryOpenExisting(DraftStopEvent, out var stop))
        {
            return false;
        }

        using (stop)
        {
            return stop.Set();
        }
    }

    public static string WithoutName(string name, string? value)
    {
        if (value is null or "")
        {
            return "not found";
        }

        return value.StartsWith(name + " ", StringComparison.Ordinal)
            ? value[(name.Length + 1)..].TrimStart()
            : value;
    }
}

public static class ChallengeBridge
{
    public sealed record Challenge(string Uuid, int Slots, string Name);

    public static List<Challenge> Parse(string text)
    {
        var found = new List<Challenge>();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[1], out var slots))
            {
                continue;
            }

            found.Add(new Challenge(parts[0], slots, parts[2].Trim()));
        }

        return found;
    }

    public static List<Challenge> All(string netplay)
    {
        var text = NetplayTool.Run(netplay, ["--list-challenges"]);
        return text is null ? [] : Parse(text);
    }
}

public static class MachineBridge
{
    public sealed record Machine(
        string Uuid, string Name, int Cost, int Health, int Move, int Range, int Power,
        string Pattern, string Ability, string Problem)
    {
        public bool Playable
        {
            get
            {
                return Problem.Length == 0;
            }
        }
    }

    public static List<Machine> Parse(string text)
    {
        var found = new List<Machine>();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            if (f.Length < 9)
            {
                continue;
            }

            if (!int.TryParse(f[2], out var cost) || !int.TryParse(f[3], out var health) ||
                !int.TryParse(f[4], out var move) || !int.TryParse(f[5], out var range) ||
                !int.TryParse(f[6], out var power))
            {
                continue;
            }

            found.Add(new Machine(f[0], f[1], cost, health, move, range, power, f[7], f[8],
                                  f.Length > 9 ? f[9].Trim() : ""));
        }

        return found;
    }

    public static List<Machine> All(string netplay)
    {
        var text = NetplayTool.Run(netplay, ["--list-machines"]);
        return text is null ? [] : Parse(text);
    }
}

public sealed class Army
{
    public const int MaxName = 24;

    public const int MaxStoredName = MaxName + 8;

    public static string CleanName(string? raw, int cap = MaxName)
    {
        var kept = (raw ?? "").Trim();
        return kept.Length <= cap ? kept : kept[..cap];
    }

    [JsonPropertyName("name")] public string Name { get; set; } = "Untitled";

    [JsonPropertyName("machines")] public List<string> Machines { get; set; } = [];

    public int Size
    {
        get
        {
            return Machines.Count;
        }
    }
}

public sealed class Settings
{
    public const int MaxName = 8;

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("offerBackdrop")] public bool OfferBackdrop { get; set; } = true;

    private static string Path()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
    }

    private bool _fromUnreadableFile;

    public static Settings Read()
    {
        return Read(Path());
    }

    internal static Settings Read(string path)
    {
        if (!File.Exists(path))
        {
            return new Settings();
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), StoreJson.Default.Settings) ?? new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Settings { _fromUnreadableFile = true };
        }
    }

    public string? Write()
    {
        return Write(Path());
    }

    internal string? Write(string path)
    {
        if (_fromUnreadableFile)
        {
            return Play.UnreadableFile("settings.json");
        }

        try
        {
            Play.WriteWhole(path, JsonSerializer.Serialize(this, StoreJson.Default.Settings));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Play.WithoutPath($"could not write {path}: {e.Message}", path);
        }
    }

    public static string CleanName(string? raw)
    {
        var kept = new System.Text.StringBuilder();
        foreach (var c in (raw ?? "").Trim().ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' && kept.Length < MaxName)
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }
}

public static class Names
{
    public static string Free(string wanted, IEnumerable<string> existing, int cap = 0)
    {
        var taken = existing.ToList();

        bool Used(string name)
        {
            return taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
        }

        string Fit(string label)
        {
            if (cap <= 0 || wanted.Length + label.Length <= cap)
            {
                return wanted + label;
            }

            var keep = Math.Max(1, cap - label.Length);
            return wanted[..keep] + label;
        }

        if (!Used(wanted))
        {
            return wanted;
        }

        var candidate = Fit(" copy");
        var n = 2;
        while (Used(candidate))
        {
            candidate = Fit($" copy {n}");
            n++;
        }

        return candidate;
    }
}

public static class ArmyStore
{
    public static string Path()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "armies.json");
    }

    public static List<Army> ReadAll()
    {
        return ReadAll(Path(), out _);
    }

    internal static List<Army> ReadAll(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Normalise(JsonSerializer.Deserialize(File.ReadAllText(path), StoreJson.Default.ListArmy));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = Play.UnreadableFile("armies.json");
            return [];
        }
    }

    public static List<Army> Normalise(List<Army>? read)
    {
        var armies = new List<Army>();
        foreach (var army in read ?? [])
        {
            if (army == null)
            {
                continue;
            }

            army.Name = Army.CleanName(army.Name, Army.MaxStoredName);
            if (army.Name.Length == 0)
            {
                army.Name = "Untitled";
            }

            army.Machines = [.. (army.Machines ?? []).Where(u => !string.IsNullOrEmpty(u)).Take(Play.MaxArmy)];
            armies.Add(army);
            if (armies.Count >= MaxArmies)
            {
                break;
            }
        }

        return armies;
    }

    public static string? WriteAll(List<Army> armies)
    {
        return WriteAll(Path(), armies);
    }

    internal static string? WriteAll(string path, List<Army> armies)
    {
        try
        {
            Play.WriteWhole(path, JsonSerializer.Serialize(armies, StoreJson.Default.ListArmy));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Play.WithoutPath($"could not write {path}: {e.Message}", path);
        }
    }

    public const int MaxArmies = 32;

    public static bool HasRoom(int saved, bool replacing)
    {
        return replacing || saved < MaxArmies;
    }

    public static string FreeName(string wanted, IEnumerable<Army> existing)
    {
        return Names.Free(wanted, existing.Select(a => a.Name), Army.MaxStoredName);
    }

    public static string? Save(Army army)
    {
        return Save(Path(), army);
    }

    internal static string? Save(string path, Army army)
    {
        army.Name = Army.CleanName(army.Name, Army.MaxStoredName);

        var all = ReadAll(path, out var unreadable);
        if (unreadable is not null)
        {
            return unreadable;
        }

        var replacing = all.RemoveAll(a => string.Equals(a.Name, army.Name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!HasRoom(all.Count, replacing))
        {
            return $"{MaxArmies} saved armies is the limit. Delete one to save another.";
        }

        all.Add(army);
        return WriteAll(path, all);
    }

    public static string? Delete(string name)
    {
        return Delete(Path(), name);
    }

    internal static string? Delete(string path, string name)
    {
        var all = ReadAll(path, out var unreadable);
        if (unreadable is not null)
        {
            return unreadable;
        }

        all.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        return WriteAll(path, all);
    }
}
