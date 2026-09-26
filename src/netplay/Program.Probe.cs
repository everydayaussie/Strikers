namespace Strikers.Netplay;

internal static partial class Program
{
    private const string MachineA = "0B7E1F3C5A9D42E8B16C4F7A2D8E90B3";
    private const string MachineB = "3F5C8A1E7B0D46C2A94E1F6B8D0C25A7";

    private static bool RefusedGuestRecovers(Lobby l, string goodUuid)
    {
        l.OnSetup(new Frame { Kind = MsgKind.Setup, Challenge = goodUuid, Army = [MachineA] });
        return l.Refusal is null;
    }

    private static string BothReadyInstructions(Lobby lobby, IEnumerable<Preset> presets)
    {
        var remote = lobby.Remote;
        if (remote is null)
        {
            return "  their setup is gone, the other player started over";
        }

        var known = presets.ToList();
        var steps = lobby.BoundSetup is null
            ? "Compare the safety code (the fingerprint line) with the other player, then run: confirm <code>\n" +
              "Then stand in the Machine Strike menu, on the challenge list, and run: write"
            : "Both confirmed. Stand in the Machine Strike menu, on the challenge list, and run: write";
        return $"""

        BOTH READY, challenge {lobby.Challenge}

        {steps}
          your army   {string.Join(' ', lobby.Local.Army)}
          their army  {string.Join(' ', remote.Army)}
          their squares, rotated into your frame: {string.Join(", ", remote.Placements.Select(p => $"({p.X},{p.Y}) dir {p.Dir}"))}
          you place by hand: {Presets.PlacementAdvice(lobby.Local.Army, lobby.Local.Placements, known)}
        """;
    }

    internal static (string What, string[] Args)[] WriteSteps(Lobby lobby, Lobby.SeatSetup remote,
                                                              bool interactivePlacement = false,
                                                              Preset? preset = null)
    {
        var steps = new List<(string What, string[] Args)>();

        var written = lobby.ToWrite(preset);
        var boardToWrite = written.Board;
        var victoryToWrite = written.VictoryPoints;
        var draftToWrite = written.DraftPoints;
        var depthToWrite = written.PlacementRows;

        if (victoryToWrite is not null || draftToWrite is not null || depthToWrite is not null)
        {
            string[] rules = ["--set-rules", "--board-game", lobby.Challenge!];
            if (victoryToWrite is { } vp)
            {
                rules = [.. rules, "--victory-points", $"{vp}"];
            }
            if (draftToWrite is { } dp)
            {
                rules = [.. rules, "--draft-points", $"{dp}"];
            }
            if (depthToWrite is { } pr)
            {
                rules = [.. rules, "--placement-rows", $"{pr}"];
            }

            steps.Add(("rules", rules));
        }

        if (boardToWrite is { Count: > 0 })
        {
            var width = lobby.BoardWidth;
            var height = lobby.BoardHeight;
            if (width != Preset.BoardSide || height != Preset.BoardSide)
            {
                steps.Add(($"board shape {width}x{height}",
                           ["--set-board-size", "--board-game", lobby.Challenge!,
                            "--rows", $"{height}", "--cols", $"{width}"]));
            }

            var asSeenHere = lobby.IsHost ? boardToWrite : Preset.Rotate180(boardToWrite, width, height);
            steps.Add((lobby.IsHost ? "the custom board" : "the custom board, rotated into your frame",
                       ["--set-board", .. asSeenHere.Select(t => $"{t}"), "--board-game", lobby.Challenge!]));
        }

        var freeDraft = Challenges.Find(lobby.Challenge) is { Slots: 0 };
        (string What, string[] Args) armies = freeDraft
            ? ("armies, each seat sized to its own",
               ["--set-units", "--allocate", "--board-game", lobby.Challenge!,
                "--human", .. lobby.Local.Army, "--ai", .. remote.Army])
            : ("armies, yours into the human seat, theirs into the AI seat",
               ["--set-units", "--board-game", lobby.Challenge!,
                "--human", .. lobby.Local.Army, "--ai", .. remote.Army]);

        steps.Add(armies);

        if (!interactivePlacement)
        {
            steps.Add(("their starting squares into the AI seat",
                       ["--set-placement",
                        .. remote.Placements.SelectMany(p => new[] { $"{p.X}", $"{p.Y}", $"{p.Dir}" })]));
        }

        return [.. steps];
    }


