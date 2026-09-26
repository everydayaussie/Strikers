namespace Strikers.Netplay;

internal static partial class Program
{
    private const string LobbyCommandList =
        "commands:  preset [<name>]             one command instead of the four below\n" +
        "           name <NAME>                 your display name, shown on your friend's board\n" +
        "           challenges                  list loaded challenges with their UUIDs\n" +
        "           challenge <uuid>            (host only, both PCs load this challenge)\n" +
        "           board <t0> ... <t63>        (host only, the terrain both PCs play on)\n" +
        "           rules <victory> <draft>     (host only, -1 on either keeps the stock number)\n" +
        "           army <uuid> [<uuid> ...]    your machines, in placement order\n" +
        "           place <x> <y> <dir> [...]   your starting squares, same order as army\n" +
        "           place auto                  the near placing row, one square per machine\n" +
        "           roster                      list the machines this game has loaded\n" +
        "           confirm <code>              the safety code, once both players compared it\n" +
        "           write                       write both seats, from the Machine Strike menu\n" +
        "           ready | status | quit";

    private static async Task<int> RunLobby(
        Peer peer, string[] args, bool joining, string room, string server, int port, CancellationTokenSource cts)
    {
        var probe = ArgStr(args, "--live-probe") ?? DefaultProbe();
        var yes = args.Contains("--yes");

        peer.LocalRole = SessionRole.Lobby;

        var interactivePlacement = args.Contains("--interactive-placement");
        var build = ArgStr(args, "--build");
        if (build is null)
        {
            var read = ReadBuild(probe);
            build = read.Build;
            if (ProbeRefusedBuild(read.Exit) is { } refused)
            {
                Console.WriteLine(refused);
            }
        }

        var lobby = new Lobby(build, isHost: !joining,
                              localNetplay: OwnVersion(), localProbe: ReadProbeVersion(probe));

        if (interactivePlacement && !Console.IsOutputRedirected)
        {
            Console.WriteLine("  interactive placement: the AI seat will NOT be pre-written, " +
                              "each player picks their own squares in game.");
        }

        if (!joining)
        {
            Console.WriteLine($"room code: {room}");
            Console.WriteLine($"room id: {peer.RoutingId}");
            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine($"  the other player runs: netplay --lobby --join {room} --room-id {peer.RoutingId} " +
                                  $"--server {server}:{port}");
            }
        }

        var identified = false;
        var answered = false;
        var answerWanted = false;

        var presets = Presets.Load(null, out var presetComplaint);
        if (presetComplaint is not null)
        {
            Console.Error.WriteLine($"  {presetComplaint}");
        }

        var readyWhenPossible = false;

        var resending = false;

        var channels = 0;
        var rekeyPending = false;
        peer.ChannelBuilt += () =>
        {
            channels++;
            rekeyPending = channels > 1;
        };

        void PeerStartedOver()
        {
            lobby.ForgetPeer();
            identified = false;
            answered = false;
            if (lobby.LocalReady)
            {
                readyWhenPossible = true;
                resending = true;
            }

            Console.WriteLine("  the other player started over, so our build and setup go out again");
        }

