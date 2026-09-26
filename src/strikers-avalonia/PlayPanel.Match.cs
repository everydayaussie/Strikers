using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;

namespace Strikers.App;

public partial class PlayPanel
{
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DateTime waitingSince;
    private string waitingHeadline = "";
    private string waitingFor = "";
    private bool waitingIsTechnical = true;
    private int waitingSlowAfter = Play.SlowAfter;

    private bool waiting;

    private bool writing;

    private bool setupFailed;

    private int linkDowns;
    private (string Headline, string What, bool Technical, int SlowAfter, DateTime Since)? pausedWait;

    private bool everConnected;

    private bool confirmedByMe;
    private bool confirmedByPeer;

    private bool armySent;

    private DateTime? peerLeftAt;

    private bool halted;

    private string? pendingCode;

    private string? sessionBinding;

    private DateTime playSince;

    private void WireTicker()
    {
        tick.Tick += (_, _) => Ticked();
    }

    private void StartWaiting(string headline, string what, bool technical = true,
                              int slowAfter = Play.SlowAfter)
    {
        BeginWait(headline, what, technical, slowAfter, DateTime.UtcNow);
    }

    private void BeginWait(string headline, string what, bool technical, int slowAfter, DateTime since)
    {
        waitingHeadline = headline;
        waitingFor = what;
        waitingIsTechnical = technical;
        waitingSlowAfter = slowAfter;
        waitingSince = since;
        waiting = true;
        Find<Button>("ReportProblemButton").IsVisible = false;
        Ink("StepLine", Palette.SlateDark.Text);
        Ink("DetailLine", Palette.SlateDark.Muted);

        Find<Arc>("WaitSpinner").IsVisible = technical;
        LogScreen(headline, what);
        tick.Start();
        Ticked();
    }

    private string lastScreenLine = "";
    private string lastHeadline = "";

    private void LogScreen(string headline, string detail)
    {
        var full = detail.Length > 0 ? $"{headline} | {detail}" : headline;
        if (full.Length == 0 || full == lastScreenLine)
        {
            return;
        }

        var line = headline.Length == 0 || headline == lastHeadline ? full : headline;
        lastScreenLine = full;
        lastHeadline = headline;
        driver?.Say($"  screen: {line.Replace("\n", " ")}");
    }

    private bool RetitleWait(string headline)
    {
        if (!IsWaiting())
        {
            return false;
        }

        waitingHeadline = headline;
        Ticked();
        return true;
    }

    private bool IsWaiting()
    {
        return waiting;
    }

    private void StopWaiting()
    {
        waiting = false;
        waitingFor = "";
        Find<Arc>("WaitSpinner").IsVisible = false;
    }

    private void InviteMade()
    {
        inviteClock.Heard(Play.InviteClock.Event.Made, DateTime.UtcNow);
        WaitForJoin();
    }

    private void WaitForJoin()
    {
        Find<Button>("NewInviteButton").IsEnabled = true;
        StartWaiting("Send the invite", "Copy it and send it to your opponent.", technical: false);
        ShowInviteClock();
    }

    private void ShowInviteClock()
    {
        var clock = Find<TextBlock>("InviteClock");
        if (inviteClock.Left(DateTime.UtcNow) is not { } left || flow.Current != Stage.HostInvite)
        {
            clock.IsVisible = false;
            return;
        }

        var (text, urgent) = Play.InviteClockText(left);
        Ink("InviteClock", urgent ? Palette.SlateDark.Bad : Palette.SlateDark.Muted);
        clock.Text = text;
        clock.IsVisible = true;
    }

    private const string HostingHeadline = "Creating the invite";

    private void NewAttempt()
    {
        if (Play.CodeIsCleared(Play.CodeEvent.NewAttempt))
        {
            fingerprint.Reset();
        }

        ResetSafetyInputs();
        playStarted = false;
        playSpawned = false;
        peerHere = false;
        writing = false;
        linkDowns = 0;
        pausedWait = null;
        everConnected = false;
        armySent = false;
        peerLeftAt = null;
        halted = false;
        codeShown = false;
        opponentName = null;
        inviteClock.Heard(Play.InviteClock.Event.NewAttempt, DateTime.UtcNow);
        Attention.Settle(TopLevel.GetTopLevel(this) as Window);
        pendingCode = null;
        sessionBinding = null;
        tunnelServer = null;
        Find<Button>("WriteButton").IsEnabled = true;
        ForgetStop();
    }