    internal static string[] NamesHoldArgs(string[] naming, int parentPid)
    {
        return [.. naming, "--yes", "--hold-names", "--wait", "600", "--parent-pid", parentPid.ToString()];
    }

    public const string ArmyNameHexFlag = "--army-name-hex";

    public const int MaxArmyNameChars = 32;

    public static string? ArmyNameFromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length > MaxArmyNameChars * 8 || hex.Length % 2 != 0)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }

        return CleanArmyName(System.Text.Encoding.UTF8.GetString(bytes));
    }

    public static string? CleanArmyName(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var kept = new System.Text.StringBuilder();
        var count = 0;
        foreach (var rune in raw.Trim().EnumerateRunes())
        {
            if (count == MaxArmyNameChars)
            {
                break;
            }

            if (System.Text.Rune.IsControl(rune) || rune == System.Text.Rune.ReplacementChar)
            {
                continue;
            }

            kept.Append(rune.ToString());
            count++;
        }

        var name = kept.ToString().Trim();
        return name.Length == 0 ? null : name;
    }

    public static string ArmyNameHex(string name)
    {
        return Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name));
    }

    public static string[] NameArgs(string? mine, string? theirs, string? army = null)
    {
        var me = Names.Clean(mine);
        var them = Names.Clean(theirs);
        var armyName = CleanArmyName(army);
        if (me.Length == 0 && them.Length == 0 && armyName is null)
        {
            return [];
        }

        var args = new List<string> { "--set-names" };
        if (armyName is not null)
        {
            args.Add(ArmyNameHexFlag);
            args.Add(ArmyNameHex(armyName));
        }
        if (me.Length > 0)
        {
            args.Add("--me");
            args.Add(me);
        }

        if (them.Length > 0)
        {
            args.Add("--them");
            args.Add(them);
        }

        return [.. args];
    }

    private static Preset? _appliedPreset;

    private static int LobbyWrite(Peer peer, Lobby lobby, string probe, bool yes, bool interactivePlacement = false)
    {
        if (!lobby.BothReady)
        {
            Console.Error.WriteLine("  not both ready yet, run status");
            return 1;
        }

        var frozen = lobby.Frozen();
        if (frozen.Challenge is null)
        {
            Console.Error.WriteLine("  no challenge agreed");
            return 1;
        }
        var remote = frozen.Remote;
        if (remote is null || !remote.Complete)
        {
            Console.Error.WriteLine("  their setup is incomplete, refusing to write a partial army");
            return 1;
        }

        if (!frozen.Local.Complete)
        {
            Console.Error.WriteLine("  your own setup is incomplete, run army and place first");
            return 1;
        }

        if (peer.Binding is null || lobby.BoundSetup is not { } confirmed)
        {
            Console.Error.WriteLine($"  {Lobby.WriteUnconfirmed}");
            return 1;
        }

        if (!Lobby.SetupAgrees(confirmed, frozen.SetupDigest(_appliedPreset)))
        {
            peer.HaltAndTell(Lobby.SetupsDiffer);
            return 1;
        }

        if (yes && SetupRefusal(ReadBuild(probe).Exit, RunProbeQuiet(probe, ["--match-live"], 2)) is { } notNow)
        {
            Console.Error.WriteLine(notNow);
            return 1;
        }

        foreach (var (what, a) in WriteSteps(frozen, remote, interactivePlacement, _appliedPreset))
        {
            var full = yes ? (string[])[.. a, "--yes"] : a;
            if (!yes)
            {
                Console.WriteLine($"\n  {what}:\n    {probe} {string.Join(' ', full)}");
                continue;
            }

            Console.WriteLine($"  {what}");

            if (RunWriteStep(probe, full) is not { } failed)
            {
                continue;
            }

            foreach (var line in failed)
            {
                Console.Error.WriteLine(line);
            }

            return 1;
        }

        if (!yes)
        {
            Console.WriteLine("\n  printed only, start netplay with --yes to run these.");
            return 0;
        }

        var step2 = interactivePlacement
            ? "Place your own machines wherever you like. The AI seat was NOT pre-written, so\n" +
              "                   each square you choose is sent to the other player and written into their\n" +
              "                   AI seat, and theirs into yours. --play freezes the board between the two."
            : $"Place your own machines by hand on: {string.Join(", ", frozen.Local.Placements.Select(p => $"({p.X},{p.Y}) dir {p.Dir}"))}";

        Console.WriteLine("\n  setup written");
        if (Console.IsOutputRedirected)
        {
            return 0;
        }

        Console.WriteLine($"""
              Now, in order:
                1. Run netplay --play with the same room code, on BOTH PCs, BEFORE either match is
                   entered. It arms the AI hold; a match entered first lets that seat's AI take a
                   free turn, and the boards diverge before anyone has played.
                2. {step2}
                3. Start the match WITHOUT backing out of the challenge list, navigating away
                   re-creates the draft resources and wipes the army write.
            """);
        return 0;
    }

    private static System.Diagnostics.Process? _moveBoundsHolder;

    internal static readonly string[][] StartClears =
    [
        ["--force-first", "clear", "--yes"],
        ["--patch-move-bounds", "--clear"],
        ["--commit-ring", "--clear"],
    ];

    private static bool _moveBoundsPending;

    internal enum BoundsStep
    {
        Nothing,
        Apply,
        Wait,
    }

    internal static BoundsStep MoveBoundsStep(bool firstSample, bool pending, bool holderAlive)
    {
        if (!firstSample && !pending)
        {
            return BoundsStep.Nothing;
        }

        return holderAlive ? BoundsStep.Wait : BoundsStep.Apply;
    }

    private static void ApplyMoveBoundsPatch(string probe, int width, int height)
    {
        if (width <= 0 || height <= 0 || !TurnBoundary.MoveBoundsPatchWanted(width, height))
        {
            return;
        }

        if (_moveBoundsHolder is { HasExited: false })
        {
            return;
        }

        Console.WriteLine($"  the board is {width}x{height}: patching the game's move bounds for it");

        if (RunProbeUntilArmed(probe, out _moveBoundsHolder, "--patch-move-bounds", "--yes",
                               "--parent-pid", Environment.ProcessId.ToString()) != 0)
        {
            Console.Error.WriteLine("  Warning: the move-bounds patch did not apply; on this board the game " +
                                    "may refuse to select a machine or to reach a square");
        }
    }

    private static System.Diagnostics.Process? _commitRingHolder;

    private static void ApplyCommitRing(string probe)
    {
        if (_commitRingHolder is { HasExited: false })
        {
            return;
        }

        if (RunProbeUntilArmed(probe, out _commitRingHolder, "--commit-ring", "--yes",
                               "--parent-pid", Environment.ProcessId.ToString()) != 0)
        {
            Console.Error.WriteLine("  Warning: the commit ring did not install; the turn is read from the board alone");
        }
    }

    private static int RunProbe(string probe, params string[] probeArgs)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe) { UseShellExecute = false };
            foreach (var a in probeArgs)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode ?? -1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not run {probe} ({ex.GetType().Name})");
            return -1;
        }
    }

    internal static string[]? RunWriteStep(string probe, string[] full,
                                           Func<string, string[], List<string>, int[], int>? run = null)
    {
        run ??= RunProbeKeeping;
        var said = new List<string>();
        var code = full.Contains("--restore-when-live")
            ? RunProbeUntilArmed(probe, out _, full)
            : run(probe, full, said, []);
        if (code == 0)
        {
            return null;
        }

        if (ProbeRefusedBuild(code) is { } refused)
        {
            return [refused];
        }

        string[] heard;
        lock (said)
        {
            heard = [.. said];
        }

        return [$"  the step was: {probe} {string.Join(' ', full)}", $"\n  {WriteStepFailed(code, heard)}"];
    }

    internal const string NoChallengeListLine =
        "the game has no Machine Strike challenge list open, so the setup is incomplete. " +
        "Open the challenge list, then run write again.";

    private const string NoBoardGameLoaded = "no loaded BoardGame carries";

    internal static string WriteStepFailed(int code, IEnumerable<string> said)
    {
        if (said.Any(line => line.Contains(NoBoardGameLoaded, StringComparison.Ordinal)))
        {
            return NoChallengeListLine;
        }

        return $"live-probe exited {code}. Stopping, the later steps did not run, " +
               "so the setup is incomplete. Do not start the match.";
    }

    private static int RunProbeQuiet(string probe, string[] probeArgs, params int[] okCodes)
    {
        return RunProbeKeeping(probe, probeArgs, [], okCodes);
    }

    private static int RunProbeKeeping(string probe, string[] probeArgs, List<string> lines, int[] okCodes)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in probeArgs)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (lines)
                    {
                        lines.AddRange(ChildLines(e.Data, stderr: false));
                    }
                }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (lines)
                    {
                        lines.AddRange(ChildLines(e.Data, stderr: true));
                    }
                }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            var ok = p.ExitCode == 0 || okCodes.Contains(p.ExitCode);
            if (!ok)
            {
                lock (lines)
                {
                    foreach (var line in lines)
                    {
                        Console.WriteLine(line);
                    }
                }

                Console.WriteLine($"    | exit {p.ExitCode}");
            }

            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not run {probe} ({ex.GetType().Name})");
            return -1;
        }
    }

    internal static IEnumerable<string> ChildLines(string? raw, bool stderr)
    {
        var mark = stderr ? "    ! " : "    | ";
        foreach (var line in (raw ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return mark + line;
        }
    }

    private const string ArmedMarker = "holding";

    private static int RunProbeUntilArmed(string probe, out System.Diagnostics.Process? child, params string[] probeArgs)
    {
        child = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            foreach (var a in probeArgs)
            {
                psi.ArgumentList.Add(a);
            }

            var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            var armed = false;
            var before = new List<string>();
            while (p.StandardOutput.ReadLine() is { } line)
            {
                if (line.Contains(ArmedMarker, StringComparison.Ordinal))
                {
                    foreach (var shown in ChildLines(line, stderr: false))
                    {
                        Console.WriteLine(shown);
                    }

                    armed = true;
                    break;
                }

                before.AddRange(ChildLines(line, stderr: false));
            }

            if (!armed)
            {
                foreach (var line in before)
                {
                    Console.WriteLine(line);
                }

                p.WaitForExit();
                return p.ExitCode == 0 ? -1 : p.ExitCode;
            }

            var reader = new Thread(() =>
            {
                while (p.StandardOutput.ReadLine() is { } line)
                {
                    foreach (var shown in ChildLines(line, stderr: false))
                    {
                        Console.WriteLine(shown);
                    }
                }
            })
            {
                IsBackground = true,
            };
            reader.Start();
            child = p;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  could not run {probe} ({ex.GetType().Name})");
            return -1;
        }
    }

    private static (int Width, int Height)? LocalShape(string probe)
    {
        if (_lastSnap is { Width: > 0, Height: > 0 } cached)
        {
            return (cached.Width, cached.Height);
        }

        if (ReadSnapshot(probe) is { Width: > 0, Height: > 0 } fresh)
        {
            return (fresh.Width, fresh.Height);
        }

        return null;
    }

    internal static (int Width, int Height)? ShapeFrom(BoardSnapshot? cached, BoardSnapshot? fresh)
    {
        if (cached is { Width: > 0, Height: > 0 } c)
        {
            return (c.Width, c.Height);
        }

        if (fresh is { Width: > 0, Height: > 0 } f)
        {
            return (f.Width, f.Height);
        }

        return null;
    }

    internal const int ProbeUnknownBuild = 9;

    internal const string UnknownBuildRefusal =
        "REFUSED: game build not supported: this Strikers does not know this game build, so nothing is written into the game";

    internal static string? ProbeRefusedBuild(int probeExit)
    {
        if (probeExit != ProbeUnknownBuild)
        {
            return null;
        }

        return UnknownBuildRefusal;
    }

    internal const string StillInMatch =
        "  the game is still in a match, or on its victory screen. Press Continue in " +
        "the game, stand on the challenge list, then Set up the match again. " +
        "Nothing was written.";

    internal static string? SetupRefusal(int buildExit, int matchLiveExit)
    {
        if (ProbeRefusedBuild(buildExit) is { } refused)
        {
            return refused;
        }

        if (matchLiveExit == 0)
        {
            return StillInMatch;
        }

        return null;
    }

    internal static string BuildFrom(int probeExit, string text)
    {
        var answered = probeExit == 0 || probeExit == ProbeUnknownBuild;
        if (!answered || text.Length == 0)
        {
            return "unknown";
        }

        return text;
    }

    private static (string Build, int Exit) ReadBuild(string probe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("--build");

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return ("unknown", -1);
            }

            var text = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (BuildFrom(p.ExitCode, text), p.ExitCode);
        }
        catch (Exception) { return ("unknown", -1); }
    }

    private static string OwnVersion()
    {
        return System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N");
    }

    private static string ReadProbeVersion(string probe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return "unknown";
            }

            var text = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || text.Length == 0)
            {
                return "unknown";
            }

            var line = text.Split('\n')[0].Trim();
            if (line.StartsWith("live-probe ", StringComparison.Ordinal))
            {
                line = line["live-probe ".Length..].Trim();
            }

            return line.Length > 0 ? line : "unknown";
        }
        catch (Exception) { return "unknown"; }
    }

}
