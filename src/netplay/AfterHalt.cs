using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Strikers.Netplay;

internal static class AfterHalt
{
    internal static readonly TimeSpan Check = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan Beat = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan BeatFor = TimeSpan.FromMinutes(10);
    internal const int GameLogTailBytes = 16 * 1024;
    internal const int GameLogLinesKept = 8;
    internal const int GameLogLineWidth = 200;

    internal const string GameProcess = "HorizonForbiddenWest";
    private const string Helper = "live-probe";

    private static int _watching;

    private static readonly Regex LongNumber = new(@"\d{10,}", RegexOptions.Compiled);

    public static void Start()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1)
        {
            return;
        }

        _ = Task.Run(Watch);
    }

    private static async Task Watch()
    {
        var started = DateTime.UtcNow;
        var game = FindGame();
        var lastBeat = TimeSpan.Zero;
        while (true)
        {
            var since = DateTime.UtcNow - started;
            if (game is null || Exited(game))
            {
                var code = game is null ? null : ExitCode(game);
                foreach (var line in ExitLines(since, code, ReadGameLogTail()))
                {
                    Console.WriteLine(line);
                }

                return;
            }

            if (BeatDue(since, lastBeat))
            {
                lastBeat = since;
                if (WorkingSet(game) is { } workingSet)
                {
                    Console.WriteLine(BeatLine(since, workingSet, Helpers()));
                }
            }

            await Task.Delay(Check);
        }
    }

    internal static List<string> ExitLines(TimeSpan since, int? code, IEnumerable<string> tail)
    {
        var lines = new List<string> { ExitLine(since, code) };
        foreach (var line in GameLogLines(tail))
        {
            lines.Add($"    game log: {line}");
        }

        return lines;
    }

    private static long? WorkingSet(Process game)
    {
        try
        {
            game.Refresh();
            return game.WorkingSet64;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool BeatDue(TimeSpan since, TimeSpan lastBeat)
    {
        return since <= BeatFor && since - lastBeat >= Beat;
    }

    internal static string Took(TimeSpan since)
    {
        var seconds = (int)since.TotalSeconds;
        if (seconds < 60)
        {
            return $"{seconds} s";
        }

        return $"{seconds / 60} min {seconds % 60} s";
    }

    internal static string BeatLine(TimeSpan since, long workingSet, int helpers)
    {
        return $"  after the stop, {Took(since)}: the game runs, working set {workingSet / (1024 * 1024)} MB, " +
               $"{helpers} {Helper} helper(s) running";
    }

    internal static string ExitLine(TimeSpan since, int? code)
    {
        var said = code is { } c ? $"code 0x{unchecked((uint)c):X8}" : "code unknown";
        return $"  after the stop, {Took(since)}: the game exited, {said}";
    }

    internal static List<string> GameLogLines(IEnumerable<string> tail)
    {
        var kept = new List<string>();
        foreach (var raw in tail)
        {
            var line = raw.Trim();
            if (!line.Contains("[D3D]") && !line.Contains("GPU temperature"))
            {
                continue;
            }

            if (line.Contains('\\') || line.Contains(":/"))
            {
                continue;
            }

            line = FrameLimits.Safe(LongNumber.Replace(line, "*"), GameLogLineWidth);
            kept.Add(line);
        }

        if (kept.Count <= GameLogLinesKept)
        {
            return kept;
        }

        return kept.GetRange(kept.Count - GameLogLinesKept, GameLogLinesKept);
    }

    private static Process? FindGame()
    {
        Process? game;
        try
        {
            game = Process.GetProcessesByName(GameProcess).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            _ = game?.SafeHandle;
        }
        catch (Exception)
        {
        }

        return game;
    }

    private static bool Exited(Process game)
    {
        try
        {
            return game.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static int? ExitCode(Process game)
    {
        try
        {
            return game.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Helpers()
    {
        try
        {
            return Process.GetProcessesByName(Helper).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static List<string> ReadGameLogTail()
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = Path.Combine(folder, "Horizon Forbidden West Complete Edition",
                                    "Horizon Forbidden West Complete Edition.log");
            if (!File.Exists(path))
            {
                return [];
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(file.Length, GameLogTailBytes);
            file.Seek(-take, SeekOrigin.End);
            var bytes = new byte[take];
            file.ReadExactly(bytes);
            return [.. Encoding.UTF8.GetString(bytes).Split('\n').Skip(1)];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
