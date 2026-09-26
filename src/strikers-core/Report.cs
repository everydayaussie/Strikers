using System.IO.Compression;

namespace Strikers.Core;

public static class Report
{
    public const string Folder = "reports";

    public const string RecordingsFolder = "recordings";
    public const string RecordingPrefix = "match-";

    public const string OpponentRecordingPrefix = "opponent-";

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

    public const int FolderWindowWidth = 900;
    public const int FolderWindowHeight = 600;
    public const int FolderWindowMargin = 16;

    public static (int X, int Y, int Width, int Height) FolderWindowBounds(int left, int top, int right, int bottom,
                                                                           double scale)
    {
        var s = scale > 0 ? scale : 1.0;
        var margin = (int)Math.Round(FolderWindowMargin * s);
        var roomWidth = Math.Max(0, right - left - 2 * margin);
        var roomHeight = Math.Max(0, bottom - top - 2 * margin);
        var width = Math.Min((int)Math.Round(FolderWindowWidth * s), roomWidth);
        var height = Math.Min((int)Math.Round(FolderWindowHeight * s), roomHeight);

        return (right - margin - width, bottom - margin - height, width, height);
    }

    public static bool IsExplorerImage(string? image, string explorer)
    {
        return image is not null && string.Equals(image, explorer, StringComparison.OrdinalIgnoreCase);
    }

    public static nint? NewWindow(IReadOnlyCollection<nint> before, IReadOnlyCollection<nint> after)
    {
        var fresh = after.Where(w => !before.Contains(w)).Distinct().ToList();
        if (fresh.Count != 1)
        {
            return null;
        }

        return fresh[0];
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
        return Newest(names, RecordingPrefix);
    }

    public static string? NewestOpponentRecording(IEnumerable<string> names)
    {
        return Newest(names, OpponentRecordingPrefix);
    }

    private static string? Newest(IEnumerable<string> names, string prefix)
    {
        return names.Where(n => IsRecording(n, prefix))
                    .OrderByDescending(n => n, StringComparer.Ordinal)
                    .FirstOrDefault();
    }

    private static bool IsRecording(string name, string prefix)
    {
        return name.StartsWith(prefix, StringComparison.Ordinal)
               && name.EndsWith(".jsonl", StringComparison.Ordinal);
    }

    public const string OpponentStartSuffix = "-start.jsonl";

    public const string OpponentLogSuffix = ".log";

    public static (string? Ours, string? Theirs, string? TheirStart, string? TheirLog) Pair(IEnumerable<string> names)
    {
        var all = names.ToList();
        var ours = NewestRecording(all);
        if (ours is null)
        {
            return (null, NewestOpponentRecording(all), null, null);
        }

        var stamp = ours[RecordingPrefix.Length..^".jsonl".Length];
        var theirs = $"{OpponentRecordingPrefix}{stamp}.jsonl";
        var start = $"{OpponentRecordingPrefix}{stamp}{OpponentStartSuffix}";
        var log = $"{OpponentRecordingPrefix}{stamp}{OpponentLogSuffix}";
        return (ours, Held(all, theirs), Held(all, start), Held(all, log));
    }

    private static string? Held(List<string> names, string name)
    {
        return names.Contains(name, StringComparer.Ordinal) ? name : null;
    }

    public const string AboutName = "about.txt";

    public static string Seat(bool? hosted)
    {
        return hosted switch
        {
            true => "host",
            false => "joiner",
            null => "not in a match",
        };
    }

    public const string TheirWords =
        "the halt reason after 'the other PC stopped the match:' is the other PC's own words";

    private static string AsSent(string? name)
    {
        if (name is null)
        {
            return "none arrived";
        }

        return $"{name}, as they sent it, unverified";
    }