    private void ResetSafetyInputs()
    {
        codeShown = false;
        DrawEye();
        var typed = Find<TextBox>("TypedCode");
        typed.Text = "";
        typed.IsEnabled = true;
        confirmedByMe = false;
        confirmedByPeer = false;
        sessionBinding = null;
        setupFailed = false;
        Find<Button>("CodeMatchesButton").IsEnabled = false;
        DrawTypedCells();
    }

    private void WaitForTheirArmy()
    {
        Find<ComboBox>("ArmyBox").IsVisible = false;
        Find<ScrollViewer>("ArmyScroll").IsVisible = false;
        ShowArmyHead();
        Find<TextBlock>("ArmyCostLine").IsVisible = false;
        Find<Button>("UseArmyButton").IsVisible = false;
        Reflow();
        StartWaiting("Waiting for your opponent to choose their army", "", technical: false);
    }

    private void BackToTheirArmy(string? toast)
    {
        ResetSafetyInputs();
        var rewound = flow.Rewind(Stage.ChooseArmy);
        if (rewound)
        {
            ShowStage(Stage.ChooseArmy);
            Announce(Stage.ChooseArmy);
        }

        if (armySent)
        {
            WaitForTheirArmy();
        }
        else if (rewound)
        {
            EnterArmyScreen();
        }

        if (toast is null)
        {
            return;
        }

        ToastRail.Show(ToastKind.Info, toast);

        if (armySent)
        {
            return;
        }

        if (chosen is null)
        {
            StartWaiting("Waiting for your opponent to choose their army", toast, technical: false);
            return;
        }

        Say("Choose your army", toast);
    }

    private bool AtPlayStage()
    {
        return writing || playStarted || playSpawned;
    }

    private bool BothConfirmedAtSetUp()
    {
        return Play.BothConfirmedAtSetUp(flow.Current, confirmedByMe, confirmedByPeer);
    }

    private void CheckOpponentLeft()
    {
        if (!Play.ScreenMayChange(halted, startIdle: false))
        {
            peerLeftAt = null;
            return;
        }

        if (peerLeftAt is not { } at)
        {
            return;
        }

        var verdict = Play.JudgeOpponentLeft((DateTime.UtcNow - at).TotalSeconds, AtPlayStage(),
                                             BothConfirmedAtSetUp());
        if (verdict == Play.OpponentLeftVerdict.Nothing)
        {
            return;
        }

        peerLeftAt = null;
        if (verdict == Play.OpponentLeftVerdict.WaitInPlace)
        {
            StartWaiting("Your opponent left",
                         "Waiting for them to come back. If they do not, press Stop.",
                         technical: false);
            return;
        }

        if (Play.CodeIsCleared(Play.CodeEvent.OpponentLeft))
        {
            fingerprint.Reset();
        }

        BackToTheirArmy(armySent ? null : "Your opponent left. The same invite works if they join again.");
        inviteClock.Heard(Play.InviteClock.Event.KeyedPeerGone, DateTime.UtcNow);
        if (armySent)
        {
            StartWaiting("Your opponent left",
                         "Waiting for them to rejoin with the same invite. If they do not, press Stop.",
                         technical: false);
        }
    }

    private void ConfirmCode()
    {
        if (confirmedByMe)
        {
            return;
        }

        if (!Play.SafetyTypedState(fingerprint.Code, Find<TextBox>("TypedCode").Text ?? "", flow.Hosting).Continue)
        {
            return;
        }

        if (!driver.TypeAtLobby([$"confirm {fingerprint.Code}"], "> confirm", Play.PartClosedHeadline))
        {
            return;
        }

        confirmedByMe = true;
        Find<Button>("CodeMatchesButton").IsEnabled = false;
        Find<TextBox>("TypedCode").IsEnabled = false;
        if (confirmedByPeer)
        {
            Go(Stage.SetUp);
            return;
        }

        StartWaiting("Waiting for your opponent to confirm their code", "", technical: false);
    }

    private void LinkRestored()
    {
        everConnected = true;
        var wasLost = linkDowns >= Play.LinkDownAfter;
        linkDowns = 0;
        if (!wasLost)
        {
            return;
        }

        if (pausedWait is { } paused)
        {
            pausedWait = null;
            BeginWait(paused.Headline, paused.What, paused.Technical, paused.SlowAfter, paused.Since);
            return;
        }

        Say("Connected again", "Carry on where you were.");
    }

