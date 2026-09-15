using System.Runtime.InteropServices;

namespace Strikers.Core;

public static class Selftest
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static void AttachToCaller()
    {
        AttachConsole(-1);
    }

    private static string TestClaim(string what)
    {
        return $@"Local\Strikers-selftest-{what}-{Environment.ProcessId}";
    }

    public static int Run(Action<Action<string, bool>>? uiChecks = null)
    {
        AttachConsole(-1);

        var passed = 0;
        var failed = 0;

        void Check(string what, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (ok)
            {
                passed++;
            }
            else
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Strikers selftest");
        Console.WriteLine();

        const string nonce = "a1b2c3d4e5f6a7b8";
        var plain = Play.ParseInvite($"bore.pub:12345 {nonce} GATE01");
        Check("the invite we generate reads back",
              plain is { Address: "bore.pub:12345", RoomId: nonce, Code: "GATE01" });

        Check("the three parts may arrive in any order",
              Play.ParseInvite($"GATE01 bore.pub:12345 {nonce}")
                  is { Address: "bore.pub:12345", RoomId: nonce, Code: "GATE01" });

        Check("a line break between them still reads",
              Play.ParseInvite($"bore.pub:12345 {nonce}\nGATE01")
                  is { Address: "bore.pub:12345", RoomId: nonce, Code: "GATE01" });

        Check("a lower-case code is normalised, since codes are read aloud",
              Play.ParseInvite($"bore.pub:12345 {nonce} gate01") is { Code: "GATE01" });

        Check("a numeric address reads",
              Play.ParseInvite($"192.0.2.10:47801 {nonce} ABC123") is { Address: "192.0.2.10:47801", Code: "ABC123" });

        Check("a pasted command line does not hand back a noise word as the room code",
              Play.ParseInvite($"netplay --server bore.pub:12345 --room-id {nonce} --join GATE01")
                  is { Address: "bore.pub:12345", RoomId: nonce, Code: "GATE01" });

        Check("chatty wording around the invite still reads",
              Play.ParseInvite($"hey join me on server bore.pub:12345 room {nonce} GATE01 thanks")
                  is { Address: "bore.pub:12345", RoomId: nonce, Code: "GATE01" });

        Check("an invite with no address at all is refused rather than half-read",
              Play.ParseInvite($"{nonce} GATE01") is { Address: null, Code: null });

        Check("empty text is refused",
              Play.ParseInvite("") is { Address: null });

        Check("an invite with no routing id is refused rather than silently unroutable",
              Play.ParseInvite("bore.pub:12345 GATE01") is { RoomId: null });

        var genNonce = Play.NewRoomId();
        var genCode = Play.NewRoomCode();
        var gen = Play.ParseInvite($"bore.pub:12345 {genNonce} {genCode}");
        Check("a freshly generated invite round-trips through the parser",
              gen.Address == "bore.pub:12345" && gen.RoomId == genNonce && gen.Code == genCode);

        Check("a real safety code lets the player confirm a match, with nothing to warn about",
              Play.SafetyScreenState("A1B2C3D4") is { Warning: false, Note: "", Press: "Continue" });

        Check("no safety code shows a warning that says the link is not verified, on a press that says so",
              Play.SafetyScreenState("") is { Warning: true, Text: "not shown", Press: "Continue unverified" } noCode
              && noCode.Note.Contains("not verified"));

        const string fingerprint = "1E1833535E873CBD";

        Check("the host types the first four of the code and the guest the last four",
              Play.SafetyTyped(fingerprint, hosting: true) == "1E18"
              && Play.SafetyTyped(fingerprint, hosting: false) == "3CBD");

        Check("what one side reads out is exactly what the other side types",
              Play.SafetyReadOut(fingerprint, hosting: true) == Play.SafetyTyped(fingerprint, hosting: false)
              && Play.SafetyReadOut(fingerprint, hosting: false) == Play.SafetyTyped(fingerprint, hosting: true));

        Check("typing the opponent's four continues, and the press says the codes match",
              Play.SafetyTypedState(fingerprint, "3CBD", hosting: false) is { Continue: true, Good: true, Press: "Codes match, continue" }
              && Play.SafetyTypedState(fingerprint, "1E18", hosting: true) is { Continue: true, Good: true });

        Check("case and spacing do not matter, since the code is read aloud",
              Play.SafetyTypedState(fingerprint, " 3cbd ", hosting: false).Continue);

        var mismatch = Play.SafetyTypedState(fingerprint, "3CBE", hosting: false);
        Check("a code that does not match refuses to continue and the press says the codes differ",
              mismatch is { Continue: false, Good: false, Press: "Codes differ" });

        Check("typing the four shown on your own screen is refused, for the host and the guest alike",
              Play.SafetyTypedState(fingerprint, "3CBD", hosting: true) is { Continue: false, Press: "Codes differ" }
              && Play.SafetyTypedState(fingerprint, "1E18", hosting: false) is { Continue: false, Press: "Codes differ" });

        Check("a partly typed code neither continues, cries mismatch, nor counts",
              Play.SafetyTypedState(fingerprint, "3C", hosting: false) is { Continue: false, Press: "Continue" });

        Check("nothing typed does not continue",
              Play.SafetyTypedState(fingerprint, "", hosting: false) is { Continue: false });

        Check("the other seat emptying keeps the safety code",
              !Play.CodeIsCleared(Play.CodeEvent.OpponentLeft));
        Check("a new attempt clears the safety code",
              Play.CodeIsCleared(Play.CodeEvent.NewAttempt));
        Check("D-193: a peer that started over clears the code for the one its new key derived",
              Play.CodeIsCleared(Play.CodeEvent.PeerStartedOver));

        Check("an absent code cannot be satisfied by typing anything",
              Play.SafetyTypedState("", "", hosting: false) is { Continue: false }
              && Play.SafetyTypedState("", "3CBD", hosting: false) is { Continue: false }
              && Play.SafetyTypedState("", "", hosting: true) is { Continue: false });

        Check("the real fingerprint line is read",
              Play.Fingerprint("  encrypted, fingerprint 1E1833535E873CBD") == "1E1833535E873CBD");

        Check("a peer's game build cannot pose as a fingerprint line",
              Play.Fingerprint("  <- peer build fingerprint AABBCCDD11223344") is null);

        Check("a peer's challenge cannot pose as a fingerprint line",
              Play.Fingerprint("  <- their setup: 2 machine(s), challenge fingerprint AABBCCDD") is null);

        Check("a line that merely mentions the word is not a code",
              Play.Fingerprint("  (read that fingerprint to the other player)") is null
              && Play.Fingerprint("  paired, seat 0") is null);

        Check("the no-code placeholder is not a code",
              Play.SafetyTypedState(Play.SafetyScreenState("").Text, "HOWN", hosting: false) is { Continue: false });

        var board = new StrikeBoard();
        board.Paint(0, 0, Terrain.Mountains);
        Check("painting a square paints its opposite too",
              board.At(0, 0) == Terrain.Mountains && board.At(7, 7) == Terrain.Mountains);

        board.Paint(2, 5, Terrain.Marsh);
        Check("the mirror holds away from the corners",
              board.At(2, 5) == Terrain.Marsh && board.At(5, 2) == Terrain.Marsh);

        Check("a board built only by painting is always legal",
              board.Problem() is null);

        var lopsided = new StrikeBoard();
        lopsided.Cells[3] = Terrain.Hills;
        Check("an asymmetric board is playable, because the guest writes the rotation",
              lopsided.Problem() is null);

        Check("a rectangle is a playable board",
              new StrikeBoard { Width = 5, Height = 8 }.Problem() is null);

        Check("a board bigger than the game allocates is refused",
              new StrikeBoard { Width = 9, Height = 8 }.Problem() is not null);

        Check("a placing depth that would make the two zones meet is ALLOWED",
              new StrikeBoard { Width = 5, Height = 5, PlacementRows = 3 }.Problem() is null);

        Check("a board wider than it is deep is ALLOWED",
              new StrikeBoard { Width = 8, Height = 5 }.Problem() is null &&
              new StrikeBoard { Width = 6, Height = 3 }.Problem() is null);

        Check("the same board turned on its side is accepted",
              new StrikeBoard { Width = 5, Height = 8 }.Problem() is null &&
              new StrikeBoard { Width = 3, Height = 6 }.Problem() is null);

        Check("a square board is unaffected by the width rule",
              new StrikeBoard { Width = 8, Height = 8 }.Problem() is null &&
              new StrikeBoard { Width = 4, Height = 4 }.Problem() is null);

        Check("a placing depth that fits is accepted",
              new StrikeBoard { Width = 5, Height = 8, PlacementRows = 2 }.Problem() is null);

        Check("a placing depth outside the board's range is still refused",
              new StrikeBoard { Width = 5, Height = 8, PlacementRows = 0 }.Problem() is not null &&
              new StrikeBoard { Width = 5, Height = 8, PlacementRows = 9 }.Problem() is not null);

        var rect = new StrikeBoard { Width = 4, Height = 2 };
        rect.Paint(0, 1, Terrain.Mountains);
        Check("a mirrored stroke on a rectangle paints its true opposite square",
              rect.At(0, 1) == Terrain.Mountains && rect.At(3, 0) == Terrain.Mountains);

        var chasmAtTheStart = new StrikeBoard();
        chasmAtTheStart.Paint(4, 1, Terrain.Chasm);
        Check("a Chasm in a placing row no longer blocks the editor board",
              chasmAtTheStart.Problem() is null);

        var chasmInTheMiddle = new StrikeBoard();
        chasmInTheMiddle.Paint(4, 3, Terrain.Chasm);
        Check("a Chasm away from the placing rows is allowed, which The Abyss needs",
              chasmInTheMiddle.Problem() is null);

        Check("a set-board line carries all 64 squares",
              new StrikeBoard().SetBoardCommand("X").Split(' ').Count(t => t is "0") == 64);

        var flow = new Flow();
        Check("the opening screen is a choice, not a numbered step",
              flow.Current == Stage.Start && flow.Number == 0);

        flow.Begin(hosting: true);
        Check("hosting starts on the match picker, step 1",
              flow.Current == Stage.HostChoose && flow.Number == 1 && flow.Total == 6);

        Check("both sides pick an army, and only after the challenge is known",
              Array.IndexOf(Flow.PathFor(hosting: true), Stage.ChooseArmy) >
              Array.IndexOf(Flow.PathFor(hosting: true), Stage.HostInvite) &&
              Array.IndexOf(Flow.PathFor(hosting: false), Stage.ChooseArmy) >
              Array.IndexOf(Flow.PathFor(hosting: false), Stage.JoinWait));

        Check("the host is never shown the joiner's paste box",
              flow.GoTo(Stage.JoinPaste) is false && flow.Current == Stage.HostChoose);

        Check("the host moves on to the invite",
              flow.GoTo(Stage.HostInvite) && flow.Number == 2);

        Check("it never goes back a screen",
              flow.GoTo(Stage.HostChoose) is false && flow.Current == Stage.HostInvite);

        var rewound = new Flow();
        rewound.Begin(hosting: true);
        rewound.GoTo(Stage.SetUp);
        Check("a rewind lands on an earlier stage of the same path and refuses anything else",
              rewound.Rewind(Stage.Playing) is false && rewound.Rewind(Stage.SetUp) is false
              && rewound.Rewind(Stage.JoinWait) is false && rewound.Current == Stage.SetUp
              && rewound.Rewind(Stage.ChooseArmy) && rewound.Current == Stage.ChooseArmy
              && rewound.GoTo(Stage.Safety) && rewound.Current == Stage.Safety);

        var noFingerprint = new Flow();
        noFingerprint.Begin(hosting: true);
        Check("a missing step can be skipped forward, never stranding the player",
              noFingerprint.GoTo(Stage.Safety) && noFingerprint.Current == Stage.Safety);

        var joiner = new Flow();
        joiner.Begin(hosting: false);
        Check("the joiner starts on the paste box and is never shown the host's invite screen",
              joiner.Current == Stage.JoinPaste && joiner.GoTo(Stage.HostInvite) is false);

        Check("both sides end on the playing screen",
              Array.IndexOf(joiner.Path, Stage.Playing) == joiner.Total - 1
              && Array.IndexOf(flow.Path, Stage.Playing) == flow.Total - 1);

        Check("every screen the flow routes through has a page behind it",
              Flow.PathFor(hosting: true).All(s => Play.PagedStages.Contains(s))
              && Flow.PathFor(hosting: false).All(s => Play.PagedStages.Contains(s)));

        flow.Reset();
        Check("stopping returns to the opening choice",
              flow.Current == Stage.Start && flow.Number == 0);

        Check("stopping clears the hosting flag", !flow.Hosting);

        var fresh = new Flow();
        Check("nothing but Begin leaves the opening screen, before a match or after Stop",
              fresh.GoTo(Stage.Safety) is false && fresh.GoTo(Stage.ChooseArmy) is false
              && fresh.GoTo(Stage.Playing) is false && fresh.Current == Stage.Start
              && flow.GoTo(Stage.Safety) is false && flow.GoTo(Stage.JoinWait) is false
              && flow.Current == Stage.Start);

        Check("the line netplay prints when the write worked is recognised",
              Play.WriteSucceeded("  setup written"));

        Check("a live-probe failure mid-write is recognised as a failure",
              Play.WriteFailed("  live-probe exited 1. Stopping, the later steps did not run, "
                                  + "so the setup is incomplete. Do not start the match."));

        Check("writing before both players are ready is recognised as a failure",
              Play.WriteFailed("  not both ready yet, run status"));

        Check("a half-sent peer setup is recognised as a failure",
              Play.WriteFailed("  their setup is incomplete, refusing to write a partial army"));

        Check("a write refused because a match is still live is recognised as a failure",
              Play.WriteFailed("  the game is still in a match, or on its victory screen. Press Continue in the game, "
                               + "stand on the challenge list, then Set up the match again. Nothing was written."));

        Check("the success line is not also read as a failure",
              Play.WriteFailed("  setup written") is false);

        Check("ordinary lobby chatter is neither",
              Play.WriteSucceeded("  paired, seat 0") is false
              && Play.WriteFailed("  paired, seat 0") is false
              && Play.WriteSucceeded("BOTH READY, challenge 8BBC182B83FC495AA2150021217530D5") is false
              && Play.WriteFailed("BOTH READY, challenge 8BBC182B83FC495AA2150021217530D5") is false);

        var readyLine = "  both players are in the match (peer role: play)";
        var stillSetupLine = "  waiting: the other player is still in setup (peer role: lobby)";
        var movedOnLine = "  <- the other player is in the match (peer role: play)";

        Check("the peer-is-playing line is recognised as both ready",
              Play.BothPlayersReady(readyLine));

        Check("the peer-still-in-lobby line is recognised as keep waiting",
              Play.PeerStillInSetup(stillSetupLine));

        Check("the peer-moved-to-play line is recognised as this PC being behind",
              Play.PeerMovedToPlay(movedOnLine));

        Check("both-ready does not fire on the still-in-setup line",
              Play.BothPlayersReady(stillSetupLine) is false);

        Check("both-ready does not fire on the moved-on (lobby) line",
              Play.BothPlayersReady(movedOnLine) is false);

        Check("the role predicates ignore ordinary chatter",
              Play.BothPlayersReady("  paired, seat 0") is false
              && Play.PeerStillInSetup("  encrypted, fingerprint ABCD1234") is false
              && Play.PeerMovedToPlay("  <- they are ready") is false);

        Check("a wait with no headline counts its seconds and moves its dots",
              Play.WaitingText("", "Opening a public address", true, 0).Step == "Working."
              && Play.WaitingText("", "Opening a public address", true, 1).Step == "Working.."
              && Play.WaitingText("", "Opening a public address", true, 2).Step == "Working..."
              && Play.WaitingText("", "Opening a public address", true, 3).Step == "Working.");

        Check("the wait says what it is waiting for, without a count",
              Play.WaitingText("", "Checking the tunnel server", true, 7).Detail == "Checking the tunnel server"
              && Play.WaitingText("Waiting for your opponent to choose their army", "", false, 30).Detail == "");

        Check("a technical wait warns once it is slower than usual",
              Play.WaitingText("", "Opening a public address", true, Play.SlowAfter).Detail.Contains("slower than usual")
              && Play.WaitingText("", "Opening a public address", true, Play.SlowAfter - 1).Detail
                     .Contains("slower than usual") is false);

        Check("a wait on the other player never warns, however long it takes",
              Play.WaitingText("Send the invite", "Waiting for your opponent", false, 600).Detail
                  .Contains("slower than usual") is false);

        Check("a wait keeps the headline it was given, so an instruction outlives the lines under it",
              Play.WaitingText("Send the invite", "Waiting for your opponent to join", false, 45) is
              { Step: "Send the invite", Detail: "Waiting for your opponent to join" });

        Check("the setup write is only slow past its own threshold",
              Play.WriteSlowAfter > Play.SlowAfter
              && !Play.WaitingText("Writing", "x", true, Play.WriteSlowAfter - 1, Play.WriteSlowAfter).Detail
                      .Contains("slower")
              && Play.WaitingText("Writing", "x", true, Play.WriteSlowAfter, Play.WriteSlowAfter).Detail
                     .Contains("slower"));

        Check("the opponent-ahead line names the press for its screen and nothing on the others",
              Play.OpponentAheadHeadline.Length > 0
              && Play.OpponentAheadDetail(Stage.Safety) is { } aheadOnSafety
              && aheadOnSafety.Contains("Continue")
              && Play.OpponentAheadDetail(Stage.SetUp) is { } aheadOnSetUp
              && aheadOnSetUp.Contains("Set up the match") && aheadOnSetUp.Contains(Play.DoNotEnterYet)
              && Play.OpponentAheadDetail(Stage.ChooseArmy) is null
              && Play.OpponentAheadDetail(Stage.Playing) is null);

        Check("a lost link tells the player what to do, before and after a connection",
              Play.LinkDownAfter >= 2
              && Play.LinkLost(everConnected: false).Detail.Contains("invite")
              && Play.LinkLost(everConnected: true).Detail.Contains("Stop")
              && Play.LinkLost(false).Step != Play.LinkLost(true).Step);

        Check("the first retry line after the relay's notice is the opponent's Stop, at the lobby stages only",
              Play.JudgeLinkDown(1, peerLeftPending: true, atPlayStage: false) == Play.LinkDownVerdict.OpponentStopped
              && Play.JudgeLinkDown(1, peerLeftPending: false, atPlayStage: false) == Play.LinkDownVerdict.Nothing
              && Play.JudgeLinkDown(1, peerLeftPending: true, atPlayStage: true) == Play.LinkDownVerdict.Nothing
              && Play.JudgeLinkDown(Play.LinkDownAfter, false, false) == Play.LinkDownVerdict.LinkLost
              && Play.JudgeLinkDown(Play.LinkDownAfter, true, true) == Play.LinkDownVerdict.OpponentStopped
              && Play.JudgeLinkDown(Play.LinkDownAfter + 1, false, true) == Play.LinkDownVerdict.Nothing);

        Check("the setup clock counts down in minutes and seconds, and says what the time is for",
              Play.PlayClockText(TimeSpan.FromSeconds(605)).Text == "10m 05s to enter the challenge."
              && Play.PlayClockText(TimeSpan.FromSeconds(600)).Text.StartsWith("10m 00s"));

        Check("the last two minutes are urgent, and more than two are not",
              Play.PlayClockText(TimeSpan.FromSeconds(119)).Urgent
              && Play.PlayClockText(TimeSpan.FromSeconds(121)).Urgent is false);

        Check("a run-out clock says to start again rather than showing a negative time",
              Play.PlayClockText(TimeSpan.Zero).Text.Contains("ran out")
              && Play.PlayClockText(TimeSpan.FromSeconds(-5)).Text.Contains("ran out")
              && Play.PlayClockText(TimeSpan.FromSeconds(-5)).Urgent);

        Check("the clock is measured against the window netplay actually gives",
              Play.SetupWindow == TimeSpan.FromSeconds(600));

        var starts = new MatchDriver(action => action(), claimName: TestClaim("starts"));
        Check("a start can be claimed when nothing is running", starts.BeginStart());

        Check("a second start is refused while the first is still opening",
              starts.BeginStart() is false && starts.AnythingRunning is false);

        starts.CancelStart();
        Check("an attempt that never became a process frees the claim", starts.BeginStart());

        starts.StopAll();
        Check("Stop frees the claim as well", starts.BeginStart());

        var oneClaim = TestClaim("one-match");
        var firstWindow = new MatchDriver(action => action(), claimName: oneClaim);
        var secondWindow = new MatchDriver(action => action(), claimName: oneClaim);
        var secondWasTold = "";
        secondWindow.Trouble += (headline, _) =>
        {
            secondWasTold = headline;
        };

        Check("the first launcher takes the PC's match claim",
              firstWindow.BeginStart() && firstWindow.HoldsMatchClaim);

        Check("a second launcher is refused a match of its own, and told why",
              secondWindow.BeginStart() is false
              && secondWasTold.Contains("Another Strikers")
              && secondWindow.HoldsMatchClaim is false);

        firstWindow.StopAll();
        Check("Stop hands the claim on to the launcher that was refused",
              firstWindow.HoldsMatchClaim is false && secondWindow.BeginStart());

        firstWindow.StopAll();
        secondWindow.StopAll();
        var heldBeforeCancel = firstWindow.BeginStart();
        firstWindow.CancelStart();
        Check("an attempt cancelled before it spawned frees the claim for the other launcher",
              heldBeforeCancel && firstWindow.HoldsMatchClaim is false && secondWindow.BeginStart());

        secondWindow.StopAll();

        var generations = new MatchDriver(action => action(), claimName: TestClaim("generations"));
        generations.BeginStart();
        var firstAttempt = generations.Generation;
        Check("work started under this attempt is still wanted", generations.StillWanted(firstAttempt));

        generations.StopAll();
        Check("Stop makes the work in flight unwanted", generations.StillWanted(firstAttempt) is false);

        generations.BeginStart();
        Check("a new attempt does not adopt the last one's work in flight",
              generations.StillWanted(firstAttempt) is false
              && generations.StillWanted(generations.Generation));

        var lines = new List<string>();
        var queued = new List<Action>();
        var straggler = new MatchDriver(queued.Add, claimName: TestClaim("straggler"));
        straggler.Output += lines.Add;

        void Drain()
        {
            var pending = queued.ToArray();
            queued.Clear();
            foreach (var a in pending)
            {
                a();
            }
        }

        straggler.BeginStart();
        straggler.OnLine("hello from the current attempt", straggler.Generation);
        Drain();
        Check("a line read under the current attempt reaches the reader", lines.Count == 1);

        straggler.StopAll();
        straggler.OnLine("the closing words of the attempt just stopped", straggler.Generation);
        Drain();
        Check("a stopped attempt still shows its own last lines", lines.Count == 2);

        straggler.OnLine("a straggler from the killed process", straggler.Generation);
        straggler.BeginStart();
        Drain();
        Check("a straggler cannot be read under the next attempt's flow", lines.Count == 2);

        var spawnedUnder = straggler.Generation;
        straggler.StopAll();
        straggler.BeginStart();
        straggler.OnLine("a line the killed process read before the Stop, handed over after the next start", spawnedUnder);
        Drain();
        Check("a line is judged by the attempt that spawned its process, not the one running when it arrives",
              lines.Count == 2);

        Check("once a halt is up, nothing but Stop changes the screen",
              !Play.ScreenMayChange(halted: true, startIdle: false) && Play.ScreenMayChange(halted: false, startIdle: false));

        Check("a line reaching the Start screen with nothing running changes nothing on it",
              !Play.ScreenMayChange(halted: false, startIdle: true));

        var storeDir = Directory.CreateTempSubdirectory("strikers-selftest-");
        try
        {
            const string tornJson = "[{\"name\":\"Rush\",\"machines\":[]},";
            var armiesFile = Path.Combine(storeDir.FullName, "armies.json");
            File.WriteAllText(armiesFile, tornJson);
            var saveRefused = ArmyStore.Save(armiesFile, new Army { Name = "Heavy line" }) is not null;
            var deleteRefused = ArmyStore.Delete(armiesFile, "Rush") is not null;
            Check("a save or a delete over an armies file that cannot be read refuses and leaves it whole",
                  saveRefused && deleteRefused && File.ReadAllText(armiesFile) == tornJson);

            var boardsFile = Path.Combine(storeDir.FullName, "boards.json");
            File.WriteAllText(boardsFile, tornJson);
            _ = BoardStore.ReadAll(boardsFile, out var boardsUnreadable);
            _ = BoardStore.ReadAll(Path.Combine(storeDir.FullName, "absent.json"), out var boardsAbsent);
            Check("a boards file that cannot be read is told apart from one that is not there",
                  boardsUnreadable is not null && boardsAbsent is null);

            var settingsFile = Path.Combine(storeDir.FullName, "settings.json");
            File.WriteAllText(settingsFile, tornJson);
            var settingsRefused = Settings.Read(settingsFile).Write(settingsFile) is not null;
            Check("settings read from a file that cannot be read are not written back over it",
                  settingsRefused && File.ReadAllText(settingsFile) == tornJson);
        }
        finally
        {
            storeDir.Delete(recursive: true);
        }

        Check("a report carries at most the tail of a recording, and a short one whole",
              Report.TailFrom(1_000, Report.RecordingInZip) == 0
              && Report.TailFrom(Report.RecordingInZip, Report.RecordingInZip) == 0
              && Report.TailFrom(Report.RecordingInZip + 4_096, Report.RecordingInZip) == 4_096);

        Check("a log line is cut to its cap, and a line inside it is untouched",
              MatchLog.Cut("short line") == "short line"
              && MatchLog.Cut(new string('x', MatchLog.MaxLineChars)).Length == MatchLog.MaxLineChars
              && MatchLog.Cut(new string('x', MatchLog.MaxLineChars * 3)).Length == MatchLog.MaxLineChars + 10);

        var tidyDir = Directory.CreateTempSubdirectory("strikers-selftest-reports-");
        try
        {
            var zips = Path.Combine(tidyDir.FullName, "reports");
            Directory.CreateDirectory(zips);
            for (var i = 1; i <= 8; i++)
            {
                File.WriteAllText(Path.Combine(zips, Report.FileName(new DateTime(2026, 9, 12, 10, 0, i, DateTimeKind.Local))), "z");
            }

            File.WriteAllText(Path.Combine(zips, "a-note-the-player-left.txt"), "keep me");
            Report.Tidy(tidyDir.FullName);
            var left = Directory.GetFiles(zips).Select(Path.GetFileName).ToList();
            Check("the reports folder is tidied at start, down to its ceiling, and nothing else is touched",
                  left.Count(n => (n ?? "").EndsWith(".zip", StringComparison.Ordinal)) == Report.Keep
                  && left.Contains("a-note-the-player-left.txt"));
        }
        finally
        {
            tidyDir.Delete(recursive: true);
        }

        var longestInvite = $"netplay --server bore.pub:12345 --room-id {new string('a', 24)} --join GATE01";
        Check("the paste cap holds the longest legitimate invite",
              Play.MaxInviteLength >= longestInvite.Length &&
              Play.ParseInvite(longestInvite) is { Address: "bore.pub:12345", Code: "GATE01" });

        var safety = new Play.SafetyCode();
        Check("the code starts empty until a fingerprint is taken", safety.Code.Length == 0);

        Check("the first code shown is the one that is kept",
              safety.Take("AAAA1111") && safety.Code == "AAAA1111");

        Check("a re-key cannot swap a code the player may have read out",
              safety.Take("BBBB2222") is false && safety.Code == "AAAA1111");

        safety.Reset();
        Check("a new attempt starts with no code, so nothing stale can be confirmed",
              safety.Code.Length == 0 && Play.SafetyScreenState(safety.Code).Warning);

        Check("the next attempt shows its own code",
              safety.Take("CCCC3333") && safety.Code == "CCCC3333");

        Check("last attempt's typed characters cannot confirm a fresh exchange",
              Play.SafetyTypedState("", "1111", hosting: false).Continue is false);

        const string easy = "8BBC182B83FC495AA2150021217530D5";
        var abyss = new int[64];
        abyss[27] = 3;
        abyss[36] = 3;

        var hostLobby = Play.LobbyArgs(hosting: true, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                          "EXAMPLE", easy, abyss, 20, 40, Play.FirstJoiner);
        var joinLobby = Play.LobbyArgs(hosting: false, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                          "EXAMPLE", easy, abyss, 20, 40, Play.FirstHost);

        Check("a lobby is always interactive-placement, on both sides",
              hostLobby.Contains("--interactive-placement")
              && joinLobby.Contains("--interactive-placement"));

        Check("the host asks for a room and the joiner joins one",
              hostLobby.Contains("--room") && !hostLobby.Contains("--join")
              && joinLobby.Contains("--join") && !joinLobby.Contains("--room"));

        Check("the host's lobby carries the challenge, the board and the rules",
              hostLobby.Contains("--challenge") && hostLobby.Contains(easy)
              && hostLobby.Contains("--board")
              && hostLobby.Contains("--victory-points") && hostLobby.Contains("--draft-points"));

        Check("the joiner's lobby configures nothing: it is told",
              !joinLobby.Contains("--challenge") && !joinLobby.Contains("--board")
              && !joinLobby.Contains("--victory-points") && !joinLobby.Contains("--draft-points")
              && !joinLobby.Contains("--first"));

        Check("D-235: the host's lobby carries who goes first",
              hostLobby.SkipWhile(a => a != "--first").Skip(1).FirstOrDefault() == Play.FirstJoiner);
        Check("D-235: the picker reads You as the host, Opponent as the joiner, and Random as either throw",
              Play.FirstFromPick(0, coin: true) == Play.FirstHost
              && Play.FirstFromPick(1, coin: false) == Play.FirstJoiner
              && Play.FirstFromPick(2, coin: false) == Play.FirstHost
              && Play.FirstFromPick(2, coin: true) == Play.FirstJoiner
              && Play.FirstFromPick(-1, coin: true) == Play.FirstHost);

        Check("the board goes out as all 64 squares",
              hostLobby.SkipWhile(a => a != "--board").Skip(1).TakeWhile(a => !a.StartsWith("--")).Count() == 64);

        var stockRules = Play.LobbyArgs(hosting: true, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                           "", easy, null, -1, -1, Play.FirstHost);
        Check("a host that changed nothing sends no board and no rule numbers",
              !stockRules.Contains("--board") && !stockRules.Contains("--victory-points")
              && !stockRules.Contains("--draft-points") && stockRules.Contains("--challenge"));

        var limited = Play.LobbyArgs(hosting: true, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                     "", easy, null, 7, 12, Play.FirstHost);
        var draftAt = Array.IndexOf(limited, "--draft-points");
        Check("the cost limit goes on the lobby's command line",
              draftAt >= 0 && limited[draftAt + 1] == "12");
        Check("no army size reaches the command line",
              !limited.Contains("--army-size") && !limited.Contains("--their-army-size")
              && !hostLobby.Contains("--army-size") && !joinLobby.Contains("--army-size"));

        Check("both sides pass their own display name",
              hostLobby.Contains("--name") && hostLobby.Contains("EXAMPLE")
              && joinLobby.Contains("--name") && joinLobby.Contains("EXAMPLE"));

        Check("an empty name passes no --name at all", !stockRules.Contains("--name"));

        var messyName = Play.LobbyArgs(hosting: true, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                          "exam ple!!!-2", easy, null, -1, -1, Play.FirstHost);
        Check("a name is cleaned and capped before it reaches the command line",
              messyName.SkipWhile(a => a != "--name").Skip(1).First() == "EXAMPLE2");

        string[] army = ["1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144"];
        var sampleBinding = new string('A', 64);
        var hostPlay = Play.PlayArgs(hosting: true, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                        "EXAMPLE", army, sampleBinding, Play.FirstJoiner);
        var joinPlay = Play.PlayArgs(hosting: false, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345",
                                        "EXAMPLE", army, sampleBinding, Play.FirstJoiner);

        Check("D-219: --play carries the session binding, on both sides",
              hostPlay.SkipWhile(a => a != "--binding").Skip(1).FirstOrDefault() == sampleBinding
              && joinPlay.SkipWhile(a => a != "--binding").Skip(1).FirstOrDefault() == sampleBinding);
        Check("D-219: a match does not start without a binding of the right shape",
              Play.BindingProblem(sampleBinding) is null && Play.BindingProblem(null) is { } noBinding
              && !noBinding.Contains(';') && Play.BindingProblem(new string('A', 63)) is not null
              && Play.BindingProblem(new string('G', 64)) is not null);
        Check("D-219: the lobby's binding line is read with its value, and nothing like it is",
              NetplayLine.Read("  session bound " + sampleBinding, hosting: true, haveInvite: true)
                  is { Meaning: LineMeaning.SessionBound } boundRead && boundRead.Binding == sampleBinding
              && NetplayLine.Read("  session bound " + sampleBinding + "0", hosting: true, haveInvite: true).Meaning
                  != LineMeaning.SessionBound
              && NetplayLine.Read("HALT: session bound " + sampleBinding, hosting: true, haveInvite: true).Meaning
                  == LineMeaning.Halt);
        Check("D-219: the binding is masked in the file, as the lobby prints it and as --play is given it",
              !MatchDriver.ForTheRecord("  session bound " + sampleBinding).Contains(sampleBinding)
              && !MatchDriver.ForTheRecord("> netplay --play --binding " + sampleBinding + " --army x").Contains(sampleBinding));

        Check("--play carries the display name, on both sides",
              hostPlay.Contains("--name") && hostPlay.Contains("EXAMPLE")
              && joinPlay.Contains("--name") && joinPlay.Contains("EXAMPLE"));

        Check("D-235: --play carries who goes first on both sides, ahead of the army",
              hostPlay.SkipWhile(a => a != "--first").Skip(1).FirstOrDefault() == Play.FirstJoiner
              && joinPlay.SkipWhile(a => a != "--first").Skip(1).FirstOrDefault() == Play.FirstJoiner
              && Array.IndexOf(joinPlay, "--first") < Array.IndexOf(joinPlay, "--army"));

        Check("an empty name passes no --name to --play either",
              !Play.PlayArgs(hosting: true, "GATE01", "a1b2c3d4e5f6a7b8", "bore.pub:12345", "", army, sampleBinding, Play.FirstHost)
                  .Contains("--name"));

        Check("both sides tell --play where the relay is",
              hostPlay.Contains("--server") && joinPlay.Contains("--server"));

        Check("--play carries the same room code and the matching side",
              hostPlay.Contains("--room") && hostPlay.Contains("GATE01")
              && joinPlay.Contains("--join") && joinPlay.Contains("GATE01"));

        Check("--play carries this seat's own army, in order",
              hostPlay.SkipWhile(a => a != "--army").Skip(1).SequenceEqual(army)
              && joinPlay.Contains("--army"));

        Check("both command lines carry the routing id so routing never uses the code",
              hostLobby.Contains("--room-id") && hostLobby.Contains("a1b2c3d4e5f6a7b8")
              && joinPlay.Contains("--room-id") && joinPlay.Contains("a1b2c3d4e5f6a7b8"));

        var challenges = ChallengeBridge.Parse("""
            8BBC182B83FC495AA2150021217530D5  2  Beginner's Practice: Easy
            1E46038FF9804646813C959A31EAFB91  4  Beginner's Practice: Medium
            74771C9B66D9441AA4CBD5E47D4851AD  6  Beginner's Practice: Hard
            0ECEA5D9B9F841908D9716A1421F0AEC  0  Regular Challenge
            """);

        Check("the four challenges read back with their sizes",
              challenges.Count == 4 && challenges[0].Uuid == easy && challenges[0].Slots == 2);

        Check("a challenge name keeps its spaces and its colon",
              challenges[2].Name == "Beginner's Practice: Hard");

        Check("a free-draft challenge says so rather than offering nothing",
              Play.MatchLabel(challenges[3]).Contains("own army")
              && Play.MatchLabel(challenges[0]).Contains("2 machines"));

        Check("a line that is not a challenge is dropped rather than crashing",
              ChallengeBridge.Parse("\n\nnot a challenge\n").Count == 0);

        var roster = MachineBridge.Parse(
            "1C96A39FFE37791F8AE07BD49A2230FF\tBurrower\t1\t4\t2\t1\t2\tStrike\t-\t\n"
            + "B78EF94227B7D36454715138E9A17144\tScrounger\t1\t5\t3\t1\t2\tStrike\t-\t\n"
            + "F9433C1448F8F052BD457978CD0BFEC5\tThunderjaw\t6\t10\t3\t2\t3\tDash\tSpread\t"
            + "Dash charges through its victim, and a charge cannot be sent\n");

        Check("a machine line reads back with its stats",
              roster.Count == 3 && roster[0] is { Name: "Burrower", Cost: 1, Health: 4, Power: 2 });

        Check("a machine that cannot cross the wire is marked unplayable, with the reason",
              roster[2] is { Playable: false } && roster[2].Problem.Contains("charge")
              && roster[0].Playable && roster[1].Playable);

        Check("a name with a space in it survives, which is why the fields are tab-separated",
              MachineBridge.Parse("0E44B98882BA9AFD876C0DB6144D35F5\tRedeye Watcher\t3\t5\t2\t2\t2\tShot\tBlind\t")
                  is [{ Name: "Redeye Watcher" }]);

        var underBudget = Play.DescribeArmy(army, roster, 10);
        Check("an army shows its running cost against the budget",
              underBudget.Contains("cost 2 of 10") && underBudget.Contains("Burrower"));

        Check("an over-budget army says so rather than showing a total to work out",
              Play.DescribeArmy(["F9433C1448F8F052BD457978CD0BFEC5"], roster, 4).Contains("over the 4"));

        Check("an empty army does not render as a total of nothing",
              Play.DescribeArmy([], roster, 10) == "No army chosen.");

        Check("only real machine ids can reach the lobby's stdin",
              Play.ArmyIds(["1C96A39FFE37791F8AE07BD49A2230FF", "AAAA\nwrite", "short", ""])
                  is ["1C96A39FFE37791F8AE07BD49A2230FF"]);

        Check("a lower-case id is kept, normalised rather than dropped",
              Play.ArmyIds(["1c96a39ffe37791f8ae07bd49a2230ff"]) is ["1C96A39FFE37791F8AE07BD49A2230FF"]);

        Check("a real board passes",
              Play.PlayableBoard(new int[64]) is { Count: 64 });

        Check("a board of the wrong length is refused rather than sent",
              Play.PlayableBoard(new int[63]) is null
              && Play.PlayableBoard(new int[4096]) is null
              && Play.PlayableBoard(null) is null);

        var outOfRange = new int[64];
        outOfRange[7] = 99;
        Check("a terrain value this game does not have is refused",
              Play.PlayableBoard(outOfRange) is null);

        var theirs = Play.TheirSetup(
            $"  <- their setup: 2 machine(s), challenge {easy}, custom board, rules 20/40");
        Check("the host's setup line hands back the challenge and the rule numbers",
              theirs is { Challenge: easy, VictoryPoints: 20, DraftPoints: 40 });

        Check("a host that changed no rules reads back as the challenge's own numbers",
              Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}")
                  is { Challenge: easy, VictoryPoints: -1, DraftPoints: -1 });

        const string burrower = "1C96A39FFE37791F8AE07BD49A2230FF";
        const string grazer = "B78EF94227B7D36454715138E9A17144";
        Check("D-236: the other player's army line hands back its machines, in order",
              Play.TheirArmy($"  <- their army: {burrower} {grazer}") is [burrower, grazer]
              && NetplayLine.Read($"  <- their army: {burrower} {grazer}", false, true)
                  is { Meaning: LineMeaning.TheirArmy, Army: [burrower, grazer] });
        Check("D-236: nothing but netplay's own army line reads as an army",
              Play.TheirArmy($"  <- their army: {burrower.ToLowerInvariant()}") is null
              && Play.TheirArmy($"  <- their army: {burrower} junk") is null
              && Play.TheirArmy("  <- their army:") is null
              && Play.TheirArmy("  <- their army: " + string.Join(' ', Enumerable.Repeat(burrower, 17))) is null
              && Play.TheirArmy($"  <- hash for turn 1: pieces <- their army: {burrower}") is null);

        Check("D-235: the host's setup line says who goes first, and silence is the host",
              Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, rules 20/40, first joiner")
                  is { First: Play.FirstJoiner, DraftPoints: 40 }
              && Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, first host")
                  is { First: Play.FirstHost }
              && Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, rules 20/40")
                  is { First: Play.FirstHost }
              && Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, first both") is null
              && NetplayLine.Read($"  <- their setup: 2 machine(s), challenge {easy}, first joiner", false, true)
                  is { Meaning: LineMeaning.TheirSetup, First: Play.FirstJoiner });

        Check("a first clause holding anything but one allowed word refuses the setup line",
              Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, first joiner extra") is null
              && Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, first") is null
              && Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, first  joiner ")
                  is { First: Play.FirstJoiner });

        Check("ordinary lobby chatter is not read as a setup",
              Play.TheirSetup("  paired, seat 0") is null
              && Play.TheirSetup("  <- their setup: 2 machine(s)") is null);

        var arabicThree = ((char)0x0663).ToString();
        var oddRules = new[] { "99999999999999999999/1", $"{arabicThree}{arabicThree}/40", "20/-99999999999" };
        var oddRead = 0;
        foreach (var clause in oddRules)
        {
            try
            {
                if (Play.TheirSetup($"  <- their setup: 2 machine(s), challenge {easy}, rules {clause}") is null)
                {
                    oddRead++;
                }
            }
            catch (Exception)
            {
            }
        }

        Check("a rules clause that is not two ints refuses the line and throws nothing",
              oddRead == oddRules.Length);

        Exception? thrownEarly = null;
        Exception? thrownLate = null;
        Exception? nothing = new InvalidOperationException("not yet run");
        var escaped = false;
        try
        {
            thrownEarly = Play.Caught(() => throw new TimeoutException("Timeout opening clipboard.")).GetAwaiter().GetResult();
            thrownLate = Play.Caught(async () =>
            {
                await Task.Yield();
                throw new TimeoutException("Timeout opening clipboard.");
            }).GetAwaiter().GetResult();
            nothing = Play.Caught(() => Task.CompletedTask).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            escaped = true;
        }

        Check("a clipboard call that throws is handed back to the handler, never out of it",
              !escaped && thrownEarly is TimeoutException && thrownLate is TimeoutException && nothing is null);

        Check("a challenge list that did not load is said only when no attempt is under way",
              Play.MatchListNote(0, attemptUnderWay: false) is { Headline: "Cannot reach netplay" }
              && Play.MatchListNote(0, attemptUnderWay: true) is null
              && Play.MatchListNote(4, attemptUnderWay: false) is null);

        Check("a version line does not repeat the tool's name",
              NetplayTool.WithoutName("netplay", "netplay 7f53d5be  protocol 11") == "7f53d5be  protocol 11");

        Check("a value that does not start with the name is left alone",
              NetplayTool.WithoutName("live-probe", "667B1777-949F000") == "667B1777-949F000");

        Check("a tool that could not be run says so",
              NetplayTool.WithoutName("netplay", null) == "not found");

        Check("a stop with no draft guard running finds nothing to signal",
              !NetplayTool.StopDraftGuard());
        using (var guard = new EventWaitHandle(false, EventResetMode.ManualReset, NetplayTool.DraftStopEvent))
        {
            Check("a stop reaches a draft guard holding the event",
                  NetplayTool.StopDraftGuard() && guard.WaitOne(0));
        }

        var oldFile = System.Text.Json.JsonSerializer.Deserialize<Settings>(
                          "{\"name\":\"ALOY\",\"grain\":false,\"vignette\":false,\"frost\":false,"
                          + "\"wake\":false,\"legible\":false,\"parallax\":false,\"shuffle\":true,"
                          + "\"log\":true}")
                          ?? new Settings();
        Check("a settings file carrying the removed switches still reads",
              oldFile.Name == "ALOY");

        Check("a settings file with no offer key still offers the backdrop", oldFile.OfferBackdrop);

        var offerOff = System.Text.Json.JsonSerializer.Deserialize<Settings>(
            System.Text.Json.JsonSerializer.Serialize(new Settings { OfferBackdrop = false }));
        Check("never show again survives the round trip", offerOff is { OfferBackdrop: false });

        var sample = new byte[64];
        BackdropSource.PngSignature.CopyTo(sample, 0);
        var sampleHash = BackdropSource.HexSha256(sample);
        Check("the backdrop source is https on the named host",
              BackdropSource.Url.StartsWith("https://" + BackdropSource.Host + "/", StringComparison.Ordinal)
              && BackdropSource.Sha256.Length == 64
              && BackdropSource.Sha256.All(Uri.IsHexDigit)
              && BackdropSource.Bytes > 0);
        Check("the exact body is accepted",
              BackdropSource.Problem(sample.Length, sampleHash, sample, sample.Length, sampleHash) is null);
        Check("a body of the wrong length is refused",
              BackdropSource.Problem(sample.Length + 1, sampleHash, sample, sample.Length, sampleHash)
                  is { } shortRefusal && shortRefusal.Contains("bytes"));
        Check("a body that is not a PNG is refused",
              BackdropSource.Problem(sample.Length, sampleHash, new byte[64], sample.Length, sampleHash)
                  is { } pngRefusal && pngRefusal.Contains("PNG"));
        Check("a body with the wrong fingerprint is refused",
              BackdropSource.Problem(sample.Length, new string('0', 64), sample, sample.Length, sampleHash)
                  is { } hashRefusal && hashRefusal.Contains("fingerprint"));
        Check("the fingerprint compare is case-insensitive",
              BackdropSource.Problem(sample.Length, sampleHash.ToUpperInvariant(), sample, sample.Length,
                                     sampleHash) is null);

        var iconBytes = IconSource.Bytes;
        Check("D-237: the icon's source is one https file on the backdrop's host, with a pinned hash and length",
              IconSource.Url.StartsWith("https://" + IconSource.Host + "/", StringComparison.Ordinal)
              && IconSource.Host == BackdropSource.Host
              && IconSource.Sha256.Length == 64 && IconSource.Sha256.All(Uri.IsHexDigit)
              && iconBytes > 0 && iconBytes < 1_000_000
              && IconSource.Hosted() && IconSource.FileName != BackdropSource.FileName);

        List<Army> taken = [new() { Name = "Rush" }, new() { Name = "Rush copy" }];
        Check("a free army name is left alone",
              ArmyStore.FreeName("Heavy line", taken) == "Heavy line");

        Check("a taken army name gains a copy label",
              ArmyStore.FreeName("Heavy line", [new Army { Name = "Heavy line" }]) == "Heavy line copy");

        Check("a taken copy label counts up rather than colliding",
              ArmyStore.FreeName("Rush", taken) == "Rush copy 2");

        Check("army names are compared without case",
              ArmyStore.FreeName("RUSH", taken) == "RUSH copy 2");

        List<StrikeBoard> shelf = [new() { Name = "Default" }, new() { Name = "Ridge" }];
        var stock = Play.PickedBoard(0, "Default", shelf);
        Check("index 0 is the stock board whatever it is called",
              stock.Board is null && stock.Problem is null);

        Check("a picked board is found by index and name",
              Play.PickedBoard(2, "Ridge", shelf).Board?.Name == "Ridge");

        Check("a picker past the end of the list is refused",
              Play.PickedBoard(3, "Ridge", shelf).Problem == Play.BoardListChanged);

        Check("a board that moved into the slot is refused",
              Play.PickedBoard(1, "Ridge", shelf).Problem == Play.BoardListChanged);

        var stockLayout = StrikeBoard.Stock();
        Check("the stock layout is the game's 8x8 with two placing rows and only terrain the game has",
              stockLayout.Name == Play.StockBoard && stockLayout.Width == StrikeBoard.Size
              && stockLayout.Height == StrikeBoard.Size && stockLayout.PlacementRows == 2
              && stockLayout.Problem() is null);

        var stockTurned = true;
        for (var i = 0; i < stockLayout.Cells.Length; i++)
        {
            if (stockLayout.Cells[i] != stockLayout.Cells[stockLayout.Cells.Length - 1 - i])
            {
                stockTurned = false;
            }
        }

        Check("the stock layout is its own 180-degree rotation, so both players see one picture",
              stockTurned);

        var joinWait = Play.JoinWaitingFor("bore.pub:12345", "a1b2c3d4e5f6a7b8", "GATE01");
        Check("the join wait names neither the address, the room id nor the code",
              joinWait.Length > 0 && !joinWait.Contains("bore.pub") && !joinWait.Contains("12345")
              && !joinWait.Contains("a1b2c3d4e5f6a7b8") && !joinWait.Contains("GATE01"));

        var boardProblems = Library.Boards.Select(b => Library.Check(b, out _)).Where(p => p is not null).ToList();
        Check("every library board builds and is playable", boardProblems.Count == 0);

        Check("library board names are unique and fit the typed cap",
              Library.Boards.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                  == Library.Boards.Count
              && Library.Boards.All(b => b.Name.Length <= Army.MaxName && b.Name.Trim() == b.Name));

        Check("every library army is a size a match can take",
              Library.Armies.All(a => a.Machines.Length >= 1 && a.Machines.Length <= Play.MaxArmy));

        Check("library army names are unique and fit the typed cap",
              Library.Armies.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                  == Library.Armies.Count
              && Library.Armies.All(a => a.Name.Length <= Army.MaxName && a.Name.Trim() == a.Name));

        Check("every library army names its machines",
              Library.Armies.All(a => a.Machines.All(m => m.Trim().Length > 0)));

        Check("the library has every tier on both shelves",
              new[] { Library.Normal, Library.Fun, Library.Mad, Library.Outlandish, Library.Insane }
                  .All(t => Library.Boards.Any(b => b.Tier == t) && Library.Armies.Any(a => a.Tier == t)));

        Check("a random board avoids the names already saved",
              Library.PickBoard(Library.Boards.Skip(1).Select(b => b.Name), new Random(1))?.Name
                  == Library.Boards[0].Name);

        Check("a random army avoids the names already saved",
              Library.PickArmy(Library.Armies.Skip(1).Select(a => a.Name), new Random(1))?.Name
                  == Library.Armies[0].Name);

        Check("the library holds 256 boards and 256 armies",
              Library.Boards.Count == 256 && Library.Armies.Count == 256);

        Check("the starter shelf is 32 real boards, none twice",
              Library.StarterBoards.Length == BoardStore.MaxBoards
              && Library.StarterBoards.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                  == Library.StarterBoards.Length
              && Library.StarterBoards.All(n => Library.Boards.Any(b => b.Name == n))
              && Library.Starter(Library.Boards).Count() == BoardStore.MaxBoards);

        Check("the starter shelf is 32 real armies, none twice",
              Library.StarterArmies.Length == ArmyStore.MaxArmies
              && Library.StarterArmies.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                  == Library.StarterArmies.Length
              && Library.StarterArmies.All(n => Library.Armies.Any(a => a.Name == n))
              && Library.Starter(Library.Armies).Count() == ArmyStore.MaxArmies);

        Check("the boards cap is the armies cap and refuses at the cap",
              BoardStore.MaxBoards == ArmyStore.MaxArmies
              && !BoardStore.HasRoom(BoardStore.MaxBoards)
              && BoardStore.HasRoom(BoardStore.MaxBoards - 1));

        Check("the stock rules table answers every mode",
              new[] { 2, 4, 6, 0 }.All(s => Play.StockRules(s).Draft == 10 && Play.StockRules(s).Victory >= 1));

        Check("the launcher's army cap is the one netplay refuses on",
              Play.MaxArmy == 9);

        var priced = MachineBridge.Parse(
            "0E44B98882BA9AFD876C0DB6144D35F5\tRedeye Watcher\t3\t5\t2\t2\t2\tShot\tBlind\t\n"
            + "2B34B3566FC1ED50071517AE89C5252F\tGrazer\t1\t4\t2\t1\t1\tRam\tRoam\t");
        Check("an army costs the sum of its machines",
              Play.ArmyCost(["0E44B98882BA9AFD876C0DB6144D35F5", "2B34B3566FC1ED50071517AE89C5252F"],
                            priced) == 4);
        Check("an empty army costs nothing and an unknown machine adds nothing",
              Play.ArmyCost([], priced) == 0
              && Play.ArmyCost(["F9433C1448F8F052BD457978CD0BFEC5"], priced) == 0);

        Check("a short army name is left alone",
              Army.CleanName("Rush") == "Rush");

        Check("a long army name is cut to the typed cap",
              Army.CleanName(new string('x', 200)).Length == Army.MaxName);

        Check("an army name loses its leading and trailing space",
              Army.CleanName("  Heavy line  ") == "Heavy line");

        Check("the stored cap leaves room for the longest copy label the army cap allows",
              Army.MaxStoredName >= Army.MaxName + $" copy {ArmyStore.MaxArmies}".Length);

        var mangled = ArmyStore.Normalise(System.Text.Json.JsonSerializer.Deserialize<List<Army>>(
            "[null, {\"name\": null, \"machines\": [\"AB\", null, \"\"]}, {\"machines\": [\"CD\"]}]"));
        Check("a hand-mangled armies.json normalises instead of crashing",
              mangled is [{ Name: "Untitled", Machines: ["AB"] }, { Name: "Untitled", Machines: ["CD"] }]);

        var armyCrowd = Enumerable.Range(0, ArmyStore.MaxArmies + 50)
            .Select(i => new Army { Name = $"a{i}", Machines = [.. Enumerable.Range(0, Play.MaxArmy + 5).Select(j => $"M{j}")] })
            .ToList();
        var armyBounded = ArmyStore.Normalise(armyCrowd);
        Check("a crowded armies.json is cut to the saved caps on read",
              armyBounded.Count == ArmyStore.MaxArmies && armyBounded.All(a => a.Machines.Count == Play.MaxArmy));

        Check("a normalised army name is cut to the stored cap",
              ArmyStore.Normalise([new Army { Name = new string('x', 200) }])[0].Name.Length
                  == Army.MaxStoredName);

        Check("there is room below the saved-army cap",
              ArmyStore.HasRoom(ArmyStore.MaxArmies - 1, replacing: false));

        Check("a new army is refused at the saved-army cap",
              !ArmyStore.HasRoom(ArmyStore.MaxArmies, replacing: false));

        Check("replacing an army is allowed at the cap, because it does not grow the list",
              ArmyStore.HasRoom(ArmyStore.MaxArmies, replacing: true));

        var mangledBoards = BoardStore.Normalise(System.Text.Json.JsonSerializer.Deserialize<List<StrikeBoard>>(
            "[null, {\"Name\": null, \"Width\": 4, \"Height\": 8, \"Cells\": null}]"));
        Check("a hand-mangled boards.json normalises instead of crashing",
              mangledBoards is [{ Name: "Untitled", Width: 4, Height: 8 }]
              && mangledBoards[0].Cells.Length == 32);

        var shaped = BoardStore.Normalise(System.Text.Json.JsonSerializer.Deserialize<List<StrikeBoard>>(
            "[{\"Name\": \"Cove\", \"Width\": 6, \"Height\": 6, \"PlacementRows\": 2, \"Cells\": ["
            + string.Join(',', new int[36]) + "]}]"));
        Check("a D-107 shaped board survives the reader rather than being dropped as not-8x8",
              shaped is [{ Name: "Cove", Width: 6, Height: 6 }]
              && shaped[0].At(5, 5) == Terrain.Grassland);

        Check("a cells list that does not match its shape is dropped rather than crashing the drawer",
              BoardStore.Normalise(System.Text.Json.JsonSerializer.Deserialize<List<StrikeBoard>>(
                  "[{\"Width\": 8, \"Height\": 8, \"Cells\": [0,0,0,0,0,0,0,0,0,0]}]")) is []);

        var crowdedBoards = Enumerable.Range(0, BoardStore.MaxBoards + 50)
            .Select(i => new StrikeBoard { Name = $"b{i}", Width = 8, Height = 8 })
            .ToList();
        Check("a crowded boards.json is cut to the saved cap on read",
              BoardStore.Normalise(crowdedBoards).Count == BoardStore.MaxBoards);

        Check("a shape the game has no memory for is dropped",
              BoardStore.Normalise([new StrikeBoard { Width = 9, Height = 8 }]) is []
              && BoardStore.Normalise([new StrikeBoard { Width = 8, Height = 0 }]) is []);

        Check("a normalised board name is cut to the stored cap boards share with armies",
              BoardStore.Normalise([new StrikeBoard { Name = new string('x', 200) }])[0].Name.Length
                  == Army.MaxStoredName);

        static long RowLight(byte[] pixels, int imageWidth, int x0, int x1, int y)
        {
            long sum = 0;
            for (var x = x0; x < x1; x++)
            {
                var at = ((y * imageWidth) + x) * 4;
                sum += pixels[at] + pixels[at + 1] + pixels[at + 2];
            }

            return sum;
        }

        var reliefBoard = new StrikeBoard
        {
            Width = 2,
            Height = 1,
            Cells = [Terrain.Mountains, Terrain.Chasm],
        };
        var reliefTiles = BoardArt.Tiles(reliefBoard, 16);
        Check("the tile pixels are deterministic and sized to the board",
              reliefTiles.Length == 2 * 16 * 16 * 4
              && reliefTiles.AsSpan().SequenceEqual(BoardArt.Tiles(reliefBoard, 16)));

        var heightTiles = BoardArt.Tiles(new StrikeBoard
        {
            Width = 3,
            Height = 1,
            Cells = [Terrain.Mountains, Terrain.Grassland, Terrain.Chasm],
        }, 16);
        long mountainsLight = 0;
        long grassLight = 0;
        long chasmLight = 0;
        for (var band = 4; band <= 11; band++)
        {
            mountainsLight += RowLight(heightTiles, 48, 0, 16, band);
            grassLight += RowLight(heightTiles, 48, 16, 32, band);
            chasmLight += RowLight(heightTiles, 48, 32, 48, band);
        }

        Check("height paints as light: higher terrain is brighter across its whole face",
              mountainsLight > grassLight && grassLight > chasmLight);

        var seamTile = BoardArt.Tiles(new StrikeBoard { Width = 1, Height = 1 }, 16);
        Check("each tile carries its own grid seam on the bottom row",
              RowLight(seamTile, 16, 0, 16, 15) < RowLight(seamTile, 16, 0, 16, 14));

        var bevelTile = BoardArt.Tiles(new StrikeBoard { Width = 1, Height = 1 }, 16);
        Check("the bevel lights a tile's top-left corner and shadows its bottom-right",
              RowLight(bevelTile, 16, 1, 2, 1) > RowLight(bevelTile, 16, 14, 15, 14) + 60);

        static int Spread(Rgb c)
        {
            return Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        }

        Check("the muted lens greys and darkens every terrain rather than repainting it",
              Terrain.Cycle.All(v => Spread(BoardArt.MutedShade(v)) <= Spread(Terrain.Shade(v)))
              && Terrain.Cycle.All(v => BoardArt.MutedShade(v).R <= Terrain.Shade(v).R
                                        && BoardArt.MutedShade(v).G <= Terrain.Shade(v).G
                                        && BoardArt.MutedShade(v).B <= Terrain.Shade(v).B));

        static long RowSpread(byte[] pixels, int imageWidth, int x0, int x1, int y)
        {
            long sum = 0;
            for (var x = x0; x < x1; x++)
            {
                var at = ((y * imageWidth) + x) * 4;
                var high = Math.Max(pixels[at], Math.Max(pixels[at + 1], pixels[at + 2]));
                var low = Math.Min(pixels[at], Math.Min(pixels[at + 1], pixels[at + 2]));
                sum += high - low;
            }

            return sum;
        }

        var mirrorBoard = new StrikeBoard
        {
            Width = 1,
            Height = 2,
            Cells = [Terrain.Forest, Terrain.Forest],
        };
        var sharp = BoardArt.Tiles(mirrorBoard, 16);
        var frosted = BoardArt.Tiles(mirrorBoard, 16, frostRows: 16);
        long spreadFrosted = 0;
        long spreadSharp = 0;
        long lightFrosted = 0;
        long lightSharp = 0;
        for (var band = 4; band <= 11; band++)
        {
            spreadFrosted += RowSpread(frosted, 16, 0, 16, band);
            spreadSharp += RowSpread(sharp, 16, 0, 16, band);
            lightFrosted += RowLight(frosted, 16, 0, 16, band);
            lightSharp += RowLight(sharp, 16, 0, 16, band);
        }

        Check("the mirror frost flattens and darkens the far half",
              spreadFrosted < spreadSharp && lightFrosted < lightSharp);

        Check("the mirror frost never touches the near half",
              frosted.AsSpan(16 * 16 * 4).SequenceEqual(sharp.AsSpan(16 * 16 * 4)));

        static (int Light, byte Alpha) At(byte[] pixels, int size, int x, int y)
        {
            var at = ((y * size) + x) * 4;
            return (pixels[at] + pixels[at + 1] + pixels[at + 2], pixels[at + 3]);
        }

        var icon = IconArt.Render(256);
        Check("the icon renders at the size asked for",
              icon.Length == 256 * 256 * 4 && IconArt.Render(16).Length == 16 * 16 * 4);

        Check("the icon is a disc: clear corners, solid middle",
              At(icon, 256, 2, 2).Alpha == 0 && At(icon, 256, 253, 253).Alpha == 0
              && At(icon, 256, 128, 128).Alpha == 0xFF);

        var greens = 0;
        for (var i = 0; i < 256 * 256; i++)
        {
            var r = icon[(i * 4) + 0];
            var g = icon[(i * 4) + 1];
            var b = icon[(i * 4) + 2];
            if (icon[(i * 4) + 3] > 0x80 && g > r + 8 && g > b + 8)
            {
                greens++;
            }
        }

        Check("the icon's blocks wear the board's terrain paint", greens > 256 * 256 / 20);

        var topLight = At(icon, 256, 128, 150).Light;
        var sideLight = At(icon, 256, 128, 200).Light;
        Check("a block's top is lit and its side falls into shadow", topLight > sideLight + 30);

        var preShape = new StrikeBoard
        {
            Name = "Cove",
            Width = 3,
            Height = 2,
            Cells = [1, 2, 3, -1, -2, 0],
        };
        var shrunk = preShape.Reshaped(2, 3, 1);
        Check("a reshape keeps what overlaps and fills the rest with Grassland",
              shrunk is { Name: "Cove", Width: 2, Height: 3, PlacementRows: 1 }
              && shrunk.At(0, 0) == 1 && shrunk.At(1, 0) == 2
              && shrunk.At(0, 1) == -1 && shrunk.At(1, 1) == -2
              && shrunk.At(0, 2) == Terrain.Grassland && shrunk.At(1, 2) == Terrain.Grassland);

        var grown = new StrikeBoard { Width = 1, Height = 1, Cells = [3] }.Reshaped(2, 2, 1);
        Check("a grown board keeps its old squares at the same coordinates",
              grown.At(0, 0) == 3 && grown.At(1, 0) == Terrain.Grassland
              && grown.At(0, 1) == Terrain.Grassland && grown.At(1, 1) == Terrain.Grassland);

        static bool ChasmInPlacingRow(StrikeBoard b)
        {
            for (var y = 0; y < b.Height; y++)
            {
                var placing = y < b.PlacementRows || y >= b.Height - b.PlacementRows;
                if (!placing)
                {
                    continue;
                }

                for (var x = 0; x < b.Width; x++)
                {
                    if (b.At(x, y) == Terrain.Chasm)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var randSym = StrikeBoard.Random(6, 7, 2, symmetric: true, new Random(7));
        var randSymAgain = StrikeBoard.Random(6, 7, 2, symmetric: true, new Random(7));
        Check("a seeded random board is deterministic, the asked shape, and in range",
              randSym is { Width: 6, Height: 7, PlacementRows: 2 } && randSym.Cells.Length == 42
              && randSym.Cells.All(v => v is >= Terrain.Chasm and <= Terrain.Mountains)
              && randSym.Cells.AsSpan().SequenceEqual(randSymAgain.Cells));

        var randAsym = StrikeBoard.Random(6, 7, 2, symmetric: false, new Random(7));
        Check("a symmetric random board is a palindrome and an asymmetric one is not",
              randSym.IsSymmetric() && !randAsym.IsSymmetric());

        var chasmInPlacingSeen = false;
        for (var s = 0; s < 200 && !chasmInPlacingSeen; s++)
        {
            if (ChasmInPlacingRow(StrikeBoard.Random(8, 8, 2, symmetric: false, new Random(s))))
            {
                chasmInPlacingSeen = true;
            }
        }

        Check("a random fill can place a Chasm in a placing row now", chasmInPlacingSeen);

        var shapeRng = new Random(3);
        var shapesOk = true;
        for (var t = 0; t < 200; t++)
        {
            var (w, h, d) = StrikeBoard.RandomShape(shapeRng);
            if (w < StrikeBoard.MinRandomSide || w > StrikeBoard.MaxSide
                || h < w || h > StrikeBoard.MaxSide
                || d < 1 || d > Math.Max(1, h / 2))
            {
                shapesOk = false;
            }
        }

        Check("a random shape stays in range, keeps height at least width, and keeps placing rows shallow",
              shapesOk);

        Check("a board save never replaces: a used name gains the armies' copy label",
              BoardStore.FreeName("Cove", [new StrikeBoard { Name = "Cove" }]) == "Cove copy"
              && BoardStore.FreeName("cove",
                     [new StrikeBoard { Name = "Cove" }, new StrikeBoard { Name = "cove copy" }])
                  == "cove copy 2");

        Check("the readable-glass cap passes a dark pixel unchanged",
              Legibility.Cap(40, 44, 48) == ((byte)40, (byte)44, (byte)48));

        var bright = Legibility.Cap(220, 200, 160);
        var brightLum = ((bright.R * 2126) + (bright.G * 7152) + (bright.B * 722)) / 10000;
        Check("the readable-glass cap dims a bright pixel to the knee's range",
              brightLum > Legibility.Ceiling - 1
              && brightLum <= Legibility.Ceiling + (int)((255 - Legibility.Ceiling) * Legibility.Knee) + 1);

        Check("the readable-glass cap keeps the channel order and ratios of what it dims",
              bright.R > bright.G && bright.G > bright.B
              && Math.Abs(((double)bright.R / bright.G) - (220.0 / 200.0)) < 0.02);

        string[] shippedPatterns = ["Strike", "Ram", "Dive", "Shot", "Dash", "Tow"];
        Check("every shipped attack pattern has a display name and a rule",
              shippedPatterns.All(p => Glossary.Attack(p).Display.Length > 0
                                       && Glossary.Attack(p).Rule.Length > 0));

        string[] shippedAbilities = ["Roam", "Stalk", "Scurry", "Climb", "Spread", "Shield",
                                     "Retaliate", "Burn", "Freeze", "Seed", "Unearth", "Blind",
                                     "Enpower", "Spill", "Confuse"];
        Check("every ability in use in the shipped set has a display name and a rule",
              shippedAbilities.All(a => Glossary.Ability(a).Display.Length > 0
                                        && Glossary.Ability(a).Rule.Length > 0));

        Check("the swapped pair is encoded the way the game displays it",
              Glossary.Ability("Scurry").Display == "Climb"
              && Glossary.Ability("Scurry").Rule.Contains("Hills")
              && Glossary.Ability("Climb").Display == "High Ground"
              && Glossary.Ability("Climb").Rule.Contains("Mountains"));

        Check("the shipped enum's Enpower typo never reaches the screen",
              Glossary.Ability("Enpower").Display == "Empower");

        Check("the renamed patterns and abilities show the game's names",
              Glossary.Attack("Strike").Display == "Melee"
              && Glossary.Attack("Dive").Display == "Swoop"
              && Glossary.Attack("Shot").Display == "Gunner"
              && Glossary.Attack("Tow").Display == "Pull"
              && Glossary.Ability("Spread").Display == "Sweep"
              && Glossary.Ability("Spill").Display == "Spray"
              && Glossary.Ability("Confuse").Display == "Whiplash"
              && Glossary.Ability("Roam").Display == "Gallop"
              && Glossary.Ability("Seed").Display == "Growth"
              && Glossary.Ability("Unearth").Display == "Alter Terrain");

        Check("an unknown name comes back as itself with no rule",
              Glossary.Ability("Blight").Display == "Blight" && Glossary.Ability("Blight").Rule == ""
              && Glossary.Attack("Slam").Display == "Slam" && Glossary.Attack("Slam").Rule == "");

        Check("an address is handed back untouched, with no lookup at all",
              TunnelDns.ResolveAsync("159.223.110.159").Result == "159.223.110.159");

        var queries = TunnelDns.QueryUrls("a b&c=d");
        Check("the resolver queries carry the name escaped and ask only the two resolvers",
              queries.Length == 2 && queries.All(q => q.Contains("name=a%20b%26c%3Dd&type=A"))
              && queries[0].StartsWith("https://1.1.1.1/") && queries[1].StartsWith("https://8.8.8.8/")
              && TunnelDns.MaxAnswerBytes <= 64 * 1024);

        Check("the frost scale caps the longer side of the backdrop",
              Play.FrostScale(5120, 2880) == 1600.0 / 5120 && Play.FrostScale(1600, 120000) == 1600.0 / 120000
              && Play.FrostScale(800, 600) == 1.0 && Play.FrostScale(0, 0) == 1.0);

        var resolved = TunnelDns.ResolveAsync("bore.pub").Result;
        Check("bore.pub resolves to something usable, by name or by falling back to an address",
              resolved == "bore.pub" || System.Net.IPAddress.TryParse(resolved, out _));
        Console.WriteLine($"         (tunnel server here resolves to: {resolved})");

        Check("a name that cannot be resolved anywhere comes back unchanged",
              TunnelDns.ResolveAsync("not-a-real-host-strikers-test.invalid").Result
                  == "not-a-real-host-strikers-test.invalid");

        static double Luminance(Rgb c)
        {
            static double Channel(int v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        static double Contrast(Rgb a, Rgb b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        foreach (var palette in new[] { Palette.SlateDark, Palette.HighContrastDark })
        {
            foreach (var (name, ink) in new (string, Rgb)[]
                     {
                         ("text", palette.Text), ("muted", palette.Muted), ("accent", palette.Accent),
                         ("good", palette.Good), ("bad", palette.Bad),
                     })
            {
                Check($"{name} is readable on both the window and the panel",
                      Contrast(ink, palette.Window) >= 4.5 && Contrast(ink, palette.Panel) >= 4.5);
            }
        }

        foreach (var value in Terrain.Cycle)
        {
            Check($"the {Terrain.Name(value)} label is readable on its own tile",
                  Contrast(Terrain.Ink(value), Terrain.Shade(value)) >= 4.5);
        }

        Check("the tonal drift is deterministic and stays inside -1..1",
              Grunge.Tone(37, 91) == Grunge.Tone(37, 91)
              && Enumerable.Range(0, 200).All(i => Math.Abs(Grunge.Tone(i * 7, i * 13)) <= 1.0)
              && Enumerable.Range(0, 200).Select(i => Grunge.Tone(i * 7, i * 13)).Distinct().Count() > 1);

        Check("the per-tile jitter stays inside -9..9",
              Enumerable.Range(0, 8).SelectMany(x => Enumerable.Range(0, 8).Select(y => Grunge.TileJitter(x, y)))
                        .All(j => j is >= -9 and <= 9));
        Check("the jitter separates at least some neighbouring tiles",
              Enumerable.Range(0, 8).Any(x => Grunge.TileJitter(x, 0) != Grunge.TileJitter(x + 1, 0)));

        Check("the paint buttons run -2 to 3 in numerical order",
              Terrain.Cycle.SequenceEqual([-2, -1, 0, 1, 2, 3]));

        var mirrored = new StrikeBoard();
        mirrored.Paint(0, 0, Terrain.Mountains);
        Check("a mirrored stroke paints the 180-degree opposite",
              mirrored.At(0, 0) == Terrain.Mountains && mirrored.At(7, 7) == Terrain.Mountains);

        var freehand = new StrikeBoard();
        freehand.Paint(0, 0, Terrain.Mountains, mirror: false);
        Check("an unmirrored stroke leaves the opposite square alone",
              freehand.At(0, 0) == Terrain.Mountains && freehand.At(7, 7) == Terrain.Grassland);

        Check("an asymmetric board is playable",
              freehand.Problem() is null);

        Check("with mirroring on the far half refuses a stroke",
              Enumerable.Range(0, 4).All(y => !StrikeBoard.CanPaint(y, mirror: true)));

        Check("with mirroring on the near half still takes one",
              Enumerable.Range(4, 4).All(y => StrikeBoard.CanPaint(y, mirror: true)));

        Check("with mirroring off every row takes a stroke",
              Enumerable.Range(0, 8).All(y => StrikeBoard.CanPaint(y, mirror: false)));

        var reach = new StrikeBoard();
        for (var y = 4; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                reach.Paint(x, y, Terrain.Mountains);
            }
        }

        Check("the near half alone still reaches all 64 squares",
              reach.Cells.All(c => c == Terrain.Mountains));

        const string ready = "  both players are in the match (peer role: play)";
        Check("the real green light reads as itself",
              NetplayLine.Read(ready, hosting: true, haveInvite: true).Meaning
                  == LineMeaning.BothPlayersReady);

        Check("a halt whose reason spells the green light is still a halt",
              NetplayLine.Read($"HALT: {ready}", hosting: true, haveInvite: true).Meaning
                  == LineMeaning.Halt);

        Check("a halt reason cannot spell BOTH READY, a write result, or a pairing either",
              NetplayLine.Read("HALT: BOTH READY", hosting: true, haveInvite: true).Meaning
                  == LineMeaning.Halt
              && NetplayLine.Read("HALT: setup written", hosting: true, haveInvite: true).Meaning
                  == LineMeaning.Halt
              && NetplayLine.Read("HALT: both seats filled", hosting: true, haveInvite: true).Meaning
                  == LineMeaning.Halt);

        Check("the halt screen says the match stopped in plain words, with nothing from the wire on it",
              Play.HaltText(disagreement: false) is { Headline: "Stopped" } plainHalt
              && plainHalt.Detail.Contains("stopped the match") && !plainHalt.Detail.Contains("HALT")
              && Play.HaltText(disagreement: true) is { Headline: "Stopped: the two games disagree" } boardsHalt
              && boardsHalt.Detail.Contains("no longer match"));
        Check("the halted Match line says what to do, nothing about the boards, and no line carries a semicolon",
              Play.HaltStatus.StartsWith("Halted.") && Play.HaltStatus.Contains("Stop")
              && !Play.HaltStatus.Contains("boards") && !Play.HaltStatus.Contains(';')
              && !Play.HaltText(false).Detail.Contains(';') && !Play.HaltText(true).Detail.Contains(';'));

        Check("a player leaving reads as that, on the PC that left and on the other, and a desync does not",
              NetplayLine.Read("HALT: a player left the match before it ended, so it cannot go on", true, true)
                  is { Meaning: LineMeaning.Halt, PlayerLeft: true, LeftHere: true, Disagreement: false }
              && NetplayLine.Read("HALT: the other PC stopped the match: a player left the match before it ended, " +
                                  "so it cannot go on", true, true)
                  is { Meaning: LineMeaning.Halt, PlayerLeft: true, LeftHere: false }
              && NetplayLine.Read("HALT: desync after turn 1: pieces differ (raised on seat 0)", true, true)
                  is { PlayerLeft: false }
              && Play.LeftText(true).Headline.Contains("you left") && Play.LeftText(false).Headline.Contains("opponent")
              && !Play.LeftText(true).Detail.Contains(';') && !Play.LeftText(false).Detail.Contains(';'));

        Check("a REFUSED line is a halt wherever it says so",
              NetplayLine.Read("REFUSED: their setup is not acceptable", hosting: false, haveInvite: true).Meaning
                  == LineMeaning.Halt);

        Check("a relay refusal is NOT reported as the two games disagreeing",
              !NetplayLine.Read("HALT: room full", true, true).Disagreement
              && NetplayLine.Read("HALT: hash compared before pairing, no seat", true, true).Disagreement);

        Check("a real desync is reported as the two games disagreeing, pieces or terrain",
              NetplayLine.Read("HALT: desync after turn 4: pieces differ (raised on seat 1)", true, true) is
              { Meaning: LineMeaning.Halt, Disagreement: true }
              && NetplayLine.Read("HALT: desync after turn 1: terrain differ (raised on seat 0)", true, true) is
              { Meaning: LineMeaning.Halt, Disagreement: true });

        const string tunnel = "  tunnel open: --server bore.pub:12345";
        Check("the tunnel address is read on a host that has no invite yet",
              NetplayLine.Read(tunnel, hosting: true, haveInvite: false, tunnelHost: "bore.pub") is
              { Meaning: LineMeaning.TunnelAddress, Address: "bore.pub:12345" });

        Check("no address is read once the invite exists, or on the joiner",
              NetplayLine.Read(tunnel, hosting: true, haveInvite: true, tunnelHost: "bore.pub").Meaning
                  != LineMeaning.TunnelAddress
              && NetplayLine.Read(tunnel, hosting: false, haveInvite: false, tunnelHost: "bore.pub").Meaning
                  != LineMeaning.TunnelAddress);

        Check("only netplay's own tunnel line is an address, never the tunnel server's words or a halt",
              NetplayLine.Read("  Warning: the tunnel DIED, unexpected first message from the server: " +
                               "--server evil.example:9000", hosting: true, haveInvite: false,
                               tunnelHost: "evil.example").Meaning == LineMeaning.TunnelDown
              && NetplayLine.Read("  tunnel: server error: --server evil.example:9000", hosting: true,
                                  haveInvite: false, tunnelHost: "evil.example").Meaning != LineMeaning.TunnelAddress
              && NetplayLine.Read("HALT: --server evil.example:1234", hosting: true, haveInvite: false,
                                  tunnelHost: "evil.example").Meaning == LineMeaning.Halt);

        Check("a tunnel line naming another host, or read with no host known, is not an address",
              NetplayLine.Read("  tunnel open: --server evil.example:9000", hosting: true, haveInvite: false,
                               tunnelHost: "bore.pub").Meaning != LineMeaning.TunnelAddress
              && NetplayLine.Read(tunnel, hosting: true, haveInvite: false).Meaning != LineMeaning.TunnelAddress);

        Check("the tunnel's public port is a real port",
              NetplayLine.Read("  tunnel open: --server bore.pub:0", hosting: true, haveInvite: false,
                               tunnelHost: "bore.pub").Meaning != LineMeaning.TunnelAddress
              && NetplayLine.Read("  tunnel open: --server bore.pub:70000", hosting: true, haveInvite: false,
                                  tunnelHost: "bore.pub").Meaning != LineMeaning.TunnelAddress
              && NetplayLine.Read("  tunnel open: --server bore.pub:65535", hosting: true, haveInvite: false,
                                  tunnelHost: "bore.pub") is { Meaning: LineMeaning.TunnelAddress, Address: "bore.pub:65535" });

        Check("the two not-ready role lines are told apart from the ready one",
              NetplayLine.Read("  waiting: the other player is still in setup (peer role: lobby)", true, true).Meaning
                  == LineMeaning.PeerStillInSetup
              && NetplayLine.Read("  <- the other player is in the match (peer role: play)", true, true).Meaning
                  == LineMeaning.PeerMovedToPlay);

        Check("a fingerprint line hands back the code it carries",
              NetplayLine.Read("  encrypted, fingerprint 1E1833535E873CBD", true, true) is
              { Meaning: LineMeaning.Fingerprint, Code: "1E1833535E873CBD" });

        Check("the peer's safety-code confirmation is read, and a halt spelling it is still a halt",
              NetplayLine.Read("  <- they confirmed the safety code", true, true).Meaning == LineMeaning.PeerConfirmed
              && NetplayLine.Read("  <- they confirmed the safety code, then left", true, true).Meaning
                  != LineMeaning.PeerConfirmed
              && NetplayLine.Read("HALT: <- they confirmed the safety code", true, true).Meaning == LineMeaning.Halt);

        Check("the other player leaving is read, proves the link, and a halt spelling it is still a halt",
              NetplayLine.Read("  <- the other player left", true, true).Meaning == LineMeaning.PeerLeft
              && NetplayLine.ProvesTheLink(LineMeaning.PeerLeft)
              && NetplayLine.Read("  <- the other player left the room", true, true).Meaning != LineMeaning.PeerLeft
              && NetplayLine.Read("HALT: <- the other player left", true, true).Meaning == LineMeaning.Halt);

        Check("the other player starting over is read from the lobby's line, whole",
              NetplayLine.Read("  the other player started over, so our build and setup go out again", false, true).Meaning
                  == LineMeaning.PeerStartedOver
              && NetplayLine.ProvesTheLink(LineMeaning.PeerStartedOver)
              && NetplayLine.Read("  the other player started over", true, true).Meaning != LineMeaning.PeerStartedOver
              && NetplayLine.Read("HALT: the other player started over, so our build and setup go out again", true, true).Meaning
                  == LineMeaning.Halt);

        Check("a confirmation refused for naming an old code reads as the code changing, not as a halt",
              NetplayLine.Read("  nothing was confirmed: the safety code changed since it was compared", false, true).Meaning
                  == LineMeaning.CodeChanged
              && NetplayLine.Read("  nothing was confirmed: there is no safety code on this link yet", false, true).Meaning
                  != LineMeaning.CodeChanged
              && NetplayLine.Read("HALT: nothing was confirmed: the safety code changed since it was compared", true, true).Meaning
                  == LineMeaning.Halt);

        Check("a retry line reads as the link being down, behind the halt",
              NetplayLine.Read("  link down (SocketException); retrying in 2s", true, true).Meaning
                  == LineMeaning.LinkDown
              && NetplayLine.Read("  link down (IOException); retrying in 15s", false, true).Meaning
                  == LineMeaning.LinkDown
              && NetplayLine.Read("HALT: link down (x); retrying in 1s", true, true).Meaning
                  == LineMeaning.Halt);

        var everyMeaning = Enum.GetValues<LineMeaning>();
        Check("every meaning is judged on whether it proves the link, and the local ones do not",
              everyMeaning.All(m => NetplayLine.ProvesTheLink(m)
                                    == m is not (LineMeaning.Nothing or LineMeaning.TunnelAddress
                                                 or LineMeaning.Halt or LineMeaning.PortInUse
                                                 or LineMeaning.Crashed or LineMeaning.TunnelDown
                                                 or LineMeaning.LinkDown or LineMeaning.CodeChanged
                                                 or LineMeaning.OtherVersionTried))
              && everyMeaning.Count(NetplayLine.ProvesTheLink) == everyMeaning.Length - 9);

        Check("netplay's own status lines read as themselves, whole",
              NetplayLine.Read("    in sync", true, true).Meaning == LineMeaning.InSync
              && NetplayLine.Read("  auto ON (seat 0).", true, true).Meaning
                  == LineMeaning.MatchRunning
              && NetplayLine.Read("BOTH READY, challenge 0ECEA5D9B9F841908D9716A1421F0AEC", false, true).Meaning
                  == LineMeaning.BothReady
              && NetplayLine.Read("BOTH READY, challenge ", false, true).Meaning == LineMeaning.BothReady
              && NetplayLine.Read("    setup written", true, true).Meaning == LineMeaning.WriteSucceeded
              && NetplayLine.Read("  no challenge agreed", true, true).Meaning == LineMeaning.WriteFailed
              && NetplayLine.Read("  Warning: the tunnel DIED, could not reach 159.223.110.159:7835 in 3s", true, true).Meaning
                  == LineMeaning.TunnelDown
              && NetplayLine.Read("   at System.Net.Sockets.Socket.Bind: Only one usage of each socket address is normally permitted.", true, true).Meaning
                  == LineMeaning.PortInUse
              && NetplayLine.Read("Unhandled exception. System.IO.IOException: broken", true, true).Meaning
                  == LineMeaning.Crashed);

        Check("a halt relayed from the other PC reads as a halt",
              NetplayLine.Read("HALT: the other PC stopped the match: could not read the local turn automatically: " +
                               "no machine of ours acted", true, true) is { Meaning: LineMeaning.Halt, Disagreement: false }
              && NetplayLine.Read("HALT: the other PC stopped the match: desync after turn 4: pieces differ", true, true)
                  is { Meaning: LineMeaning.Halt, Disagreement: true });

        Check("the match-over line and the unshared-match notice read as themselves",
              NetplayLine.Read("  match over: you won, 1 point(s) to 0; 2 turn(s) sent, 2 applied", true, true).Meaning
                  == LineMeaning.MatchOver
              && NetplayLine.Read("  a new match started on this PC after the shared one ended: it is not shared " +
                                  "with the other player", true, true).Meaning == LineMeaning.UnsharedMatch
              && !Play.MatchOverText().Detail.Contains(';') && !Play.UnsharedMatchText().Detail.Contains(';'));

        var spelled = new[]
        {
            "both players are in the match (peer role: play)",
            "waiting: the other player is still in setup (peer role: lobby)",
            "<- the other player is in the match (peer role: play)",
            "BOTH READY, challenge 0ECEA5D9B9F841908D9716A1421F0AEC",
            "setup written",
            "not both ready yet, run status",
            "room ABC: both seats filled",
            "paired, seat 0",
            "in sync",
            "auto ON (seat 0).",
            "link down (SocketException); retrying in 1s",
            "Warning: the tunnel DIED, x",
            "could not reach a:1 in 3s",
            "<- their setup: 1 machine(s), challenge 0ECEA5D9B9F841908D9716A1421F0AEC, rules 99/1",
            "match over: you won, 1 point(s) to 0",
            "a new match started on this PC after the shared one ended",
            "REFUSED: This test build of Strikers is no longer active",
        };
        var hashLinesReadAsNothing = spelled.All(s =>
            NetplayLine.Read($"  <- hash for turn 3: pieces {s} terrain 0000000000000000", true, true).Meaning
                == LineMeaning.Nothing
            && NetplayLine.Read($"  <- hash for turn 3: pieces 0000000000000000 terrain {s}", false, true).Meaning
                == LineMeaning.Nothing);
        Check("a peer-chosen hash string cannot spell a status, a green light or a setup",
              hashLinesReadAsNothing);

        Check("a peer's display name on a live-probe line cannot read as a pairing",
              NetplayLine.Read("    their corner       0x1F6CE2D02B0  6 bytes  'PAIRED'", true, true).Meaning
                  == LineMeaning.Nothing
              && NetplayLine.Read("    their turn banner  0x1F199AF0270  15 bytes  'PAIRED'S TURN'", true, true).Meaning
                  == LineMeaning.Nothing);

        Check("a halt or refusal spelling a tunnel or port failure is a halt",
              NetplayLine.Read("HALT: the tunnel DIED, x", true, true).Meaning == LineMeaning.Halt
              && NetplayLine.Read("HALT: Only one usage of each socket address", true, true).Meaning
                  == LineMeaning.Halt
              && NetplayLine.Read("REFUSED: could not reach a:1 in 3s", false, true).Meaning
                  == LineMeaning.Halt);

        Check("the host's setup line hands back the challenge and the rule numbers",
              NetplayLine.Read($"  <- their setup: 2 machine(s), challenge {easy}, rules 20/40", false, true) is
              { Meaning: LineMeaning.TheirSetup, Challenge: easy, VictoryPoints: 20, DraftPoints: 40 });

        Check("our own pairing is not read as the room filling",
              NetplayLine.Read("  paired, seat 0", true, true).Meaning == LineMeaning.Paired
              && NetplayLine.Read("room ABC123: both seats filled", true, true).Meaning
                  == LineMeaning.BothSeatsFilled);

        Check("ordinary chatter means nothing at all",
              NetplayLine.Read("  loading presets", true, true).Meaning == LineMeaning.Nothing
              && NetplayLine.Read("", true, true).Meaning == LineMeaning.Nothing);

        uiChecks?.Invoke(Check);

        var toShare = new StrikeBoard();
        toShare.Paint(2, 1, Terrain.Marsh);
        toShare.Paint(5, 3, Terrain.Chasm);
        var shared = toShare.ToShareString();
        var back = StrikeBoard.FromShareString(shared, out var shareProblem);
        Check("a shared board round-trips every square",
              back is not null && shareProblem is null && back.Cells.SequenceEqual(toShare.Cells));

        Check("a truncated shared board is refused, not half-applied",
              StrikeBoard.FromShareString(shared[..^6], out var cutProblem) is null && cutProblem is not null);

        var corrupted = shared[..12] + (shared[12] == '0' ? '1' : '0') + shared[13..];
        Check("a shared board with one square altered fails its check digits",
              StrikeBoard.FromShareString(corrupted, out _) is null);

        Check("something that is not a shared board at all says so",
              StrikeBoard.FromShareString("hello", out var notABoard) is null &&
              notABoard!.Contains(StrikeBoard.SharePrefix));

        var asymmetric = new StrikeBoard();
        asymmetric.Paint(0, 0, Terrain.Marsh);
        Check("an over-long shared board is refused before it is walked",
              StrikeBoard.FromShareString(StrikeBoard.SharePrefix + new string('a', 400), out var tooLong) is null
              && tooLong!.Contains("longer")
              && StrikeBoard.MaxShareLength >= asymmetric.ToShareString().Length);

        Check("a shared 8x8 board is 24 characters",
              shared.Length == 24);

        var futurePayload = "zzee";
        var futureHash = 2166136261u;
        foreach (var c in futurePayload)
        {
            futureHash = (futureHash ^ c) * 16777619u;
        }

        var futureDigits = (int)(futureHash % (36 * 36 * 36));
        var futureCode = new char[3];
        for (var i = 2; i >= 0; i--)
        {
            futureCode[i] = "0123456789abcdefghijklmnopqrstuvwxyz"[futureDigits % 36];
            futureDigits /= 36;
        }

        Check("a reserved head with good check digits reads as a newer version, not a damaged paste",
              StrikeBoard.FromShareString(
                  StrikeBoard.SharePrefix + futurePayload + new string(futureCode),
                  out var futureProblem) is null &&
              futureProblem!.Contains("newer"));

        var uneven = new StrikeBoard();
        uneven.Paint(2, 1, Terrain.Marsh);
        uneven.Paint(5, 3, Terrain.Chasm);
        uneven.Paint(0, 0, Terrain.Mountains, mirror: false);
        var unevenShared = uneven.ToShareString();
        var unevenBack = StrikeBoard.FromShareString(unevenShared, out var unevenProblem);
        Check("an asymmetric board sends both halves, and is 16 characters longer for it",
              unevenShared.Length == shared.Length + 16 && unevenProblem is null &&
              unevenBack!.Cells.SequenceEqual(uneven.Cells));

        var odd = new StrikeBoard { Width = 5, Height = 5, PlacementRows = 2 };
        odd.Paint(1, 3, Terrain.Forest);
        odd.Paint(2, 2, Terrain.Hills);
        var oddBack = StrikeBoard.FromShareString(odd.ToShareString(), out var oddProblem);
        Check("an odd-sized board round-trips, middle square included",
              oddProblem is null && oddBack is { Width: 5, Height: 5 } &&
              oddBack.Cells.SequenceEqual(odd.Cells) && oddBack.At(2, 2) == Terrain.Hills);

        Check("a shared board still reads after being shouted in upper case",
              StrikeBoard.FromShareString(shared.ToUpperInvariant(), out _) is
              { } shouted && shouted.Cells.SequenceEqual(toShare.Cells));

        var crowd = new List<StrikeBoard>();
        var crowdedName = new string('w', Army.MaxName);
        var collided = false;
        for (var i = 0; i < 105 && !collided; i++)
        {
            var free = BoardStore.FreeName(crowdedName, crowd);
            collided = free.Length > Army.MaxStoredName ||
                       crowd.Any(b => string.Equals(b.Name, free, StringComparison.OrdinalIgnoreCase));
            crowd.Add(new StrikeBoard { Name = free });
        }

        Check("a board copy label never overflows the stored cap or collides after read-back",
              !collided);

        Check("the store clamps a depth the share head cannot carry",
              BoardStore.Normalise([new StrikeBoard { PlacementRows = 0 },
                                    new StrikeBoard { PlacementRows = 9 }])
                  is [{ PlacementRows: 1 }, { PlacementRows: 4 }]);

        var stray = new StrikeBoard();
        stray.Cells[10] = 9;
        Check("a square holding a value no terrain owns makes the board unplayable",
              stray.Problem() is { } strayProblem && strayProblem.Contains("not a terrain"));

        var tornHead = ("0123456789abcdefghijklmnopqrstuvwxyz".IndexOf(shared[3]) * 36) +
                       "0123456789abcdefghijklmnopqrstuvwxyz".IndexOf(shared[4]) + 6;
        var torn = shared[..3] +
                   "0123456789abcdefghijklmnopqrstuvwxyz"[tornHead / 36] +
                   "0123456789abcdefghijklmnopqrstuvwxyz"[tornHead % 36] +
                   shared[5..];
        Check("a mangled head is blamed on the trip, not the board's design",
              StrikeBoard.FromShareString(torn, out var tornProblem) is null &&
              tornProblem!.Contains("check digits"));

        Check("the match path ALLOWS a board wider than it is deep",
              Play.PlayableBoard(new int[40], 8, 5) is { Count: 40 } &&
              Play.PlayableBoard(new int[40], 5, 8) is { Count: 40 });

        Check("the match path still refuses a count the shape does not imply",
              Play.PlayableBoard(new int[39], 8, 5) is null &&
              Play.PlayableBoard(new int[40], 9, 5) is null);

        Check("an unknown terrain value cannot lift a plate past the mountain level",
              BoardArt.PlateLevels(20) == BoardArt.PlateLevels(Terrain.Mountains) &&
              BoardArt.PlateLevels(-9) == 0);

        var loud = new StrikeBoard { Width = 1, Height = 1, PlacementRows = 1 };
        loud.Cells[0] = 60;
        var loudPixels = BoardArt.Tiles(loud, 16, 0);
        var centre = ((8 * 16) + 8) * 4;
        Check("an unknown terrain value still draws loud, not wrapped to near-black",
              loudPixels[centre + 2] > 100 && loudPixels[centre] > 100);

        var oblong = new StrikeBoard { Width = 8, Height = 5, PlacementRows = 2 };
        oblong.Paint(1, 4, Terrain.Mountains, mirror: false);
        var oblongBack = StrikeBoard.FromShareString(oblong.ToShareString(), out var oblongProblem);
        Check("a shared rectangle comes back the same shape, depth and terrain",
              oblongProblem is null && oblongBack is { Width: 8, Height: 5, PlacementRows: 2 } &&
              oblongBack.At(1, 4) == Terrain.Mountains);

        Check("a short toast holds its base time",
              Toasts.HoldMs(ToastKind.Info, 8) == 3500 && Toasts.HoldMs(ToastKind.Good, 40) == 3500);

        Check("a long toast earns reading time and stops at the cap",
              Toasts.HoldMs(ToastKind.Info, 60) == 4100 && Toasts.HoldMs(ToastKind.Info, 400) == 8000);

        Check("a bad toast never leaves faster than its floor",
              Toasts.HoldMs(ToastKind.Bad, 8) == 6000);

        Check("a bad toast reads longer but still ends",
              Toasts.HoldMs(ToastKind.Bad, 140) == 6500 && Toasts.HoldMs(ToastKind.Bad, 900) == 12000);

        string[] shareRoster =
        [
            "Burrower", "Grazer", "Leaplasher", "Scrounger", "Spikesnout", "Bristleback",
            "Charger", "Fanghorn", "Glinthawk", "Lancehorn", "Longleg", "Plowhorn",
            "Clawstrider", "Elemental Clawstrider", "Apex Clawstrider", "Slaughterspine",
        ];
        string[] pickedArmy = ["Burrower", "Elemental Clawstrider", "Grazer"];
        var sharedArmy = ArmyShare.ToShareString(pickedArmy, shareRoster)!;
        var backArmy = ArmyShare.FromShareString(sharedArmy, shareRoster, out var armyProblem);
        Check("a shared army comes back with its machines in order",
              armyProblem is null && backArmy is not null && backArmy.SequenceEqual(pickedArmy));

        Check("a shared army starts with its prefix",
              sharedArmy.StartsWith(ArmyShare.SharePrefix, StringComparison.Ordinal));

        Check("a full army's code is 19 characters",
              ArmyShare.ToShareString(Enumerable.Repeat("Longleg", Play.MaxArmy), shareRoster)!.Length == 19
              && ArmyShare.BodyLength(Play.MaxArmy) == 11);

        Check("a shared army reads the same against a differently ordered roster",
              ArmyShare.FromShareString(sharedArmy, shareRoster.Reverse(), out _) is { } reordered
              && reordered.SequenceEqual(pickedArmy));

        Check("a shared army from another machine list is refused, not misread",
              ArmyShare.FromShareString(sharedArmy, shareRoster.Append("Widemaw"), out var otherRoster) is null
              && otherRoster is not null);

        Check("a truncated shared army is refused, not half read",
              ArmyShare.FromShareString(sharedArmy[..^2], shareRoster, out var cutArmy) is null
              && cutArmy is not null);

        Check("something that is not a shared army is refused",
              ArmyShare.FromShareString("SB-abc123", shareRoster, out _) is null
              && ArmyShare.FromShareString("", shareRoster, out _) is null
              && ArmyShare.FromShareString(null, shareRoster, out _) is null);

        Check("an over-long shared army is refused before it is walked",
              ArmyShare.FromShareString(ArmyShare.SharePrefix + new string('a', 400), shareRoster, out _) is null);

        Check("a shared army may not name more machines than a side can field",
              ArmyShare.ToShareString(Enumerable.Repeat("Burrower", Play.MaxArmy + 1), shareRoster) is null);

        Check("an army naming a machine the roster lacks cannot be shared",
              ArmyShare.ToShareString(["Widemaw"], shareRoster) is null);

        Check("a one-machine army shares and comes back",
              ArmyShare.FromShareString(ArmyShare.ToShareString(["Slaughterspine"], shareRoster),
                                        shareRoster, out _) is { Count: 1 });

        Check("every army size from 1 to the maximum round-trips",
              Enumerable.Range(1, Play.MaxArmy).All(n =>
              {
                  var picks = Enumerable.Range(0, n).Select(i => shareRoster[i % shareRoster.Length]).ToArray();
                  var code = ArmyShare.ToShareString(picks, shareRoster);
                  return code is not null
                         && ArmyShare.FromShareString(code, shareRoster, out _) is { } read
                         && read.SequenceEqual(picks);
              }));

        Check("an unlock that cleared something reads as cleared",
              Play.ReadUnlock("  cleared 39 of 39. Re-open the challenge list to see it.")
                  == Play.UnlockOutcome.Cleared);

        Check("an unlock with nothing left to do is not a failure",
              Play.ReadUnlock("\n  BoardGame: 0 challenge(s) carry a prerequisite\n  nothing to do.")
                  == Play.UnlockOutcome.AlreadyClear);

        Check("an unlock that said neither is a failure",
              Play.ReadUnlock(null) == Play.UnlockOutcome.Unreachable
              && Play.ReadUnlock("") == Play.UnlockOutcome.Unreachable);

        Check("a slow wait names the log file",
              Play.WaitingText("", "Opening a public address", true, Play.SlowAfter).Detail.Contains(MatchLog.Name)
              && !Play.WaitingText("", "Opening a public address", true, Play.SlowAfter).Detail.Contains("Settings"));

        var logDir = Directory.CreateTempSubdirectory("strikers-log-");
        try
        {
            var log = new MatchLog(logDir.FullName);
            log.Write("first line");
            Check("a line written to the match log is read back with its stamp",
                  File.ReadAllText(log.Path).Contains("first line")
                  && File.ReadAllText(log.Path).TrimStart().Length > "first line".Length + 12);

            File.WriteAllText(log.Path, new string('x', (int)MatchLog.MaxBytes + 1));
            log.Write("after the roll");
            Check("the match log rolls to its predecessor past the cap",
                  File.Exists(log.PreviousPath)
                  && new FileInfo(log.PreviousPath).Length > MatchLog.MaxBytes
                  && new FileInfo(log.Path).Length < 200
                  && File.ReadAllText(log.Path).Contains("after the roll"));

            var masked = new MatchDriver(a => a(), log) { RoomCode = "SECRET7" };
            var seen = new List<string>();
            masked.Output += seen.Add;
            masked.OnLine("lobby SECRET7 open", masked.Generation);
            masked.Say("> netplay --room SECRET7");
            var file = File.ReadAllText(log.Path);
            Check("the driver masks the room code before the file and the screen alike",
                  !file.Contains("SECRET7") && file.Contains("lobby ****** open")
                  && file.Contains("--room ******")
                  && seen.Count == 1 && seen[0] == "lobby ****** open");

            var fingerprintLine = "  encrypted, fingerprint 56266CBB6C879E84";
            var spawnLine = "> netplay --room-id f9766870e760ac5bb56f07da --server 159.223.110.159:24877";
            seen.Clear();
            masked.OnLine(fingerprintLine, masked.Generation);
            masked.Say(spawnLine);
            var record = File.ReadAllText(log.Path);

            Check("the safety fingerprint does not reach the file",
                  !record.Contains("56266CBB6C879E84")
                  && record.Contains("encrypted, fingerprint ********"));

            Check("the routing id does not reach the file, so a report cannot open the room",
                  !record.Contains("f9766870e760ac5bb56f07da") && record.Contains("--room-id ******"));


            Check("the tunnel address DOES stay in the file, on purpose",
                  record.Contains("--server 159.223.110.159:24877"));

            Check("the launcher still reads the fingerprint, which the file no longer holds",
                  seen.Count == 1 && Play.Fingerprint(seen[0]) == "56266CBB6C879E84"
                  && Play.Fingerprint(MatchDriver.ForTheRecord(seen[0])) is null);

            foreach (var spelling in new[]
                     {
                         "  room 29CB9690657048F301F3783C created",
                         "  room 29CB9690657048F301F3783C: both seats filled",
                         "  room 29CB9690657048F301F3783C: peer took seat 1",
                         "  room id: 29cb9690657048f301f3783c",
                     })
            {
                masked.OnLine(spelling, masked.Generation);
            }

            var relayLines = File.ReadAllText(log.Path);
            Check("every spelling of the routing id is masked, not just the spawn line's flag",
                  !relayLines.Contains("29CB9690657048F301F3783C")
                  && !relayLines.Contains("29cb9690657048f301f3783c"));

            Check("the room code's own lines are left alone by the routing-id mask",
                  MatchDriver.ForTheRecord("  room code: ******") == "  room code: ******"
                  && MatchDriver.ForTheRecord("> netplay --room ******") == "> netplay --room ******");

            var installFolder = @"C:\Users\Someone\Games\Strikers\";
            var spawnWithProbe = MatchDriver.ForTheRecord(
                @"> netplay --play --live-probe C:\Users\Someone\Games\Strikers\live-probe.exe --army x", installFolder);
            var capturing = MatchDriver.ForTheRecord(
                @"     capturing to c:\users\someone\games\strikers\recordings\match-20260911-090000.jsonl", installFolder);
            var workingDir = MatchDriver.ForTheRecord(@"with working directory 'C:\Users\Someone\Games\Strikers'.", installFolder);
            masked.OnLine($"     capturing to {AppContext.BaseDirectory}recordings\\match-20260911-090000.jsonl",
                          masked.Generation);
            var ownFolder = File.ReadAllText(log.Path);
            Check("the install folder never reaches the file, and what is inside it still reads",
                  spawnWithProbe == "> netplay --play --live-probe live-probe.exe --army x"
                  && capturing == @"     capturing to recordings\match-20260911-090000.jsonl"
                  && workingDir == "with working directory '.'."
                  && !ownFolder.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                  && ownFolder.Contains(@"capturing to recordings\match-20260911-090000.jsonl"));

            var runtimeShaped = new System.ComponentModel.Win32Exception(5,
                @"An error occurred trying to start process 'C:\Users\Someone\Games\Strikers\netplay.exe' "
                + @"with working directory 'C:\Users\Someone'. Access is denied.");
            var reason = MatchDriver.StartFailedReason(runtimeShaped);
            Check("a process that would not start is told by its reason, never by its paths",
                  reason == new System.ComponentModel.Win32Exception(5).Message
                  && !reason.Contains("Someone")
                  && MatchDriver.StartFailedReason(new InvalidOperationException(@"C:\Users\Someone\x")) == "");

            var pretend = @"C:\Users\Someone\Games\Strikers\armies.json";
            var runtime = $"could not write {pretend}: Access to the path '{pretend}' is denied.";
            var stripped = Play.WithoutPath(runtime, pretend);

            Check("a message about a file names the file and not the path to it",
                  !stripped.Contains(@"C:\Users\Someone")
                  && !stripped.Contains(@"\Games\Strikers")
                  && stripped.Contains("could not write armies.json")
                  && stripped.Contains("path 'armies.json'"));

            Check("a message that never held the path is left as it is",
                  Play.WithoutPath("nothing to report", pretend) == "nothing to report");
        }
        finally
        {
            logDir.Delete(recursive: true);
        }

        Check("the newest recording is picked by its stamp and strangers are ignored",
              Report.NewestRecording(["match-20260901-1200.jsonl", "netplay-auto-x.jsonl",
                                      "match-20260903-2105.jsonl", "notes.txt"])
              == "match-20260903-2105.jsonl"
              && Report.NewestRecording(["notes.txt"]) is null);

        var reportDir = Directory.CreateTempSubdirectory("strikers-report-");
        try
        {
            Check("a folder with nothing to report makes no report",
                  Report.Save(reportDir.FullName, DateTime.Now) is null);

            File.WriteAllText(System.IO.Path.Combine(reportDir.FullName, MatchLog.Name), "log");
            File.WriteAllText(System.IO.Path.Combine(reportDir.FullName, MatchLog.PreviousName), "older");
            var recDir = Directory.CreateDirectory(
                System.IO.Path.Combine(reportDir.FullName, Report.RecordingsFolder));
            File.WriteAllText(System.IO.Path.Combine(recDir.FullName, "match-20260901-1200.jsonl"), "{}");
            File.WriteAllText(System.IO.Path.Combine(recDir.FullName, "match-20260903-2105.jsonl"), "{}");

            var zip = Report.Save(reportDir.FullName, new DateTime(2026, 9, 3, 21, 5, 0));
            var entries = new List<string>();
            if (zip is not null)
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
                {
                    entries.AddRange(archive.Entries.Select(e => e.FullName));
                }

                entries.Sort(StringComparer.Ordinal);
            }

            Check("the report holds the log, its predecessor and the newest recording, nothing else",
                  zip == System.IO.Path.Combine(reportDir.FullName, Report.Folder,
                                                "strikers-report-20260903-210500.zip")
                  && entries.SequenceEqual(["match-20260903-2105.jsonl", MatchLog.PreviousName, MatchLog.Name]));

            var blocked = Directory.CreateDirectory(System.IO.Path.Combine(reportDir.FullName, "blocked"));
            File.WriteAllText(System.IO.Path.Combine(blocked.FullName, MatchLog.Name), "log");
            File.WriteAllText(System.IO.Path.Combine(blocked.FullName, Report.Folder), "in the way");
            var refused = Report.TrySave(blocked.FullName, DateTime.Now, out var why);
            Check("a report folder that cannot be written yields no report and says why",
                  refused is null && why is { Length: > 0 } && why != Report.NothingToReport);

            var reportsDir = System.IO.Path.Combine(reportDir.FullName, Report.Folder);
            foreach (var day in new[] { 4, 5, 6, 7, 8 })
            {
                File.WriteAllText(System.IO.Path.Combine(reportsDir, Report.FileName(new DateTime(2026, 8, day))), "old");
            }

            File.WriteAllText(System.IO.Path.Combine(reportsDir, "strikers-report-mine.txt"), "the player's");
            Report.Save(reportDir.FullName, new DateTime(2026, 9, 11, 9, 0, 0));
            var kept = Directory.GetFiles(reportsDir).Select(p => System.IO.Path.GetFileName(p)).ToList();
            Check("the reports folder keeps the newest few and nothing of the player's is touched",
                  kept.Count(n => n.EndsWith(".zip", StringComparison.Ordinal)) == Report.Keep
                  && !kept.Contains(Report.FileName(new DateTime(2026, 8, 4)))
                  && !kept.Contains(Report.FileName(new DateTime(2026, 8, 5)))
                  && kept.Contains(Report.FileName(new DateTime(2026, 9, 3, 21, 5, 0)))
                  && kept.Contains(Report.FileName(new DateTime(2026, 9, 11, 9, 0, 0)))
                  && kept.Contains("strikers-report-mine.txt"));
        }
        finally
        {
            reportDir.Delete(recursive: true);
        }

        var saves = 0;
        var once = new ReportOnce();
        var said = new List<string>();
        foreach (var attempt in new[] { 3, 3, 3, 4 })
        {
            said.Add(once.For(attempt, () =>
            {
                saves++;
                return $"report {saves}";
            }));
        }

        Check("an attempt writes one report however many halt lines it prints",
              saves == 2 && said.SequenceEqual(["report 1", "report 1", "report 1", "report 2"]));

        var sept30 = new DateOnly(2026, 9, 30);
        Check("a test build's mark names the tester and never the date, and a public build has none",
              TestBuild.Mark("Jane", sept30) == "private test build for Jane"
              && TestBuild.Mark(null, null) is null
              && TestBuild.Mark(null, sept30) == "private test build");
        Check("a test build runs through its last day and refuses from the next, a public build never, and the refusal names no date",
              !TestBuild.Expired(sept30, sept30) && TestBuild.Expired(sept30, sept30.AddDays(1))
              && !TestBuild.Expired(null, sept30.AddYears(10))
              && !TestBuild.ExpiredMessage().Contains(';') && !TestBuild.ExpiredMessage().Contains("2026")
              && !TestBuild.ExpiredMessage().Contains("expir"));

        Check("the no-longer-active refusal reads as a halt flagged inactive, and its screen names no date",
              NetplayLine.Read("REFUSED: This test build of Strikers is no longer active. Ask the person who gave it to you for a new one.", true, true)
                  is { Meaning: LineMeaning.Halt, Inactive: true }
              && NetplayLine.Read("HALT: desync after turn 4: pieces differ", true, true) is { Meaning: LineMeaning.Halt, Inactive: false }
              && !Play.InactiveText().Detail.Contains(';') && !Play.InactiveText().Headline.Contains("expir"));

        Check("a peer's halt reason spelling the inactive sentence is an ordinary halt",
              NetplayLine.Read("HALT: the other PC stopped the match: This test build of Strikers is no longer active. Ask the person who gave it to you for a new one.", true, true)
                  is { Meaning: LineMeaning.Halt, Inactive: false });

        Check("a version number reads as its numbers, and anything else is no version (D-242)",
              Release.Clean("1.2.3") == "1.2.3" && Release.Clean(" v1.02 ") == "1.2"
              && Release.Clean("1.2-beta") is null && Release.Clean("") is null && Release.Clean("1..2") is null
              && Release.Clean("1.2.3.4.5") is null && Release.Clean("123456") is null
              && Release.Clean("-1") is null && Release.Clean("1." + (char)0x0662) is null && Release.Clean(null) is null);

        Check("a commit stamp is a lowercase git hash of 7 to 40 hex digits, anything else is no stamp, and a dev build carries none (D-250)",
              Release.CleanCommit("abc1234") == "abc1234"
              && Release.CleanCommit(" 0123456789abcdef0123456789abcdef01234567 ") == "0123456789abcdef0123456789abcdef01234567"
              && Release.CleanCommit("ABC1234") is null && Release.CleanCommit("abc123") is null
              && Release.CleanCommit(new string('a', 41)) is null && Release.CleanCommit("abc1234;x") is null
              && Release.CleanCommit("") is null && Release.CleanCommit(null) is null
              && Release.Commit(typeof(Release).Assembly) is null && Release.Commit(null) is null);

        Check("a newer version is newer by its numbers, not its text (D-242)",
              Release.Newer("1.10.0", "1.9.9") && !Release.Newer("1.0", "1.0.0") && Release.Newer("1.0.1", "1.0")
              && !Release.Newer("0.9", "1.0") && !Release.Newer("garbage", "1.0") && !Release.Newer("2.0", null));

        Check("the update popup asks on every open while Nexus has a newer version, and never otherwise (D-242)",
              Release.Offer("1.0.0", "1.1.0") && Release.Offer("1.0.9", "1.1")
              && !Release.Offer("1.1.0", "1.1") && !Release.Offer("1.2.0", "1.1.0")
              && !Release.Offer("1.0.0", null) && !Release.Offer(null, "1.1.0") && !Release.Offer("1.0.0", "soon"));

        var nexusAnswer = "{\"data\":{\"legacyModsByDomain\":{\"nodes\":[{\"modId\":2,\"name\":\"x\",\"version\":\"9.9\","
                          + "\"status\":\"published\"},{\"modId\":123,\"version\":\"1.4.0\",\"status\":\"published\"}]}}}";
        Check("the Nexus answer gives the published version of our mod and nothing else (D-242)",
              Release.NewestFrom(nexusAnswer, 123) == "1.4.0" && Release.NewestFrom(nexusAnswer, 2) == "9.9"
              && Release.NewestFrom(nexusAnswer, 77) is null
              && Release.NewestFrom(nexusAnswer.Replace("\"status\":\"published\"}]", "\"status\":\"removed\"}]"), 123) is null
              && Release.NewestFrom(nexusAnswer.Replace("1.4.0", "1.4 beta"), 123) is null
              && Release.NewestFrom(nexusAnswer.Replace("\"modId\":123", "\"modId\":\"123\""), 123) is null
              && Release.NewestFrom("{\"data\":{\"legacyModsByDomain\":{\"nodes\":{\"modId\":123}}}}", 123) is null
              && Release.NewestFrom("{\"data\":null}", 123) is null && Release.NewestFrom("[]", 123) is null
              && Release.NewestFrom("not json", 123) is null && Release.NewestFrom(null, 123) is null
              && Release.NewestFrom("{\"data\":\"" + (char)0xDC00 + "\"}", 123) is null
              && Release.NewestFrom(new string(' ', Release.MaxAnswerBytes) + nexusAnswer, 123) is null);

        var query = System.Text.Json.Nodes.JsonNode.Parse(Release.Query(123))?["query"]?.GetValue<string>() ?? "";
        Check("the query names our game and mod, and the page opened is the fixed Nexus address (D-242)",
              query.Contains("gameDomain: \"horizonforbiddenwest\"") && query.Contains("modId: 123")
              && Release.PageUrl(123) == "https://www.nexusmods.com/horizonforbiddenwest/mods/123?tab=files"
              && Release.NexusApi.StartsWith("https://api.nexusmods.com/", StringComparison.Ordinal));

        Check("a version refusal reads as a halt flagged different versions, from this PC's lobby or the other PC's relay (D-242)",
              NetplayLine.Read("REFUSED: Strikers version mismatch: this PC runs netplay aa, the other netplay bb. Update both PCs to the same Strikers version, then try again.", true, true)
                  is { Meaning: LineMeaning.Halt, DifferentVersions: true }
              && NetplayLine.Read("HALT: the other PC stopped the match: protocol v25, relay speaks v26", false, false)
                  is { Meaning: LineMeaning.Halt, DifferentVersions: true }
              && NetplayLine.Read("HALT: the other PC stopped the match: protocol v25, relay speaks v26 and more", false, false)
                  is { Meaning: LineMeaning.Halt, DifferentVersions: false }
              && NetplayLine.Read("REFUSED: game build mismatch: this PC is A, the other is B.", true, true)
                  is { Meaning: LineMeaning.Halt, DifferentVersions: false }
              && NetplayLine.Read("HALT: desync after turn 4: pieces differ", true, true)
                  is { Meaning: LineMeaning.Halt, DifferentVersions: false });

        Check("the host's relay line about another version reads as a notice, not a halt (D-242)",
              NetplayLine.Read(Release.OtherVersionTried + " (protocol v24)", true, true).Meaning == LineMeaning.OtherVersionTried
              && NetplayLine.Read(Release.OtherVersionTried + " (protocol v24) from 1.2.3.4", true, true).Meaning == LineMeaning.Nothing);

        var behind = Release.DifferentVersionsText("1.0.0", "1.1.0");
        var current = Release.DifferentVersionsText("1.1.0", "1.1.0");
        var unknown = Release.DifferentVersionsText("1.0.0", null);
        var noNumber = Release.DifferentVersionsText(null, null);
        Check("the version messages say who updates, from this PC's and Nexus's numbers only, with no semicolon (D-242)",
              behind.Detail.Contains("You have 1.0.0 and the newest is 1.1.0")
              && current.Detail.Contains("The other player needs to update")
              && unknown.Detail.Contains("Whoever has the older version")
              && noNumber.Detail.StartsWith("Both of you", StringComparison.Ordinal)
              && Release.OfferText("1.0.0", "1.1.0") == "You have 1.0.0. The newest is 1.1.0, on Nexus Mods."
              && new[] { behind.Headline, behind.Detail, current.Detail, unknown.Detail, noNumber.Detail,
                         Release.TriedToJoinText("1.0.0", "1.1.0"), Release.OfferText("1.0.0", "1.1.0") }
                  .All(text => !text.Contains(';')));

        Check("this build carries a version number and a Nexus mod id that is a number (D-242)",
              Release.Version(System.Reflection.Assembly.GetEntryAssembly()) is not null
              && Release.ModId(System.Reflection.Assembly.GetEntryAssembly()) >= 0
              && Release.ModId(null) == 0);

        Check("the version screen is only for a refusal before play, and a halt during a match is an ordinary halt",
              Play.ShowsVersionScreen(true, playStarted: false) && !Play.ShowsVersionScreen(true, playStarted: true)
              && !Play.ShowsVersionScreen(false, playStarted: false));

        Check("the other-version notice shows only to a host whose match has not started",
              Play.ShowsOtherVersionNotice(hosting: true, playStarted: false)
              && !Play.ShowsOtherVersionNotice(hosting: true, playStarted: true)
              && !Play.ShowsOtherVersionNotice(hosting: false, playStarted: false));

        var downloads = @"C:\Users\someone\Downloads\Strikers\";
        var repoBuild = @"C:\Code\Strikers\src\strikers-avalonia\bin\Release\net10.0\";
        Check("a missing netplay or live-probe is looked for beside the exe only, unless the launcher runs from a repo build",
              NetplayTool.Netplay(downloads, _ => false) == downloads + "netplay.exe"
              && NetplayTool.LiveProbe(downloads, _ => false) is null
              && NetplayTool.LiveProbe(@"C:\Program Files\Strikers\", path => !path.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase)) is null
              && NetplayTool.LiveProbe(downloads, _ => true) == downloads + "live-probe.exe"
              && NetplayTool.Netplay(repoBuild, _ => false) == @"C:\Code\Strikers\src\netplay\bin\Release\net10.0\netplay.exe"
              && NetplayTool.LiveProbe(repoBuild, path => path.StartsWith(@"C:\Code\Strikers\src\live-probe\", StringComparison.OrdinalIgnoreCase))
                  == @"C:\Code\Strikers\src\live-probe\bin\Release\net10.0\live-probe.exe"
              && !NetplayTool.InRepoBuild(@"C:\Games\bin\Release\net10.0\"));

        Check("the backdrop is looked for beside the exe only, unless the launcher runs from a repo build",
              BackdropSource.Places(@"C:\Program Files\Strikers\") is [@"C:\Program Files\Strikers"]
              && BackdropSource.Places(repoBuild) is [@"C:\Code\Strikers\src\strikers-avalonia\bin\Release\net10.0", @"C:\Code\Strikers\src\strikers-avalonia\Assets"]);

        var pinDir = Directory.CreateTempSubdirectory("strikers-pin-").FullName;
        try
        {
            var pinned = Path.Combine(pinDir, "icon.png");
            var pinBytes = new byte[64];
            BackdropSource.PngSignature.CopyTo(pinBytes, 0);
            File.WriteAllBytes(pinned, pinBytes);
            var pinSha = BackdropSource.HexSha256(pinBytes);
            Check("an icon file is used only when its length and fingerprint are the pinned download's",
                  BackdropSource.Pinned(pinned, 64, pinSha)
                  && !BackdropSource.Pinned(pinned, 63, pinSha)
                  && !BackdropSource.Pinned(pinned, 64, new string('0', 64))
                  && !BackdropSource.Pinned(Path.Combine(pinDir, "missing.png"), 64, pinSha)
                  && !IconSource.Pinned(pinned));
        }
        finally
        {
            Directory.Delete(pinDir, recursive: true);
        }

        Check("a read that hears nothing inside its quiet time ends as a timeout, and a cancel stays a cancel",
              QuietReadEnds(cancelFirst: false) is TimeoutException && QuietReadEnds(cancelFirst: true) is OperationCanceledException);

        var capped = FetchAgainst(LoopbackAnswer.OverTheCapThenHold);
        var answered = FetchAgainst(LoopbackAnswer.Published);
        Check("an answer past 64 KB is refused as it arrives, not after the host finishes sending, and a small one reads",
              capped.Newest is null && capped.Seconds < 4 && answered.Newest == "1.4.0");

        Check("the port-in-use reading is anchored to the runtime's unhandled-exception head",
              NetplayLine.Read("Unhandled exception. System.Net.Sockets.SocketException (10048): Only one usage of each socket address (protocol/network address/port) is normally permitted.", true, true).Meaning
                  == LineMeaning.PortInUse
              && NetplayLine.Read(" ---> System.Net.Sockets.SocketException (10048): Only one usage of each socket address is normally permitted.", true, true).Meaning
                  == LineMeaning.PortInUse
              && NetplayLine.Read("  <- hash for turn 3: pieces Only one usage of each socket address terrain 0000000000000000", true, true).Meaning
                  == LineMeaning.Nothing);

        Console.WriteLine();
        Console.WriteLine($"  {passed} passed, {failed} failed");
        Console.WriteLine();
        return failed == 0 ? 0 : 1;
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead
        {
            get
            {
                return true;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return false;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return false;
            }
        }

        public override long Length
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException();
            }

            set
            {
                throw new NotSupportedException();
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static Exception? QuietReadEnds(bool cancelFirst)
    {
        using var cancel = new CancellationTokenSource();
        if (cancelFirst)
        {
            cancel.Cancel();
        }

        var read = Streams.ReadWithinAsync(new StalledStream(), new byte[16], TimeSpan.FromMilliseconds(300), cancel.Token);
        var settled = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        if (settled != read)
        {
            return null;
        }

        return read.Exception?.InnerException ?? (read.IsCanceled ? new OperationCanceledException() : null);
    }

    private enum LoopbackAnswer
    {
        OverTheCapThenHold,
        Published,
    }

    private static (string? Newest, double Seconds) FetchAgainst(LoopbackAnswer answer)
    {
        using var hold = new CancellationTokenSource();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var seen = new System.Text.StringBuilder();
            var buffer = new byte[4096];
            while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                seen.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
            }

            if (answer == LoopbackAnswer.Published)
            {
                var body = "{\"data\":{\"legacyModsByDomain\":{\"nodes\":[{\"modId\":123,\"version\":\"1.4.0\",\"status\":\"published\"}]}}}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(bytes);
                return;
            }

            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n");
            await stream.WriteAsync(head);
            var chunk = new byte[Release.MaxAnswerBytes + 1];
            Array.Fill(chunk, (byte)' ');
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"));
            await stream.WriteAsync(chunk);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("\r\n"));
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(6), hold.Token);
        });

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (newest, _) = Release.FetchNewestAsync($"http://127.0.0.1:{port}/", 123, "Strikers-selftest",
                                                       TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8))
                                     .GetAwaiter().GetResult();
            return (newest, clock.Elapsed.TotalSeconds);
        }
        finally
        {
            hold.Cancel();
            listener.Stop();
            try
            {
                server.Wait(TimeSpan.FromSeconds(8));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
