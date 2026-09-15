using System.IO.Compression;

namespace Strikers.Core;

public static class Report
{
    public const string Folder = "reports";

    public const string RecordingsFolder = "recordings";
    public const string RecordingPrefix = "match-";

    public const int Keep = 5;

    private const string Prefix = "strikers-report-";

    public static string FileName(DateTime when)
    {
        return $"{Prefix}{when:yyyyMMdd-HHmmss}.zip";
    }

    public static List<string> Stale(IEnumerable<string> names, int keep)
    {
        var ours = names.Where(IsReport).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var spare = ours.Count - (keep - 1);
        if (spare <= 0)
        {
            return [];
        }

        return ours.Take(spare).ToList();
    }

    private static bool IsReport(string name)
    {
        return name.StartsWith(Prefix, StringComparison.Ordinal)
               && name.EndsWith(".zip", StringComparison.Ordinal);
    }

    public const long RecordingInZip = 8L * 1024 * 1024;

    public static long TailFrom(long length, long cap)
    {
        return length <= cap ? 0 : length - cap;
    }

    public static void Tidy(string folder)
    {
        var dir = Path.Combine(folder, Folder);
        if (Directory.Exists(dir))
        {
            Prune(dir, Keep + 1);
        }
    }

    private static void Prune(string dir, int keep = Keep)
    {
        var names = Directory.GetFiles(dir).Select(p => Path.GetFileName(p));
        foreach (var stale in Stale(names, keep))
        {
            try
            {
                File.Delete(Path.Combine(dir, stale));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string? NewestRecording(IEnumerable<string> names)
    {
        return names.Where(IsRecording)
                    .OrderByDescending(n => n, StringComparer.Ordinal)
                    .FirstOrDefault();
    }

    private static bool IsRecording(string name)
    {
        return name.StartsWith(RecordingPrefix, StringComparison.Ordinal)
               && name.EndsWith(".jsonl", StringComparison.Ordinal);
    }

    public const string NothingToReport = "no log and no recording to report yet";

    public static string? TrySave(string folder, DateTime when, out string? problem)
    {
        try
        {
            var zip = Save(folder, when);
            problem = zip is null ? NothingToReport : null;
            return zip;
        }
        catch (Exception e)
        {
            problem = Play.WithoutPath(e.Message, System.IO.Path.Combine(folder, FileName(when)));
            return null;
        }
    }

    public static string? Save(string folder, DateTime when)
    {
        var files = new List<string>();
        foreach (var name in new[] { MatchLog.Name, MatchLog.PreviousName })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        var recordings = Path.Combine(folder, RecordingsFolder);
        if (Directory.Exists(recordings))
        {
            var names = Directory.GetFiles(recordings).Select(p => Path.GetFileName(p));
            var newest = NewestRecording(names);
            if (newest is not null)
            {
                files.Add(Path.Combine(recordings, newest));
            }
        }

        if (files.Count == 0)
        {
            return null;
        }

        var dir = Path.Combine(folder, Folder);
        Directory.CreateDirectory(dir);
        Prune(dir);
        var zip = Path.Combine(dir, FileName(when));
        using (var stream = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                using (var into = entry.Open())
                using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var from = TailFrom(source.Length, RecordingInZip);
                    if (from > 0)
                    {
                        source.Seek(from, SeekOrigin.Begin);
                        while (source.ReadByte() is var b && b != -1 && b != '\n')
                        {
                        }
                    }

                    source.CopyTo(into);
                }
            }
        }

        return zip;
    }
}

public sealed class ReportOnce
{
    private int _attempt;
    private string? _said;

    public string For(int attempt, Func<string> save)
    {
        if (_said is not null && attempt == _attempt)
        {
            return _said;
        }

        _attempt = attempt;
        _said = save();
        return _said;
    }
}
