namespace Strikers.App;

internal static class BackdropFile
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];

    private const string Stem = "backdrop";

    public static IEnumerable<string> Places()
    {
        return BackdropSource.Places(AppContext.BaseDirectory);
    }

    public static bool AnyBesideExe()
    {
        return PoolIn(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Count > 0;
    }

    public static string? Find()
    {
        foreach (var place in Places())
        {
            var pool = PoolIn(place);
            pool.Sort(StringComparer.OrdinalIgnoreCase);
            if (pool.Count > 0)
            {
                return pool[0];
            }
        }

        return null;
    }

    private static List<string> PoolIn(string place)
    {
        var pool = new List<string>();
        if (!Directory.Exists(place))
        {
            return pool;
        }

        foreach (var file in Directory.EnumerateFiles(place, Stem + "*"))
        {
            if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                continue;
            }

            var tail = Path.GetFileNameWithoutExtension(file)[Stem.Length..];
            if (tail.Length == 0 || tail.All(char.IsDigit))
            {
                pool.Add(file);
            }
        }

        return pool;
    }
}