        peer.Received += f =>
        {
            switch (f.Kind)
            {
                case MsgKind.Ident:
                    var known = lobby.PeerBuild is not null;
                    var refusal = lobby.OnIdent(f);
                    if (!known)
                    {
                        var shown = FrameLimits.Safe(f.Build, 64);
                        Console.WriteLine(refusal is null
                            ? $"  <- peer build {shown}"
                            : $"  <- peer build {shown}\nREFUSED: {refusal}");
                    }

                    answerWanted = true;
                    break;

                case MsgKind.Setup:
                    lobby.OnSetup(f);

                    if (lobby.Refusal is { } setupRefusal)
                    {
                        Console.Error.WriteLine($"REFUSED: {setupRefusal}");
                        break;
                    }

                    Console.WriteLine(SetupSummary(f, lobby));
                    if (TheirArmyLine(lobby) is { } armyLine)
                    {
                        Console.WriteLine(armyLine);
                    }

                    if (f.Placements is { Count: > 0 })
                    {
                        Console.WriteLine("     their squares in your frame: " +
                                          string.Join(", ", lobby.Remote!.Placements.Select(p => $"({p.X},{p.Y}) dir {p.Dir}")));
                    }

                    break;

                case MsgKind.Ready:
                    lobby.OnReady(f);
                    Console.WriteLine("  <- they are ready");
                    if (lobby.BothReady)
                    {
                        Console.WriteLine(BothReadyInstructions(lobby, presets));
                    }

                    break;

                case MsgKind.Confirmed:
                    TakeTheirConfirmation(peer, lobby, f);
                    break;

                case MsgKind.Left:
                    LobbyHeardLeft(lobby);
                    answered = false;
                    break;

                case MsgKind.Role when f.Role == SessionRole.Play:
                    rekeyPending = false;
                    Console.WriteLine("  <- the other player is in the match (peer role: play)");
                    break;

                case MsgKind.Role:
                    if (rekeyPending)
                    {
                        rekeyPending = false;
                        PeerStartedOver();
                    }

                    Console.WriteLine("  <- peer role: lobby");
                    if (peer.RemoteName is { Length: > 0 } theirName)
                    {
                        Console.WriteLine($"  <- their name: {Names.Hex(theirName)}");
                    }

                    break;

                default:
                    Console.WriteLine($"  <- {f.Kind} seq {f.Seq}");
                    break;
            }
        };