    public static string AboutText(ReportFacts facts, DateTimeOffset when, string? ours, string? theirs,
                                   string? theirStart, string? theirLog)
    {
        var lines = new List<string>
        {
            "Strikers report",
            $"saved: {when:yyyy-MM-dd HH:mm:ss}",
            $"why: {facts.Why}",
            $"version: {facts.Version ?? "not set"}",
            $"commit: {facts.Commit ?? "not stamped"}",
            $"Strikers: {facts.LauncherId}",
            $"netplay: {facts.NetplayId ?? "not read"}",
            $"live-probe: {facts.LiveProbeId ?? "not read"}",
            $"this PC: {Seat(facts.Hosted)}",
            $"this PC's recording: {ours ?? "none"}",
            $"the other PC's recording: {AsSent(theirs)}",
            $"the start of the other PC's recording: {AsSent(theirStart)}",
            $"the other PC's log: {AsSent(theirLog)}",
            TheirWords,
        };

        return string.Join("\n", lines) + "\n";
    }

    public static string ForTheRecord(string text)
    {
        var masked = text.Split('\n').Select(line => MatchDriver.ForTheRecord(line));
        return string.Join("\n", masked);
    }

    public static void Drop(string? zip)
    {
        if (zip is null || !IsReport(Path.GetFileName(zip)))
        {
            return;
        }

        try
        {
            File.Delete(zip);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public const string NothingToReport = "no log and no recording to report yet";

    public static string? TrySave(string folder, DateTime when, out string? problem, ReportFacts? facts = null)
    {
        try
        {
            var zip = Save(folder, when, facts);
            problem = zip is null ? NothingToReport : null;
            return zip;
        }
        catch (Exception e)
        {
            problem = Play.WithoutPath(e.Message, System.IO.Path.Combine(folder, FileName(when)));
            return null;
        }
    }

    internal static void CopyMasked(Stream source, Stream into)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        using var reader = new StreamReader(source, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(into, utf8, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            writer.Write(MatchDriver.ForTheRecord(line));
            writer.Write('\n');
        }
    }

    public static string? Save(string folder, DateTime when, ReportFacts? facts = null)
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

        string? ours = null;
        string? theirs = null;
        string? theirStart = null;
        string? theirLog = null;
        var recordings = Path.Combine(folder, RecordingsFolder);
        if (Directory.Exists(recordings))
        {
            var names = Directory.GetFiles(recordings).Select(p => Path.GetFileName(p));
            (ours, theirs, theirStart, theirLog) = Pair(names);
            foreach (var found in new[] { theirLog, ours, theirStart, theirs })
            {
                if (found is not null)
                {
                    files.Add(Path.Combine(recordings, found));
                }
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
            if (facts is not null)
            {
                var about = archive.CreateEntry(AboutName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(about.Open(), new System.Text.UTF8Encoding(false));
                var said = AboutText(facts, new DateTimeOffset(when), ours, theirs, theirStart, theirLog);
                writer.Write(ForTheRecord(said));
            }

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

                    CopyMasked(source, into);
                }
            }
        }

        return zip;
    }
}

public sealed record ReportFacts(string Why, string? Version, string? Commit, string LauncherId,
                                 string? NetplayId, string? LiveProbeId, bool? Hosted);

public sealed class StopReport
{
    private int _attempt = -1;

    public bool SavedAtStop { get; private set; }

    public bool SavedSecond { get; private set; }

    public bool TheirsLanded { get; private set; }

    public bool TheirLogLanded { get; private set; }

    private void StartAttempt(int attempt)
    {
        if (attempt == _attempt)
        {
            return;
        }

        _attempt = attempt;
        SavedAtStop = false;
        SavedSecond = false;
        TheirsLanded = false;
        TheirLogLanded = false;
    }

    public void NoteTheirRecording(int attempt)
    {
        StartAttempt(attempt);
        TheirsLanded = true;
    }

    public bool SaveAtStop(int attempt)
    {
        StartAttempt(attempt);
        if (SavedAtStop)
        {
            return false;
        }

        SavedAtStop = true;
        return true;
    }

    public bool SaveOnTheirLog(int attempt)
    {
        StartAttempt(attempt);
        var landedBefore = TheirLogLanded;
        TheirLogLanded = true;
        if (landedBefore || !SavedAtStop || SavedSecond)
        {
            return false;
        }

        SavedSecond = true;
        return true;
    }

    public bool SaveOnSettle(int attempt)
    {
        StartAttempt(attempt);
        if (!SavedAtStop || SavedSecond || !TheirsLanded)
        {
            return false;
        }

        SavedSecond = true;
        return true;
    }
}
