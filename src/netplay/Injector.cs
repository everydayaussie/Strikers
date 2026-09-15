using System.Diagnostics;

namespace Strikers.Netplay;

internal sealed class Injector(string liveProbePath, bool armed)
{
    public string? LastCommand { get; private set; }

    internal static string? GameTimeOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("game time ", StringComparison.Ordinal) && trimmed.EndsWith(" s", StringComparison.Ordinal))
            {
                return trimmed["game time ".Length..];
            }
        }

        return null;
    }

    public Task<bool> Apply(Move m, CancellationToken token)
    {
        return Apply([m], token);
    }

    public Task<bool> Apply(IReadOnlyList<Move> turn, CancellationToken token)
    {
        return Apply(turn, final: false, token);
    }

    public async Task<bool> Apply(IReadOnlyList<Move> turn, bool final, CancellationToken token)
    {
        if (turn.Count == 0)
        {
            return true;
        }

        var args = new List<string>();

        if (turn.Count == 1)
        {
            var m = turn[0];
            args.Add("--script-move");
            args.Add($"{m.SrcX}");
            args.Add($"{m.SrcY}");

            if (m.Attack)
            {
                var from = m.StrikeFrom;
                args.Add($"{m.TargetX}");
                args.Add($"{m.TargetY}");
                args.Add("--attack");
                args.Add("--from");
                args.Add($"{from.X}");
                args.Add($"{from.Y}");
            }
            else
            {
                args.Add($"{m.DstX}");
                args.Add($"{m.DstY}");
            }

            args.Add("--facing");
            args.Add($"{m.Facing}");

            if (m.Burst)
            {
                args.Add("--burst");
            }
        }
        else
        {
            args.Add("--script-turn");
            args.Add(string.Join(";", turn.Select(m =>
            {
                var from = m.StrikeFrom;
                var spec = m.Attack
                    ? $"attack,{m.SrcX},{m.SrcY},{m.TargetX},{m.TargetY},{m.Facing},{from.X},{from.Y}"
                    : $"move,{m.SrcX},{m.SrcY},{m.DstX},{m.DstY},{m.Facing}";
                return m.Burst ? spec + ",burst" : spec;
            })));
        }

        if (final)
        {
            args.Add("--final");
        }

        args.Add("--yes");

        LastCommand = $"{liveProbePath} {string.Join(' ', args)}";

        if (!armed)
        {
            Console.WriteLine($"  would run: {LastCommand}");
            Console.WriteLine("  (not armed, pass --yes to netplay to actually write to the game)");
            return true;
        }

        Console.WriteLine($"  running: {Path.GetFileName(liveProbePath)} {string.Join(' ', args)}");

        var psi = new ProcessStartInfo(liveProbePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine($"  could not start {liveProbePath}");
            return false;
        }

        var lines = new List<string>();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lines)
                {
                    lines.Add(e.Data);
                }
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.Error.WriteLine($"    ! {e.Data}");
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var started = Stopwatch.StartNew();

        await proc.WaitForExitAsync(CancellationToken.None);

        if (proc.ExitCode == 0)
        {
            string? gameTime;
            lock (lines)
            {
                gameTime = GameTimeOf(lines);
            }

            Console.WriteLine($"    applied: {turn.Count} action(s) in {started.Elapsed.TotalSeconds:0.0} s" +
                              (gameTime is null ? "" : $", {gameTime} of game time"));
            return true;
        }

        lock (lines)
        {
            foreach (var line in lines)
            {
                Console.WriteLine($"    | {line}");
            }
        }

        Console.WriteLine($"    | exit {proc.ExitCode}");
        Console.Error.WriteLine($"  {args[0]} exited {proc.ExitCode}; its lines are above");
        return false;
    }
}
