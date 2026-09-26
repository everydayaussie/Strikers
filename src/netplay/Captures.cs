namespace Strikers.Netplay;

internal static class Captures
{
    public const string Folder = "recordings";

    public const string Prefix = "match-";
    public const int Keep = 5;

    public static string Dir()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, Folder);
    }

    public static string Path(string name)
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, name);
    }

    public static string? NewRecording(DateTime now, string? dir = null)
    {
        try
        {
            var folder = dir ?? Dir();
            Prune(folder, Prefix);
            Directory.CreateDirectory(folder);
            return System.IO.Path.Combine(folder, $"{Prefix}{now:yyyyMMdd-HHmmss}.jsonl");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"  recording off ({e.GetType().Name})");
            return null;
        }
    }

    public static List<string> Stale(IEnumerable<string> names, int keep, string prefix = Prefix, string? family = null)
    {
        var families = names.Where(n => IsRecording(n, prefix))
                            .GroupBy(n => Family(n, prefix))
                            .OrderBy(g => g.Key, StringComparer.Ordinal)
                            .ToList();
        var room = family is not null && families.Any(g => g.Key == family) ? keep : keep - 1;
        var spare = families.Count - room;
        if (spare <= 0)
        {
            return [];
        }

        return families.Take(spare).SelectMany(g => g).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static readonly string[] Suffixes = ["-start.jsonl", ".jsonl", ".log"];

    private static bool IsRecording(string name, string prefix)
    {
        return name.StartsWith(prefix, StringComparison.Ordinal)
               && Suffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
    }

    public static string Family(string name, string prefix)
    {
        var rest = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        foreach (var suffix in Suffixes)
        {
            if (rest.EndsWith(suffix, StringComparison.Ordinal))
            {
                return rest[..^suffix.Length];
            }
        }

        return rest;
    }

    internal static void Prune(string dir, string prefix, string? family = null)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        var names = Directory.GetFiles(dir).Select(p => System.IO.Path.GetFileName(p));
        foreach (var stale in Stale(names, Keep, prefix, family))
        {
            try
            {
                File.Delete(System.IO.Path.Combine(dir, stale));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
