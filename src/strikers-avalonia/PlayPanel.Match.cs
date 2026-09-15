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

    private void WaitForJoin()
    {
        StartWaiting("Send the invite", "Copy it and send it to your opponent. Waiting for them to join.",
                     technical: false);
    }

    private string HostingHeadline()
    {
        return "Hosting a match";
    }

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
        pendingCode = null;
        sessionBinding = null;
        tunnelServer = null;
        Find<Button>("WriteButton").IsEnabled = true;
    }

    private void ResetSafetyInputs()
    {
        var typed = Find<TextBox>("TypedCode");
        typed.Text = "";
        typed.IsEnabled = true;
        confirmedByMe = false;
        confirmedByPeer = false;
        sessionBinding = null;
        Find<Button>("CodeMatchesButton").IsEnabled = false;
        SetContinueLabel("Continue");
    }

    private void WaitForTheirArmy()
    {
        Find<ComboBox>("ArmyBox").IsVisible = false;
        Find<ScrollViewer>("ArmyScroll").IsVisible = false;
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

    private void CheckOpponentLeft()
    {
        if (!Play.ScreenMayChange(halted, startIdle: false))
        {
            peerLeftAt = null;
            return;
        }

        if (peerLeftAt is not { } at || (DateTime.UtcNow - at).TotalSeconds < Play.OpponentLeftAfter)
        {
            return;
        }

        peerLeftAt = null;
        if (AtPlayStage())
        {
            StartWaiting("Your opponent left",
                         "Waiting for them to come back. If this does not clear, press Stop and start again.",
                         technical: false);
            return;
        }

        if (Play.CodeIsCleared(Play.CodeEvent.OpponentLeft))
        {
            fingerprint.Reset();
        }

        BackToTheirArmy(armySent ? null : "Your opponent left. The same invite still works if they come back.");
        if (armySent)
        {
            StartWaiting("Your opponent left",
                         "Waiting for them to come back with the same invite. If they do not, press Stop and start again.",
                         technical: false);
        }
    }

    private void ConfirmCode()
    {
        if (confirmedByMe)
        {
            return;
        }

        if (!driver.TypeAtLobby([$"confirm {fingerprint.Code}"], "> confirm", "Nothing to confirm to"))
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

    private void SayBad(string headline, string detail, bool detailToo = false)
    {
        Say(headline, detail);
        Ink("StepLine", Palette.SlateDark.Bad);
        if (detailToo)
        {
            Ink("DetailLine", Palette.SlateDark.Bad);
        }
    }

    private void Ticked()
    {
        ShowWaiting();
        ShowPlayClock();
        CheckOpponentLeft();

        if (!IsWaiting() && playSpawned is false && peerLeftAt is null)
        {
            tick.Stop();
        }
    }

    private static string LogWhy()
    {
        return Play.WhereTheLogIs;
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

    private void StartHosting()
    {
        if (!driver.BeginStart())
        {
            return;
        }

        if (hostChallenge is not { } picked)
        {
            driver.CancelStart();
            ToastRail.Show(ToastKind.Bad, "That challenge is not available. The challenge list has not loaded.");
            return;
        }

        chosen = picked;
        var (board, boardProblem) = ChosenBoard();
        if (boardProblem is not null)
        {
            driver.CancelStart();
            ToastRail.Show(ToastKind.Bad, $"That board is not where it was. {boardProblem}");
            RefreshBoards();
            return;
        }

        hostBoard = board;
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

        StartWaiting(HostingHeadline(), "Reaching the relay.");

        pages[Stage.Start].IsVisible = false;
        Find<Button>("StopButton").IsVisible = true;
        TellFocus(true);

        var attempt = driver.Generation;

        _ = Task.Run(async () =>
        {
            var (server, note) = await TunnelDns.LookUpAsync(TunnelDns.DefaultServer);
            Dispatcher.UIThread.Post(() =>
            {
                if (!driver.StillWanted(attempt))
                {
                    return;
                }

                if (note is not null)
                {
                    driver.Say($"  network: {note}");
                }

                StartWaiting(HostingHeadline(), "Opening a public address.");

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

        driver.Spawn(Play.LobbyArgs(hosting: false, code, room, address, Settings.Read().Name,
                                    "", null, -1, -1, Play.FirstHost),
                     keepInput: true);
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
        playSince = DateTime.UtcNow;
        tick.Start();

        var play = new List<string>(Play.PlayArgs(flow.Hosting, driver.RoomCode, roomId, serverAddress,
                                                  Settings.Read().Name, Play.ArmyIds(army), sessionBinding,
                                                  firstPlayer));
        if (NetplayTool.LiveProbe() is { } probe)
        {
            play.Add("--live-probe");
            play.Add(probe);
        }

        driver.Spawn(play);
    }

    private void WriteSetup()
    {
        if (driver.TypeAtLobby(["write"], "> write", "Nothing to set up") is false)
        {
            return;
        }

        Find<Button>("WriteButton").IsEnabled = false;
        writing = true;

        StartWaiting("Writing the setup into the game",
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

        StartWaiting("Setup written", $"Connecting. {Play.DoNotEnterYet}");
        driver.EndLobby();
    }

    private void SendArmy()
    {
        if (army.Count == 0)
        {
            ToastRail.Show(ToastKind.Bad, "No army chosen. Pick one above.");
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
            ToastRail.Show(ToastKind.Bad,
                           $"That army costs {cost}, and the limit is {Budget()}. Pick a cheaper one, "
                           + "or press Stop and build one.");
            return;
        }

        if (army.Count > Play.MaxArmy)
        {
            ToastRail.Show(ToastKind.Bad,
                           $"That army is too big: {army.Count} machines, and a side may field {Play.MaxArmy}.");
            return;
        }

        var safe = Play.ArmyIds(army);
        if (safe.Count != army.Count)
        {
            ToastRail.Show(ToastKind.Bad,
                           "That army has a bad entry: one of its machines is not a valid id. "
                           + "Rebuild it in the Armies panel.");
            return;
        }

        var sent = driver.TypeAtLobby([$"army {string.Join(' ', safe)}", "place auto", "ready"],
                                      "> army / place auto / ready",
                                      "Nothing to send it to");
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

        ToastRail.Show(ToastKind.Bad, "Stopped, but the game may still hold the set-up. Do not back out of " +
                                      "the challenge list for ten minutes.");
    }

    public void ShutDown()
    {
        _ = SignalDraftGuard();
        driver.StopAll();
    }

    private void Interpret(string line)
    {
        if (!Play.ScreenMayChange(halted, flow.Current == Stage.Start && !driver.AnythingRunning))
        {
            return;
        }

        var read = NetplayLine.Read(line, flow.Hosting && driver.AnythingRunning,
                                    haveInvite: (Find<TextBox>("ShareBox").Text ?? "").Length > 0,
                                    tunnelHost: tunnelServer);

        if (NetplayLine.ProvesTheLink(read.Meaning))
        {
            LinkRestored();
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
                    WaitForJoin();
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
                           "Something else is holding it, usually a relay still running from an earlier try. "
                           + "Press Stop, then host again.");
                    return;
                }

            case LineMeaning.Crashed:
                {
                    SayBad("Something went wrong",
                           $"{LogWhy()} Press Stop before trying again.");
                    return;
                }

            case LineMeaning.TunnelDown:
                {
                    SayBad("Could not open a public address",
                           $"The relay could not be reached. {LogWhy()} Press Stop and try again.");
                    return;
                }

            case LineMeaning.Halt:
                {
                    halted = true;

                    Ink("PlayStatus", Palette.SlateDark.Bad);
                    Find<TextBlock>("PlayStatus").Text = Play.HaltStatus;
                    Find<Border>("PlaySyncDot").Classes.Remove("on");

                    if (read.Inactive)
                    {
                        var (inactiveStep, inactiveDetail) = Play.InactiveText();
                        SayBad(inactiveStep, inactiveDetail);
                        return;
                    }

                    if (Play.ShowsVersionScreen(read.DifferentVersions, playSpawned))
                    {
                        var (versionsStep, versionsDetail) = Release.DifferentVersionsText(UpdateCheck.Ours(),
                                                                                           UpdateCheck.Newest);
                        SayBad(versionsStep, versionsDetail);
                        return;
                    }

                    if (read.PlayerLeft)
                    {
                        var (leftStep, leftDetail) = Play.LeftText(read.LeftHere);
                        SayBad(leftStep, leftDetail);
                        return;
                    }

                    var halt = Play.HaltText(read.Disagreement);
                    var reported = haltReport.For(driver.Generation, SaveReport);
                    SayBad(halt.Headline, $"{halt.Detail} {reported}".TrimEnd(), detailToo: true);
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

                    Say("You are both ready. Enter the challenge now",
                        "Open the challenge and place your machines. Do not back out of the list on the way in.");
                    Ink("StepLine", Palette.SlateDark.Good);
                    return;
                }

            case LineMeaning.PeerStillInSetup:
                {
                    StartWaiting("Waiting for your opponent to finish setting up",
                                 "Do not enter the challenge until this screen says you are both ready.",
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

                    if (Play.OpponentAheadDetail(flow.Current) is not { } detail)
                    {
                        return;
                    }

                    if (flow.Current == Stage.SetUp && writing)
                    {
                        if (!RetitleWait(Play.OpponentAheadHeadline))
                        {
                            Say(Play.OpponentAheadHeadline,
                                $"Your setup is still being written here. {Play.DoNotEnterYet}");
                        }

                        return;
                    }

                    Say(Play.OpponentAheadHeadline, detail);
                    return;
                }

            case LineMeaning.Fingerprint:
                {
                    var derived = read.Code!;

                    peerLeftAt = null;

                    if (playSpawned)
                    {
                        return;
                    }

                    if (playStarted)
                    {
                        return;
                    }

                    if (fingerprint.Code.Length > 0 && derived != fingerprint.Code)
                    {
                        pendingCode = derived;
                        return;
                    }

                    peerHere = true;

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
                        Say("Choose your army", "Your opponent is ready. Pick yours and press Use this army.");
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
                                Say("Choose your army", "Your opponent is ready. Pick yours and press Use this army.");
                            }
                        }
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
                    var state = Play.SafetyScreenState(fingerprint.Code);
                    ShowSafetyCode(state.Text, state.Warning, flow.Hosting);
                    Go(Stage.Safety);

                    if (state.Warning)
                    {
                        SayBad("No safety code appeared", state.Note);
                    }

                    Find<TextBox>("TypedCode").IsVisible = !state.Warning;
                    Find<Button>("CodeMatchesButton").IsEnabled = state.Warning;
                    SetContinueLabel(state.Press);
                    if (!state.Warning)
                    {
                        CheckTypedCode();
                    }

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
                            "Your opponent has confirmed. Type the four they read to you.");
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
                            WaitForJoin();
                            ToastRail.Show(ToastKind.Info, "Your opponent left. The same invite still works if they join again.");
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
                    if (pendingCode is { } code)
                    {
                        fingerprint.Reset();
                        fingerprint.Take(code);
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
                               "Your setup is already written here, so this match cannot go on. Press Stop on both PCs and start again.");
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
                    Say("The safety code changed", "Compare the code on your screens again, then press Continue.");
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
                    SayBad("The setup did not go into the game", line, detailToo: true);
                    return;
                }

            case LineMeaning.BothSeatsFilled:
                {
                    if (peerHere)
                    {
                        return;
                    }

                    peerHere = true;
                    Say("Your opponent has joined", "");
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
                        SayBad("Your opponent stopped",
                               "They pressed Stop or closed Strikers. Press Stop and start again.");
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
                    Find<TextBlock>("PlayStatus").Text = "Both boards agree.";
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