    private void SayBad(string headline, string detail)
    {
        Say(headline, detail);
        Ink("StepLine", Palette.SlateDark.Bad);
    }

    private void Ticked()
    {
        ShowWaiting();
        ShowPlayClock();
        CheckOpponentLeft();
        CheckInviteWentStale();
        ShowInviteClock();
        CheckReportSettled();

        if (!IsWaiting() && playSpawned is false && peerLeftAt is null && reportPendingSince is null
            && !inviteClock.Running)
        {
            tick.Stop();
        }
    }

    private void CheckInviteWentStale()
    {
        if (!flow.Hosting || AtPlayStage() || !inviteClock.RunOut(DateTime.UtcNow))
        {
            return;
        }

        inviteClock.Heard(Play.InviteClock.Event.RanOut, DateTime.UtcNow);
        if (Play.InviteExpiredText(halted) is not { } expired)
        {
            driver.StopAllAndDropStragglers();
            return;
        }

        flow.Rewind(Stage.HostInvite);
        ShowStage(flow.Current);
        driver.StopAllAndDropStragglers();
        Find<Button>("StopButton").IsVisible = true;
        MaskInvite();
        Find<TextBox>("ShareBox").Text = "";
        Find<Button>("CopyInviteButton").IsEnabled = false;
        SayBad(expired.Headline, expired.Detail);
    }

    private void NewInvite()
    {
        if (flow.Current != Stage.HostInvite)
        {
            ToastRail.Show(ToastKind.Bad, "There is no invite to replace yet.");
            return;
        }

        HostSetup? setup = null;
        var restarted = driver.Restart(() =>
        {
            setup = HostingReady();
            return setup is not null;
        });

        if (!restarted || setup is not { } ready)
        {
            return;
        }

        BeginHosting(ready);
        ToastRail.Show(ToastKind.Good, "The old invite no longer works.");
    }

    private void ShowWaiting()
    {
        if (!IsWaiting())
        {
            return;
        }

        var seconds = (int)(DateTime.UtcNow - waitingSince).TotalSeconds;
        var (step, detail) = Play.WaitingText(waitingHeadline, waitingFor, waitingIsTechnical, seconds,
                                              waitingSlowAfter);
        Show("StepLine", step);
        Show("DetailLine", detail);
    }

    private void ShowPlayClock()
    {
        if (playSpawned is false)
        {
            return;
        }

        ShowClock(Play.SetupWindow - (DateTime.UtcNow - playSince));
    }

    private void ShowClock(TimeSpan left)
    {
        var (text, urgent) = Play.PlayClockText(left);
        Ink("SetUpClock", urgent ? Palette.SlateDark.Bad : Palette.SlateDark.Muted);
        var clock = Find<TextBlock>("SetUpClock");
        clock.Text = text;
        clock.IsVisible = true;
    }

    private readonly record struct HostSetup(ChallengeBridge.Challenge Challenge, List<int>? Board,
                                             (int Width, int Height, int PlacementRows) Shape);

    private HostSetup? HostingReady()
    {
        if (hostChallenge is not { } picked)
        {
            ToastRail.Show(ToastKind.Bad, "The challenge list has not loaded yet. Try again in a moment.");
            return null;
        }

        var (board, shape, boardProblem) = ChosenBoard();
        if (boardProblem is not null)
        {
            ToastRail.Show(ToastKind.Bad, boardProblem);
            RefreshBoards();
            return null;
        }

        return new HostSetup(picked, board, shape);
    }

    private void StartHosting()
    {
        if (!driver.BeginStart())
        {
            return;
        }

        if (HostingReady() is not { } setup)
        {
            driver.CancelStart();
            return;
        }

        BeginHosting(setup);
    }

