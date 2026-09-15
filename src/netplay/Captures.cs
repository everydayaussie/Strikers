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
            Prune(folder);
            Directory.CreateDirectory(folder);
            return System.IO.Path.Combine(folder, $"{Prefix}{now:yyyyMMdd-HHmmss}.jsonl");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"  recording off: {e.Message}");
            return null;
        }
    }

    public static List<string> Stale(IEnumerable<string> names, int keep)
    {
        var ours = names.Where(IsRecording).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var spare = ours.Count - (keep - 1);
        if (spare <= 0)
        {
            return [];
        }

        return ours.Take(spare).ToList();
    }

    private static bool IsRecording(string name)
    {
        return name.StartsWith(Prefix, StringComparison.Ordinal)
               && name.EndsWith(".jsonl", StringComparison.Ordinal);
    }

    private static void Prune(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        var names = Directory.GetFiles(dir).Select(p => System.IO.Path.GetFileName(p));
        foreach (var stale in Stale(names, Keep))
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