        _ = peer.Run(cts.Token);

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && !peer.Halted)
            {
                if (peer.Seat >= 0 && !identified)
                {
                    lobby.Seat = peer.Seat;
                    peer.Send(lobby.Ident());
                    identified = true;
                    Console.WriteLine($"  -> our build {lobby.LocalBuild}");
                }

                if (answerWanted && !answered && lobby.TakeAnswer() is { } reply)
                {
                    answered = true;
                    peer.Send(reply);
                    Console.WriteLine($"  -> our build {lobby.LocalBuild} (answering the peer)");
                }

                if (readyWhenPossible && lobby.PeerBuild is not null && TrySendReady(peer, lobby) is null)
                {
                    readyWhenPossible = false;
                    Console.WriteLine(resending ? "  -> setup and ready sent again" : "  -> setup and ready sent (preset)");
                    resending = false;
                    if (lobby.BothReady)
                    {
                        Console.WriteLine(BothReadyInstructions(lobby, presets));
                    }
                }

                await Task.Delay(200, cts.Token);
            }
        }, cts.Token);

        if (!Console.IsOutputRedirected)
        {
            Console.WriteLine(
                LobbyCommandList +
                (interactivePlacement
                    ? "\n  (--interactive-placement: write skips the AI seat, you pick squares in game)"
                    : "") +
                (yes ? "" : "\n  (no --yes: write will print the commands rather than run them)"));
        }

        foreach (var command in LobbyMatchCommands(args))
        {
            LobbyCommand(peer, lobby, probe, yes, command, presets, interactivePlacement);
        }

        if (ArgStr(args, "--preset") is { } presetName)
        {
            readyWhenPossible = LobbyCommand(peer, lobby, probe, yes, $"preset {presetName}", presets,
                                             interactivePlacement);
        }

        while (!cts.IsCancellationRequested && !peer.Halted)
        {
            string? line;
            try
            {
                line = await ReadLineOrCancel(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null || line.Trim() == "quit")
            {
                break;
            }

            if (LobbyCommand(peer, lobby, probe, yes, line, presets, interactivePlacement))
            {
                readyWhenPossible = true;
            }
        }

        return peer.Halted || lobby.Refusal is not null ? 2 : 0;
    }

    internal static void LobbyHeardLeft(Lobby lobby)
    {
        if (!(lobby.LocalReady && lobby.RemoteReady))
        {
            lobby.ForgetPeer();
        }
    }

    private static bool TakeTheirConfirmation(Peer peer, Lobby lobby, Frame f)
    {
        if (!lobby.ConfirmedByPeer(peer.ChannelGeneration, f.SetupDigest, lobby.SetupDigest(_appliedPreset)))
        {
            peer.HaltAndTell(Lobby.SetupsDiffer);
            return false;
        }

        Console.WriteLine("  <- they confirmed the safety code");
        BindIfBothConfirmed(peer, lobby);
        return true;
    }

    private static bool ConfirmCode(Peer peer, Lobby lobby, string? typed, (string? Code, int Channel) shown)
    {
        if (Lobby.ConfirmProblem(typed, shown.Code) is { } notIt)
        {
            Console.WriteLine($"  nothing was confirmed: {notIt}");
            return false;
        }

        var confirmed = new Frame { Kind = MsgKind.Confirmed, SetupDigest = lobby.SetupDigest(_appliedPreset) };
        if (!peer.SendIfStillOn(confirmed, shown.Channel))
        {
            Console.WriteLine($"  nothing was confirmed: {Lobby.CodeMoved}");
            return false;
        }

        lobby.ConfirmedLocally(shown.Channel, shown.Code!);
        Console.WriteLine("  -> confirmed the safety code");
        BindIfBothConfirmed(peer, lobby);
        return true;
    }

    private static void BindIfBothConfirmed(Peer peer, Lobby lobby)
    {
        var (code, current) = peer.CodeAndChannel();
        if (lobby.BindingChannel(code, current) is { } channel && peer.BindToChannel(channel, code) is { } bound)
        {
            lobby.BoundOn(channel);
            Console.WriteLine($"  session bound {Convert.ToHexString(bound)}");
        }
    }

    private static async Task<string?> ReadLineOrCancel(CancellationToken token)
    {
        var read = Task.Run(() => Console.In.ReadLine(), CancellationToken.None);
        var cancelled = Task.Delay(Timeout.Infinite, token);

        if (await Task.WhenAny(read, cancelled) == cancelled)
        {
            throw new OperationCanceledException(token);
        }

        return await read;
    }

    private static string? TrySendReady(Peer peer, Lobby lobby)
    {
        if (lobby.Refusal is not null)
        {
            return lobby.Refusal;
        }

        if (!lobby.Local.Complete)
        {
            return $"not ready: {lobby.Local.Army.Count} machine(s) but " +
                   $"{lobby.Local.Placements.Count} placement(s), the game places one per record, in order.";
        }

        if (lobby.Challenge is null)
        {
            return "no challenge chosen yet";
        }

        peer.Send(lobby.Setup());
        peer.Send(lobby.Ready());
        return null;
    }

    private static bool LobbyCommand(Peer peer, Lobby lobby, string probe, bool yes, string line,
                                     List<Preset> presets, bool interactivePlacement = false)
    {
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0)
        {
            return false;
        }

        try
        {
            switch (w[0])
            {
                case "challenge" when w.Length >= 2:
                    if (!lobby.IsHost)
                    {
                        Console.Error.WriteLine("  the host picks the challenge");
                        break;
                    }
                    lobby.Choose(string.Join(' ', w[1..]));
                    Console.WriteLine($"  challenge set to {lobby.Challenge}");
                    break;

                case "challenges":
                    RunProbe(probe, "--list-board-games");
                    break;

                case "name" when w.Length >= 2:
                    if (lobby.SetName(w[1]) is { } nameBad)
                    {
                        Console.Error.WriteLine($"  {nameBad}");
                        break;
                    }

                    Console.WriteLine(Names.SetLine(lobby.LocalName));
                    break;

                case "board" when w.Length >= 2:
                    if (!lobby.IsHost)
                    {
                        Console.Error.WriteLine("  the host picks the board");
                        break;
                    }

                    var terrain = new List<int>();
                    foreach (var t in w[1..])
                    {
                        if (!int.TryParse(t, out var value))
                        {
                            Console.Error.WriteLine($"  '{t}' is not a terrain value; they run -2 to 3");
                            terrain.Clear();
                            break;
                        }

                        terrain.Add(value);
                    }

                    if (terrain.Count == 0)
                    {
                        break;
                    }

                    if (Preset.BoardProblem(terrain, "that board", lobby.BoardWidth, lobby.BoardHeight,
                                            lobby.PlacementRows) is { } boardBad)
                    {
                        Console.Error.WriteLine($"  {boardBad}");
                        break;
                    }

                    lobby.ChooseBoard(terrain, lobby.VictoryPoints, lobby.DraftPoints,
                                      lobby.BoardWidth, lobby.BoardHeight, lobby.PlacementRows);
                    Console.WriteLine($"  board set, {terrain.Count} squares, " +
                                      $"{lobby.BoardWidth}x{lobby.BoardHeight}");
                    break;

                case "shape" when w.Length >= 3:
                    if (!lobby.IsHost)
                    {
                        Console.Error.WriteLine("  the host picks the board");
                        break;
                    }

                    if (!int.TryParse(w[1], out var shapeW) || !int.TryParse(w[2], out var shapeH))
                    {
                        Console.Error.WriteLine("  shape <width> <height> [placing depth]");
                        break;
                    }

                    var shapeDepth = Preset.RuleNotSet;
                    if (w.Length >= 4 && !int.TryParse(w[3], out shapeDepth))
                    {
                        Console.Error.WriteLine("  the placing depth must be a number");
                        break;
                    }

                    var shapeProbe = shapeW > 0 && shapeH > 0 && shapeW * shapeH <= 4096
                        ? new int[shapeW * shapeH].ToList()
                        : [];
                    if (Preset.BoardProblem(shapeProbe, "that shape", shapeW, shapeH, shapeDepth) is { } shapeBad)
                    {
                        Console.Error.WriteLine($"  {shapeBad}");
                        break;
                    }

                    lobby.ChooseBoard(lobby.Board, lobby.VictoryPoints, lobby.DraftPoints,
                                      shapeW, shapeH, shapeDepth);
                    Console.WriteLine($"  shape set, {shapeW}x{shapeH}" +
                                      (shapeDepth != Preset.RuleNotSet ? $", placing depth {shapeDepth}" : ""));
                    break;

                case "rules" when w.Length >= 3:
                    if (!lobby.IsHost)
                    {
                        Console.Error.WriteLine("  the host picks the rules");
                        break;
                    }

                    if (!int.TryParse(w[1], out var victory) || !int.TryParse(w[2], out var draft))
                    {
                        Console.Error.WriteLine("  rules takes two numbers: <victory points> <draft points>");
                        break;
                    }

                    if (Preset.RuleProblem(victory, draft, "you") is { } ruleBad)
                    {
                        Console.Error.WriteLine($"  {ruleBad}");
                        break;
                    }

                    lobby.ChooseBoard(lobby.Board, victory, draft,
                                      lobby.BoardWidth, lobby.BoardHeight, lobby.PlacementRows);
                    Console.WriteLine($"  rules set: {victory} victory points, {draft} draft points");
                    break;

                case "first" when w.Length >= 2:
                    if (!lobby.IsHost)
                    {
                        Console.Error.WriteLine("  the host picks who goes first");
                        break;
                    }

                    if (!Lobby.IsFirstChoice(w[1]))
                    {
                        Console.Error.WriteLine($"  first takes {Lobby.FirstHost} or {Lobby.FirstJoiner}");
                        break;
                    }

                    lobby.ChooseFirst(w[1]);
                    Console.WriteLine($"  first set: the {w[1]} goes first");
                    break;

                case "write":
                    LobbyWrite(peer, lobby, probe, yes, interactivePlacement);
                    break;

                case "army" when w.Length >= 2:
                    if (lobby.Challenge is { } armyChallenge && Challenges.Find(armyChallenge) is { } entry)
                    {
                        var budget = lobby.DraftPoints != Preset.RuleNotSet
                            ? lobby.DraftPoints
                            : Machines.StockDraftPoints;

                        var armyIds = w[1..].Select(m => Lobby.NormaliseUuid(m) ?? m).ToList();
                        if (Machines.ArmyProblem(armyIds, entry.Slots, budget) is { } armyBad)
                        {
                            Console.Error.WriteLine($"  {armyBad}");
                            break;
                        }
                    }

                    var bad = lobby.SetArmy(w[1..]);
                    if (bad is not null)
                    {
                        Console.Error.WriteLine($"  {bad}");
                        break;
                    }
                    Console.WriteLine($"  your army: {lobby.Local.Army.Count} machine(s), " +
                                      $"cost {Machines.Cost(lobby.Local.Army)}");
                    break;

                case "place" when w.Length == 2 && w[1] == "auto":
                    if (lobby.Local.Army.Count == 0)
                    {
                        Console.Error.WriteLine("  choose an army first: army <uuid> ...");
                        break;
                    }

                    var auto = lobby.AutoSquares(lobby.Local.Army.Count);
                    if (auto.Count == 0)
                    {
                        Console.Error.WriteLine($"  {lobby.Local.Army.Count} machines is more than the " +
                                                $"{lobby.BoardWidth * lobby.AutoDepth} squares the placing " +
                                                $"rows of this {lobby.BoardWidth}x{lobby.BoardHeight} board hold");
                        break;
                    }

                    lobby.Local.Placements = auto;
                    Console.WriteLine($"  your squares: {string.Join(", ", lobby.Local.Placements.Select(p => $"({p.X},{p.Y}) dir {p.Dir}"))}" +
                                      $"  (on the {lobby.BoardWidth}x{lobby.BoardHeight} board, placing depth {lobby.AutoDepth})");
                    break;

                case "place" when w.Length >= 4:
                    var vals = w[1..];
                    if (vals.Length % 3 != 0)
                    {
                        Console.Error.WriteLine("  place takes triples: <x> <y> <dir> ...");
                        break;
                    }
                    lobby.Local.Placements = [.. Enumerable.Range(0, vals.Length / 3).Select(i => new Placement
                    {
                        X = int.Parse(vals[i * 3]), Y = int.Parse(vals[i * 3 + 1]), Dir = byte.Parse(vals[i * 3 + 2]),
                    })];
                    Console.WriteLine($"  your squares: {string.Join(", ", lobby.Local.Placements.Select(p => $"({p.X},{p.Y}) dir {p.Dir}"))}");
                    break;

                case "roster":
                    RunProbe(probe, "--survey");
                    break;

                case "preset" when w.Length >= 2:
                    var chosen = Presets.Find(presets, w[1]);
                    if (chosen is null)
                    {
                        Console.Error.WriteLine($"  no preset called '{w[1]}'. Known: " +
                                                $"{string.Join(", ", presets.Select(p => p.Name))}");
                        break;
                    }

                    if (lobby.ApplyPreset(chosen) is { } presetBad)
                    {
                        Console.Error.WriteLine($"  {presetBad}");
                        break;
                    }

                    _appliedPreset = chosen;

                    Console.WriteLine($"  {chosen.Describe()}");
                    Console.WriteLine($"  open '{chosen.ChallengeName}' in Machine Strike, then confirm the safety code " +
                                      "and write once both are ready");

                    return true;

                case "preset":
                    Console.WriteLine("  presets:");
                    foreach (var p in presets)
                    {
                        Console.WriteLine($"    {p.Describe()}" + (p.Note.Length > 0 ? $"  ({p.Note})" : ""));
                    }

                    Console.WriteLine($"  add your own in {Presets.FileName} beside the exe; a file entry " +
                                      "of the same name replaces the built-in.");
                    break;

                case "ready":
                    if (TrySendReady(peer, lobby) is { } notYet)
                    {
                        Console.Error.WriteLine($"  {notYet}");
                        break;
                    }

                    Console.WriteLine("  -> setup and ready sent");

                    if (lobby.PeerBuild is null)
                    {
                        Console.Error.WriteLine("  warning: the peer has not identified yet, so that was sent " +
                                                "to an empty seat and dropped. Type 'ready' again once it has.");
                    }

                    if (lobby.BothReady)
                    {
                        Console.WriteLine(BothReadyInstructions(lobby, presets));
                    }

                    break;

                case "status":
                    Console.WriteLine(lobby.Status());
                    break;

                case "confirm":
                    ConfirmCode(peer, lobby, w.Length > 1 ? w[1] : null, peer.CodeAndChannel());
                    break;

                default:
                    Console.WriteLine("  commands: preset [<name>] | challenges | challenge <uuid> | " +
                                      "army <uuid>... | place <x> <y> <dir>... | roster | ready | " +
                                      "confirm <code> | write | status | quit");
                    break;
            }
        }
        catch (FormatException) { Console.Error.WriteLine("  could not parse those numbers"); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine($"  {ex.Message}"); }

        return false;
    }

}
