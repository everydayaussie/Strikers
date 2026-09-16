using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Strikers.Netplay;

internal static partial class Program
{
    internal static byte[]? BindingFrom(string? text)
    {
        if (text is not { Length: KeyExchange.BindingLength * 2 } || !text.All(Uri.IsHexDigit))
        {
            return null;
        }

        return Convert.FromHexString(text);
    }

    private static (TcpClient, TcpClient) LoopbackPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var near = new TcpClient();
        near.Connect((IPEndPoint)listener.LocalEndpoint);
        var far = listener.AcceptTcpClient();
        listener.Stop();
        return (near, far);
    }

    private static async Task<int> Main(string[] args)
    {
        if (LauncherOnly.Refuses(args))
        {
            return LauncherOnly.ExitCode;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (ArgInt(args, "--parent-pid") is { } parentPid)
        {
            ParentWatch.Start(parentPid, cts.Cancel);
        }

        if (args.Contains("--selftest"))
        {
            return await SelfTest();
        }

        var diffAt = Array.IndexOf(args, "--diff");
        if (diffAt >= 0 && diffAt + 2 < args.Length)
        {
            if (!SnapshotJson.TryParse((await File.ReadAllTextAsync(args[diffAt + 1])).Trim(), out var b4, out var w1))
            { Console.Error.WriteLine($"before: {w1}"); return 1; }
            if (!SnapshotJson.TryParse((await File.ReadAllTextAsync(args[diffAt + 2])).Trim(), out var af, out var w2))
            { Console.Error.WriteLine($"after: {w2}"); return 1; }

            var owner = ArgInt(args, "--local-owner") ?? af.LocalOwner;
            Console.WriteLine($"local owner {owner} (aiSeat {af.AiSeat})");

            var res = MoveDetector.Detect(b4, af, owner);
            if (!res.Ok)
            {
                Console.Error.WriteLine($"REFUSED: {res.Refusal}");
                return 1;
            }

            if (res.Warning is not null)
            {
                Console.Error.WriteLine($"WARNING: {res.Warning}");
            }

            foreach (var m in res.Moves)
            {
                Console.WriteLine(Describe(m));
            }

            return 0;
        }

        var streamAt = Array.IndexOf(args, "--diff-stream");
        if (streamAt >= 0 && streamAt + 1 < args.Length)
        {
            var samples = new List<BoardSnapshot>();
            var skipped = 0;
            foreach (var l in ReadLinesShared(args[streamAt + 1]))
            {
                if (SnapshotJson.TryParse(l.Trim(), out var s, out _))
                {
                    samples.Add(s);
                }
                else
                {
                    skipped++;
                }
            }

            if (samples.Count < 2)
            { Console.Error.WriteLine($"only {samples.Count} usable sample(s) in that file"); return 1; }

            var seqOwner = ArgInt(args, "--local-owner") ?? samples[^1].LocalOwner;
            Console.WriteLine($"{samples.Count} samples ({skipped} skipped), local owner {seqOwner} (aiSeat {samples[^1].AiSeat})");

            var seq = MoveDetector.DetectSequence(samples, seqOwner);
            if (!seq.Ok)
            {
                Console.Error.WriteLine($"REFUSED: {seq.Refusal}");
                return 1;
            }

            if (seq.Warning is not null)
            {
                Console.Error.WriteLine($"WARNING: {seq.Warning}");
            }

            foreach (var m in seq.Moves)
            {
                Console.WriteLine(Describe(m));
            }

            return 0;
        }

        var pairAt = Array.IndexOf(args, "--diff-pair");
        if (pairAt >= 0 && pairAt + 2 >= args.Length)
        {
            Console.Error.WriteLine("netplay --diff-pair <first stream> <second stream>: two capture files are needed");
            return 1;
        }
        if (pairAt >= 0)
        {
            var sides = new List<BoardSnapshot>[2];
            for (var f = 0; f < 2; f++)
            {
                sides[f] = [];
                foreach (var l in ReadLinesShared(args[pairAt + 1 + f]))
                {
                    if (SnapshotJson.TryParse(l.Trim(), out var s, out _))
                    {
                        sides[f].Add(s);
                    }
                }
            }
            if (sides[0].Count < 2 || sides[1].Count < 2)
            {
                Console.Error.WriteLine($"need two usable streams ({sides[0].Count} and {sides[1].Count} sample(s) read)");
                return 1;
            }

            var evA = StreamPair.Events(sides[0], rotate: false);
            var evB = StreamPair.Events(sides[1], rotate: true);
            var res = StreamPair.Pair(evA, evB);
            Console.WriteLine($"{sides[0].Count}/{sides[1].Count} samples, {evA.Count}/{evB.Count} events, " +
                              $"{res.Paired.Count} paired (file 2 rotated into file 1's frame)");

            for (var f = 0; f < 2; f++)
            {
                var first = sides[f].FirstOrDefault(s => s.Stamp is not null)?.Stamp;
                var last = sides[f].LastOrDefault(s => s.Stamp is not null)?.Stamp;
                if (first is not null)
                {
                    Console.WriteLine($"  file {f + 1} spans {first} to {last}; one-sided events after " +
                                      "the other file's end may be truncation, not desync");
                }
            }
            foreach (var e in res.OnlyA)
            {
                Console.WriteLine($"  ONLY file 1: {StreamPair.Describe(e)}");
            }
            foreach (var e in res.OnlyB)
            {
                Console.WriteLine($"  ONLY file 2: {StreamPair.Describe(e)}");
            }
            if (res.OnlyA.Count == 0 && res.OnlyB.Count == 0)
            {
                Console.WriteLine("  every event is two-sided; the boards told one story");
            }
            return res.OnlyA.Count == 0 && res.OnlyB.Count == 0 ? 0 : 2;
        }

        var turnsAt = Array.IndexOf(args, "--diff-turns");
        if (turnsAt >= 0 && turnsAt + 1 < args.Length)
        {
            var lines = ReadLinesShared(args[turnsAt + 1]);
            var parsed = new List<BoardSnapshot>();
            var dropped = 0;
            foreach (var l in lines)
            {
                if (SnapshotJson.TryParse(l.Trim(), out var s, out _))
                {
                    parsed.Add(s);
                }
                else
                {
                    dropped++;
                }
            }

            if (parsed.Count < 2)
            { Console.Error.WriteLine($"only {parsed.Count} usable sample(s) in that file"); return 1; }

            var owner = ArgInt(args, "--local-owner") ?? parsed[^1].LocalOwner;
            Console.WriteLine($"{parsed.Count} samples ({dropped} skipped), local owner {owner}");

            var tr = new TurnTracker(owner);
            var found = 0;
            var bad = 0;

            for (var i = 0; i < parsed.Count; i++)
            {
                var cut = tr.Push(parsed[i]);
                if (cut is null)
                {
                    continue;
                }

                found++;
                var r = MoveDetector.ReadTurn(cut, owner, out var cutNote);
                Console.WriteLine($"\nturn {found}  (ends at sample {i + 1}, {cut.Count} samples)");
                if (cutNote is not null)
                {
                    Console.Error.WriteLine($"  note: {cutNote}");
                }

                if (!r.Ok)
                {
                    Console.Error.WriteLine($"  REFUSED: {r.Refusal}");
                    bad++;
                    continue;
                }
                if (r.Warning is not null)
                {
                    Console.Error.WriteLine($"  WARNING: {r.Warning}");
                }

                foreach (var m in r.Moves)
                {
                    Console.WriteLine($"  {Describe(m)}");
                }
            }

            if (tr.Flush() is { } tail)
            {
                var r = MoveDetector.ReadTurn(tail, owner, out var tailNote);
                Console.WriteLine($"\ntail  ({tail.Count} samples, no closing edge)");
                if (tailNote is not null)
                {
                    Console.Error.WriteLine($"  note: {tailNote}");
                }

                if (r.Ok)
                {
                    foreach (var m in r.Moves)
                    {
                        Console.WriteLine($"  {Describe(m)}");
                    }
                }
                else
                {
                    Console.WriteLine($"  nothing readable: {r.Refusal}");
                }
            }

            Console.WriteLine($"\n{found} complete turn(s), {bad} refused");
            return bad == 0 ? 0 : 1;
        }

        var hashPath = ArgStr(args, "--hash");
        if (hashPath is not null)
        {
            var seat = ArgInt(args, "--seat") ?? -1;
            if (seat is not (0 or 1))
            {
                Console.Error.WriteLine("--hash needs --seat 0 (host) or --seat 1 (guest): the digest is taken in the host's frame.");
                return 1;
            }

            var text = hashPath == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(hashPath);
            if (!SnapshotJson.TryParse(text.Trim(), out var snap, out var why))
            {
                Console.Error.WriteLine($"bad snapshot: {why}");
                return 1;
            }

            Console.WriteLine($"pieces  {BoardHash.Pieces(snap, seat)}");
            Console.WriteLine($"terrain {BoardHash.Terrain(snap, seat)}");
            Console.WriteLine($"({snap.Pieces.Count} pieces on {snap.Width}x{snap.Height}, seat {seat})");
            return 0;
        }

        var port = ArgInt(args, "--port") ?? 47801;

        var (testFor, testExpires) = TestBuild.Read(typeof(Program).Assembly);
        if (args.Contains("--version"))
        {
            var mvid = System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId;
            var mark = TestBuild.Mark(testFor, testExpires);
            Console.WriteLine($"netplay {mvid:N}  protocol {Protocol.Version}" + (mark is null ? "" : $"  ({mark})"));
            return 0;
        }

        if (TestBuild.Expired(testExpires, DateOnly.FromDateTime(DateTime.Now)))
        {
            Console.Error.WriteLine($"  {TestBuild.ExpiredMessage()}");
            return 3;
        }

        if (ArgStr(args, "--dump-preset") is { } dumpName)
        {
            var all = Presets.Load(null, out _);
            var found = all.FirstOrDefault(p => string.Equals(p.Name, dumpName, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                Console.Error.WriteLine($"no preset called '{dumpName}'. Known: {string.Join(", ", all.Select(p => p.Name))}");
                return 2;
            }

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(found, DumpJson.Default.Preset));
            return 0;
        }

        if (args.Contains("--list-presets"))
        {
            foreach (var p in Presets.Load(null, out _))
            {
                Console.WriteLine($"{p.Name,-12} {p.ChallengeName}");
            }

            return 0;
        }

        if (args.Contains("--list-challenges"))
        {
            foreach (var c in Challenges.Known)
            {
                Console.WriteLine($"{c.Uuid}  {c.Slots}  {c.Name}");
            }

            return 0;
        }

        if (args.Contains("--list-machines"))
        {
            foreach (var m in Machines.Known)
            {
                Console.WriteLine(string.Join('\t',
                    m.Uuid, m.Name, m.Cost, m.Health, m.Move, m.Range, m.Power,
                    m.Pattern, m.Ability, Machines.CannotPlay(m) ?? ""));
            }

            return 0;
        }

        if (args.Contains("--relay"))
        {
            var tunnel = args.Contains("--tunnel");
            var bind = Relay.BindFor(ArgStr(args, "--bind"), tunnel);

            var relay = new Relay(ArgStr(args, "--room-id"));

            if (tunnel)
            {
                _ = TunnelClient.Start(port, ArgStr(args, "--tunnel-server") ?? "bore.pub",
                                       ArgStr(args, "--tunnel-secret"), relay.Seated, cts.Token);
            }

            await relay.Run(port, cts.Token, bind);
            return 0;
        }

        var server = ArgStr(args, "--server") ?? "127.0.0.1";
        if (server.Contains(':'))
        {
            var bits = server.Split(':');
            server = bits[0];
            port = int.Parse(bits[1]);
        }

        var room = ArgStr(args, "--join") ?? ArgStr(args, "--room") ?? Relay.NewCode();

        var joining = ArgStr(args, "--join") is not null;

        var routingId = ArgStr(args, "--room-id");
        if (routingId is null)
        {
            if (joining)
            {
                Console.Error.WriteLine("  --join needs --room-id <id>, from the line the host printed.");
                return 1;
            }

            routingId = RoomId.Random();
        }

        if (!args.Contains("--lobby") && (args.Contains("--host") || args.Contains("--room")))
        {
            Console.WriteLine($"room code: {room}");

            Console.WriteLine($"room id: {routingId}");
            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine($"  (the other player runs: netplay --join {room} --room-id {routingId} " +
                                  $"--server {server}:{port})");
            }
        }

        var peer = new Peer(server, port, room, joining ? 1 : 0, routingId: routingId);

        if (args.Contains("--lobby"))
        {
            return await RunLobby(peer, args, joining, room, server, port, cts);
        }

        if (args.Contains("--play"))
        {
            var probe = ArgStr(args, "--live-probe") ?? DefaultProbe();
            var injector = new Injector(probe, args.Contains("--yes"));
            var pending = new Queue<(List<Move> Turn, bool Final)>();

            peer.LocalRole = SessionRole.Play;

            if (ArgStr(args, "--name") is { } playName)
            {
                peer.LocalName = Names.Clean(playName);
            }

            if (ArgStr(args, "--binding") is { } bindingText)
            {
                if (BindingFrom(bindingText) is not { } binding)
                {
                    Console.Error.WriteLine($"  --binding takes {KeyExchange.BindingLength * 2} hex digits");
                    return 2;
                }

                peer.Binding = binding;
                peer.RequireBinding = true;
            }

            var firstPlayer = ArgStr(args, "--first") ?? Lobby.FirstHost;
            if (!Lobby.IsFirstChoice(firstPlayer))
            {
                Console.Error.WriteLine($"  --first takes {Lobby.FirstHost} or {Lobby.FirstJoiner}");
                return 2;
            }

            Peer.OnHalt = _ =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(probe, "--freeze")
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                    Console.Error.WriteLine("  the local match is being FROZEN (live-probe --freeze); " +
                                            "release by hand with: live-probe --freeze --clear");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  could not freeze the match: {ex.Message}");
                }
            };

            if (ArgMany(args, "--army") is { Length: > 0 } playArmy)
            {
                _playArmy = [.. playArmy.Select(m => Lobby.NormaliseUuid(m) ?? m)];
                Console.WriteLine($"  your army: {_playArmy.Count} machine(s)");
            }

            if (ArgStr(args, "--preset") is { } playPresetName)
            {
                var playPresets = Presets.Load(null, out var playPresetComplaint);
                if (playPresetComplaint is not null)
                {
                    Console.Error.WriteLine($"  {playPresetComplaint}");
                }

                if (Presets.Find(playPresets, playPresetName) is { } playPreset)
                {
                    _rematchPlacement = Presets.PlacementAdvice(
                        [.. playPreset.Army.Select(m => m.Uuid)], playPreset.Placements, playPresets);

                    _playPreset = playPreset;
                }
                else
                {
                    Console.Error.WriteLine($"  no preset called '{playPresetName}', so a rematch will not be " +
                                            "able to remind you which machine goes on which square.");
                }
            }

            foreach (var clear in StartClears)
            {
                RunProbeQuiet(probe, clear);
            }

            ApplyCommitRing(probe);

            System.Diagnostics.Process? holder = null;
            if (args.Contains("--yes") && !args.Contains("--no-hold"))
            {
                holder = StartHold(probe);
                if (holder is null)
                {
                    Console.Error.WriteLine("  Warning: could not start the AI hold, an AI-first turn will improvise");
                }
                else
                {
                    Console.WriteLine("  AI hold started");
                }

                _placeHold = StartPlacementHold(probe);
                if (_placeHold is null)
                {
                    Console.Error.WriteLine("  Warning: could not start the placement hold; with an " +
                                            "interactive-placement setup the AI seat will place its " +
                                            "draft squares instead of the peer's");
                }
                else
                {
                    Console.WriteLine("  placement hold started");
                }
            }

            if (args.Contains("--yes") && !args.Contains("--no-force-first"))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (peer.Seat < 0 && !cts.IsCancellationRequested)
                        {
                            await Task.Delay(100, cts.Token);
                        }

                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        var mode = ForceModeFor(joining, firstPlayer);
                        _forceMode = mode;
                        Console.WriteLine($"  coin flip forced: this PC takes `{mode}`, the {firstPlayer} goes first");

                        _forcer = StartForceFirst(probe, mode);
                        if (_forcer is null)
                        {
                            Console.Error.WriteLine("  Warning: could not force the coin flip, check whose turn it is " +
                                                    "on BOTH PCs before moving, and re-roll if they agree");
                        }
                    }
                    catch (OperationCanceledException) { }
                }, cts.Token);
            }

            if (args.Contains("--yes") && !args.Contains("--no-names"))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var misses = 0;
                        while (!cts.IsCancellationRequested && ReadSnapshot(probe) is null)
                        {
                            await Task.Delay(SnapshotPollDelay(misses++, 1000), cts.Token);
                        }

                        for (var i = 0; i < 10 && peer.RemoteName is null && !cts.IsCancellationRequested; i++)
                        {
                            await Task.Delay(500, cts.Token);
                        }

                        if (cts.IsCancellationRequested ||
                            NameArgs(peer.LocalName, peer.RemoteName) is not { Length: > 0 } naming)
                        {
                            return;
                        }

                        Console.WriteLine("  writing the player names onto the board");
                        RunProbeQuiet(probe, [.. naming, "--yes"]);
                    }
                    catch (OperationCanceledException) { }
                }, cts.Token);
            }

            peer.Received += f =>
            {
                var wrongPhase = GatePeerFrame(f);
                if (wrongPhase is not null)
                {
                    peer.HaltAndTell($"the other side sent {wrongPhase}");
                    return;
                }

                switch (f.Kind)
                {
                    case MsgKind.Move when f.Moves is { Count: > 0 }:
                        Console.WriteLine($"  <- turn of {f.Moves.Count} action(s)");
                        var localTurn = new List<Move>();
                        if (LocalShape(probe) is not { } turnShape)
                        {
                            peer.HaltAndTell("the local board could not be read, so the other player's " +
                                             "turn cannot be rotated into this PC's frame");
                            break;
                        }

                        if (FrameLimits.SquaresOff(f.Moves, turnShape.Width, turnShape.Height) is { } offTurn)
                        {
                            peer.HaltAndTell($"the other side sent a turn whose {offTurn}. Nothing of it was applied.");
                            break;
                        }

                        foreach (var their in f.Moves)
                        {
                            var local = their.Rotated(turnShape.Width, turnShape.Height);
                            Console.WriteLine($"     ({their.SrcX},{their.SrcY})->({their.DstX},{their.DstY}) " +
                                              $"in their frame = ({local.SrcX},{local.SrcY})->({local.DstX},{local.DstY}) in ours");
                            localTurn.Add(local);
                        }
                        lock (pending)
                        {
                            if (pending.Count >= FrameLimits.MaxPendingTurns)
                            {
                                peer.HaltAndTell(
                                    $"more than {FrameLimits.MaxPendingTurns} turns queued and unapplied");
                                break;
                            }

                            pending.Enqueue((localTurn, f.Final));
                        }

                        break;

                    case MsgKind.Move:
                        Console.Error.WriteLine("  <- a Move frame with no actions (a pre-v3 peer?), nothing applied");
                        break;

                    case MsgKind.Place when f.Place is not null:
                        ReceivePlacement(peer, probe, f);
                        break;

                    case MsgKind.Hash:
                        Console.WriteLine(HashLine(f));

                        var (candidates, boundary) = BoundaryView();
                        var snap = candidates.Count > 0 ? null : ReadSnapshot(probe);

                        var ahead = f.Turn > boundary.Count;
                        if ((candidates.Count > 0 && boundary.Closing) || ahead)
                        {
                            if (!HoldClosingHash(peer, f, candidates))
                            {
                                Console.WriteLine("    in sync");
                            }
                            else
                            {
                                Console.WriteLine($"    the closing hash matches no board yet; waiting up to " +
                                                  $"{ClosingSettleSeconds} s for this board to settle");
                                StartClosingSettle(peer, probe);
                            }
                        }
                        else if (candidates.Count > 0)
                        {
                            if (peer.CheckHash(f, candidates, f.Turn))
                            {
                                Console.WriteLine("    in sync");
                            }
                        }
                        else if (snap is null)
                        {
                            Console.Error.WriteLine("    could not read a local snapshot to compare");
                        }
                        else if (peer.CheckHash(f, snap, f.Turn))
                        {
                            Console.WriteLine("    in sync");
                        }

                        break;

                    case MsgKind.Rematch:
                        StartRematch(probe, fromPeer: true);
                        break;

                    case MsgKind.Role when f.Role == SessionRole.Play:
                        Console.WriteLine("  both players are in the match (peer role: play)");
                        break;

                    case MsgKind.Role:
                        Console.WriteLine("  waiting: the other player is still in setup (peer role: lobby)");
                        break;

                    case MsgKind.Left:
                        break;

                    default:
                        Console.WriteLine($"  <- {f.Kind} seq {f.Seq}");
                        break;
                }
            };

            _ = peer.Run(cts.Token);
            _ = Task.Run(() => Guarded(async () =>
            {
                while (!cts.IsCancellationRequested && !peer.Halted)
                {
                    (List<Move> Turn, bool Final)? next = null;
                    lock (pending)
                    {
                        if (pending.Count > 0)
                        {
                            next = pending.Dequeue();
                        }
                    }

                    if (next is null)
                    {
                        await Task.Delay(200, cts.Token);
                        continue;
                    }

                    Console.WriteLine("  applying their turn" +
                                      (next.Value.Final ? "\n  (their turn ended the match, so no EndTurn is expected)" : ""));

                    if (next.Value.Final)
                    {
                        _peerEndedMatch = true;
                    }

                    var boundaryBefore = CurrentBoundary().Count;

                    if (PreflightBoard(_lastSnap, () => ReadSnapshot(probe)) is not { } board)
                    {
                        peer.HaltAndTell("the other side's turn arrived with no board of ours to check it against.\n" +
                                         "    Nothing of it was applied.");
                        continue;
                    }

                    if (Machines.TurnProblem(next.Value.Turn, board, board.AiSeat) is { } illegal)
                    {
                        peer.HaltAndTell($"the other side sent a turn this game cannot play: {illegal}.\n" +
                                         "    Nothing of it was applied. Do not play on: compare both boards before restarting.");
                        continue;
                    }

                    if (!await injector.Apply(next.Value.Turn, next.Value.Final, cts.Token))
                    {
                        peer.HaltAndTell(
                            $"injection failed on a turn of {next.Value.Turn.Count} action(s), this board may hold only " +
                            "part of it.\n    Do not play on: compare both boards before restarting.");
                    }


                    if (SendsAppliedHash(_auto, peer.Halted, next.Value.Final, _peerEndedMatch, _autoOffByUser))
                    {
                        _appliedTurns++;
                        var applied = await BoundaryBoard(boundaryBefore, probe, cts.Token);
                        if (applied is null)
                        {
                            Console.Error.WriteLine("  auto: applied the turn but could not read a board to hash; " +
                                                    "this turn goes unchecked.");
                        }
                        else
                        {
                            peer.SendHash(_appliedTurns, applied);
                            Console.WriteLine($"  -> hash for turn {_appliedTurns} sent automatically");
                        }
                    }
                }
            }, reason => peer.HaltAndTell(reason), "turn applier"), cts.Token);

            if (!Console.IsOutputRedirected)
            {
                await Console.Out.WriteLineAsync(
                    "commands:  auto (read and send every turn as you end it)\n" +
                    "           rematch (after a match: both PCs press Retry, nothing is re-written)\n" +
                    "           start (at the top of your turn)   done (after your moves, BEFORE ending the turn)\n" +
                    "           send  (after you have ended the turn, done reads it, send transmits it)\n" +
                    "           move <sx> <sy> <dx> <dy> <facing>   attack <sx> <sy> <tx> <ty> <facing>\n" +
                    "           hash <turn>   quit");
            }

            _ = Task.Run(async () =>
            {
                var misses = 0;
                while (!cts.IsCancellationRequested && !_auto && !_autoOffByUser)
                {
                    try
                    {
                        await Task.Delay(SnapshotPollDelay(misses++, 2000), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (_auto || _autoOffByUser)
                    {
                        return;
                    }

                    if (ReadSnapshot(probe) is { LocalOwner: >= 0 })
                    {
                        HandleCommand(peer, probe, "auto");
                        return;
                    }
                }
            }, cts.Token);

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

                HandleCommand(peer, probe, line);
            }

            StopWatcher();

            if (holder is { HasExited: false })
            {
                Console.WriteLine("  releasing the AI hold");
                try
                {
                    holder.StandardInput.WriteLine("release");
                    holder.StandardInput.Flush();
                    if (!holder.WaitForExit(10_000))
                    {
                        Console.Error.WriteLine("  Warning: the hold did not exit in time");
                    }
                }
                catch (IOException) { }
            }

            if (_placeHold is { HasExited: false })
            {
                try
                {
                    _placeHold.StandardInput.WriteLine("release");
                    _placeHold.StandardInput.Flush();
                    if (!_placeHold.WaitForExit(10_000))
                    {
                        Console.Error.WriteLine("  Warning: the placement hold did not exit; if the AI " +
                                                "seat cannot place, restart the game");
                    }
                }
                catch (IOException) { }
            }

            if (_forcer is { HasExited: false } || args.Contains("--yes") && !args.Contains("--no-force-first"))
            {
                try { if (_forcer is { HasExited: false })
                    {
                        _forcer.Kill(entireProcessTree: false);
                    }
                }
                catch (Exception) { }

                Console.WriteLine("  restoring the coin flip to stock");
                RunProbeQuiet(probe, ["--force-first", "clear", "--yes"]);
            }

            Console.WriteLine("  restoring the game's move bounds to stock");
            RunProbeQuiet(probe, ["--patch-move-bounds", "--clear"]);

            Console.WriteLine("  restoring the game's commit entries to stock");
            RunProbeQuiet(probe, ["--commit-ring", "--clear"]);

            return peer.Halted ? 2 : 0;
        }

        peer.Received += f => Console.WriteLine($"  <- {f.Kind} seq {f.Seq} from seat {f.Seat}");
        await peer.Run(cts.Token);
        return peer.Halted ? 2 : 0;
    }

}