    private void BeginHosting(HostSetup setup)
    {
        chosen = setup.Challenge;
        hostBoard = setup.Board;
        hostShape = setup.Shape;
        budget = ChosenDraftPoints();

        firstPlayer = Play.FirstFromPick(Find<ComboBox>("FirstBox").SelectedIndex, Random.Shared.Next(2) == 1);
        opponentArmy = [];

        flow.Begin(hosting: true);

        MaskInvite();
        NewAttempt();
        driver.RoomCode = Play.NewRoomCode();
        roomId = Play.NewRoomId();
        Find<TextBox>("ShareBox").Text = "";
        Find<Button>("CopyInviteButton").IsEnabled = false;

        StartWaiting(HostingHeadline, "");

        pages[Stage.Start].IsVisible = false;
        Find<Button>("StopButton").IsVisible = true;
        TellFocus(true);

        var attempt = driver.Generation;

        _ = Task.Run(async () =>
        {
            (string Host, string? Note)? lookup;
            string? failure = null;
            try
            {
                lookup = await TunnelDns.LookUpAsync(TunnelDns.DefaultServer);
            }
            catch (Exception e)
            {
                lookup = null;
                failure = e.GetType().Name;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!driver.StillWanted(attempt))
                {
                    return;
                }

                if (lookup is not { } found)
                {
                    driver.Say($"  network: the lookup failed ({failure})");
                    driver.CancelStart();
                    StopWaiting();
                    SayBad("Could not create the invite", "Press Stop and try again.");
                    return;
                }

                var (server, note) = found;
                if (note is not null)
                {
                    driver.Say($"  network: {note}");
                }

                StartWaiting(HostingHeadline, "");

                if (MatchDriver.FreePort() is not { } port)
                {
                    driver.CancelStart();
                    StopWaiting();
                    SayBad("Could not start", "Windows gave Strikers no free port. Try hosting again.");
                    return;
                }

                tunnelServer = server;
                driver.Spawn(["--relay", "--tunnel", "--tunnel-server", server,
                              "--port", port.ToString(), "--room-id", roomId]);
            });
        });
    }

    private void StartJoining()
    {
        if (!driver.BeginStart())
        {
            return;
        }

        var (address, room, code) = Play.ParseInvite(Find<TextBox>("PasteBox").Text ?? "");
        if (address is null || room is null || code is null)
        {
            driver.CancelStart();
            ToastRail.Show(ToastKind.Bad, NotAnInvite);
            return;
        }

        var pin = Play.PinInvite(address, null);
        if (pin == Play.InvitePin.Refuse)
        {
            driver.CancelStart();
            driver.Say(JoinRefusedLine);
            ToastRail.Show(ToastKind.Bad, NotOurInvite);
            return;
        }

        NewAttempt();
        chosen = null;
        budget = -1;
        firstPlayer = Play.FirstHost;
        opponentArmy = [];
        driver.RoomCode = code;
        roomId = room;
        serverAddress = address;

        flow.Begin(hosting: false);
        Go(Stage.JoinWait);
        StartWaiting("Joining", Play.JoinWaitingFor(address, room, code));
        Find<Button>("StopButton").IsVisible = true;
        TellFocus(true);

        if (pin == Play.InvitePin.Accept)
        {
            SpawnJoin(address, room, code);
            return;
        }

        var attempt = driver.Generation;
        _ = Task.Run(async () =>
        {
            IReadOnlyCollection<string> answers;
            try
            {
                answers = await TunnelDns.AnswersAsync(TunnelDns.DefaultServer);
            }
            catch (Exception)
            {
                answers = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!driver.StillWanted(attempt))
                {
                    return;
                }

                if (Play.PinInvite(address, answers) != Play.InvitePin.Accept)
                {
                    RefuseJoin();
                    return;
                }

                SpawnJoin(address, room, code);
            });
        });
    }

    private const string JoinRefusedLine = "  join refused: the invite does not name the tunnel";

    private void SpawnJoin(string address, string room, string code)
    {
        driver.Spawn(Play.LobbyArgs(hosting: false, code, room, address, Settings.Read().Name,
                                    "", null, -1, -1, Play.FirstHost),
                     keepInput: true);
    }

    private void RefuseJoin()
    {
        driver.Say(JoinRefusedLine);
        driver.StopAll();
        NewAttempt();
        tick.Stop();
        StopWaiting();
        BackToStart();
        ToastRail.Show(ToastKind.Bad, NotOurInvite);
    }

    private void StartPlay()
    {
        if (playSpawned)
        {
            return;
        }

        if (Play.BindingProblem(sessionBinding) is { } unbound)
        {
            SayBad("The safety check did not finish", unbound);
            return;
        }

        playSpawned = true;
        Identity.Refresh();
        playSince = DateTime.UtcNow;
        tick.Start();

        var play = new List<string>(Play.PlayArgs(flow.Hosting, driver.RoomCode, roomId, serverAddress,
                                                  Settings.Read().Name, Play.ArmyIds(army), sessionBinding,
                                                  firstPlayer, armyName));
        if (NetplayTool.LiveProbe() is { } probe)
        {
            play.Add("--live-probe");
            play.Add(probe);
        }

        driver.Spawn(play);
    }

    private void WriteSetup()
    {
        if (driver.TypeAtLobby(["write"], "> write", Play.PartClosedHeadline) is false)
        {
            return;
        }

        Find<Button>("WriteButton").IsEnabled = false;
        writing = true;
        setupFailed = false;

        StartWaiting("Setting up the match",
                     $"This takes about half a minute. {Play.DoNotEnterYet}",
                     technical: true, slowAfter: Play.WriteSlowAfter);
    }

    private void EndLobbyThenPlay()
    {
        if (playStarted || driver.HaveLobby is false)
        {
            return;
        }

        playStarted = true;
        writing = false;

        StartWaiting("Setting up the match", Play.DoNotEnterYet);
        driver.EndLobby();
    }

    private void SendArmy()
    {
        if (army.Count == 0)
        {
            ToastRail.Show(ToastKind.Bad, "Pick an army first.");
            return;
        }

        if (driver.LobbyRunning is false)
        {
            ToastRail.Show(ToastKind.Bad, "Nothing is running. Press Stop and start again.");
            return;
        }

        var cost = Play.ArmyCost(army, machines);
        if (cost > Budget())
        {
            ToastRail.Show(ToastKind.Bad, $"That army costs {cost} and the limit is {Budget()}. Pick a cheaper one.");
            return;
        }

        if (army.Count > Play.MaxArmy)
        {
            ToastRail.Show(ToastKind.Bad, $"That army has {army.Count} machines. The most is {Play.MaxArmy}.");
            return;
        }

        if (flow.Hosting)
        {
            var squares = Play.PlacingSquares(hostShape.Width, hostShape.PlacementRows);
            if (Play.ArmyDoesNotFit(army.Count, squares) is { } tooBig)
            {
                ToastRail.Show(ToastKind.Bad, tooBig);
                return;
            }
        }

        var safe = Play.ArmyIds(army);
        if (safe.Count != army.Count)
        {
            ToastRail.Show(ToastKind.Bad, "That army has a machine Strikers does not know. Rebuild it in the Armies panel.");
            return;
        }

        var sent = driver.TypeAtLobby([$"army {string.Join(' ', safe)}", "place auto", "ready"],
                                      "> army / place auto / ready",
                                      Play.PartClosedHeadline);
        if (sent)
        {
            armySent = true;
            WaitForTheirArmy();
        }
    }

    private static string? SignalDraftGuard()
    {
        try
        {
            NetplayTool.StopDraftGuard();
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name;
        }
    }

    private void StopAll()
    {
        driver.Say(Play.StopPressedLine(flow.Current, halted));
        var unsignalled = SignalDraftGuard();
        if (unsignalled is not null)
        {
            driver.Say($"  could not signal the draft guard ({unsignalled}); it hands the seats back at its own timeout");
        }

        driver.StopAll();
        NewAttempt();
        tick.Stop();
        StopWaiting();
        BackToStart();

        if (unsignalled is null)
        {
            ToastRail.Show(ToastKind.Info, "Stopped.");
            return;
        }

        ToastRail.Show(ToastKind.Bad, "Stopped. Do not back out of the challenge list for ten minutes.");
    }

    public void ShutDown()
    {
        _ = SignalDraftGuard();
        driver.StopAll();
    }

    private void Interpret(string line)
    {
        var read = NetplayLine.Read(line, flow.Hosting && driver.AnythingRunning,
                                    haveInvite: inviteClock.InviteShown,
                                    tunnelHost: tunnelServer);

        if (read.Meaning == LineMeaning.TheirRecording)
        {
            TheirRecordingLanded();
            return;
        }

        if (read.Meaning == LineMeaning.TheirLog)
        {
            TheirLogLanded();
            return;
        }

        if (!Play.ScreenMayChange(halted, flow.Current == Stage.Start && !driver.AnythingRunning))
        {
            return;
        }

        if (NetplayLine.ProvesTheLink(read.Meaning))
        {
            LinkRestored();
        }

        if (NetplayLine.ProvesTheOpponentIsHere(read.Meaning, BothConfirmedAtSetUp()))
        {
            peerLeftAt = null;
        }

        switch (read.Meaning)
        {
            case LineMeaning.TunnelAddress:
                {
                    var address = read.Address!;
                    serverAddress = address;
                    Find<TextBox>("ShareBox").Text = $"{address} {roomId} {driver.RoomCode}";
                    Find<Button>("CopyInviteButton").IsEnabled = true;
                    Go(Stage.HostInvite);
                    InviteMade();
                    driver.Spawn(Play.LobbyArgs(hosting: true, driver.RoomCode, roomId, address,
                                                Settings.Read().Name, chosen?.Uuid ?? "", hostBoard,
                                                ChosenVictoryPoints(), ChosenDraftPoints(), firstPlayer,
                                                hostShape.Width, hostShape.Height, hostShape.PlacementRows),
                                 keepInput: true);
                    return;
                }

            case LineMeaning.PortInUse:
                {
                    SayBad("That port is already in use",
                           "Press Stop, then create the invite again.");
                    return;
                }

            case LineMeaning.Crashed:
                {
                    if (Play.RingsOnHalt(halted))
                    {
                        Attention.Raise(TopLevel.GetTopLevel(this) as Window);
                    }

                    halted = true;
                    StopWithReport("Something went wrong", "Press Stop before trying again.", ask: true,
                                   swapComing: false);
                    return;
                }

            case LineMeaning.TunnelDown:
                {
                    SayBad("Could not create the invite",
                           "Press Stop and try again.");
                    return;
                }

            case LineMeaning.Halt:
                {
                    if (Play.RingsOnHalt(halted))
                    {
                        Attention.Raise(TopLevel.GetTopLevel(this) as Window);
                    }

                    halted = true;

                    Ink("PlayStatus", Palette.SlateDark.Bad);
                    Find<TextBlock>("PlayStatus").Text = Play.HaltStatus;
                    Find<Border>("PlaySyncDot").Classes.Remove("on");

                    if (read.UnknownGameBuild)
                    {
                        var (updatedStep, updatedDetail) = Release.GameUpdatedText(UpdateCheck.Ours(),
                                                                                   UpdateCheck.Newest);
                        SayBad(updatedStep, updatedDetail);
                        return;
                    }

                    if (Play.ShowsRefusalScreen(read.DifferentVersions, playSpawned))
                    {
                        var (versionsStep, versionsDetail) = Release.DifferentVersionsText(UpdateCheck.Ours(),
                                                                                           UpdateCheck.Newest);
                        SayBad(versionsStep, versionsDetail);
                        return;
                    }

                    if (Play.ShowsRefusalScreen(read.DifferentGameBuilds, playSpawned))
                    {
                        var (buildsStep, buildsDetail) = Play.GameBuildsText();
                        SayBad(buildsStep, buildsDetail);
                        return;
                    }

                    if (read.GameClosed)
                    {
                        var (closedStep, closedDetail) = Play.ClosedText(read.ClosedHere);
                        StopWithReport(closedStep, closedDetail, ask: false, swapComing: playSpawned);
                        return;
                    }

                    if (read.PlayerLeft)
                    {
                        var (leftStep, leftDetail) = Play.LeftText(read.LeftHere);
                        StopWithReport(leftStep, leftDetail, ask: false, swapComing: playSpawned);
                        return;
                    }

                    var halt = Play.StopText(read.SetupsDiffer, read.Disagreement);
                    StopWithReport(halt.Headline, halt.Detail, ask: true, swapComing: playSpawned);
                    return;
                }

            case LineMeaning.BothPlayersReady:
                {
                    StopWaiting();

                    if (flow.Current == Stage.Playing)
                    {
                        Say("Connected again", "Carry on in game.");
                        Ink("StepLine", Palette.SlateDark.Good);
                        return;
                    }

                    Say("You are both ready",
                        "Open the challenge and place your machines. Do not back out of the challenge list.");
                    Ink("StepLine", Palette.SlateDark.Good);
                    return;
                }

            case LineMeaning.PeerStillInSetup:
                {
                    StartWaiting(Play.PeerInSetupHeadline,
                                 Play.DoNotEnterYet,
                                 technical: false);
                    return;
                }

            case LineMeaning.PeerMovedToPlay:
                {
                    pendingCode = null;

                    if (playStarted)
                    {
                        return;
                    }

                    if (Array.IndexOf(flow.Path, flow.Current) < Array.IndexOf(flow.Path, Stage.Safety))
                    {
                        SayBad("Your opponent is already in the match",
                               "They set the match up before you joined. Press Stop on both PCs and start again.");
                        return;
                    }

                    if (Play.OpponentAheadDetail(flow.Current, setupFailed) is not { } detail)
                    {
                        return;
                    }

                    if (flow.Current == Stage.SetUp && writing)
                    {
                        if (!RetitleWait(Play.OpponentAheadHeadline))
                        {
                            Say(Play.OpponentAheadHeadline,
                                $"Your side is still setting up. {Play.DoNotEnterYet}");
                        }

                        return;
                    }

                    Say(Play.OpponentAheadHeadline, detail);
                    return;
                }

            case LineMeaning.Fingerprint:
                {
                    var derived = read.Code!;

                    if (playSpawned)
                    {
                        return;
                    }

                    if (playStarted)
                    {
                        return;
                    }

                    inviteClock.Heard(Play.InviteClock.Event.KeyedPeerHere, DateTime.UtcNow);

                    if (fingerprint.Code.Length > 0 && derived != fingerprint.Code)
                    {
                        pendingCode = derived;
                        return;
                    }

                    peerHere = true;
                    peerLeftAt = null;

                    fingerprint.Take(derived);
                    ShowSafetyCode(fingerprint.Code, warning: false, flow.Hosting);

                    if (!TryGo(Stage.ChooseArmy))
                    {
                        return;
                    }

                    EnterArmyScreen();
                    if (flow.Hosting)
                    {
                        Say("Choose your army", "");
                        return;
                    }

                    if (chosen is not null)
                    {
                        Say("Choose your army", "Your opponent has chosen theirs.");
                        return;
                    }

                    StartWaiting("Waiting for your opponent to choose their army", "", technical: false);
                    return;
                }

            case LineMeaning.TheirSetup:
                {
                    if (!flow.Hosting)
                    {
                        chosen = challenges.FirstOrDefault(c => c.Uuid == read.Challenge) ?? chosen;

                        firstPlayer = read.First ?? Play.FirstHost;
                        if (read.DraftPoints > 0)
                        {
                            budget = read.DraftPoints;
                        }

                        if (flow.Current == Stage.ChooseArmy && !armySent)
                        {
                            EnterArmyScreen();
                            if (chosen is not null)
                            {
                                Say("Choose your army", "Your opponent has chosen theirs.");
                            }
                        }
                    }

                    return;
                }

            case LineMeaning.ArmyDoesNotFit:
                {
                    var message = Play.ArmyDoesNotFit(read.Machines, read.PlacingSquares)
                                  ?? Play.ArmyDoesNotFitHere;
                    armySent = false;
                    StopWaiting();
                    EnterArmyScreen();
                    SayBad("That army does not fit this board", message);
                    return;
                }

            case LineMeaning.TheirName:
                {
                    opponentName = read.Name;
                    if (flow.Current == Stage.HostInvite || flow.Current == Stage.JoinWait)
                    {
                        Say(Play.JoinedHeadline(opponentName), "");
                    }

                    return;
                }

            case LineMeaning.TheirArmy:
                {
                    if (!flow.Hosting && read.Army is { Count: > 0 } theirs)
                    {
                        opponentArmy = [.. theirs];
                        if (flow.Current == Stage.ChooseArmy && !armySent)
                        {
                            EnterArmyScreen();
                        }
                    }

                    return;
                }

            case LineMeaning.BothReady:
                {
                    OpenSafetyScreen();
                    Reflow();
                    return;
                }

            case LineMeaning.PeerConfirmed:
                {
                    confirmedByPeer = true;
                    if (confirmedByMe)
                    {
                        Go(Stage.SetUp);
                        return;
                    }

                    if (flow.Current == Stage.Safety)
                    {
                        Say("Compare safety codes",
                            "Your opponent has confirmed. Type the code they read to you.");
                    }

                    return;
                }

            case LineMeaning.SessionBound:
                {
                    sessionBinding = read.Binding;
                    return;
                }

            case LineMeaning.PeerLeft:
                {
                    if (fingerprint.Code.Length == 0 && !playSpawned)
                    {
                        if (flow.Hosting && flow.Current == Stage.HostInvite)
                        {
                            peerHere = false;
                            inviteClock.Heard(Play.InviteClock.Event.PeerLeft, DateTime.UtcNow);
                            WaitForJoin();
                            ToastRail.Show(ToastKind.Info, "Your opponent left. The same invite works if they join again.");
                        }

                        return;
                    }

                    peerLeftAt = DateTime.UtcNow;
                    tick.Start();
                    return;
                }

            case LineMeaning.PeerStartedOver:
                {
                    peerLeftAt = null;
                    inviteClock.Heard(Play.InviteClock.Event.KeyedPeerHere, DateTime.UtcNow);
                    if (pendingCode is { } code)
                    {
                        fingerprint.Reset();
                        fingerprint.Take(code);
                        codeShown = false;
                        DrawEye();
                        DrawTypedCells();
                        ShowSafetyCode(fingerprint.Code, warning: false, flow.Hosting);
                        pendingCode = null;
                    }

                    if (playStarted || playSpawned)
                    {
                        return;
                    }

                    if (writing)
                    {
                        SayBad("Your opponent started over",
                               "This match cannot go on. Press Stop on both PCs and start again.");
                        return;
                    }

                    BackToTheirArmy("Your opponent connected again.");
                    return;
                }

            case LineMeaning.CodeChanged:
                {
                    if (pendingCode is { } changed)
                    {
                        fingerprint.Reset();
                        fingerprint.Take(changed);
                        pendingCode = null;
                    }

                    ResetSafetyInputs();
                    ShowSafetyCode(fingerprint.Code, warning: false, flow.Hosting);
                    Say("The safety code changed", "Compare the new codes, then press Continue.");
                    return;
                }

            case LineMeaning.WriteSucceeded:
                {
                    EndLobbyThenPlay();
                    return;
                }

            case LineMeaning.WriteFailed:
                {
                    Find<Button>("WriteButton").IsEnabled = true;
                    writing = false;
                    setupFailed = true;
                    SayBad("The setup did not go into the game", Play.WriteFailedText(line));
                    return;
                }

            case LineMeaning.BothSeatsFilled:
                {
                    if (peerHere)
                    {
                        return;
                    }

                    peerHere = true;
                    Say(Play.JoinedHeadline(opponentName), "");
                    Ink("StepLine", Palette.SlateDark.Good);
                    return;
                }

            case LineMeaning.Paired:
                {
                    if (peerHere)
                    {
                        return;
                    }

                    if (flow.Hosting)
                    {
                        return;
                    }

                    StartWaiting("Connecting to your opponent", "");
                    return;
                }

            case LineMeaning.LinkDown:
                {
                    if (flow.Hosting && !peerHere)
                    {
                        return;
                    }

                    linkDowns++;
                    var verdict = Play.JudgeLinkDown(linkDowns, peerLeftAt is not null, AtPlayStage());
                    if (verdict == Play.LinkDownVerdict.Nothing)
                    {
                        return;
                    }

                    if (verdict == Play.LinkDownVerdict.OpponentStopped)
                    {
                        peerLeftAt = null;
                        pausedWait = null;
                        linkDowns = Play.LinkDownAfter;
                        halted = true;
                        StopWithReport("Your opponent stopped",
                                       "Press Stop and start again.",
                                       ask: false, swapComing: false);
                        return;
                    }

                    if (IsWaiting())
                    {
                        pausedWait = (waitingHeadline, waitingFor, waitingIsTechnical, waitingSlowAfter, waitingSince);
                    }

                    var (step, detail) = Play.LinkLost(everConnected);
                    SayBad(step, detail);
                    return;
                }

            case LineMeaning.InSync:
                {
                    Ink("PlayStatus", Palette.SlateDark.Good);
                    Find<TextBlock>("PlayStatus").Text = "The games are in sync.";
                    Find<Border>("PlaySyncDot").Classes.Add("on");
                    return;
                }

            case LineMeaning.MatchRunning:
                {
                    Go(Stage.Playing);
                    Ink("StepLine", Palette.SlateDark.Good);
                    return;
                }

            case LineMeaning.MatchOver:
                {
                    var (step, detail) = Play.MatchOverText();
                    Say(step, detail);
                    Ink("StepLine", Palette.SlateDark.Good);
                    return;
                }

            case LineMeaning.UnsharedMatch:
                {
                    var (step, detail) = Play.UnsharedMatchText();
                    SayBad(step, detail);
                    return;
                }

            case LineMeaning.OtherVersionTried:
                {
                    if (Play.ShowsOtherVersionNotice(flow.Hosting, playSpawned))
                    {
                        ToastRail.Show(ToastKind.Bad, Release.TriedToJoinText(UpdateCheck.Ours(), UpdateCheck.Newest));
                    }

                    return;
                }

            default:
                {
                    return;
                }
        }
    }
}
