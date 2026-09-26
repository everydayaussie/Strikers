using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Strikers.Netplay;

internal static partial class Program
{
    private static async Task<int> SelfTest()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var relay = new Relay();
        _ = relay.Run(port, cts.Token, IPAddress.Loopback);
        await Task.Delay(200, cts.Token);

        var room = Relay.NewCode();
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

        var inboxA = new List<Frame>();
        var inboxB = new List<Frame>();
        var a = new Peer("127.0.0.1", port, room);
        var b = new Peer("127.0.0.1", port, room);
        a.Received += f => { lock (inboxA) { inboxA.Add(f); } };
        b.Received += f => { lock (inboxB) { inboxB.Add(f); } };

        a.LocalRole = SessionRole.Lobby;
        b.LocalRole = SessionRole.Play;

        _ = a.Run(cts.Token);
        await Until(() => a.Seat == 0, cts.Token);
        _ = b.Run(cts.Token);
        await Until(() => b.Seat == 1, cts.Token);

        Check("two peers pair by room code and get distinct seats", a.Seat == 0 && b.Seat == 1);

        const string lookAlikes = "ILO01";
        var codeClean = true;
        for (var i = 0; i < 200; i++)
        {
            var sample = Relay.NewCode();
            if (sample.Length != 6 || sample.Any(ch => lookAlikes.Contains(ch)) || sample.Any(ch => !char.IsLetterOrDigit(ch)))
            {
                codeClean = false;
                break;
            }
        }

        Check("the relay's fallback code is 6 chars from the read-aloud alphabet, no look-alikes", codeClean);

        await Until(() => a.RemoteRole is not null && b.RemoteRole is not null, cts.Token);
        Check("a peer learns the other side's role: the lobby sees a play peer",
              a.RemoteRole == SessionRole.Play);
        Check("a peer learns the other side's role: the play sees a lobby peer",
              b.RemoteRole == SessionRole.Lobby);

        var sent = new Move { SrcX = 5, SrcY = 3, DstX = 5, DstY = 3, Facing = 2, Attack = true, TargetX = 5, TargetY = 4 };
        a.SendMove(sent);
        await Until(() => { lock (inboxB) { return inboxB.Any(f => f.Kind == MsgKind.Move); } }, cts.Token);

        Frame? got;
        lock (inboxB)
        {
            got = inboxB.LastOrDefault(f => f.Kind == MsgKind.Move);
        }

        Check("a move reaches the other peer intact",
              got?.Moves?.FirstOrDefault() is { } m && m.SrcX == 5 && m.SrcY == 3 && m.Attack && m.TargetY == 4 && m.Facing == 2);
        Check("the relay stamps the sender's seat", got?.Seat == 0);

        var preDropFingerprint = b.Fingerprint;
        b.DropLink();
        await Task.Delay(200, cts.Token);

        var duringOutage = new Move { SrcX = 2, SrcY = 2, DstX = 2, DstY = 3, Facing = 2 };
        a.SendMove(duringOutage);

        var beforeDrop = preDropFingerprint;

        await Until(() => b.Seat == 1 && b.Fingerprint is not null && b.Fingerprint != beforeDrop, cts.Token);

        Check("a dropped peer reclaims the same seat", b.Seat == 1);
        Check("a reconnected peer is encrypted again under a FRESH session key",
              b.Fingerprint is not null && b.Fingerprint != beforeDrop);

        var afterOutage = new Move { SrcX = 6, SrcY = 6, DstX = 6, DstY = 5, Facing = 0 };
        a.SendMove(afterOutage);
        await Until(() =>
        {
            lock (inboxB)
            {
                return inboxB.Any(f => f.Moves?.FirstOrDefault() is { SrcX: 6, SrcY: 6, DstY: 5 });
            }
        }, cts.Token);

        lock (inboxB)
        {
            got = inboxB.LastOrDefault(f => f.Kind == MsgKind.Move);
        }

        Check("a move sent AFTER a reconnect still crosses, so both sides re-keyed together",
              got?.Moves?.FirstOrDefault() is { SrcX: 6, SrcY: 6, DstY: 5 });

        int ownFrames;
        lock (inboxA)
        {
            ownFrames = inboxA.Count(f => f.Kind == MsgKind.Move && f.Seat == a.Seat);
        }

        Check("a peer is never handed a frame it sent itself", ownFrames == 0);

        var inboxC = new List<Frame>();
        var c = new Peer("127.0.0.1", port, room, 1);
        c.Received += f => { lock (inboxC) { inboxC.Add(f); } };
        _ = c.Run(cts.Token);
        await Until(() => c.Seat >= 0, cts.Token);
        await Task.Delay(300, cts.Token);

        int replayed;
        lock (inboxC)
        {
            replayed = inboxC.Count(f => f.Kind == MsgKind.Move);
        }

        Check("a freshly started peer is not replayed a match already in progress", replayed == 0);

        var inboxH = new List<Frame>();
        var h1 = new Peer("127.0.0.1", port, "HALTRM", 0);
        var h2 = new Peer("127.0.0.1", port, "HALTRM", 1);
        h2.Received += f => { lock (inboxH) { inboxH.Add(f); } };
        _ = h1.Run(cts.Token);
        _ = h2.Run(cts.Token);
        await Until(() => h1.Seat >= 0 && h2.Seat >= 0, cts.Token);

        h1.HaltAndTell("injection failed on a turn of 3 action(s)");
        await Until(() => h1.Halted && h2.Halted && h2.HaltReason is not null, cts.Token);

        Check("a local halt stops the peer that raised it", h1.Halted);
        Check("a local halt stops the OTHER peer too", h2.Halted);
        Check("the halt reason crosses to the other peer",
              h2.HaltReason?.Contains("injection failed") == true);

        var hx1 = new Peer("127.0.0.1", port, "HASHRM", 0);
        var hx2 = new Peer("127.0.0.1", port, "HASHRM", 1);
        _ = hx1.Run(cts.Token);
        _ = hx2.Run(cts.Token);
        await Until(() => hx1.Fingerprint is not null && hx2.Fingerprint is not null, cts.Token);

        var comparedBlind = hx1.CheckHash(
            new Frame { Kind = MsgKind.Hash, Turn = 7, PieceHash = new string('a', FrameLimits.HashLength),
                        TerrainHash = new string('b', FrameLimits.HashLength) },
            new List<BoardSnapshot>(), 7);
        await Until(() => hx2.Halted && hx2.HaltReason is not null, cts.Token);

        Check("a halt raised while comparing hashes tells the other side too",
              !comparedBlind && hx1.Halted && hx2.Halted
              && hx1.HaltReason?.Contains("no board of ours") == true
              && hx2.HaltReason?.Contains("no board of ours") == true
              && hx2.HaltReason?.StartsWith("the other PC stopped the match: ", StringComparison.Ordinal) == true);

        var inboxBig = new List<Frame>();
        var big1 = new Peer("127.0.0.1", port, "BIGRM", 0);
        var big2 = new Peer("127.0.0.1", port, "BIGRM", 1);
        big2.Received += f => { lock (inboxBig) { inboxBig.Add(f); } };
        _ = big1.Run(cts.Token);
        _ = big2.Run(cts.Token);
        await Until(() => big1.Fingerprint is not null && big2.Fingerprint is not null, cts.Token);

        big1.SendMoves(Enumerable.Range(0, FrameLimits.MaxMoves + 1)
            .Select(_ => new Move { SrcX = 5, SrcY = 3, DstX = 5, DstY = 2 }));
        await Until(() => big2.Halted, cts.Token);

        Check("an oversize turn is refused after the seal is opened, not only on plaintext",
              big2.Halted && big2.HaltReason?.Contains("over the") == true);
        Check("the oversize turn never reached a handler",
              !inboxBig.Any(f => f.Kind == MsgKind.Move));

        async Task<(TcpClient client, StreamReader reader, StreamWriter writer)> RawConnect()
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var w = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(c.GetStream(), Encoding.UTF8);
            return (c, r, w);
        }

        async Task<string?> HaltReasonFrom(StreamReader r)
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            patience.CancelAfter(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 60; i++)
            {
                string? l;
                try
                {
                    l = await r.ReadLineAsync(patience.Token);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    return null;
                }

                if (l is null)
                {
                    return null;
                }

                if (Protocol.Decode(l) is { Kind: MsgKind.Halt } h)
                {
                    return h.Reason;
                }
            }

            return null;
        }

        var (floodC, floodR, floodW) = await RawConnect();
        await floodW.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "FLOODRM", Seat = 0 }));
        for (var i = 0; i < FrameLimits.MaxFramesPerWindow + 5; i++)
        {
            await floodW.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Ready }));
        }

        Check("the relay halts a peer that floods it past the window",
              (await HaltReasonFrom(floodR))?.Contains("frames in") == true);
        floodC.Dispose();

        var (bigC, bigR, bigW) = await RawConnect();
        await bigW.WriteAsync(new string('x', FrameLimits.MaxLine + 16));
        await bigW.FlushAsync();

        Check("the relay refuses a line over the cap instead of buffering it",
              (await HaltReasonFrom(bigR))?.Contains("no end of line") == true);
        bigC.Dispose();

        var roomsBefore = relay.RoomCount;
        var (g1C, _, g1W) = await RawConnect();
        var (g2C, _, g2W) = await RawConnect();
        await g1W.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "GONERM", Seat = 0 }));
        await g2W.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "GONERM", Seat = 1 }));
        await Until(() => relay.RoomCount == roomsBefore + 1, cts.Token);
        g1C.Dispose();
        g2C.Dispose();
        await Until(() => relay.RoomCount == roomsBefore, cts.Token);

        Check("an empty room is removed once both peers leave", relay.RoomCount == roomsBefore);

        var (stayC, stayR, stayW) = await RawConnect();
        var (goC, _, goW) = await RawConnect();
        await stayW.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "LEFTRM", Seat = 0 }));
        await goW.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "LEFTRM", Seat = 1 }));
        await Until(() => relay.RoomCount == roomsBefore + 1, cts.Token);
        await Task.Delay(100, cts.Token);
        goC.Dispose();

        Frame? left = null;
        for (var i = 0; i < 60 && left is null; i++)
        {
            var l = await stayR.ReadLineAsync(cts.Token);
            if (l is null)
            {
                break;
            }

            if (Protocol.Decode(l) is { Kind: MsgKind.Left } gone)
            {
                left = gone;
            }
        }

        Check("the relay tells the seat that stays when the other empties, naming the seat that left",
              left is { Seat: 1, Room: "LEFTRM" });
        stayC.Dispose();
        await Until(() => relay.RoomCount == roomsBefore, cts.Token);

        var inboxStay = new List<Frame>();
        var stayer = new Peer("127.0.0.1", port, "REKEYRM", 0);
        stayer.LocalRole = SessionRole.Lobby;
        stayer.Received += f => { lock (inboxStay) { inboxStay.Add(f); } };
        var channelsBuilt = 0;
        stayer.ChannelBuilt += () => Interlocked.Increment(ref channelsBuilt);
        using var leaverCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var leaver = new Peer("127.0.0.1", port, "REKEYRM", 1);
        leaver.LocalRole = SessionRole.Lobby;
        _ = stayer.Run(cts.Token);
        await Until(() => stayer.Seat == 0, cts.Token);
        _ = leaver.Run(leaverCts.Token);
        await Until(() => stayer.Fingerprint is not null && leaver.Fingerprint is not null
                          && stayer.RemoteRole is not null, cts.Token);
        var firstCode = stayer.Fingerprint;
        var oneChannel = channelsBuilt == 1;

        leaverCts.Cancel();
        await Until(() => { lock (inboxStay) { return inboxStay.Any(f => f.Kind == MsgKind.Left); } }, cts.Token);
        Check("a peer is handed the relay's Left notice after the key exchange and forgets the old peer's role",
              !stayer.Halted && stayer.RemoteRole is null);

        var comer = new Peer("127.0.0.1", port, "REKEYRM", 1);
        comer.LocalRole = SessionRole.Lobby;
        _ = comer.Run(cts.Token);
        await Until(() => channelsBuilt == 2 && comer.Fingerprint is not null, cts.Token);
        Check("the next peer on the seat builds a second channel with a fresh fingerprint the two share",
              oneChannel && stayer.Fingerprint != firstCode && stayer.Fingerprint == comer.Fingerprint
              && !stayer.Halted && !comer.Halted);

        var (twoC, twoR, twoW) = await RawConnect();
        await twoW.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "TWORM", Seat = 0 }));
        await twoW.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "TWORM2", Seat = 0 }));

        Check("the relay refuses a second Hello on one connection",
              (await HaltReasonFrom(twoR))?.Contains("second Hello") == true);
        twoC.Dispose();

        var fakeRelay = new TcpListener(IPAddress.Loopback, 0);
        fakeRelay.Start();
        var fakePort = ((IPEndPoint)fakeRelay.LocalEndpoint).Port;
        var seatPeer = new Peer("127.0.0.1", fakePort, "SEATRM", 0);
        _ = seatPeer.Run(cts.Token);
        using (var fake = await fakeRelay.AcceptTcpClientAsync(cts.Token))
        {
            using var fakeReader = new StreamReader(fake.GetStream(), Encoding.UTF8);
            var fakeWriter = new StreamWriter(fake.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            await fakeReader.ReadLineAsync(cts.Token);
            await fakeWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Paired, Room = "SEATRM", Seat = 99 }));
            await Until(() => seatPeer.Halted, cts.Token);
        }

        fakeRelay.Stop();
        Check("a Paired with an impossible seat halts the peer instead of faulting it",
              seatPeer.Halted && seatPeer.HaltReason?.Contains("impossible seat") == true);

        var plainRelay = new TcpListener(IPAddress.Loopback, 0);
        plainRelay.Start();
        var plainPort = ((IPEndPoint)plainRelay.LocalEndpoint).Port;
        var plainPeer = new Peer("127.0.0.1", plainPort, "PLAINRM", 0);
        var plainSeen = new List<MsgKind>();
        plainPeer.Received += f =>
        {
            lock (plainSeen)
            {
                plainSeen.Add(f.Kind);
            }
        };
        _ = plainPeer.Run(cts.Token);
        using (var fake = await plainRelay.AcceptTcpClientAsync(cts.Token))
        {
            using var fakeReader = new StreamReader(fake.GetStream(), Encoding.UTF8);
            var fakeWriter = new StreamWriter(fake.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            await fakeReader.ReadLineAsync(cts.Token);
            await fakeWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Paired, Room = "PLAINRM", Seat = 0 }));
            await fakeWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Setup, Seat = 1, Challenge = "x" }));
            await Until(() => plainPeer.Halted, cts.Token);
        }

        plainRelay.Stop();
        Check("a plaintext frame before the key exchange halts the peer and reaches no handler",
              plainPeer.Halted && plainPeer.HaltReason?.Contains("before the key exchange") == true
              && !plainSeen.Contains(MsgKind.Setup));

        var (haltHost, haltGuest) = await Pair(port, cts.Token);
        var haltSeen = new List<MsgKind>();
        haltHost.Received += f =>
        {
            lock (haltSeen)
            {
                haltSeen.Add(f.Kind);
            }

            if (f.Kind == MsgKind.Hash)
            {
                haltHost.HaltAndTell("refused in a handler");
            }
        };
        await Until(() => haltHost.Fingerprint is not null && haltGuest.Fingerprint is not null, cts.Token);
        haltGuest.Send(new Frame { Kind = MsgKind.Hash, Turn = 1, PieceHash = "0123456789abcdef", TerrainHash = "fedcba9876543210" });
        haltGuest.Send(new Frame { Kind = MsgKind.Rematch });
        await Until(() => haltHost.Halted, cts.Token);
        await Task.Delay(300, cts.Token);
        bool haltSawHashOnly;
        lock (haltSeen)
        {
            haltSawHashOnly = haltSeen.Contains(MsgKind.Hash) && !haltSeen.Contains(MsgKind.Rematch);
        }

        Check("a halt raised in a handler stops the read loop, and the frame behind it reaches no handler",
              haltHost.Halted && haltSawHashOnly);

        var (drainHost, drainGuest) = await Pair(port, cts.Token);
        var drainSeen = new List<MsgKind>();
        drainHost.Received += f =>
        {
            lock (drainSeen)
            {
                drainSeen.Add(f.Kind);
            }
        };
        await Until(() => drainHost.Fingerprint is not null && drainGuest.Fingerprint is not null, cts.Token);
        drainHost.HaltAndTell("halted by the poller, outside any handler");
        drainGuest.Send(new Frame
        {
            Kind = MsgKind.RecordingStart, Part = 0, Parts = 1,
            Data = Convert.ToBase64String("{}"u8.ToArray()),
        });
        drainGuest.Send(new Frame { Kind = MsgKind.Rematch });
        await Task.Delay(800, cts.Token);
        bool drainTookTheStart;
        lock (drainSeen)
        {
            drainTookTheStart = drainSeen.Contains(MsgKind.RecordingStart) && !drainSeen.Contains(MsgKind.Rematch);
        }

        Check("the first file frame read after this side's own halt is drained to its handler, not dropped, and a Rematch behind it still is",
              drainHost.Halted && drainTookTheStart);

        var closeLines = new Relay();
        var closeAt = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var unseatedClose = closeLines.CloseLine(-1, closeAt);
        Check("the relay names a connection that closed before taking a seat, and never prints seat -1",
              unseatedClose == "  a connection closed before it took a seat"
              && closeLines.CloseLine(1, closeAt) == "  peer disconnected (seat 1)"
              && unseatedClose?.Contains("-1") == false);

        var tally = new Relay();
        var tallyStart = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var tallyFirst = tally.CloseLine(-1, tallyStart);
        var tallyQuiet = 0;
        var seatedEveryTime = true;
        for (var i = 1; i <= 500; i++)
        {
            var tallyAt = tallyStart + TimeSpan.FromMilliseconds(i);
            if (tally.CloseLine(-1, tallyAt) is not null)
            {
                tallyQuiet++;
            }

            if (tally.CloseLine(i % 2, tallyAt) != $"  peer disconnected (seat {i % 2})")
            {
                seatedEveryTime = false;
            }
        }

        var tallyLater = tally.CloseLine(-1, tallyStart + Relay.UnseatedQuiet);
        Check("connections that close before taking a seat print one relay line in ten seconds, carrying their count, while a seated peer's close prints every time",
              tallyFirst == "  a connection closed before it took a seat" && tallyQuiet == 0 && seatedEveryTime
              && tallyLater == "  501 connections closed before they took a seat");

        Check("a minted routing id is 16 hex characters and two mints differ",
              RoomId.Random().Length == 16 && RoomId.Random().All(Uri.IsHexDigit)
              && RoomId.Random() != RoomId.Random());

        var (replayA, _, replayAWriter) = await RawConnect();
        var (replayB, _, replayBWriter) = await RawConnect();
        await replayAWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "REPLAYRM", Seat = 0 }));
        await replayBWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "REPLAYRM", Seat = 1 }));
        await replayAWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Move, Moves = [] }));
        await Task.Delay(200, cts.Token);
        replayB.Dispose();
        await Task.Delay(500, cts.Token);
        var (replayC, replayCReader, replayCWriter) = await RawConnect();
        await replayCWriter.WriteLineAsync(Protocol.Encode(new Frame
        {
            Kind = MsgKind.Hello, Room = "REPLAYRM", Seat = 1, ResumeFrom = 0,
        }));
        var replayKinds = new List<MsgKind>();
        using (var replayWindow = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            replayWindow.CancelAfter(TimeSpan.FromMilliseconds(600));
            try
            {
                while (await replayCReader.ReadLineAsync(replayWindow.Token) is { } replayLine)
                {
                    if (Protocol.Decode(replayLine) is { } handed)
                    {
                        replayKinds.Add(handed.Kind);
                    }
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
            }
        }

        Check("a Hello that asks the relay for a replay is paired and handed nothing before it",
              replayKinds.Contains(MsgKind.Paired) && !replayKinds.Contains(MsgKind.Move));
        replayA.Dispose();
        replayC.Dispose();

        static int FreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var free = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return free;
        }

        var quietRelay = new Relay(helloWithin: TimeSpan.FromMilliseconds(300));
        var quietPort = FreeLoopbackPort();
        _ = quietRelay.Run(quietPort, cts.Token, IPAddress.Loopback);
        using (var quiet = new TcpClient())
        {
            await quiet.ConnectAsync(IPAddress.Loopback, quietPort, cts.Token);
            using var quietReader = new StreamReader(quiet.GetStream(), Encoding.UTF8);
            Check("a connection with no Hello is refused at the deadline",
                  (await HaltReasonFrom(quietReader))?.Contains("no Hello within") == true);
        }

        Check("a relay named a room serves that room alone, case-blind, and a CLI relay serves any",
              Relay.RoomAllowed(null, "ANY") && Relay.RoomAllowed("ab12", "AB12")
              && !Relay.RoomAllowed("AB12", "AB13") && !Relay.RoomAllowed("AB12", null));
        var ownRelay = new Relay(onlyRoom: "OWNRM");
        var ownPort = FreeLoopbackPort();
        _ = ownRelay.Run(ownPort, cts.Token, IPAddress.Loopback);
        using (var stranger = new TcpClient())
        {
            await stranger.ConnectAsync(IPAddress.Loopback, ownPort, cts.Token);
            var strangerWriter = new StreamWriter(stranger.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            using var strangerReader = new StreamReader(stranger.GetStream(), Encoding.UTF8);
            await strangerWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "OTHERRM", Seat = 0 }));
            Check("a Hello for another room on the launcher's relay is refused",
                  (await HaltReasonFrom(strangerReader))?.Contains("unknown room") == true);
        }

        Check("a relay with a tunnel binds loopback, one without binds every interface, --bind wins",
              Relay.BindFor(null, tunnel: true).Equals(IPAddress.Loopback)
              && Relay.BindFor(null, tunnel: false).Equals(IPAddress.Any)
              && Relay.BindFor("0.0.0.0", tunnel: true).Equals(IPAddress.Any));

        var backlogRelay = new Relay(onlyRoom: "BACKRM");
        var backlog = new TcpListener(IPAddress.Loopback, 0);
        backlog.Start();
        var backlogEnd = (IPEndPoint)backlog.LocalEndpoint;
        var droppedFirst = new TcpClient(AddressFamily.InterNetwork);
        await droppedFirst.ConnectAsync(backlogEnd, cts.Token);
        droppedFirst.Client.Close(0);
        using var behindIt = new TcpClient(AddressFamily.InterNetwork);
        await behindIt.ConnectAsync(backlogEnd, cts.Token);
        await Task.Delay(100, cts.Token);
        _ = backlogRelay.AcceptAll(backlog, cts.Token);
        var behindItSeated = false;
        try
        {
            var behindItWriter = new StreamWriter(behindIt.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            await behindItWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "BACKRM", Seat = 0 }));
            behindItSeated = await Until(() => backlogRelay.Seated((IPEndPoint)behindIt.Client.LocalEndPoint!),
                                         cts.Token, 2000);
        }
        catch (IOException)
        {
        }

        Check("the relay goes on accepting past a connection reset while it waited to be accepted",
              behindItSeated);
        droppedFirst.Dispose();
        var oldEnough = TunnelClient.ForwardPool.EvictionGrace + TimeSpan.FromSeconds(1);
        var grace = TunnelClient.ForwardPool.EvictionGrace;
        TimeSpan[] threeOld = [oldEnough, oldEnough, oldEnough];
        TimeSpan[] twoOld = [oldEnough, oldEnough];
        Check("a full pool gives up its oldest unseated slot, and refuses only when every one is seated",
              TunnelClient.ForwardPool.Evict([true, false, false], threeOld, grace) == 1
              && TunnelClient.ForwardPool.Evict([false, true], twoOld, grace) == 0
              && TunnelClient.ForwardPool.Evict([true, true], twoOld, grace) == -1);

        Check("a slot still inside its grace is not given up, so a dialling stranger cannot turn the pool over",
              TunnelClient.ForwardPool.Evict([false, false], [TimeSpan.Zero, TimeSpan.Zero], grace) == -1
              && TunnelClient.ForwardPool.Evict([false, false], [TimeSpan.Zero, oldEnough], grace) == 1
              && TunnelClient.ForwardPool.Evict([false, false], [oldEnough, TimeSpan.Zero], grace) == 0);

        var guestHelloBack = TimeSpan.FromSeconds(1);
        var guestSeatedBy = TunnelClient.NetworkTimeout + guestHelloBack;
        Check("an unseated tunnel slot can be given up once a third of the relay's Hello deadline has passed, and is kept through its forward's dial plus one second for the guest's Hello to come back",
              TunnelClient.ForwardPool.Evict([false], [Relay.HelloWithin / 3], TunnelClient.ForwardPool.EvictionGrace) == 0
              && TunnelClient.ForwardPool.Evict([false], [guestSeatedBy], TunnelClient.ForwardPool.EvictionGrace) == -1);

        var slotRelay = new Relay(onlyRoom: "SLOTRM");
        var slotPort = FreeLoopbackPort();
        _ = slotRelay.Run(slotPort, cts.Token, IPAddress.Loopback);
        var pool = new TunnelClient.ForwardPool(3, slotRelay.Seated, TimeSpan.Zero);
        var slots = new List<TunnelClient.ForwardPool.Slot>();
        var slotSockets = new List<TcpClient>();
        for (var i = 0; i < 3; i++)
        {
            var slot = pool.Admit(cts.Token)!;
            var (slotClient, slotLocal) = await TunnelClient.ConnectToRelay(slotPort, cts.Token);
            slot.Local = slotLocal;
            slots.Add(slot);
            slotSockets.Add(slotClient);
        }

        var slotPlayer = new StreamWriter(slotSockets[0].GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        await slotPlayer.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = "SLOTRM", Seat = 0 }));
        await Until(() => slotRelay.Seated(slots[0].Local!), cts.Token);
        var newcomer = pool.Admit(cts.Token);
        Check("a newcomer to a full pool takes the oldest idle stranger's slot, never the seated player's",
              newcomer is not null && !slots[0].Token.IsCancellationRequested
              && slots[1].Token.IsCancellationRequested && !slots[2].Token.IsCancellationRequested);
        foreach (var slotSocket in slotSockets)
        {
            slotSocket.Dispose();
        }

        var (relaySide, stalled) = LoopbackPair();
        Relay.Configure(relaySide, TimeSpan.FromMilliseconds(300));
        relaySide.Client.SendBufferSize = 4096;
        stalled.Client.ReceiveBufferSize = 4096;
        var stuck = new Relay.Connection(relaySide);
        var bulky = new Frame { Kind = MsgKind.Halt, Reason = new string('x', 60000) };
        var stuckWrites = Task.Run(() =>
        {
            for (var i = 0; i < 512; i++)
            {
                stuck.Send(bulky);
            }
        });
        Check("a relay write to a peer that has stopped reading gives up rather than blocking",
              await Task.WhenAny(stuckWrites, Task.Delay(TimeSpan.FromSeconds(2))) == stuckWrites);
        relaySide.Dispose();
        stalled.Dispose();

        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var sepA = new Peer("127.0.0.1", port, "SECRETCODE", 0, routingId: nonce);
        var sepB = new Peer("127.0.0.1", port, "SECRETCODE", 1, routingId: nonce);
        _ = sepA.Run(cts.Token);
        _ = sepB.Run(cts.Token);
        await Until(() => sepA.Fingerprint is not null && sepB.Fingerprint is not null, cts.Token);

        Check("peers pair on a routing nonce that is NOT the code's hash",
              nonce != RoomId.For("SECRETCODE") && sepA.Fingerprint is not null);
        Check("both sides still derive the same safety code from the shared room code",
              sepA.Fingerprint == sepB.Fingerprint);

        var commitRoom = RoomId.Random();
        var (committer, _, committerWriter) = await RawConnect();
        await committerWriter.WriteLineAsync(Protocol.Encode(new Frame { Kind = MsgKind.Hello, Room = commitRoom, Seat = 0 }));
        var commitAnswerer = new Peer("127.0.0.1", port, "COMMITCODE", 1, routingId: commitRoom);
        _ = commitAnswerer.Run(cts.Token);
        await Until(() => commitAnswerer.Seat == 1, cts.Token);
        using (var committedKey = KeyExchange.Begin())
        using (var revealedKey = KeyExchange.Begin())
        {
            var committed = KeyExchange.Commitment(KeyExchange.PublicBlob(committedKey), bound: false);
            await committerWriter.WriteLineAsync(Protocol.Encode(new Frame
            {
                Kind = MsgKind.KeyCommit, Commit = Convert.ToBase64String(committed),
            }));
            await committerWriter.WriteLineAsync(Protocol.Encode(new Frame
            {
                Kind = MsgKind.KeyEx, PublicKey = Convert.ToBase64String(KeyExchange.PublicBlob(revealedKey)),
            }));
            await Until(() => commitAnswerer.Halted, cts.Token);
        }

        Check("D-219: a key that is not the one committed to halts the exchange and derives nothing",
              commitAnswerer.Halted && commitAnswerer.HaltReason?.Contains("does not match the commitment") == true
              && commitAnswerer.Fingerprint is null);
        committer.Dispose();

        static async Task<(Peer A, Peer B)> BoundPair(int relayPort, byte[]? bindA, byte[]? bindB,
                                                      CancellationToken token)
        {
            var nonceAb = RoomId.Random();
            var a = new Peer("127.0.0.1", relayPort, "BINDCODE", 0, routingId: nonceAb)
            {
                Binding = bindA, RequireBinding = bindA is not null,
            };
            var b = new Peer("127.0.0.1", relayPort, "BINDCODE", 1, routingId: nonceAb)
            {
                Binding = bindB, RequireBinding = bindB is not null,
            };
            _ = a.Run(token);
            await Until(() => a.Seat == 0, token);
            _ = b.Run(token);
            await Until(() => (a.Fingerprint is not null && b.Fingerprint is not null) || a.Halted || b.Halted,
                        token);
            await Task.Delay(300, token);
            return (a, b);
        }

        var sharedBinding = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var (boundA, boundB) = await BoundPair(port, sharedBinding, [.. sharedBinding], cts.Token);
        Check("D-219: two --play sides holding the same binding pair on it",
              boundA.Fingerprint is not null && boundA.Fingerprint == boundB.Fingerprint
              && !boundA.Halted && !boundB.Halted);

        var (wrongA, wrongB) = await BoundPair(port, sharedBinding,
                                               System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), cts.Token);
        Check("D-219: two sides holding different bindings open nothing of each other's",
              wrongA.Halted || wrongB.Halted);

        var (requiring, unbound) = await BoundPair(port, sharedBinding, null, cts.Token);
        Check("D-219: --play refuses an exchange that is not bound to the compared session",
              requiring.Halted && requiring.HaltReason?.Contains("not bound to the session") == true);

        Check("D-219: both ends of a channel bind to the same value, and a moved-on channel binds nothing",
              sepA.BindToChannel(sepA.ChannelGeneration, sepA.Fingerprint) is { } boundOne
              && sepB.BindToChannel(sepB.ChannelGeneration, sepB.Fingerprint) is { } boundTwo
              && boundOne.SequenceEqual(boundTwo)
              && sepA.BindToChannel(sepA.ChannelGeneration + 1, sepA.Fingerprint) is null);

        Check("a bind names the code it compared, so the channel in use under another code binds nothing",
              sepA.BindToChannel(sepA.ChannelGeneration, "0000000000000000") is null
              && sepA.BindToChannel(sepA.ChannelGeneration, null) is null
              && sepA.BindToChannel(sepA.ChannelGeneration, sepA.Fingerprint) is not null);

        var shownOnSep = sepA.Fingerprint;
        var movedLobby = new Lobby("S", isHost: true);
        var sentOnMoved = ConfirmCode(sepA, movedLobby, shownOnSep, (shownOnSep, sepA.ChannelGeneration + 1));
        movedLobby.ConfirmedByPeer(sepA.ChannelGeneration + 1, movedLobby.SetupDigest(), movedLobby.SetupDigest());
        var keptLobby = new Lobby("S", isHost: true);
        var sentOnShown = ConfirmCode(sepA, keptLobby, shownOnSep, (shownOnSep, sepA.ChannelGeneration));
        keptLobby.ConfirmedByPeer(sepA.ChannelGeneration, keptLobby.SetupDigest(), keptLobby.SetupDigest());
        Check("a confirmation goes out only on the channel whose code was compared, and otherwise records nothing",
              !sentOnMoved && movedLobby.BindingChannel(shownOnSep, sepA.ChannelGeneration + 1) is null
              && sentOnShown && keptLobby.BindingChannel(shownOnSep, sepA.ChannelGeneration) == sepA.ChannelGeneration);

        Check("D-219: a lobby binds only when both confirmations stand on the channel in use",
              Lobby.BindingDue(1, 1, 1) && !Lobby.BindingDue(1, 1, 2) && !Lobby.BindingDue(1, 2, 2)
              && !Lobby.BindingDue(0, 0, 0));
        Check("D-219: --binding takes exactly 32 bytes of hex",
              BindingFrom(new string('a', 64)) is { Length: 32 } && BindingFrom(new string('a', 63)) is null
              && BindingFrom(new string('g', 64)) is null && BindingFrom(null) is null && BindingFrom(new string('a', 66)) is null);

        var lobbyRoom = RoomId.Random();
        using var guestLife = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var hostPeer = new Peer("127.0.0.1", port, "LOBBYBIND", 0, routingId: lobbyRoom);
        var guestPeer = new Peer("127.0.0.1", port, "LOBBYBIND", 1, routingId: lobbyRoom);
        _ = hostPeer.Run(cts.Token);
        await Until(() => hostPeer.Seat == 0, cts.Token);
        _ = guestPeer.Run(guestLife.Token);
        await Until(() => hostPeer.Fingerprint is not null && guestPeer.Fingerprint is not null, cts.Token);
        var bindHostLobby = new Lobby("B", isHost: true);
        var bindGuestLobby = new Lobby("B", isHost: false);
        bindHostLobby.ConfirmedLocally(hostPeer.ChannelGeneration, hostPeer.Fingerprint!);
        bindGuestLobby.ConfirmedByPeer(guestPeer.ChannelGeneration, bindHostLobby.SetupDigest(),
                                       bindGuestLobby.SetupDigest());
        BindIfBothConfirmed(hostPeer, bindHostLobby);
        var hostUnboundAfterOne = hostPeer.Binding is null;
        bindGuestLobby.ConfirmedLocally(guestPeer.ChannelGeneration, guestPeer.Fingerprint!);
        bindHostLobby.ConfirmedByPeer(hostPeer.ChannelGeneration, bindGuestLobby.SetupDigest(),
                                      bindHostLobby.SetupDigest());
        BindIfBothConfirmed(hostPeer, bindHostLobby);
        BindIfBothConfirmed(guestPeer, bindGuestLobby);
        Check("D-219: two lobbies bind once both have confirmed, and to the same value",
              hostUnboundAfterOne && hostPeer.Binding is { } hostBinding && guestPeer.Binding is { } guestBinding
              && hostBinding.SequenceEqual(guestBinding)
              && bindHostLobby.BoundSetup is not null && bindGuestLobby.BoundSetup is not null);

        var (retryHost, retryGuest) = AgreedLobbies([.. new int[30]]);
        var retryChannel = hostPeer.ChannelGeneration;
        retryHost.ConfirmedLocally(retryChannel, hostPeer.Fingerprint!);
        retryHost.ConfirmedByPeer(retryChannel, retryGuest.SetupDigest(), retryHost.SetupDigest());
        BindIfBothConfirmed(hostPeer, retryHost);
        var firstWrite = LobbyWrite(hostPeer, retryHost, "live-probe", yes: false);

        var lobbyCode = hostPeer.Fingerprint;
        guestLife.Cancel();
        await Task.Delay(500, cts.Token);
        using var playLife = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var guestPlay = new Peer("127.0.0.1", port, "LOBBYBIND", 1, routingId: lobbyRoom)
        {
            Binding = guestPeer.Binding, RequireBinding = true, LocalRole = SessionRole.Play,
        };
        _ = guestPlay.Run(playLife.Token);
        await Until(() => guestPlay.Fingerprint is not null && hostPeer.Fingerprint != lobbyCode, cts.Token);
        await Task.Delay(300, cts.Token);
        Check("D-219: the other PC's --play, handed the binding, pairs with the bound lobby on it",
              guestPlay.Fingerprint == hostPeer.Fingerprint && !guestPlay.Halted && !hostPeer.Halted
              && hostPeer.Binding is not null);

        LobbyHeardLeft(retryHost);
        var retryWrite = LobbyWrite(hostPeer, retryHost, "live-probe", yes: false);
        Check("a write that stopped at a game step runs again once the other PC's lobby has left and its --play holds the bound session, through the same gates",
              firstWrite == 0 && retryWrite == 0 && !hostPeer.Halted && hostPeer.Binding is not null
              && hostPeer.ChannelGeneration != retryChannel && retryHost.BothReady && retryHost.BoundSetup is not null);

        var oldCode = hostPeer.Fingerprint;
        playLife.Cancel();
        await Task.Delay(500, cts.Token);
        var restartedGuest = new Peer("127.0.0.1", port, "LOBBYBIND", 1, routingId: lobbyRoom);
        _ = restartedGuest.Run(cts.Token);
        await Until(() => restartedGuest.Fingerprint is not null && hostPeer.Fingerprint != oldCode, cts.Token);
        Check("D-219: a bound lobby meeting a player who started over derives open and drops its binding",
              hostPeer.Binding is null && restartedGuest.Fingerprint == hostPeer.Fingerprint && !hostPeer.Halted);

        Check("no halt signal fires by default, so --selftest freezes nothing", Peer.OnHalt is null);

        var signals = new List<string>();
        Peer.OnHalt = r => { lock (signals) { signals.Add(r); } };
        try
        {
            var f1 = new Peer("127.0.0.1", port, "FREEZE", 0);
            var f2 = new Peer("127.0.0.1", port, "FREEZE", 1);
            _ = f1.Run(cts.Token);
            _ = f2.Run(cts.Token);
            await Until(() => f1.Seat >= 0 && f2.Seat >= 0, cts.Token);

            f1.HaltAndTell("could not read the local turn automatically");
            await Until(() => { lock (signals) { return signals.Count == 2; } }, cts.Token);

            Check("the peer that RAISED the halt fires the signal",
                  signals.Any(r => r.Contains("could not read")));
            Check("the peer that RECEIVED the halt fires the signal too", signals.Count == 2);
        }
        finally
        {
            Peer.OnHalt = null;
        }

        var viewA = SampleBoard();
        var viewB = RotateByHand(viewA);

        Check("rotated views produce the same piece hash",
              BoardHash.Pieces(viewA, 0) == BoardHash.Pieces(viewB, 1));
        Check("rotated views produce the same terrain hash",
              BoardHash.Terrain(viewA, 0) == BoardHash.Terrain(viewB, 1));

        var shuffled = viewA with { Pieces = viewA.Pieces.Reverse().ToList() };
        Check("piece list order does not change the hash",
              BoardHash.Pieces(viewA, 0) == BoardHash.Pieces(shuffled, 0));

        var acted = viewA with
        {
            Pieces = viewA.Pieces.Select(p => p with { Facing = (byte)(p.Facing | 0x1C) }).ToList(),
        };
        Check("action and burst counters are excluded from the hash",
              BoardHash.Pieces(viewA, 0) == BoardHash.Pieces(acted, 0));

        var withCorpse = viewA with
        {
            Pieces = [.. viewA.Pieces, new Piece(0, 0, 0, 0, 1, Idx: 99)],
        };
        Check("a machine at health 0 is left out of the hash, both sides agree it is dead",
              BoardHash.Pieces(viewA, 0) == BoardHash.Pieces(withCorpse, 0));

        var stillAlive = viewA with
        {
            Pieces = [.. viewA.Pieces, new Piece(0, 0, 2, 0, 1, Idx: 99)],
        };
        Check("the same square holding a LIVING machine on one side only still differs",
              BoardHash.Pieces(withCorpse, 0) != BoardHash.Pieces(stillAlive, 0));

        var burstFlagged = viewA with { Pieces = viewA.Pieces.Select(p => p with { Burst = true }).ToList() };
        Check("the burst flag is carried on a piece but never hashed",
              BoardHash.Pieces(viewA, 0) == BoardHash.Pieces(burstFlagged, 0));

        Check("burst round-trips through the snapshot JSON",
              SnapshotJson.TryParse(AsProbeJson(viewA).Replace("\"owner\":0", "\"owner\":0,\"burst\":true"),
                                    out var burstSnap, out _) &&
              burstSnap.Pieces.Any(p => p.Burst));
        Check("a snapshot with no burst field parses with burst false",
              SnapshotJson.TryParse(AsProbeJson(viewA), out var noBurst, out _) &&
              noBurst.Pieces.All(p => !p.Burst));

        var withAct = AsProbeJson(viewA)[..^1] +
                      ",\"act\":{\"unit\":3,\"fx\":5,\"fy\":3,\"tx\":6,\"ty\":3,\"on\":false}}";
        Check("the controller action round-trips through the snapshot JSON",
              SnapshotJson.TryParse(withAct, out var actSnap, out _) &&
              actSnap.Act is { Unit: 3, Fx: 5, Fy: 3, Tx: 6, Ty: 3, On: false });

        Check("a snapshot with no act field parses with none, so old captures still replay",
              SnapshotJson.TryParse(AsProbeJson(viewA), out var noAct, out _) && noAct.Act is null);

        Check("an act naming a square off the board is dropped, not refused",
              SnapshotJson.TryParse(
                  AsProbeJson(viewA)[..^1] + ",\"act\":{\"unit\":3,\"fx\":99,\"fy\":3,\"tx\":6,\"ty\":3,\"on\":false}}",
                  out var badAct, out _) && badAct.Act is null);

        Check("the controller action is carried on a snapshot but never hashed",
              BoardHash.Pieces(viewA, 0) ==
              BoardHash.Pieces(viewA with { Act = new ActRecord(3, 5, 3, 6, 3, false) }, 0));

        const string OneRecord = "{\"seq\":37,\"kind\":\"move\",\"whose\":\"human\",\"unit\":7,\"ptr\":\"BEEF00\"," +
                                 "\"fx\":4,\"fy\":1,\"tx\":3,\"ty\":1,\"sx\":4,\"sy\":1}";
        Check("a commit record round-trips through the snapshot JSON",
              SnapshotJson.TryParse(
                  AsProbeJson(viewA)[..^1] + ",\"commits\":{\"count\":41,\"lost\":0,\"records\":[" + OneRecord + "]}}",
                  out var commitSnap, out _) &&
              commitSnap.Commits is { Count: 41, Lost: 0 } batchIn && batchIn.Records.Count == 1 &&
              batchIn.Records[0] is { Seq: 37, Kind: "move", Whose: "human", Unit: 7, Ptr: "BEEF00",
                                 Fx: 4, Fy: 1, Tx: 3, Ty: 1 } &&
              batchIn.Records[0].IsHuman);

        Check("a snapshot with no commits field parses with none, so old captures still replay",
              SnapshotJson.TryParse(AsProbeJson(viewA), out var noCommits, out _) && noCommits.Commits is null);

        Check("a commit record naming a square off the board is kept as off-board, and the loss count survives",
              SnapshotJson.TryParse(
                  AsProbeJson(viewA)[..^1] + ",\"commits\":{\"count\":41,\"lost\":2,\"records\":[" +
                  OneRecord.Replace("\"fx\":4", "\"fx\":99") + "]}}",
                  out var badRec, out _) &&
              badRec.Commits is { Lost: 2 } bad && bad.Records is [{ Kind: CommitTranscript.OffBoardKind, Seq: 37 }]);

        Check("a turn holding an off-board record is handed to the board reader, never read an action short",
              SnapshotJson.TryParse(
                  AsProbeJson(viewA)[..^1] + ",\"commits\":{\"count\":41,\"lost\":0,\"records\":[" +
                  "{\"seq\":36,\"kind\":\"activate\",\"whose\":\"human\",\"unit\":7,\"ptr\":\"BEEF00\",\"sx\":4,\"sy\":1}," +
                  OneRecord.Replace("\"fx\":4", "\"fx\":99") + "]}}",
                  out var shortTurn, out _) &&
              CommitTranscript.Read([shortTurn]) is { Refusal: null, NotCovered: not null, Actions: [] });

        const string JunkActivate = "{\"seq\":36,\"kind\":\"activate\",\"whose\":\"human\",\"unit\":7," +
                                    "\"ptr\":\"BEEF00\",\"fx\":-1906428640,\"fy\":772,\"tx\":498800896," +
                                    "\"ty\":773,\"sx\":4,\"sy\":1}";
        var junkJson = AsProbeJson(viewA)[..^1] + ",\"commits\":{\"count\":41,\"lost\":0,\"records\":[" +
                       JunkActivate + "," + OneRecord + "]}}";
        var junkParsed = SnapshotJson.TryParse(junkJson, out var junkSnap, out _);

        Check("D-201: an activate whose from and to are the controller's uninitialised memory survives the parse",
              junkParsed && junkSnap.Commits is { } junkBatch && junkBatch.Records.Count == 2 &&
              junkBatch.Records[0] is { Kind: "activate", Ptr: "BEEF00", Sx: 4, Sy: 1 });

        Check("D-201: that activate's from and to are blanked, never passed on as squares",
              junkParsed && junkSnap.Commits is { } blankBatch &&
              blankBatch.Records[0] is { Fx: -1, Fy: -1, Tx: -1, Ty: -1 });

        Check("D-201: the turn then reads from the game's own records instead of falling back to the board",
              junkParsed && CommitTranscript.Read([junkSnap]) is { Refusal: null, NotCovered: null } junkRead &&
              junkRead.Actions is [{ Kind: TranscriptKind.Move, FromX: 4, FromY: 1, ToX: 3, ToY: 1 }]);

        Check("D-201 as amended: an activate whose selected tile is off the board is kept as off-board, not read",
              SnapshotJson.TryParse(
                  AsProbeJson(viewA)[..^1] + ",\"commits\":{\"count\":41,\"lost\":0,\"records\":[" +
                  JunkActivate.Replace("\"sx\":4", "\"sx\":99") + "]}}",
                  out var offSelected, out _) &&
              offSelected.Commits is { } offBatch && offBatch.Records is [{ Kind: CommitTranscript.OffBoardKind }]);

        Check("D-201: a record of an unknown kind is kept, so the turn refuses instead of reading short",
              SnapshotJson.TryParse(
                  AsProbeJson(viewA)[..^1] + ",\"commits\":{\"count\":41,\"lost\":0,\"records\":[" +
                  JunkActivate.Replace("\"kind\":\"activate\"", "\"kind\":\"scuttle\"") + "]}}",
                  out var oddKind, out _) &&
              oddKind.Commits is { } oddBatch && oddBatch.Records.Count == 1 &&
              CommitTranscript.Read([oddKind]) is { Refusal: not null });

        Check("the commit records are carried on a snapshot but never hashed",
              BoardHash.Pieces(viewA, 0) ==
              BoardHash.Pieces(viewA with { Commits = new CommitBatch(4, 0, []) }, 0));

        var actedFlagged = viewA with { Pieces = viewA.Pieces.Select(p => p with { Acted = true }).ToList() };
        Check("the has-acted flag is carried on a piece but never hashed",
              BoardHash.Pieces(viewA, 0) == BoardHash.Pieces(actedFlagged, 0));

        Check("acted round-trips through the snapshot JSON",
              SnapshotJson.TryParse(AsProbeJson(viewA).Replace("\"owner\":0", "\"owner\":0,\"acted\":true"),
                                    out var actedSnap, out _) &&
              actedSnap.Pieces.Any(p => p.Acted));

        Check("a snapshot with no acted field parses with acted false",
              SnapshotJson.TryParse(AsProbeJson(viewA), out var noActed, out _) &&
              noActed.Pieces.All(p => !p.Acted));

        Check("the has-acted flag survives rotation into the other seat's frame",
              BoardHash.Canonicalise(actedFlagged, 1).Pieces.All(p => p.Acted));

        var midMatch = viewA with { Match = new MatchState(false, -1, 0, 0, 2) };
        var justEnded = viewA with { Match = new MatchState(true, 0, 2, 0, 2) };

        var seatZero = viewA with { AiSeat = 1 };
        Check("D-245: a quit from the pause menu reads as a forfeit (lost, no side at the points, both sides on the board)",
              seatZero.Pieces.Any(p => p.Owner == 0) && seatZero.Pieces.Any(p => p.Owner == 1)
              && TurnBoundary.LocalForfeit(seatZero with { Match = new MatchState(true, 1, 0, 0, 1) }, 0));

        Check("D-245: a real ending is not a forfeit: a win, the winner at the points, a side wiped out, a match still on",
              !TurnBoundary.LocalForfeit(seatZero with { Match = new MatchState(true, 0, 0, 0, 1) }, 0)
              && !TurnBoundary.LocalForfeit(seatZero with { Match = new MatchState(true, 1, 0, 1, 1) }, 0)
              && !TurnBoundary.LocalForfeit(seatZero with { Match = new MatchState(true, 1, 0, -1, 1) }, 0)
              && !TurnBoundary.LocalForfeit(seatZero with
              {
                  Pieces = seatZero.Pieces.Where(p => p.Owner == 1).ToList(),
                  Match = new MatchState(true, 1, 0, 0, 1),
              }, 0)
              && !TurnBoundary.LocalForfeit(seatZero with { Match = new MatchState(false, -1, 0, 0, 1) }, 0)
              && !TurnBoundary.LocalForfeit(seatZero with { Match = null }, 0));

        Check("a side whose only listed machines are dead is wiped out, not forfeiting",
              !TurnBoundary.LocalForfeit(seatZero with
              {
                  Pieces = seatZero.Pieces.Select(p => p.Owner == 0 ? p with { Health = 0 } : p).ToList(),
                  Match = new MatchState(true, 1, 0, 0, 1),
              }, 0)
              && !TurnBoundary.LocalForfeit(seatZero with
              {
                  Pieces = seatZero.Pieces.Select(p => p.Owner == 1 ? p with { Health = 0 } : p).ToList(),
                  Match = new MatchState(true, 1, 0, 0, 1),
              }, 0));

        Check("a match ending with no turn boundary is recognised",
              TurnBoundary.EndedWithoutABoundary(midMatch, justEnded, 0));

        Check("a sample after the match already ended is not the edge",
              TurnBoundary.EndedWithoutABoundary(justEnded, justEnded, 0) is false);

        Check("a match still running is not the edge",
              TurnBoundary.EndedWithoutABoundary(midMatch, midMatch, 0) is false);

        var overcharger = new Piece(3, 3, 2, 1, 0, Burst: true, Idx: 0);
        var survivor = new Piece(5, 4, 1, 0, 0, Idx: 1);
        var closingTerrain = new sbyte[64];
        closingTerrain[16] = 3;
        closingTerrain[24] = -2;
        closingTerrain[45] = 2;

        var closingEdge = new BoardSnapshot(8, 8, [overcharger, survivor], closingTerrain)
        {
            AiSeat = 1, Match = new MatchState(true, 0, 2, 0, 2),
        };

        var closingSettled = closingEdge with
        {
            Pieces = [overcharger with { Health = 0 }, survivor],
        };

        var peerClosing = RotateByHand(closingSettled) with { AiSeat = 0 };
        peerClosing = peerClosing with
        {
            Pieces = [.. peerClosing.Pieces.Where(p => p.Health > 0)],
        };

        var peerPieces = BoardHash.Pieces(peerClosing, 1);
        var peerTerrain = BoardHash.Terrain(peerClosing, 1);

        Check("the closing terrain agrees even while the pieces do not",
              BoardHash.Terrain(closingEdge, 0) == peerTerrain);

        Check("the closing board taken at the match-over edge does NOT match the peer",
              BoardHash.Pieces(closingEdge, 0) != peerPieces);

        Check("the closing board one sample later DOES match the peer",
              BoardHash.Pieces(closingSettled, 0) == peerPieces);

        List<BoardSnapshot> closingCandidates = [closingEdge, closingSettled];
        Check("the settled board is among the closing candidates the peer can match",
              closingCandidates.Any(b => BoardHash.Pieces(b, 0) == peerPieces &&
                                         BoardHash.Terrain(b, 0) == peerTerrain));

        Check("holding only the edge board leaves the peer nothing to match",
              closingCandidates.Take(1).Any(b => BoardHash.Pieces(b, 0) == peerPieces) is false);

        Check("the closing tail admits at least one settling sample", ClosingTail > 0);

        var closingFrame = new Frame
        {
            Kind = MsgKind.Hash, Turn = 2, PieceHash = peerPieces, TerrainHash = peerTerrain,
        };
        var (closingHost, _) = await Pair(port, cts.Token);

        Check("a closing hash that matches no candidate is held, and the peer is not halted",
              HoldClosingHash(closingHost, closingFrame, closingCandidates.Take(1).ToList()) &&
              !closingHost.Halted);

        Check("the settled board arriving later matches the held hash",
              closingHost.HashMatches(closingFrame, closingCandidates));

        Check("a closing hash that matches at once is not held",
              HoldClosingHash(closingHost, closingFrame, closingCandidates) is false);

        Check("the settle deadline is a real wait, longer than the six seconds the live settle exceeded",
              ClosingSettleSeconds > 6);

        var settling = new List<BoardSnapshot>(closingCandidates.Take(1));
        HoldClosingHash(closingHost, closingFrame, settling);
        Check("a read that has not settled leaves the closing hash held",
              ClosingSettleStep(closingHost, closingEdge, settling) is false && settling.Count == 1);
        Check("the settled read joins the candidates and matches the held hash",
              ClosingSettleStep(closingHost, closingSettled, settling) && settling.Count == 2 && !closingHost.Halted);
        Check("once matched, further reads are no longer held against",
              ClosingSettleStep(closingHost, closingEdge, settling));

        Check("a settle wait can be claimed once until it is released",
              ClaimSettle() && ClaimSettle() is false);
        ReleaseSettle();
        Check("a released settle wait can be claimed again", ClaimSettle());
        ReleaseSettle();

        HoldClosingHash(closingHost, closingFrame, closingCandidates);
        var clearedWantsNone = !SettleWanted();
        ClosingSettleExpired(closingHost, closingCandidates.Take(1).ToList());
        Check("the deadline with no hash held halts nothing", !closingHost.Halted);
        HoldClosingHash(closingHost, closingFrame, closingCandidates.Take(1).ToList());
        Check("a hash held with no wait asks for one, and a cleared one does not",
              SettleWanted() && clearedWantsNone);
        ClosingSettleExpired(closingHost, closingCandidates.Take(1).ToList());
        Check("the deadline turns a hash still held into the ordinary desync halt",
              closingHost.Halted && closingHost.HaltReason?.Contains("pieces differ") == true);

        Check("an emptied board after the match is treated as teardown, not a settling step",
              IsTeardownSample(closingEdge with { Pieces = [] }, 1));

        Check("a board that still has pieces is not teardown",
              IsTeardownSample(closingSettled, 1) is false);

        Check("an empty board with nothing held yet is not teardown",
              IsTeardownSample(closingEdge with { Pieces = [] }, 0) is false);

        var markedBefore = viewA with
        {
            Match = new MatchState(false, -1, 0, 0, 2),
            Pieces = viewA.Pieces.Select((p, i) => i == 0 ? p with { Acts = 1 } : p).ToList(),
        };
        Check("a closing turn that did clear the marks is left to the normal boundary path",
              TurnBoundary.AnyTurnEnded(markedBefore, justEnded) is false
              || TurnBoundary.EndedWithoutABoundary(markedBefore, justEnded, 0) is false);

        var hashFrame = new Frame
        {
            Kind = MsgKind.Hash, Turn = 4,
            PieceHash = BoardHash.Pieces(viewA, 0),
            TerrainHash = BoardHash.Terrain(viewA, 0),
        };

        var (host2, guest2) = await Pair(port, cts.Token);
        var moved = viewB with
        {
            Pieces = viewB.Pieces.Select((p, i) => i == 0 ? p with { X = p.X + 1 } : p).ToList(),
        };
        Check("a moved piece is caught as a desync", !guest2.CheckHash(hashFrame, moved, 4));
        Check("the halt reason names which half diverged", guest2.HaltReason?.Contains("pieces differ") == true);

        var terrainDiff = viewA with { Terrain = viewA.Terrain.Select((t, i) => i == 9 ? (sbyte)3 : t).ToList() };
        Check("a changed terrain byte is caught as a desync", !host2.CheckHash(hashFrame, terrainDiff, 4));
        Check("terrain-only divergence is reported as terrain",
              host2.HaltReason?.Contains("terrain differ") == true);

        Check("a live-probe snapshot parses and hashes identically",
              SnapshotJson.TryParse(AsProbeJson(viewA), out var parsed, out _) &&
              BoardHash.Pieces(parsed, 0) == BoardHash.Pieces(viewA, 0) &&
              BoardHash.Terrain(parsed, 0) == BoardHash.Terrain(viewA, 0));

        Check("a truncated terrain grid is rejected, not padded",
              !SnapshotJson.TryParse("{\"width\":8,\"height\":8,\"pieces\":[],\"terrain\":[0,0,0]}", out _, out var e1) &&
              e1.Contains("expected 64"));

        Check("a piece off the board is rejected",
              !SnapshotJson.TryParse(AsProbeJson(viewA).Replace("\"x\":1,", "\"x\":9,"), out _, out var e2) &&
              e2.Contains("off a"));

        Check("a piece owned by neither player is rejected",
              !SnapshotJson.TryParse(AsProbeJson(viewA).Replace("\"owner\":0", "\"owner\":7"), out _, out var e3) &&
              e3.Contains("owner 7"));

        Check("garbage is rejected with a reason rather than an empty board",
              !SnapshotJson.TryParse("not json at all", out _, out var e4) && e4.Length > 0);

        var b0 = SampleBoard() with { AiSeat = 1 };
        Check("the local seat comes from the snapshot, not an assumption", b0.LocalOwner == 0);

        var oneMoved = b0 with { Pieces = b0.Pieces.Select(p => p == b0.Pieces[1] ? p with { Y = p.Y - 1 } : p).ToList() };
        var r1 = MoveDetector.Detect(b0, oneMoved, 0);
        Check("a single move is read off the board",
              r1.Ok && r1.Moves is [{ SrcX: 6, SrcY: 1, DstX: 6, DstY: 0, Attack: false }]);

        var turned = b0 with { Pieces = b0.Pieces.Select(p => p == b0.Pieces[0] ? p with { Facing = 1 } : p).ToList() };
        var r2 = MoveDetector.Detect(b0, turned, 0);
        Check("a rotation on the spot is read as a move to its own square",
              r2.Ok && r2.Moves is [{ SrcX: 1, SrcY: 0, DstX: 1, DstY: 0, Facing: 1 }]);

        var withEnemy = b0 with { Pieces = [.. b0.Pieces, new Piece(6, 2, 4, 0, 1)] };
        var struck = withEnemy with
        {
            Pieces = withEnemy.Pieces.Select(p => p is { X: 6, Y: 2 } ? p with { Health = 2 } : p).ToList(),
        };
        var r3 = MoveDetector.Detect(withEnemy, struck, 0);
        Check("an attack is read from the victim's health and the attacker's facing",
              r3.Ok && r3.Moves is [{ Attack: true, SrcX: 6, SrcY: 1, DstX: 6, DstY: 1, TargetX: 6, TargetY: 2 }]);

        var twoMoved = b0 with
        {
            Pieces = b0.Pieces.Select(p => p.Owner == 0 ? p with { Y = p.Y + 1 } : p).ToList(),
        };
        var r4 = MoveDetector.Detect(b0, twoMoved, 0);
        Check("a two-activation turn is read as two actions, not refused",
              r4.Ok && r4.Moves.Count == 2);
        Check("each action is paired to its own piece by array index",
              r4.Ok && r4.Moves.Any(m => m is { SrcX: 1, SrcY: 0, DstX: 1, DstY: 1 })
                    && r4.Moves.Any(m => m is { SrcX: 6, SrcY: 1, DstX: 6, DstY: 2 }));

        var noIdx = b0 with { Pieces = b0.Pieces.Select(p => p with { Idx = -1 }).ToList() };
        var noIdxAfter = twoMoved with { Pieces = twoMoved.Pieces.Select(p => p with { Idx = -1 }).ToList() };
        var r4b = MoveDetector.Detect(noIdx, noIdxAfter, 0);
        Check("two movers with no piece indices are refused rather than guessed",
              !r4b.Ok && r4b.Refusal!.Contains("indices"));

        var chained = b0 with
        {
            Pieces = b0.Pieces.Select(p => p.Idx switch
            {
                0 => p with { X = 2, Y = 0 },
                1 => p with { X = 1, Y = 0 },
                _ => p,
            }).ToList(),
        };
        var r4c = MoveDetector.Detect(b0, chained, 0);
        Check("an action whose destination another piece vacated is ordered after it",
              r4c.Ok && r4c.Moves.Count == 2 &&
              r4c.Moves[0] is { SrcX: 1, SrcY: 0 } && r4c.Moves[1] is { DstX: 1, DstY: 0 });

        var twoTurn = MoveDetector.Detect(b0, twoMoved, 0).Moves;
        var oneFrame = Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Move, Moves = twoTurn }));
        Check("a whole turn survives the wire as one frame",
              oneFrame?.Moves?.Count == 2 && oneFrame.Moves[0].SrcX == twoTurn[0].SrcX);

        Check("a multi-action turn becomes ONE --script-turn call, not one --script-move each",
              new Injector("live-probe.exe", armed: false) is var inj &&
              inj.Apply(twoTurn).Result &&
              inj.LastCommand!.Contains("--script-turn") &&
              !inj.LastCommand.Contains("--script-move"));

        Check("a single-action turn still uses --script-move",
              new Injector("live-probe.exe", armed: false) is var inj1 &&
              inj1.Apply(r1.Moves).Result &&
              inj1.LastCommand!.Contains("--script-move"));

        Check("the final flag survives the wire on a Move frame",
              Protocol.Decode(Protocol.Encode(
                  new Frame { Kind = MsgKind.Move, Moves = twoTurn, Final = true })) is { Final: true });

        Check("an ordinary turn carries no final flag",
              Protocol.Decode(Protocol.Encode(
                  new Frame { Kind = MsgKind.Move, Moves = twoTurn })) is { Final: false });

        Check("a match-ending turn becomes --script-turn --final",
              new Injector("live-probe.exe", armed: false) is var injF &&
              injF.Apply(twoTurn, final: true).Result &&
              injF.LastCommand!.Contains("--script-turn") &&
              injF.LastCommand.Contains("--final"));

        Check("a one-action match-ending turn also carries --final, on the --script-move path",
              new Injector("live-probe.exe", armed: false) is var injF1 &&
              injF1.Apply(r1.Moves, final: true).Result &&
              injF1.LastCommand!.Contains("--script-move") &&
              injF1.LastCommand.Contains("--final"));

        Check("an ordinary turn never carries --final",
              new Injector("live-probe.exe", armed: false) is var injN &&
              injN.Apply(twoTurn, final: false).Result &&
              !injN.LastCommand!.Contains("--final"));

        Check("an attack in a multi-action turn sends the VICTIM's tile, not the destination",
              new Injector("live-probe.exe", armed: false) is var inj2 &&
              inj2.Apply([r3.Moves[0], r1.Moves[0]]).Result &&
              inj2.LastCommand!.Contains("attack,6,1,6,2,"));

        Check("an attack spec carries the standing square as the from pair",
              inj2.LastCommand!.Contains("attack,6,1,6,2,2,6,1"));

        Check("a single-action attack carries the standing square as --from",
              new Injector("live-probe.exe", armed: false) is var injFrom &&
              injFrom.Apply([r3.Moves[0]]).Result &&
              injFrom.LastCommand!.Contains("--attack") &&
              injFrom.LastCommand.Contains("--from 6 1"));

        var ramBefore = b0 with { Pieces = [.. b0.Pieces, new Piece(6, 3, 5, 2, 1, false, 4)] };
        var ramAfter = ramBefore with
        {
            Pieces = ramBefore.Pieces.Select(p => p.Idx switch
            {
                1 => p with { X = 6, Y = 3, Facing = 1 },
                4 => p with { X = 7, Y = 3, Health = 3 },
                _ => p,
            }).ToList(),
        };
        var rr = MoveDetector.Detect(ramBefore, ramAfter, 0);
        Check("an attack that shoves the target is still attributed to its attacker",
              rr.Ok && rr.Moves.Any(m => m.Attack && m is { DstX: 6, DstY: 3, TargetX: 6, TargetY: 3 }));

        var awayBefore = b0 with
        {
            Pieces = [new Piece(2, 3, 3, 0, 0, false, 0), new Piece(1, 3, 5, 2, 1, false, 1),
                      new Piece(5, 5, 2, 0, 0, false, 2)],
        };
        var awayAfter = awayBefore with
        {
            Pieces = [new Piece(3, 5, 3, 3, 0, false, 0), new Piece(1, 3, 3, 2, 1, false, 1),
                      new Piece(4, 5, 2, 0, 0, false, 2)],
        };
        var ra = MoveDetector.Detect(awayBefore, awayAfter, 0);
        Check("an attacker that strikes and then walks away is still attributed",
              ra.Ok && ra.Moves.Any(m => m.Attack && m is { SrcX: 2, SrcY: 3, TargetX: 1, TargetY: 3 }));

        Check("the strike direction is derived from where it stood, not its final facing",
              ra.Ok && ra.Moves.First(m => m.Attack).Facing == 3);

        Check("the attack is ordered before the move it preceded, and the move is kept",
              ra.Ok && ra.Moves.FindIndex(m => m.Attack) <
                       ra.Moves.FindIndex(m => !m.Attack && m is { SrcX: 2, SrcY: 3, DstX: 3, DstY: 5 }));

        Check("the whole turn is three actions, strike, leave, and the second activation",
              ra.Ok && ra.Moves.Count == 3 && ra.Moves.Any(m => m is { SrcX: 5, SrcY: 5, DstX: 4, DstY: 5 }));

        var idleNeighbour = awayBefore with
        {
            Pieces = [new Piece(2, 3, 3, 0, 0, false, 0), new Piece(1, 3, 5, 2, 1, false, 1),
                      new Piece(5, 5, 2, 0, 0, false, 2)],
        };
        var idleAfter = idleNeighbour with
        {
            Pieces = [new Piece(2, 3, 3, 0, 0, false, 0), new Piece(1, 3, 3, 2, 1, false, 1),
                      new Piece(4, 5, 2, 0, 0, false, 2)],
        };
        var ri = MoveDetector.Detect(idleNeighbour, idleAfter, 0);
        Check("damage next to a piece that never acted is refused, not blamed on it",
              !ri.Ok && ri.Refusal!.Contains("started in line with it"));

        var r5 = MoveDetector.Detect(b0, b0, 0);
        Check("an unchanged board is refused rather than sending an empty move",
              !r5.Ok && r5.Refusal!.Contains("nothing changed"));

        var r6 = MoveDetector.Detect(b0, oneMoved, -1);
        Check("an unknown local seat is refused", !r6.Ok && r6.Refusal!.Contains("which seat"));

        var bursted = b0 with
        {
            Pieces = b0.Pieces.Select(p => p == b0.Pieces[1]
                ? p with { Y = p.Y - 1, Health = (byte)(p.Health - 2), Burst = true } : p).ToList(),
        };
        var rb1 = MoveDetector.Detect(b0, bursted, 0);
        Check("a turn using burst is sent with the burst carried, not refused",
              rb1.Ok && rb1.Moves is [{ Burst: true }]);

        Check("the burst is attached to the action that starts where the machine stood",
              rb1.Ok && rb1.Moves[0] is { SrcX: 6, SrcY: 1, DstX: 6, DstY: 0 });

        Check("a bursting machine that also moved warns that the net displacement may be two actions",
              rb1.Warning is not null && rb1.Warning.Contains("(6,1)"));

        var burstAttack = withEnemy with
        {
            Pieces = withEnemy.Pieces.Select(p =>
                p is { X: 6, Y: 2 } ? p with { Health = 2 } :
                p is { X: 6, Y: 1 } ? p with { Health = (byte)(p.Health - 2), Burst = true } : p).ToList(),
        };
        var rb4 = MoveDetector.Detect(withEnemy, burstAttack, 0);
        Check("a burst on an attack from the spot is carried with no warning",
              rb4.Ok && rb4.Moves is [{ Attack: true, Burst: true }] && rb4.Warning is null);

        Check("a burst attack spec keeps the burst as the field after the from pair",
              new Injector("live-probe.exe", armed: false) is var injBurstFrom &&
              injBurstFrom.Apply([rb4.Moves[0], r1.Moves[0]]).Result &&
              injBurstFrom.LastCommand!.Contains("attack,6,1,6,2,2,6,1,burst"));

        var burstNoAction = b0 with
        {
            Pieces = b0.Pieces.Select(p => p.Idx switch
            {
                0 => p with { Health = (byte)(p.Health - 2), Burst = true },
                1 => p with { Y = p.Y - 1 },
                _ => p,
            }).ToList(),
        };
        var rb5 = MoveDetector.Detect(b0, burstNoAction, 0);
        Check("a burst with no action to attach it to is still refused",
              !rb5.Ok && rb5.Refusal!.Contains("(1,0)"));

        Check("burst survives the 180 rotation into the receiver's frame", rb1.Moves[0].Rotated().Burst);
        Check("burst survives the wire",
              Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Move, Moves = rb1.Moves }))
                  ?.Moves?[0].Burst == true);

        Check("a single bursting action arms --script-move --burst",
              new Injector("live-probe.exe", armed: false) is var inj3 &&
              inj3.Apply(rb1.Moves).Result &&
              inj3.LastCommand!.Contains("--script-move") && inj3.LastCommand.Contains("--burst"));

        Check("in a multi-action turn only the bursting action carries the 7th field",
              new Injector("live-probe.exe", armed: false) is var inj4 &&
              inj4.Apply([twoTurn[0], rb1.Moves[0]]).Result &&
              inj4.LastCommand!.Contains("move,6,1,6,0,2,burst") &&
              inj4.LastCommand.Split(",burst").Length == 2);

        var retaliated = withEnemy with
        {
            Pieces = withEnemy.Pieces
                .Select(p => p is { X: 6, Y: 2 } ? p with { Health = 2 } : p)
                .Select(p => p is { X: 6, Y: 1 } ? p with { Health = (byte)(p.Health - 1) } : p).ToList(),
        };
        var rb2 = MoveDetector.Detect(withEnemy, retaliated, 0);
        Check("losing health WITHOUT the burst flag sends no burst, that is retaliation, and it replays",
              rb2.Ok && rb2.Moves is [{ Attack: true, TargetX: 6, TargetY: 2, Burst: false }]);

        var stillBursted = bursted with
        {
            Pieces = bursted.Pieces.Select(p => p == bursted.Pieces[0] ? p with { Facing = 1 } : p).ToList(),
        };
        var rb3 = MoveDetector.Detect(bursted, stillBursted, 0);
        Check("a burst flag already set before the turn is not re-sent as a new burst",
              rb3.Ok && rb3.Moves.All(m => !m.Burst));

        var mystery = withEnemy with
        {
            Pieces = withEnemy.Pieces.Select(p => p is { X: 5, Y: 6 } ? p with { Health = 1 } : p).ToList(),
        };
        var r7 = MoveDetector.Detect(withEnemy, mystery, 0);
        Check("unattributable damage is refused", !r7.Ok && r7.Refusal!.Contains("strike line"));

        var rsBefore = Frame(Pc(0, 1, 3, 1, 0, 0, uuid: "ROLLER"), Pc(1, 0, 3, 5, 0, 0, uuid: "BRISTLE"),
                             Pc(2, 2, 1, 4, 2, 1, uuid: "BEHEMOTH"), Pc(3, 4, 1, 5, 2, 1, uuid: "SPIKE"));
        var rsSpray = Frame(Pc(0, 1, 3, 0, 0, 0, uuid: "ROLLER"), Pc(1, 0, 3, 5, 0, 0, uuid: "BRISTLE"),
                            Pc(2, 2, 1, 3, 2, 1, uuid: "BEHEMOTH"), Pc(3, 4, 1, 5, 2, 1, uuid: "SPIKE"));
        var rsOver = Frame(Pc(0, 0, 3, 5, 0, 0, uuid: "BRISTLE"), Pc(1, 2, 1, 3, 2, 1, uuid: "BEHEMOTH"),
                           Pc(2, 4, 1, 5, 2, 1, uuid: "SPIKE"))
                     with { Match = new MatchState(true, 1, 0, 1, 1) };
        Check("a tail holding only a turn-start kill and the compaction after it has nothing of ours",
              TurnBoundary.NothingOfOursIn([rsBefore, rsSpray, rsOver], 0)
              && TurnBoundary.NothingOfOursIn([rsBefore, rsSpray, rsOver], 1)
              && TurnBoundary.NothingOfOursIn([], 0));

        var rsActs = rsBefore with { Act = new ActRecord(1, 0, 3, 0, 3, true) };
        var rsTheirs = rsBefore with { Act = new ActRecord(2, 2, 1, 2, 2, true) };
        var rsMarked = Frame(Pc(0, 1, 3, 1, 0, 0, uuid: "ROLLER"), Pc(1, 0, 3, 5, 0, 0, acts: 1, uuid: "BRISTLE"),
                             Pc(2, 2, 1, 4, 2, 1, uuid: "BEHEMOTH"), Pc(3, 4, 1, 5, 2, 1, uuid: "SPIKE"));
        var rsMoved = Frame(Pc(0, 1, 3, 1, 0, 0, uuid: "ROLLER"), Pc(1, 0, 2, 5, 0, 0, uuid: "BRISTLE"),
                            Pc(2, 2, 1, 4, 2, 1, uuid: "BEHEMOTH"), Pc(3, 4, 1, 5, 2, 1, uuid: "SPIKE"));
        var rsTurned = Frame(Pc(0, 1, 3, 1, 0, 0, uuid: "ROLLER"), Pc(1, 0, 3, 5, 1, 0, uuid: "BRISTLE"),
                             Pc(2, 2, 1, 4, 2, 1, uuid: "BEHEMOTH"), Pc(3, 4, 1, 5, 2, 1, uuid: "SPIKE"));
        var rsBurst = Frame(Pc(0, 1, 3, 1, 0, 0, uuid: "ROLLER"), Pc(1, 0, 3, 5, 0, 0, burst: true, uuid: "BRISTLE"),
                            Pc(2, 2, 1, 4, 2, 1, uuid: "BEHEMOTH"), Pc(3, 4, 1, 5, 2, 1, uuid: "SPIKE"));
        Check("an activation, a mark or a burst of ours makes the tail a turn of ours",
              !TurnBoundary.NothingOfOursIn([rsBefore, rsActs], 0)
              && !TurnBoundary.NothingOfOursIn([rsBefore, rsMarked], 0)
              && !TurnBoundary.NothingOfOursIn([rsBefore, rsBurst], 0)
              && TurnBoundary.NothingOfOursIn([rsBefore, rsTheirs], 0));

        Check("an unmarked move or turn of a piece of ours is the game's or the opponent's doing, nothing of ours",
              TurnBoundary.NothingOfOursIn([rsBefore, rsMoved], 0)
              && TurnBoundary.NothingOfOursIn([rsBefore, rsTurned], 0));

        Check("a halt the other PC raised is said as theirs, HALT first",
              Peer.RelayedHaltReason("desync after turn 4") == "the other PC stopped the match: desync after turn 4");
        Check("the match summary names the winner from this seat and counts the turns each way",
              MatchSummary(new MatchState(true, 1, 0, 1, 1), 0, 2, 2)
                  == "match over: the other player won, 0 point(s) to 1; 2 turn(s) sent, 2 applied"
              && MatchSummary(new MatchState(true, 0, 3, 1, 7), 0, 5, 4)
                  == "match over: you won, 3 point(s) to 1; 5 turn(s) sent, 4 applied"
              && MatchSummary(null, 1, 1, 1)
                  == "match over: the other player has no machines left; 1 turn(s) sent, 1 applied");

        Check("the match summary says so when the score could not be read, rather than printing -1",
              MatchSummary(new MatchState(true, 0, -1, 1, 7), 0, 5, 4)
                  == "match over: you won, and the score could not be read; 5 turn(s) sent, 4 applied"
              && MatchSummary(new MatchState(true, 1, 2, -1, 7), 0, 2, 2)
                  == "match over: the other player won, and the score could not be read; 2 turn(s) sent, 2 applied");
        var overScreen = Frame() with { Match = new MatchState(true, 1, 0, 1, 1) };
        var retried = Frame() with { Match = new MatchState(false, 0, 0, 0, 1) };
        var placingAgain = Frame() with { Placing = new PlacingState(2, 2, 0) };
        Check("a live match after the shared one ended is unshared, the victory screen is not",
              !NewMatchAfterOver(overScreen) && NewMatchAfterOver(retried) && NewMatchAfterOver(placingAgain)
              && !NewMatchAfterOver(Frame()));

        var home = @"C:\Games\Strikers\";
        var ownExe = home + "netplay.exe";
        var noon = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var before = noon.AddSeconds(-5);
        Check("a launcher-only netplay runs under the Strikers beside it and nothing else, with only --version and --selftest open (D-241)",
              LauncherOnly.Allows(["--play"], ownExe, noon, home + "Strikers.exe", before, LauncherOnly.Parents)
              && LauncherOnly.Allows(["--play"], ownExe, noon, @"c:\games\strikers\STRIKERS.EXE", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--play"], ownExe, noon, @"C:\Windows\System32\cmd.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--play"], ownExe, noon, @"C:\Other\Strikers.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--play"], ownExe, noon, home + "live-probe.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--play"], ownExe, noon, home + "other-tool.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--play"], ownExe, noon, home + "Strikers.exe", noon.AddSeconds(5), LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--play"], ownExe, noon, null, null, LauncherOnly.Parents)
              && LauncherOnly.Allows(["--version"], ownExe, noon, null, null, LauncherOnly.Parents)
              && LauncherOnly.Allows(["--selftest"], ownExe, noon, null, null, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--version", "--launcher-check"], ownExe, noon, null, null, LauncherOnly.Parents)
              && !LauncherOnly.Message().Contains(';'));

        var versionRelay = new Relay();
        var toldAt = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        Check("a relay refusing another protocol says so in the launcher's line at most once per 30 s, and again after",
              Relay.OtherVersionLine(24) == "relay refused a player on another version of Strikers (protocol v24)"
              && versionRelay.TellOtherVersion(toldAt) && !versionRelay.TellOtherVersion(toldAt.AddSeconds(10))
              && versionRelay.TellOtherVersion(toldAt.AddSeconds(31)));

        lock (_placementLock)
        {
            _writtenSlots.Add(7);
            _writtenSquares.Add((3, 6));
        }

        using (var lockHeld = new ManualResetEventSlim())
        using (var letGo = new ManualResetEventSlim())
        {
            var holder = Task.Run(() =>
            {
                lock (_placementLock)
                {
                    lockHeld.Set();
                    letGo.Wait(TimeSpan.FromSeconds(5));
                }
            });
            lockHeld.Wait(TimeSpan.FromSeconds(5));
            var resetting = Task.Run(ResetPlacementBookkeeping);
            var gating = Task.Run(() => GatePeerFrame(new Frame { Kind = MsgKind.Place, PlaceIdx = 7 }));
            var waitedForLock = !resetting.Wait(TimeSpan.FromMilliseconds(300)) && !gating.Wait(TimeSpan.FromMilliseconds(50));
            letGo.Set();
            var finished = holder.Wait(TimeSpan.FromSeconds(5)) && resetting.Wait(TimeSpan.FromSeconds(5))
                           && gating.Wait(TimeSpan.FromSeconds(5));
            bool cleared;
            lock (_placementLock)
            {
                cleared = _writtenSlots.Count == 0 && _writtenSquares.Count == 0 && _releasedPlacements == 0;
            }

            Check("a rematch's reset and the peer frame gate both wait for a placement write in progress",
                  waitedForLock && finished && cleared);
        }

        static (int Width, int Height)? EightByEight()
        {
            return (8, 8);
        }

        var placeOnes = new List<string[]>();
        int PlaceOneWrites(string[] probeArgs)
        {
            placeOnes.Add(probeArgs);
            return 0;
        }

        var snapBeforePlacements = _lastSnap;
        _lastSnap = null;
        var placingPeer = new Peer("127.0.0.1", 1, "WRITE1", 0);
        lock (_placementLock)
        {
            WritePlacement(placingPeer, new Frame { Kind = MsgKind.Place, PlaceIdx = 2, Place = new Placement { X = 4, Y = 6 } },
                           EightByEight, PlaceOneWrites);
        }

        var sameSquareGated = GatePeerFrame(new Frame
        {
            Kind = MsgKind.Place, PlaceIdx = 5, Place = new Placement { X = 4, Y = 6 },
        });
        var otherSquareGated = GatePeerFrame(new Frame
        {
            Kind = MsgKind.Place, PlaceIdx = 5, Place = new Placement { X = 5, Y = 6 },
        });
        ResetPlacementBookkeeping();

        var wasAutoHeld = _auto;
        var wasOffHeld = _autoOffByUser;
        _auto = false;
        _autoOffByUser = false;
        var releaser = new Peer("127.0.0.1", 1, "WRITE2", 0);
        ReceivePlacement(releaser, "strikers-no-such-live-probe.exe",
                         new Frame { Kind = MsgKind.Place, PlaceIdx = 0, Place = new Placement { X = 2, Y = 7 } });
        ReceivePlacement(releaser, "strikers-no-such-live-probe.exe",
                         new Frame { Kind = MsgKind.Place, PlaceIdx = 1, Place = new Placement { X = 2, Y = 7 } });
        _auto = wasAutoHeld;
        _autoOffByUser = wasOffHeld;
        ReleaseHeldPlacements(releaser, EightByEight, PlaceOneWrites);
        ResetPlacementBookkeeping();
        Check("a placement written through the session is recorded by the square the other side sent, and the peer " +
              "frame gate then refuses that square, for a placement written at once and for two released after waiting",
              placeOnes.Count == 2
              && sameSquareGated is { } writtenSquare && writtenSquare.Contains("(4,6)")
              && writtenSquare.Contains("already placed")
              && (otherSquareGated is null || !otherSquareGated.Contains("already placed"))
              && releaser.Halted && releaser.HaltReason is { } releasedTwice && releasedTwice.Contains("(2,7)")
              && releasedTwice.Contains("already placed"));

        lock (_placementLock)
        {
            _writtenSquares.Add((4, 6));
        }

        var wasAuto = _auto;
        var wasOffByUser = _autoOffByUser;
        _auto = true;
        _autoOffByUser = false;
        var regated = new Peer("127.0.0.1", 1, "REGATE", 0);
        ReceivePlacement(regated, Path.Combine(Path.GetTempPath(), "strikers-no-such-live-probe.exe"),
                         new Frame { Kind = MsgKind.Place, PlaceIdx = 6, Place = new Placement { X = 4, Y = 6 } });
        _auto = wasAuto;
        _autoOffByUser = wasOffByUser;
        ResetPlacementBookkeeping();
        _lastSnap = snapBeforePlacements;
        Check("a placement written the moment it arrives is gated again under the placement lock, so a square written " +
              "since it passed the gate is refused",
              regated.Halted && regated.HaltReason is { } regate && regate.Contains("(4,6)")
              && regate.Contains("already placed"));

        static Piece Pc(int idx, int x, int y, int hp, int facing, int owner, int acts = 0, int bursts = 0,
                        bool burst = false, int skill = -1, int range = -1, string uuid = "")
        {
            return new(x, y, (byte)hp, (byte)facing, owner, burst, idx, acts > 0, acts, bursts, skill, range,
                       uuid);
        }

        static BoardSnapshot Frame(params Piece[] ps)
        {
            return new(8, 8, ps.ToList(), new sbyte[64]) { AiSeat = 1 };
        }

        var walk = new List<BoardSnapshot>
        {
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 1, 3, 2, 0), Pc(2, 5, 6, 4, 0, 1)),
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 3, 3, 2, 0), Pc(2, 5, 6, 4, 0, 1)),
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 1, 3, 2, 0), Pc(2, 5, 6, 4, 0, 1)),
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 2, 3, 2, 0), Pc(2, 5, 6, 4, 0, 1)),
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 2, 3, 2, 0, acts: 1), Pc(2, 5, 6, 4, 0, 1)),
        };
        var s1 = MoveDetector.DetectSequence(walk, 0);
        Check("a sampled move is read once, not once per preview square",
              s1.Ok && s1.Moves is [{ SrcX: 6, SrcY: 1, DstX: 6, DstY: 2, Facing: 2, Attack: false }]);

        Check("previews with no committed action fabricate nothing",
              MoveDetector.DetectSequence(walk.Take(4).ToList(), 0) is { Ok: false } sp &&
              sp.Refusal!.Contains("acted"));

        static List<BoardSnapshot> WithAct(List<BoardSnapshot> f, ActRecord a)
        {
            var copy = f.ToList();
            copy[^1] = copy[^1] with { Act = a };
            return copy;
        }

        Check("a controller act agreeing with the reading says nothing",
              MoveDetector.DetectSequence(WithAct(walk, new ActRecord(1, 6, 1, 6, 2, false)), 0)
                  is { Ok: true, Warning: null });

        Check("a controller act the reading does not contain REFUSES the turn",
              MoveDetector.DetectSequence(WithAct(walk, new ActRecord(1, 6, 0, 6, 2, false)), 0)
                  is { Ok: false, Moves.Count: 0 } wrongSrc &&
              wrongSrc.Refusal!.Contains("(6,0)->(6,2)"));

        Check("a controller act still in progress is ignored, it may be a preview",
              MoveDetector.DetectSequence(WithAct(walk, new ActRecord(1, 6, 1, 6, 4, true)), 0)
                  is { Ok: true, Warning: null });

        Check("a controller act with from == to is not compared, it is not a move",
              MoveDetector.DetectSequence(WithAct(walk, new ActRecord(1, 3, 3, 3, 3, false)), 0)
                  is { Ok: true, Warning: null });

        Check("a dive's inverted record is skipped, not read as a move",
              MoveDetector.DetectSequence(WithAct(walk, new ActRecord(1, 6, 2, 6, 1, false)), 0)
                  is { Ok: true, Warning: null });

        var struckAndStayed = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 7, 3, 0, 0), Pc(1, 4, 6, 4, 2, 1)),
            Frame(Pc(0, 4, 7, 3, 0, 0), Pc(1, 4, 6, 3, 2, 1)),
            Frame(Pc(0, 4, 7, 3, 0, 0), Pc(1, 4, 5, 3, 2, 1)),
            Frame(Pc(0, 4, 7, 3, 0, 0, acts: 1), Pc(1, 4, 5, 3, 2, 1)),
        };

        Check("a record naming a square the cursor only previewed is skipped when the machine never moved",
              MoveDetector.DetectSequence(WithAct(struckAndStayed, new ActRecord(0, 4, 6, 4, 7, false)), 0)
                  is { Ok: true });

        var walkedThere = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 7, 3, 0, 0), Pc(1, 4, 5, 3, 2, 1)),
            Frame(Pc(0, 4, 7, 3, 0, 0), Pc(1, 4, 5, 3, 2, 1)),
            Frame(Pc(0, 4, 7, 3, 0, 0, acts: 1), Pc(1, 4, 5, 3, 2, 1)),
        };

        Check("the same record still REFUSES when the machine did not start the turn there",
              MoveDetector.DetectSequence(WithAct(walkedThere, new ActRecord(0, 4, 6, 4, 7, false)), 0)
                  is { Ok: false } vacatedControl &&
              vacatedControl.Refusal!.Contains("(4,6)->(4,7)"));

        var ramAdvance = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 7, 2, 0, 0), Pc(1, 4, 6, 1, 2, 1)),
            Frame(Pc(0, 4, 7, 2, 0, 0), Pc(1, 4, 6, 0, 2, 1)),
            Frame(Pc(0, 4, 7, 2, 0, 0), Pc(1, 4, 5, 0, 2, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1)),
        };

        Check("a survivor that advanced onto its victim's square is matched across the compaction",
              MoveDetector.DetectSequence(ramAdvance, 0) is { Ok: true, Moves.Count: 1 });

        var strayAfterKill = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 7, 2, 0, 0), Pc(1, 4, 6, 1, 2, 1)),
            Frame(Pc(0, 4, 7, 2, 0, 0), Pc(1, 4, 6, 0, 2, 1)),
            Frame(Pc(0, 4, 7, 2, 0, 0), Pc(1, 4, 5, 0, 2, 1)),
            Frame(Pc(0, 6, 2, 2, 0, 0, acts: 1)),
        };

        Check("a survivor that ends somewhere the victim never stood still REFUSES",
              MoveDetector.DetectSequence(strayAfterKill, 0) is { Ok: false } strayControl &&
              strayControl.Refusal!.Contains("renumbered"));

        var claw = "ED68990FF5589A2DA853F0D163682C40";
        var killThenWalkOn = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 5, 7, 0, 0, range: 2, uuid: claw), Pc(1, 5, 4, 4, 0, 1)),
            Frame(Pc(0, 5, 5, 7, 0, 0, range: 2, uuid: claw), Pc(1, 5, 4, 0, 0, 1))
                with { Act = new ActRecord(0, 5, 5, 5, 5, true) },
            Frame(Pc(0, 5, 5, 7, 0, 0, range: 2, uuid: claw))
                with { Act = new ActRecord(0, 5, 5, 5, 5, true) },
            Frame(Pc(0, 5, 4, 7, 0, 0, range: 2, uuid: claw))
                with { Act = new ActRecord(0, 5, 5, 5, 4, true) },
            Frame(Pc(0, 5, 4, 7, 0, 0, acts: 1, range: 2, uuid: claw))
                with { Act = new ActRecord(0, 5, 5, 5, 4, false) },
        };
        var walkedOn = MoveDetector.DetectSequence(killThenWalkOn, 0);
        Check("a Strike machine that kills in place and walks onto the freed square sends that move",
              walkedOn.Ok && walkedOn.Moves is [{ Attack: true, DstX: 5, DstY: 5, TargetX: 5, TargetY: 4 },
                                                { Attack: false, SrcX: 5, SrcY: 5, DstX: 5, DstY: 4 }]);

        var grazer = "2B34B3566FC1ED50071517AE89C5252F";
        var ramKillsInPlace = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 5, 4, 0, 0, range: 1, uuid: grazer), Pc(1, 5, 4, 4, 0, 1)),
            Frame(Pc(0, 5, 5, 4, 0, 0, range: 1, uuid: grazer), Pc(1, 5, 4, 0, 0, 1))
                with { Act = new ActRecord(0, 5, 5, 5, 5, true) },
            Frame(Pc(0, 5, 4, 4, 0, 0, acts: 1, range: 1, uuid: grazer))
                with { Act = new ActRecord(0, 5, 5, 5, 5, false) },
        };
        Check("a Ram machine standing on its victim's square is still its own advance, no move sent",
              MoveDetector.DetectSequence(ramKillsInPlace, 0) is { Ok: true, Moves: [{ Attack: true }] });

        Check("carried by its hit: Ram", MoveDetector.CarriedByItsHit(Pc(0, 0, 0, 4, 0, 0, uuid: grazer)));
        Check("carried by its hit: not a Strike", !MoveDetector.CarriedByItsHit(Pc(0, 0, 0, 4, 0, 0, uuid: claw)));
        Check("carried by its hit: a capture with no uuid reads as it always did",
              MoveDetector.CarriedByItsHit(Pc(0, 0, 0, 4, 0, 0)));

        var dasher = "23C2AA3CCB2680F418CA1D7E9F179E9D";
        var dashKillOwedMove = new List<BoardSnapshot>
        {
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 3, 2, 1), Pc(2, 4, 3, 4, 0, 0, range: 2, uuid: dasher)),
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 3, 2, 1), Pc(2, 4, 1, 4, 0, 0, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 4, 3, 4, 3, true) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 0, 2, 1), Pc(2, 4, 1, 4, 0, 0, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 4, 3, 4, 3, true) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 1, 4, 0, 0, range: 2, uuid: dasher))
                with { Act = new ActRecord(1, 4, 1, 4, 3, true) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 3, 1, 4, 0, 0, acts: 1, range: 2, uuid: dasher))
                with { Act = new ActRecord(1, 4, 1, 3, 1, false) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 1, 4, 1, 0, acts: 1, bursts: 1, range: 2, uuid: dasher))
                with { Act = new ActRecord(1, 3, 1, 4, 1, false) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 1, 2, 1, 0, acts: 1, bursts: 1, range: 2, uuid: dasher))
                with { Act = new ActRecord(1, 3, 1, 4, 1, false) },
        };
        var afterTheKill = MoveDetector.DetectSequence(dashKillOwedMove, 0);
        Check("a Dash's owed move starts where the charge landed when a kill renumbered the list under it",
              afterTheKill.Ok &&
              afterTheKill.Moves is [{ Attack: true, SrcX: 4, SrcY: 3, DstX: 4, DstY: 3, TargetX: 4, TargetY: 2 },
                                     { Attack: false, Burst: false, SrcX: 4, SrcY: 1, DstX: 3, DstY: 1 },
                                     { Attack: false, Burst: true, SrcX: 3, SrcY: 1, DstX: 4, DstY: 1 }]);

        var dashOwedMove = new List<BoardSnapshot>
        {
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 3, 2, 1), Pc(2, 4, 3, 4, 0, 0, range: 2, uuid: dasher)),
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 3, 2, 1), Pc(2, 4, 1, 4, 0, 0, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 4, 3, 4, 3, true) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 1, 2, 1), Pc(2, 4, 1, 4, 0, 0, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 4, 3, 4, 3, true) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 1, 2, 1), Pc(2, 3, 1, 4, 0, 0, acts: 1, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 4, 1, 3, 1, false) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 1, 2, 1), Pc(2, 4, 1, 4, 1, 0, acts: 1, bursts: 1, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 3, 1, 4, 1, false) },
            Frame(Pc(0, 0, 0, 5, 2, 1), Pc(1, 4, 2, 1, 2, 1), Pc(2, 4, 1, 2, 1, 0, acts: 1, bursts: 1, range: 2, uuid: dasher))
                with { Act = new ActRecord(2, 3, 1, 4, 1, false) },
        };
        var noKill = MoveDetector.DetectSequence(dashOwedMove, 0);
        Check("the same Dash and owed move with no kill, and no renumbering, reads the same three records",
              noKill.Ok &&
              noKill.Moves is [{ Attack: true, SrcX: 4, SrcY: 3, DstX: 4, DstY: 3, TargetX: 4, TargetY: 2 },
                               { Attack: false, Burst: false, SrcX: 4, SrcY: 1, DstX: 3, DstY: 1 },
                               { Attack: false, Burst: true, SrcX: 3, SrcY: 1, DstX: 4, DstY: 1 }]);

        var diveTurn = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 5, 4, 0, 0), Pc(1, 4, 1, 3, 2, 1)),
            Frame(Pc(0, 4, 2, 4, 0, 0, acts: 1), Pc(1, 4, 1, 2, 2, 1))
                with { Act = new ActRecord(0, 4, 2, 4, 4, false) },
        };
        var dive = MoveDetector.DetectSequence(diveTurn, 0);
        Check("a dive's firing square is taken from the controller, not from where it landed",
              dive.Ok && dive.Moves is [{ Attack: true, DstX: 4, DstY: 2, AtkX: 4, AtkY: 4 }]);

        Check("the injected attack stands on the firing square, not the landing square",
              dive.Ok && dive.Moves[0].StrikeFrom == (4, 4));

        var arcDiver = "7875B4B22E79B8BF0996D4B74BCC0477";
        var arcDive = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 6, 9, 0, 0, range: 3, uuid: arcDiver), Pc(1, 3, 2, 4, 2, 1)),
            Frame(Pc(0, 3, 3, 9, 0, 0, acts: 1, range: 3, uuid: arcDiver), Pc(1, 3, 2, 2, 2, 1))
                with { Act = new ActRecord(0, 3, 3, 4, 4, false) },
        };
        var arc = MoveDetector.DetectSequence(arcDive, 0);
        Check("an arc dive's firing square is stamped, not left at the landing square",
              arc.Ok && arc.Moves is [{ Attack: true, DstX: 3, DstY: 3, AtkX: 4, AtkY: 4 }] &&
              arc.Moves[0].StrikeFrom == (4, 4));

        Check("the receiver's reach check accepts that turn, measuring the walk to the firing square",
              arc.Ok && Machines.TurnProblem(arc.Moves, arcDive[0] with { AiSeat = 0 }, 0) is null);

        var costKilled = "23C2AA3CCB2680F418CA1D7E9F179E9D";
        var dyingMove = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 2, 2, 0, 0, acts: 1, range: 2, uuid: costKilled), Pc(1, 5, 3, 10, 0, 0, range: 2, uuid: costKilled), Pc(2, 0, 0, 5, 2, 1)),
            Frame(Pc(1, 5, 3, 10, 0, 0, range: 2, uuid: costKilled), Pc(2, 0, 0, 5, 2, 1))
                with { Act = new ActRecord(-1, 5, 2, 4, 1, false) },
            Frame(Pc(1, 4, 2, 10, 0, 0, acts: 1, range: 2, uuid: costKilled), Pc(2, 0, 0, 5, 2, 1))
                with { Act = new ActRecord(1, 5, 3, 4, 2, false) },
        };
        var dyingRead = MoveDetector.DetectSequence(dyingMove, 0);
        Check("D-200: a machine that died of its Overcharge's cost as its move committed is read from the record, before the action after it",
              dyingRead.Ok && dyingRead.Moves is [{ Attack: false, Burst: true, SrcX: 5, SrcY: 2, DstX: 4, DstY: 1 },
                                                  { Attack: false, Burst: false, SrcX: 5, SrcY: 3, DstX: 4, DstY: 2 }]);

        var vanishedUnexplained = new List<BoardSnapshot>
        {
            dyingMove[0],
            dyingMove[1] with { Act = new ActRecord(1, 5, 3, 5, 3, true) },
            dyingMove[2],
        };
        Check("D-200: a machine of ours that leaves the board at 2 health with no record of what it did REFUSES",
              MoveDetector.DetectSequence(vanishedUnexplained, 0) is { Ok: false } unexplained &&
              unexplained.Refusal!.Contains("left the board"));

        var noRecords = new List<BoardSnapshot>
        {
            dyingMove[0],
            dyingMove[1] with { Act = null },
            dyingMove[2] with { Act = null },
        };
        Check("D-200: a capture with no records at all reads as it always did, the death unread",
              MoveDetector.DetectSequence(noRecords, 0) is { Ok: true, Moves.Count: 1 });

        var burstSampled = new List<BoardSnapshot>
        {
            dyingMove[0],
            Frame(Pc(0, 5, 2, 2, 0, 0, acts: 1, bursts: 1, burst: true, range: 2, uuid: costKilled), Pc(1, 5, 3, 10, 0, 0, range: 2, uuid: costKilled), Pc(2, 0, 0, 5, 2, 1)),
            dyingMove[1] with { Act = null },
            dyingMove[2],
        };
        Check("D-200: a machine whose burst flag was sampled before it went is the cut's own act, not refused",
              MoveDetector.DetectSequence(burstSampled, 0) is { Ok: true } sampled && sampled.Moves.Any(m => m.Burst));

        static CommitRecord Rec(long seq, string kind, string ptr, int fx, int fy, int tx, int ty,
                                string whose = "human", int unit = 0)
        {
            return new CommitRecord(seq, kind, whose, unit, ptr, fx, fy, tx, ty, fx, fy);
        }

        static BoardSnapshot WithCommits(BoardSnapshot f, params CommitRecord[] rs)
        {
            return f with { Commits = new CommitBatch(rs.Length, 0, rs.ToList()) };
        }

        var transcribed = new List<BoardSnapshot>
        {
            WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1))),
            WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1)),
                        Rec(1, "activate", "BEEF", 1, 3, 1, 3),
                        Rec(2, "move", "BEEF", 1, 3, 1, 2)),
            WithCommits(Frame(Pc(0, 3, 3, 8, 1, 0, bursts: 1, burst: true), Pc(1, 4, 4, 12, 3, 1)),
                        Rec(3, "burst", "BEEF", 1, 2, 1, 2),
                        Rec(4, "move", "BEEF", 1, 2, 3, 3)),
        };
        Check("transcript: an activate, its move, then an Overcharge and the move it bought, in order",
              CommitTranscript.Read(transcribed) is { Refusal: null } t1 && t1.Actions.Count == 2 &&
              t1.Actions[0] is { Kind: TranscriptKind.Move, FromX: 1, FromY: 3, ToX: 1, ToY: 2, Burst: false } &&
              t1.Actions[1] is { Kind: TranscriptKind.Move, FromX: 1, FromY: 2, ToX: 3, ToY: 3, Burst: true });

        Check("transcript: a move onto the machine's own square is a Rotate",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "BEEF", 1, 3, 1, 3),
                                                 Rec(2, "move", "BEEF", 1, 3, 1, 3))])
                  is { Refusal: null, Actions: [{ Kind: TranscriptKind.Rotate, FromX: 1, FromY: 3 }] });

        Check("transcript: the AI's own commits are not our turn",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "AI01", 4, 4, 4, 4, whose: "ai"),
                                                 Rec(2, "move", "AI01", 4, 4, 4, 3, whose: "ai"))])
                  is { Refusal: null, Actions: [] });

        Check("transcript: a record overwritten before it was read refuses the turn",
              CommitTranscript.Read([Frame(Pc(0, 1, 3, 10, 1, 0))
                                         with { Commits = new CommitBatch(40, 3, []) }])
                  is { Actions: [], Refusal: not null });

        Check("transcript: a move with no machine activated is a late-started reader, not a refusal",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(2, "move", "BEEF", 1, 3, 1, 2))])
                  is { Actions: [], Refusal: null, NotCovered: not null });
        Check("transcript: a move for a machine other than the activated one refuses",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "BEEF", 1, 3, 1, 3),
                                                 Rec(2, "move", "F00D", 4, 4, 4, 3))])
                  is { Actions: [], Refusal: not null });

        var ringButNoRecords = new List<BoardSnapshot>
        {
            Frame(Pc(0, 1, 3, 10, 1, 0)) with { Commits = new CommitBatch(12, 0, []) },
            Frame(Pc(0, 1, 2, 10, 1, 0)) with { Commits = new CommitBatch(12, 0, []) },
        };
        Check("a fallback with the ring installed but silent says so, where it used to say nothing",
              CommitTranscript.HasRecords(ringButNoRecords) is false &&
              CommitTranscript.HasRing(ringButNoRecords) &&
              MoveDetector.ReadTurn(ringButNoRecords, 0, out var silentNote) is not null &&
              silentNote is not null);

        Check("a capture with no ring at all still falls back with no note, as every old one does",
              MoveDetector.ReadTurn([Frame(Pc(0, 1, 3, 10, 1, 0)), Frame(Pc(0, 1, 2, 10, 1, 0))],
                                    0, out var quietNote) is not null && quietNote is null);
        Check("transcript: an Overcharge names its own machine, which need not be the last activated",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1)),
                                                 Rec(1, "activate", "AAAA", 1, 3, 1, 3),
                                                 Rec(2, "move", "AAAA", 1, 3, 1, 2),
                                                 Rec(3, "burst", "BBBB", 4, 4, 4, 4, unit: 1),
                                                 Rec(4, "attack", "BBBB", 4, 4, 4, 3, unit: 1)),
                                     Frame(Pc(0, 1, 2, 10, 1, 0), Pc(1, 4, 4, 10, 3, 1, bursts: 1, burst: true))])
                  is { Refusal: null, NotCovered: null } burstSwitch &&
              burstSwitch.Actions.Count == 2 &&
              burstSwitch.Actions[1] is { Kind: TranscriptKind.Attack, Ptr: "BBBB", Burst: true });

        Check("transcript: an activate before the action a REAL Overcharge bought refuses",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0), Pc(1, 4, 4, 12, 3, 0)),
                                                 Rec(1, "activate", "AAAA", 1, 3, 1, 3),
                                                 Rec(2, "burst", "AAAA", 1, 3, 1, 3),
                                                 Rec(3, "activate", "BBBB", 4, 4, 4, 4, unit: 1),
                                                 Rec(4, "move", "BBBB", 4, 4, 4, 3, unit: 1)),
                                     Frame(Pc(0, 1, 3, 8, 1, 0, bursts: 1, burst: true),
                                           Pc(1, 4, 3, 12, 3, 0, acts: 1))])
                  is { Actions: [], Refusal: not null });

        static List<BoardSnapshot> CancelledTurn(Piece aAtEnd, int aHealthAtActivate = 10)
        {
            return
            [
                WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0), Pc(1, 4, 4, 12, 3, 0), Pc(2, 6, 6, 9, 0, 1))),
                WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1), Pc(1, 4, 4, 12, 3, 0), Pc(2, 6, 6, 9, 0, 1)),
                            Rec(1, "activate", "AAAA", 1, 3, 1, 3),
                            Rec(2, "move", "AAAA", 1, 3, 1, 2)),
                WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1), Pc(1, 4, 4, 12, 3, 0), Pc(2, 6, 6, 9, 0, 1)),
                            Rec(3, "burst", "AAAA", 1, 2, 1, 2)),
                WithCommits(Frame(Pc(0, 1, 2, aHealthAtActivate, 1, 0, acts: 1), Pc(1, 4, 4, 12, 3, 0),
                                  Pc(2, 6, 6, 9, 0, 1)),
                            Rec(4, "activate", "BBBB", 4, 4, 4, 4, unit: 1),
                            Rec(5, "move", "BBBB", 4, 4, 4, 3, unit: 1)),
                Frame(aAtEnd, Pc(1, 4, 3, 12, 3, 0, acts: 1), Pc(2, 6, 6, 9, 0, 1)),
            ];
        }

        var cancelledTurn = CancelledTurn(Pc(0, 1, 2, 10, 1, 0, acts: 1));
        Check("transcript: a cancelled Overcharge crosses as nothing and the next machine's move reads on",
              CommitTranscript.Read(cancelledTurn) is { Refusal: null, NotCovered: null, Cancelled: [3] } c1 &&
              c1.Actions.Count == 2 && c1.Actions.All(a => !a.Burst) &&
              MoveDetector.ReadTurn(cancelledTurn, 0, out var cancelNote) is { Ok: true, Moves.Count: 2 } &&
              cancelNote is not null);
        Check("transcript: the same turn with the Overcharge mark showing later still refuses",
              CommitTranscript.Read(CancelledTurn(Pc(0, 1, 2, 8, 1, 0, acts: 1, bursts: 1, burst: true)))
                  is { Actions: [], Refusal: not null });

        Check("transcript: a machine that lost health before the next record is not a cancelled Overcharge",
              CommitTranscript.Read(CancelledTurn(Pc(0, 1, 2, 8, 1, 0, acts: 1), aHealthAtActivate: 8))
                  is { Actions: [], Refusal: not null });

        Check("transcript: a cancelled Overcharge followed by a real one on the same machine reads both",
              CommitTranscript.Read(
              [
                  WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                              Rec(1, "activate", "AAAA", 1, 3, 1, 3),
                              Rec(2, "move", "AAAA", 1, 3, 1, 2)),
                  WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1)), Rec(3, "burst", "AAAA", 1, 2, 1, 2)),
                  WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1)), Rec(4, "burst", "AAAA", 1, 2, 1, 2)),
                  WithCommits(Frame(Pc(0, 2, 2, 10, 1, 0, acts: 1)), Rec(5, "move", "AAAA", 1, 2, 2, 2)),
                  Frame(Pc(0, 2, 2, 8, 1, 0, acts: 1, bursts: 1, burst: true)),
              ]) is { Refusal: null, Cancelled: [3] } c2 &&
              c2.Actions.Count == 2 && c2.Actions[1] is { ToX: 2, ToY: 2, Burst: true });

        static List<BoardSnapshot> CancelThenKill(params Piece[] atEnd)
        {
            return
            [
                WithCommits(Frame(Pc(0, 6, 6, 1, 0, 1), Pc(1, 1, 2, 10, 1, 0, acts: 1), Pc(2, 5, 5, 12, 3, 0)),
                            Rec(1, "activate", "AAAA", 1, 3, 1, 3, unit: 1),
                            Rec(2, "move", "AAAA", 1, 3, 1, 2, unit: 1)),
                WithCommits(Frame(Pc(0, 6, 6, 1, 0, 1), Pc(1, 1, 2, 10, 1, 0, acts: 1), Pc(2, 5, 5, 12, 3, 0)),
                            Rec(3, "burst", "AAAA", 1, 2, 1, 2, unit: 1)),
                WithCommits(Frame(Pc(0, 6, 6, 1, 0, 1), Pc(1, 1, 2, 10, 1, 0, acts: 1), Pc(2, 5, 5, 12, 3, 0)),
                            Rec(4, "activate", "BBBB", 5, 5, 5, 5, unit: 2),
                            Rec(5, "attack", "BBBB", 5, 5, 5, 5, unit: 2)),
                Frame(atEnd),
            ];
        }

        Check("transcript: a cancelled Overcharge is followed by square across a kill that renumbers it",
              CommitTranscript.Read(CancelThenKill(Pc(0, 1, 2, 10, 1, 0, acts: 1), Pc(1, 5, 5, 12, 3, 0, acts: 1)))
                  is { Refusal: null, Cancelled: [3], Actions.Count: 2 });
        Check("transcript: a cancelled Overcharge whose own machine is gone after it refuses",
              CommitTranscript.Read(CancelThenKill(Pc(0, 6, 6, 1, 0, 1), Pc(1, 5, 5, 12, 3, 0, acts: 1)))
                  is { Actions: [], Refusal: not null });

        static List<BoardSnapshot> CancelThenRotate(Piece aAtEnd)
        {
            return
            [
                WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0), Pc(1, 1, 1, 9, 0, 1))),
                WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1), Pc(1, 1, 1, 9, 0, 1)),
                            Rec(1, "activate", "AAAA", 1, 3, 1, 3),
                            Rec(2, "move", "AAAA", 1, 3, 1, 2)),
                WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1), Pc(1, 1, 1, 9, 0, 1)),
                            Rec(3, "burst", "AAAA", 1, 2, 1, 2)),
                WithCommits(Frame(Pc(0, 1, 2, 10, 0, 0, acts: 1), Pc(1, 1, 1, 7, 0, 1)),
                            Rec(4, "attack", "AAAA", 1, 2, 1, 2)),
                Frame(aAtEnd, Pc(1, 1, 1, 7, 0, 1)),
            ];
        }

        Check("a cancelled Overcharge followed by the same machine's ordinary attack crosses as a plain attack",
              CommitTranscript.Read(CancelThenRotate(Pc(0, 1, 2, 10, 0, 0, acts: 1)))
                  is { Refusal: null, NotCovered: null, Cancelled: [3] } plainAttack &&
              plainAttack.Actions is [{ Kind: TranscriptKind.Move, Burst: false }, { Kind: TranscriptKind.Attack, Burst: false }] &&
              CommitTranscript.Read(CancelThenRotate(Pc(0, 1, 2, 8, 0, 0, acts: 1, bursts: 1, burst: true)))
                  is { Refusal: null, Cancelled: [] } boughtAttack &&
              boughtAttack.Actions is [{ Burst: false }, { Kind: TranscriptKind.Attack, Burst: true }]);

        Check("transcript: a record whose controller this build does not recognise refuses the turn",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "AAAA", 1, 3, 1, 3),
                                                 Rec(2, "move", "AAAA", 1, 3, 1, 2),
                                                 Rec(3, "move", "CCCC", 2, 2, 2, 1, whose: "?"))])
                  is { Actions: [], Refusal: not null });

        Check("transcript: the AI's records are attributable, so they are dropped and not refused",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "AI01", 4, 4, 4, 4, whose: "ai"),
                                                 Rec(2, "burst", "AI02", 5, 5, 5, 5, whose: "ai"))])
                  is { Refusal: null, Actions: [] });
        Check("transcript: a REAL Overcharge with nothing recorded after it refuses",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "BEEF", 1, 3, 1, 3),
                                                 Rec(2, "burst", "BEEF", 1, 3, 1, 3)),
                                     Frame(Pc(0, 1, 3, 8, 1, 0, bursts: 1, burst: true))])
                  is { Actions: [], Refusal: not null });

        Check("transcript: an Overcharge cancelled just before the turn ends crosses as nothing",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "BEEF", 1, 3, 1, 3),
                                                 Rec(2, "move", "BEEF", 1, 3, 1, 2)),
                                     WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1)),
                                                 Rec(3, "burst", "BEEF", 1, 2, 1, 2)),
                                     Frame(Pc(0, 1, 2, 10, 1, 0, acts: 1))])
                  is { Refusal: null, Cancelled: [3], Actions.Count: 1 });
        Check("transcript: a kind this reading does not cover refuses",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "BEEF", 1, 3, 1, 3),
                                                 Rec(2, "shove", "BEEF", 1, 3, 1, 2))])
                  is { Actions: [], Refusal: not null });

        Check("transcript: a record carried by two samples is read once",
              CommitTranscript.Read(
                  [WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                               Rec(1, "activate", "BEEF", 1, 3, 1, 3), Rec(2, "move", "BEEF", 1, 3, 1, 2)),
                   WithCommits(Frame(Pc(0, 1, 2, 10, 1, 0)),
                               Rec(1, "activate", "BEEF", 1, 3, 1, 3), Rec(2, "move", "BEEF", 1, 3, 1, 2))])
                  is { Refusal: null, Actions.Count: 1 });

        Check("transcript: a capture with no records has no opinion at all",
              !CommitTranscript.HasRecords(walk) && CommitTranscript.Read(walk)
                  is { Actions: [], Refusal: null });

        Check("transcript: the slot comes from each record itself, and a dead machine stays -1",
              CommitTranscript.Read([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                 Rec(1, "activate", "BEEF", 1, 3, 1, 3, unit: 8),
                                                 Rec(2, "move", "BEEF", 1, 3, 1, 2, unit: 7),
                                                 Rec(3, "burst", "BEEF", 1, 2, 1, 2, unit: 7),
                                                 Rec(4, "move", "BEEF", 1, 2, 2, 2, unit: -1))])
                  is { Refusal: null } t2 && t2.Actions[0].Unit == 7 && t2.Actions[1].Unit == -1);

        Check("join: a recorded move crosses with the facing the board shows it landed on",
              CommitTranscript.ToMoves(transcribed, 0) is { Refusal: null, NotCovered: null } j1 &&
              j1.Moves.Count == 2 &&
              j1.Moves[0] is { SrcX: 1, SrcY: 3, DstX: 1, DstY: 2, Attack: false, Burst: false } &&
              j1.Moves[1] is { SrcX: 1, SrcY: 2, DstX: 3, DstY: 3, Burst: true });

        var recordedAttack = new List<BoardSnapshot>
        {
            WithCommits(Frame(Pc(0, 3, 6, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1))),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1)),
                        Rec(1, "activate", "BEEF", 3, 6, 3, 6),
                        Rec(2, "attack", "BEEF", 3, 6, 3, 4)),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 9, 3, 1))),
        };
        Check("join: a recorded attack takes its victim from the machine of theirs that lost health",
              CommitTranscript.ToMoves(recordedAttack, 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, SrcX: 3, SrcY: 6, DstX: 3, DstY: 4, TargetX: 4, TargetY: 4 }] });

        var splashed = new List<BoardSnapshot>
        {
            WithCommits(Frame(Pc(0, 3, 6, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1), Pc(2, 2, 4, 8, 3, 1))),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1), Pc(2, 2, 4, 8, 3, 1)),
                        Rec(1, "activate", "BEEF", 3, 6, 3, 6),
                        Rec(2, "attack", "BEEF", 3, 6, 3, 4)),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1), Pc(2, 2, 4, 5, 3, 1))),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 9, 3, 1), Pc(2, 2, 4, 5, 3, 1))),
        };
        Check("join: the first machine of theirs to lose health is the target, the rest are consequences",
              CommitTranscript.ToMoves(splashed, 0)
                  is { Refusal: null, Moves: [{ Attack: true, TargetX: 2, TargetY: 4 }] });

        var killed = new List<BoardSnapshot>
        {
            WithCommits(Frame(Pc(0, 3, 6, 10, 1, 0), Pc(1, 4, 4, 2, 3, 1))),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 2, 3, 1)),
                        Rec(1, "activate", "BEEF", 3, 6, 3, 6),
                        Rec(2, "attack", "BEEF", 3, 6, 3, 4)),
            WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0))),
        };
        Check("join: a machine of theirs killed outright is named by the square it stood on",
              CommitTranscript.ToMoves(killed, 0)
                  is { Refusal: null, Moves: [{ Attack: true, TargetX: 4, TargetY: 4 }] });

        static List<BoardSnapshot> Dived(string uuid, int landX, int landY, bool lineHitFirst = false)
        {
            return
            [
                WithCommits(Frame(Pc(0, 2, 3, 9, 0, 0, range: 3, uuid: uuid), Pc(1, 2, 1, 5, 0, 1),
                                  Pc(2, 1, 1, 10, 0, 1))),
                WithCommits(Frame(Pc(0, landX, landY, 9, 0, 0, range: 3, uuid: uuid), Pc(1, 2, 1, 5, 0, 1),
                                  Pc(2, 1, 1, 10, 0, 1)),
                            Rec(1, "activate", "BEEF", 2, 3, 2, 3),
                            Rec(2, "attack", "BEEF", 2, 3, 2, 3)),
                WithCommits(Frame(Pc(0, landX, landY, 9, 0, 0, range: 3, uuid: uuid),
                                  Pc(1, 2, 1, lineHitFirst ? 3 : 5, 0, 1),
                                  Pc(2, 1, 1, lineHitFirst ? 10 : 7, 0, 1))),
                WithCommits(Frame(Pc(0, landX, landY, 9, 0, 0, range: 3, uuid: uuid), Pc(1, 2, 1, 0, 0, 1),
                                  Pc(2, 1, 1, 7, 0, 1))),
                WithCommits(Frame(Pc(0, landX, landY, 9, 0, 0, range: 3, uuid: uuid), Pc(1, 1, 1, 7, 0, 1))),
            ];
        }

        const string diveWithSpread = "7875B4B22E79B8BF0996D4B74BCC0477";
        Check("join: a recorded Dive crosses landing as Dst and the square it struck from as AtkX",
              CommitTranscript.ToMoves(Dived(diveWithSpread, 2, 2), 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, SrcX: 2, SrcY: 3, DstX: 2, DstY: 2, TargetX: 1, TargetY: 1,
                                 AtkX: 2, AtkY: 3 }] });
        Check("join: a machine that is not a Dive and left its attack square is handed back, not a dive",
              CommitTranscript.ToMoves(Dived("F9433C1448F8F052BD457978CD0BFEC5", 2, 2, lineHitFirst: true), 0)
                  is { Refusal: null, NotCovered: not null, Moves: [] });
        Check("join: a Dive that landed away from its victim is handed back, not guessed at",
              CommitTranscript.ToMoves(Dived(diveWithSpread, 4, 4, lineHitFirst: true), 0)
                  is { Refusal: null, NotCovered: not null, Moves: [] });

        var diveThenMove = new List<BoardSnapshot>
        {
            WithCommits(Frame(Pc(0, 2, 3, 9, 0, 0, range: 3, uuid: diveWithSpread), Pc(1, 1, 1, 10, 0, 1))),
            WithCommits(Frame(Pc(0, 2, 2, 9, 0, 0, range: 3, uuid: diveWithSpread), Pc(1, 1, 1, 10, 0, 1)),
                        Rec(1, "activate", "BEEF", 2, 3, 2, 3),
                        Rec(2, "attack", "BEEF", 2, 3, 2, 3)),
            WithCommits(Frame(Pc(0, 1, 2, 9, 0, 0, range: 3, uuid: diveWithSpread), Pc(1, 1, 1, 7, 0, 1)),
                        Rec(3, "move", "BEEF", 2, 2, 1, 2)),
            WithCommits(Frame(Pc(0, 1, 2, 9, 0, 0, acts: 1, range: 3, uuid: diveWithSpread), Pc(1, 1, 1, 7, 0, 1))),
        };
        Check("a Dive's landing is read before the next action's sample, never from where that action took it",
              CommitTranscript.ToMoves(diveThenMove, 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, DstX: 2, DstY: 2, AtkX: 2, AtkY: 3 }, { Attack: false, DstX: 1, DstY: 2 }] });

        const string chargerUuid = "FDC39FDFF4CE9C5B8190CE46AAFCF7B5";
        static List<BoardSnapshot> Charged(string uuid, int landX, int landY)
        {
            return
            [
                WithCommits(Frame(Pc(0, 4, 4, 3, 2, 0, range: 2, uuid: uuid), Pc(1, 3, 4, 4, 2, 1, range: 2, uuid: chargerUuid),
                                  Pc(2, 3, 3, 3, 2, 0, range: 2, uuid: chargerUuid), Pc(3, 4, 5, 4, 2, 1, range: 2, uuid: chargerUuid))),
                WithCommits(Frame(Pc(0, landX, landY, 3, 2, 0, range: 2, uuid: uuid), Pc(1, 3, 4, 4, 2, 1, range: 2, uuid: chargerUuid),
                                  Pc(2, 3, 3, 3, 2, 0, range: 2, uuid: chargerUuid), Pc(3, 4, 5, 4, 2, 1, range: 2, uuid: chargerUuid)),
                            Rec(9, "activate", "BEEF", 4, 4, 4, 4),
                            Rec(10, "attack", "BEEF", 4, 4, 4, 4)),
                WithCommits(Frame(Pc(0, landX, landY, 3, 2, 0, range: 2, uuid: uuid), Pc(1, 3, 4, 4, 2, 1, range: 2, uuid: chargerUuid),
                                  Pc(2, 3, 3, 3, 2, 0, range: 2, uuid: chargerUuid), Pc(3, 4, 5, 0, 2, 1, range: 2, uuid: chargerUuid))),
                WithCommits(Frame(Pc(0, landX, landY, 3, 2, 0, range: 2, uuid: uuid), Pc(1, 3, 4, 4, 2, 1, range: 2, uuid: chargerUuid),
                                  Pc(2, 3, 3, 3, 2, 0, range: 2, uuid: chargerUuid))),
            ];
        }

        const string burrowerUuid = "1C96A39FFE37791F8AE07BD49A2230FF";
        List<BoardSnapshot> rotatedAfterLanding =
        [
            WithCommits(Frame(Pc(0, 2, 2, 4, 0, 0, uuid: burrowerUuid), Pc(1, 1, 1, 2, 0, 1, uuid: burrowerUuid))),
            WithCommits(Frame(Pc(0, 2, 2, 4, 0, 0, uuid: burrowerUuid), Pc(1, 1, 1, 2, 0, 1, uuid: burrowerUuid)),
                        Rec(5, "activate", "BEEF", 1, 0, 0, 0),
                        Rec(6, "move", "BEEF", 2, 2, 3, 2)),
            WithCommits(Frame(Pc(0, 3, 2, 4, 0, 0, uuid: burrowerUuid), Pc(1, 1, 1, 2, 0, 1, uuid: burrowerUuid))),
            WithCommits(Frame(Pc(0, 3, 2, 4, 1, 0, acts: 1, uuid: burrowerUuid), Pc(1, 1, 1, 2, 0, 1, uuid: burrowerUuid))),
            WithCommits(Frame(Pc(0, 3, 2, 4, 1, 0, uuid: burrowerUuid), Pc(1, 1, 1, 2, 0, 1, uuid: burrowerUuid))),
        ];
        Check("a move's facing is read once the machine's own counter has ticked on the landing, not from its first sample there",
              CommitTranscript.ToMoves(rotatedAfterLanding, 0)
                  is { Refusal: null, NotCovered: null, Moves: [{ Attack: false, DstX: 3, DstY: 2, Facing: 1 }] });

        const string rockbreakerUuid = "ADEA18E33DA2CAF15010D29CA12FE1F3";
        const string dreadwingUuid = "435534A445562BA16633AF4B908D83B2";
        List<BoardSnapshot> turnedAndShot =
        [
            WithCommits(Frame(Pc(0, 7, 3, 3, 2, 0, range: 2, uuid: rockbreakerUuid), Pc(1, 5, 3, 9, 0, 1, range: 3, uuid: dreadwingUuid))),
            WithCommits(Frame(Pc(0, 7, 3, 3, 3, 0, range: 2, uuid: rockbreakerUuid), Pc(1, 5, 3, 9, 0, 1, range: 3, uuid: dreadwingUuid))),
            WithCommits(Frame(Pc(0, 7, 3, 3, 0, 0, range: 2, uuid: rockbreakerUuid), Pc(1, 5, 3, 9, 0, 1, range: 3, uuid: dreadwingUuid)),
                        Rec(59, "activate", "BEEF", 7, 3, 7, 3),
                        Rec(60, "attack", "BEEF", 7, 3, 7, 3)),
            WithCommits(Frame(Pc(0, 7, 3, 3, 3, 0, range: 2, uuid: rockbreakerUuid), Pc(1, 5, 3, 9, 0, 1, range: 3, uuid: dreadwingUuid))),
            WithCommits(Frame(Pc(0, 7, 3, 3, 3, 0, range: 2, uuid: rockbreakerUuid), Pc(1, 5, 3, 4, 0, 1, range: 3, uuid: dreadwingUuid))),
            WithCommits(Frame(Pc(0, 7, 3, 3, 3, 0, range: 2, uuid: rockbreakerUuid), Pc(1, 5, 3, 4, 0, 1, range: 3, uuid: dreadwingUuid))),
        ];
        Check("D-290: an attack's facing is the attacker's on the sample where its victim first loses health, not on the "
              + "sample where the game's record first appears",
              CommitTranscript.ToMoves(turnedAndShot, 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, DstX: 7, DstY: 3, Facing: 3, TargetX: 5, TargetY: 3 }] }
              && CommitTranscript.FacingOf(turnedAndShot[4], 0, 7, 3, rockbreakerUuid) == 3
              && CommitTranscript.FacingOf(turnedAndShot[4], 0, 7, 3, dreadwingUuid) is null
              && CommitTranscript.FacingOf(turnedAndShot[4], 0, 6, 3, "") is null);

        Check("D-291: after a halt the samples are still captured and no longer read, even when the halt switched auto off",
              CapturesSample(auto: true, tracking: true, halted: true)
              && CapturesSample(auto: false, tracking: true, halted: true)
              && CapturesSample(auto: true, tracking: true, halted: false)
              && !CapturesSample(auto: false, tracking: true, halted: false)
              && !CapturesSample(auto: true, tracking: false, halted: true)
              && ReadsSample(auto: true, tracking: true, halted: false)
              && !ReadsSample(auto: true, tracking: true, halted: true)
              && !ReadsSample(auto: false, tracking: true, halted: true));

        string[] gameLogTail =
        [
            "12:00:00:000 (00001000) > [Render] Working set: 8000MB fps: 60.000000",
            "12:00:40:000 (00001001) > [D3D] rb_resource_data.mAllocation.GetD3DResource().Map(0, &range) failed with HRESULT 2289696773 (0x887a0005)",
            "12:00:40:000 (00001002) > [D3D] ERROR! Device removed detected (0x887A0006: DXGI_ERROR_DEVICE_HUNG)",
            "12:00:40:000 (00001002) > [D3D] save under C:\\Users\\someone\\Documents\\76561198000000000",
            "12:00:40:000 (00001002) > [D3D] account 76561198000000000 lost its device",
            "12:00:40:000 (00001002) > [D3D] GPU temperature: 51 Celsius",
        ];
        var gameLog = AfterHalt.GameLogLines(gameLogTail);
        Check("D-291: after a halt the heartbeat names the time and the game's memory, an exit names its code, and only "
              + "the game log's Direct3D lines cross, with no path and no long number",
              AfterHalt.BeatLine(TimeSpan.FromSeconds(45), 8000L * 1024 * 1024, 3)
                  == "  after the stop, 45 s: the game runs, working set 8000 MB, 3 live-probe helper(s) running"
              && AfterHalt.ExitLine(TimeSpan.FromSeconds(205), -1073741819)
                  == "  after the stop, 3 min 25 s: the game exited, code 0xC0000005"
              && AfterHalt.ExitLine(TimeSpan.Zero, null).EndsWith("code unknown")
              && AfterHalt.BeatDue(TimeSpan.FromSeconds(15), TimeSpan.Zero)
              && !AfterHalt.BeatDue(TimeSpan.FromSeconds(14), TimeSpan.Zero)
              && !AfterHalt.BeatDue(TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(10))
              && gameLog.Count == 4
              && gameLog.Any(l => l.Contains("DXGI_ERROR_DEVICE_HUNG"))
              && gameLog.Any(l => l.Contains("account * lost its device"))
              && !gameLog.Any(l => l.Contains("[Render]") || l.Contains(":\\") || l.Contains("76561198"))
              && AfterHalt.GameLogLines(Enumerable.Repeat("[D3D] x", 20)).Count == AfterHalt.GameLogLinesKept);

        var afterStop = AfterHalt.ExitLines(TimeSpan.Zero, null, gameLogTail);
        Check("no line written after a stop says halt, so the launcher cannot read one as a second halt",
              !AfterHalt.BeatLine(TimeSpan.FromSeconds(45), 1024L * 1024, 3).Contains("halt", StringComparison.OrdinalIgnoreCase)
              && !afterStop.Any(l => l.Contains("halt", StringComparison.OrdinalIgnoreCase)));

        Check("a game already gone at the stop still gets its game log's Direct3D lines after the exit line",
              afterStop.Count == 5
              && afterStop[0].EndsWith("the game exited, code unknown")
              && afterStop.Skip(1).All(l => l.StartsWith("    game log: ")));

        var controlled = AfterHalt.GameLogLines(["[D3D] a" + (char)0x1B + "[2Jb" + (char)0x0D + "c" + (char)0x07 + "d"]);
        Check("a game log line reaches the console with its control characters dropped",
              controlled.Count == 1
              && !controlled[0].Any(char.IsControl)
              && controlled[0] == "[D3D] a[2Jbcd");

        Check("a game log line holding a network path or any backslash stays on this PC",
              AfterHalt.GameLogLines(["[D3D] shader cache on " + @"\\host\share\cache", "[D3D] cache at " + @"Users\someone\x"]).Count == 0
              && AfterHalt.GameLogLines(["[D3D] ERROR! Device removed detected"]).Count == 1);

        var startFault = new System.ComponentModel.Win32Exception(5,
            @"An error occurred trying to start process 'live-probe.exe' with working directory 'C:\Users\someone\Desktop'.");
        Check("a live-probe that could not start is named with the fault's kind, never its message, which carries a folder",
              StartFailedLine("live-probe.exe --hold", startFault) == "  could not start live-probe.exe --hold (Win32Exception)"
              && !StartFailedLine("live-probe.exe --hold", startFault).Contains("Users"));

        Check("a sample is still captured after a halt that switched auto off, and read only while auto is on",
              SampleGoesToAuto(auto: false, tracking: true, halted: true)
              && SampleGoesToAuto(auto: true, tracking: true, halted: false)
              && !SampleGoesToAuto(auto: false, tracking: true, halted: false)
              && !SampleGoesToAuto(auto: false, tracking: false, halted: true));

        Check("the settled landing is the first sample whose counter moved, and the first sample when none does",
              CommitTranscript.SettledLanding(rotatedAfterLanding, 0, 3, 2, 2, 4) == 3
              && CommitTranscript.SettledLanding(rotatedAfterLanding, 0, 3, 2, 2, 2) == 2
              && CommitTranscript.SettledLanding(rotatedAfterLanding, 0, 1, 1, 2, 4) == 2);

        List<BoardSnapshot> walkedThenCharged =
        [
            WithCommits(Frame(Pc(0, 1, 0, 4, 0, 0, range: 2, uuid: chargerUuid), Pc(1, 3, 1, 2, 2, 1, range: 2, uuid: chargerUuid))),
            WithCommits(Frame(Pc(0, 1, 0, 4, 0, 0, range: 2, uuid: chargerUuid), Pc(1, 3, 1, 2, 2, 1, range: 2, uuid: chargerUuid)),
                        Rec(19, "activate", "BEEF", 1, 0, 1, 0),
                        Rec(20, "attack", "BEEF", 1, 0, 3, 0)),
            WithCommits(Frame(Pc(0, 3, 0, 4, 0, 0, range: 2, uuid: chargerUuid), Pc(1, 3, 1, 2, 2, 1, range: 2, uuid: chargerUuid))),
            WithCommits(Frame(Pc(0, 3, 0, 4, 2, 0, range: 2, uuid: chargerUuid), Pc(1, 3, 1, 2, 2, 1, range: 2, uuid: chargerUuid))),
            WithCommits(Frame(Pc(0, 3, 2, 4, 2, 0, range: 2, uuid: chargerUuid), Pc(1, 3, 1, 0, 2, 1, range: 2, uuid: chargerUuid))),
            WithCommits(Frame(Pc(0, 3, 2, 4, 2, 0, acts: 1, range: 2, uuid: chargerUuid))),
        ];
        Check("a Dash that walked to its strike square and charged is read from the end of the charge, not from the walk's landing",
              CommitTranscript.ToMoves(walkedThenCharged, 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, SrcX: 1, SrcY: 0, DstX: 3, DstY: 0, Facing: 2, TargetX: 3, TargetY: 1, LandX: 3, LandY: 2 }] });

        var walkCharge = new Move
        {
            SrcX = 1, SrcY = 0, DstX = 3, DstY = 0, TargetX = 3, TargetY = 1,
            Facing = 2, Attack = true, LandX = 3, LandY = 2,
        };
        var walkBoard = new BoardSnapshot(4, 4,
            [
                new Piece(1, 0, 4, 0, 1) { Uuid = chargerUuid, Range = 2 },
                new Piece(3, 1, 2, 2, 0) { Uuid = chargerUuid, Range = 2 },
            ], []) { AiSeat = 1 };
        Check("a charge that follows a walk is measured from the strike square, not from where the walk began",
              Machines.TurnProblem([walkCharge], walkBoard, 1) is null
              && Machines.TurnProblem([Landing(walkCharge, 1, 2)], walkBoard, 1) is { } walkWrong
              && walkWrong.Contains("charge"));

        const string redeyeUuid = "0E44B98882BA9AFD876C0DB6144D35F5";
        const string tjawUuid = "F9433C1448F8F052BD457978CD0BFEC5";
        List<BoardSnapshot> overchargeMoveDeath =
        [
            WithCommits(Frame(Pc(0, 5, 5, 2, 0, 0, uuid: redeyeUuid), Pc(1, 0, 7, 4, 0, 0, uuid: chargerUuid), Pc(2, 5, 0, 5, 2, 1, uuid: tjawUuid))),
            WithCommits(Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1, uuid: redeyeUuid), Pc(1, 0, 7, 4, 0, 0, uuid: chargerUuid), Pc(2, 5, 0, 5, 2, 1, uuid: tjawUuid)),
                        Rec(29, "activate", "BEEF", 5, 5, 5, 5),
                        Rec(30, "move", "BEEF", 5, 5, 5, 6)),
            WithCommits(Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1, uuid: redeyeUuid), Pc(1, 0, 7, 4, 0, 0, uuid: chargerUuid), Pc(2, 5, 0, 5, 2, 1, uuid: tjawUuid)),
                        Rec(31, "burst", "BEEF", 5, 6, 5, 6)),
            WithCommits(Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1, uuid: redeyeUuid), Pc(1, 0, 7, 4, 0, 0, uuid: chargerUuid), Pc(2, 5, 0, 5, 2, 1, uuid: tjawUuid)),
                        Rec(32, "move", "BEEF", 5, 6, 5, 7)),
            WithCommits(Frame(Pc(0, 0, 7, 4, 0, 0, uuid: chargerUuid), Pc(1, 5, 0, 5, 2, 1, uuid: tjawUuid))),
            WithCommits(Frame(Pc(0, 0, 7, 4, 0, 0, uuid: chargerUuid), Pc(1, 5, 0, 5, 2, 1, uuid: tjawUuid))),
        ];
        Check("an Overcharge move whose machine dies of the cost at its landing is read as that move, with the facing it kept",
              CommitTranscript.ToMoves(overchargeMoveDeath, 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: false, DstX: 5, DstY: 6, Burst: false },
                               { Attack: false, SrcX: 5, SrcY: 6, DstX: 5, DstY: 7, Facing: 0, Burst: true }] });

        List<BoardSnapshot> victimInOwnSample =
        [
            WithCommits(Frame(Pc(0, 3, 4, 2, 0, 0, uuid: chargerUuid), Pc(1, 3, 1, 4, 2, 1, uuid: tjawUuid))),
            WithCommits(Frame(Pc(0, 3, 4, 2, 0, 0, uuid: chargerUuid), Pc(1, 3, 1, 4, 2, 1, uuid: tjawUuid)),
                        Rec(50, "activate", "BEEF", 3, 4, 3, 4)),
            WithCommits(Frame(Pc(0, 3, 2, 2, 0, 0, uuid: chargerUuid), Pc(1, 3, 1, 4, 2, 1, uuid: tjawUuid)),
                        Rec(51, "attack", "BEEF", 3, 4, 3, 2)),
            WithCommits(Frame(Pc(0, 3, 2, 2, 0, 0, uuid: chargerUuid), Pc(1, 3, 1, 3, 2, 1, uuid: tjawUuid))),
            WithCommits(Frame(Pc(0, 3, 2, 2, 0, 0, acts: 1, uuid: chargerUuid), Pc(1, 3, 1, 3, 2, 1, uuid: tjawUuid))),
            WithCommits(Frame(Pc(0, 3, 2, 2, 0, 0, acts: 1, uuid: chargerUuid), Pc(1, 3, 1, 3, 2, 1, uuid: tjawUuid)),
                        Rec(52, "burst", "BEEF", 3, 2, 3, 2)),
            WithCommits(Frame(Pc(0, 3, 2, 2, 0, 0, acts: 1, bursts: 1, burst: true, uuid: chargerUuid), Pc(1, 3, 1, 2, 2, 1, uuid: tjawUuid)),
                        Rec(53, "attack", "BEEF", 3, 2, 3, 2)),
            WithCommits(Frame(Pc(0, 3, 2, 2, 0, 0, acts: 1, bursts: 1, burst: true, uuid: chargerUuid), Pc(1, 3, 1, 2, 2, 1, uuid: tjawUuid))),
        ];
        Check("a victim that loses health inside the attack record's own sample is still the attack's victim",
              CommitTranscript.ToMoves(victimInOwnSample, 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, SrcX: 3, SrcY: 4, DstX: 3, DstY: 2, TargetX: 3, TargetY: 1, Burst: false },
                               { Attack: true, SrcX: 3, SrcY: 2, DstX: 3, DstY: 2, TargetX: 3, TargetY: 1, Burst: true }] });

        Check("join: a Dash whose kill ended the match crosses as its attack from the square it charged from",
              CommitTranscript.ToMoves(Charged(chargerUuid, 4, 6), 0)
                  is { Refusal: null, NotCovered: null,
                       Moves: [{ Attack: true, SrcX: 4, SrcY: 4, DstX: 4, DstY: 4, Facing: 2, TargetX: 4, TargetY: 5 }] });
        Check("join: a Dash off the end of its charge, or a machine that is not a Dash, is handed back, not a charge",
              CommitTranscript.ToMoves(Charged(chargerUuid, 4, 7), 0) is { Refusal: null, NotCovered: not null, Moves: [] }
              && CommitTranscript.ToMoves(Charged("2B34B3566FC1ED50071517AE89C5252F", 4, 6), 0)
                  is { Refusal: null, NotCovered: not null, Moves: [] });

        Check("join: a shape the join does not cover is handed back to the board reader, not refused",
              CommitTranscript.ToMoves(
                  [WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                               Rec(1, "activate", "BEEF", 1, 3, 1, 3), Rec(2, "move", "BEEF", 1, 3, 5, 5)),
                   WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)))]
                  , 0) is { Refusal: null, NotCovered: not null, Moves: [] });
        Check("join: a dropped record halts instead of being handed back",
              CommitTranscript.ToMoves([Frame(Pc(0, 1, 3, 10, 1, 0))
                                            with { Commits = new CommitBatch(40, 2, []) }], 0)
                  is { Refusal: not null, NotCovered: null });
        Check("join: an attack nothing of theirs answered is handed back, not guessed at",
              CommitTranscript.ToMoves(
                  [WithCommits(Frame(Pc(0, 3, 6, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1)),
                               Rec(1, "activate", "BEEF", 3, 6, 3, 6), Rec(2, "attack", "BEEF", 3, 6, 3, 4)),
                   WithCommits(Frame(Pc(0, 3, 4, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1)))]
                  , 0) is { Refusal: null, NotCovered: not null });
        Check("join: a capture with no records at all is handed back to the board reader",
              CommitTranscript.ToMoves(walk, 0) is { Refusal: null, NotCovered: not null });

        Check("join: a late-started reader is handed back while a dropped record still halts",
              CommitTranscript.ToMoves([WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0)),
                                                    Rec(9, "move", "BEEF", 1, 3, 1, 2))], 0)
                  is { Refusal: null, NotCovered: not null } &&
              CommitTranscript.ToMoves([Frame(Pc(0, 1, 3, 10, 1, 0))
                                            with { Commits = new CommitBatch(40, 1, []) }], 0)
                  is { Refusal: not null });

        Check("read turn: the records account for the turn, so the transcript is what crosses",
              MoveDetector.ReadTurn(transcribed, 0, out var noteA) is { Ok: true, Moves.Count: 2 } &&
              noteA is null);
        Check("read turn: a capture with no records reads from the board, and says nothing about it",
              MoveDetector.ReadTurn(walk, 0, out var noteB) is { Ok: true } &&
              noteB is null);
        Check("read turn: a dropped record halts and never falls back to the board",
              MoveDetector.ReadTurn([Frame(Pc(0, 1, 3, 10, 1, 0)) with { Commits = new CommitBatch(9, 1, []) },
                                     Frame(Pc(0, 1, 2, 10, 1, 0))], 0, out _) is { Ok: false });
        Check("read turn: a shape the join has not learned is read from the board and said so",
              MoveDetector.ReadTurn(
                  [WithCommits(Frame(Pc(0, 1, 3, 10, 1, 0), Pc(1, 5, 5, 4, 0, 1)),
                               Rec(1, "activate", "BEEF", 1, 3, 1, 3), Rec(2, "move", "BEEF", 1, 3, 5, 5)),
                   Frame(Pc(0, 1, 2, 10, 1, 0), Pc(1, 5, 5, 4, 0, 1))], 0, out var noteC) is not null &&
              noteC is not null);

        Check("a move-then-attack leaves the firing square unset, so Dst still stands",
              MoveDetector.DetectSequence(WithAct(walk, new ActRecord(1, 6, 2, 6, 2, false)), 0)
                  is { Ok: true } plain && plain.Moves[0].AtkX == -1 &&
              plain.Moves[0].StrikeFrom == (plain.Moves[0].DstX, plain.Moves[0].DstY));

        var owedMovePreviewed = new List<BoardSnapshot>
        {
            Frame(Pc(0, 3, 6, 10, 1, 0), Pc(1, 4, 4, 12, 3, 1)),
            Frame(Pc(0, 3, 4, 10, 1, 0, acts: 1), Pc(1, 4, 4, 9, 3, 1))
                with { Act = new ActRecord(0, 3, 4, 3, 3, false) },
        };
        var owed = MoveDetector.DetectSequence(owedMovePreviewed, 0);
        Check("a walk-then-strike whose owed move was only previewed still reads as one attack",
              owed is { Ok: true, Moves: [{ Attack: true, SrcX: 3, SrcY: 6, DstX: 3, DstY: 4,
                                            TargetX: 4, TargetY: 4 }] });

        Check("a cursor square that fails the strike geometry is NOT taken as a firing square",
              owed.Ok && owed.Moves[0].AtkX == -1 &&
              owed.Moves[0].StrikeFrom == (owed.Moves[0].DstX, owed.Moves[0].DstY));

        const string ravager = "8218A7A4485CED3F9DBFF00447CA94A0";
        const string charger = "FDC39FDFF4CE9C5B8190CE46AAFCF7B5";
        const string burrower = "1C96A39FFE37791F8AE07BD49A2230FF";

        static List<BoardSnapshot> Sweep(string uuid)
        {
            return
            [
                Frame(Pc(2, 4, 4, 9, 0, 0, range: 2, uuid: uuid), Pc(7, 4, 1, 5, 2, 1)),
                Frame(Pc(2, 3, 3, 9, 0, 0, range: 2, uuid: uuid), Pc(7, 4, 1, 5, 2, 1)),
                Frame(Pc(2, 3, 3, 9, 0, 0, range: 2, uuid: uuid), Pc(7, 4, 1, 4, 2, 1)),
                Frame(Pc(2, 3, 3, 9, 0, 0, acts: 1, range: 2, uuid: uuid), Pc(7, 4, 1, 4, 2, 1)),
            ];
        }

        Check("a Sweep's off-axis victim is read, and the shot is sent from the square it fired from",
              MoveDetector.DetectSequence(Sweep(ravager), 0)
                  is { Ok: true, Moves: [{ Attack: true, SrcX: 4, SrcY: 4, DstX: 3, DstY: 3,
                                           TargetX: 4, TargetY: 1, Facing: 0 }] });

        Check("the same board REFUSES for a machine that does not carry Sweep",
              MoveDetector.DetectSequence(Sweep(burrower), 0) is { Ok: false } noSweep &&
              noSweep.Refusal!.Contains("facing line"));

        static List<BoardSnapshot> Charge(string uuid, int launchX)
        {
            return
            [
                Frame(Pc(0, 2, 2, 4, 0, 0, range: 2, uuid: uuid), Pc(1, 1, 1, 4, 2, 1)),
                Frame(Pc(0, launchX, 2, 4, 0, 0, range: 2, uuid: uuid), Pc(1, 1, 1, 4, 2, 1)),
                Frame(Pc(0, 1, 0, 4, 0, 0, range: 2, uuid: uuid), Pc(1, 1, 1, 4, 2, 1)),
                Frame(Pc(0, 1, 0, 4, 0, 0, range: 2, uuid: uuid), Pc(1, 1, 1, 2, 2, 1)),
                Frame(Pc(0, 1, 0, 4, 0, 0, acts: 1, range: 2, uuid: uuid), Pc(1, 1, 1, 2, 0, 1)),
            ];
        }

        var charge = MoveDetector.DetectSequence(Charge(charger, 1), 0);
        Check("a Dash charge is read from the square it struck, not from where it finished",
              charge is { Ok: true, Moves: [{ Attack: true, SrcX: 2, SrcY: 2, DstX: 1, DstY: 2,
                                              TargetX: 1, TargetY: 1, Facing: 0 }] });

        Check("the square a charge carries the attacker to is NOT sent as a second move",
              charge.Ok && charge.Moves.Count == 1);

        Check("the same board REFUSES for a machine that does not carry Dash",
              MoveDetector.DetectSequence(Charge(burrower, 1), 0) is { Ok: false } noDash &&
              noDash.Refusal!.Contains("facing line"));

        Check("a charge REFUSES when the attacker never stood on the square the geometry names",
              MoveDetector.DetectSequence(Charge(charger, 3), 0) is { Ok: false } noLaunch &&
              noLaunch.Refusal!.Contains("facing line"));

        const string thunderjaw = "F9433C1448F8F052BD457978CD0BFEC5";
        Check("dash path: a square on the charge is hit, with or without Spread",
              MoveDetector.InDashPath(1, 2, 0, 2, false, 1, 3) && MoveDetector.InDashPath(1, 2, 0, 2, true, 1, 3));
        Check("dash path: a square beside the charge is hit only with Spread",
              MoveDetector.InDashPath(1, 2, 0, 2, true, 2, 3) && MoveDetector.InDashPath(1, 2, 0, 2, true, 0, 3)
              && !MoveDetector.InDashPath(1, 2, 0, 2, false, 2, 3));
        Check("dash path: beside the start or the end square is not licensed, nor past the range",
              !MoveDetector.InDashPath(1, 2, 0, 2, true, 2, 2) && !MoveDetector.InDashPath(1, 2, 0, 2, true, 2, 4)
              && !MoveDetector.InDashPath(1, 2, 0, 2, true, 1, 5));
        static List<BoardSnapshot> WideCharge(string uuid)
        {
            return
            [
                Frame(Pc(0, 1, 6, 10, 0, 0, range: 2, uuid: uuid), Pc(1, 2, 3, 4, 2, 1)),
                Frame(Pc(0, 1, 4, 10, 0, 0, range: 2, uuid: uuid), Pc(1, 2, 3, 4, 2, 1)),
                Frame(Pc(0, 1, 2, 10, 0, 0, range: 2, uuid: uuid), Pc(1, 2, 3, 4, 2, 1)),
                Frame(Pc(0, 1, 2, 10, 0, 0, range: 2, uuid: uuid), Pc(1, 2, 3, 2, 2, 1)),
                Frame(Pc(0, 1, 2, 10, 0, 0, acts: 1, range: 2, uuid: uuid), Pc(1, 2, 3, 2, 0, 1)),
            ];
        }

        var wideCharge = MoveDetector.DetectSequence(WideCharge(thunderjaw), 0);
        Check("a Spread machine's charge is read with its victim beside the path, from the square it struck",
              wideCharge is { Ok: true, Moves: [{ Attack: true, SrcX: 1, SrcY: 6, DstX: 1, DstY: 4,
                                            TargetX: 2, TargetY: 3, Facing: 0 }] });
        Check("the same charge REFUSES for a Dash machine without Spread",
              MoveDetector.DetectSequence(WideCharge(charger), 0) is { Ok: false } narrowCharge &&
              narrowCharge.Refusal!.Contains("facing line"));

        const string stormbird = "7875B4B22E79B8BF0996D4B74BCC0477";
        const string skydrifter = "36791A8338E8498ACD11B127EB75CF82";
        const string tremortusk = "23C2AA3CCB2680F418CA1D7E9F179E9D";
        var diver = Pc(4, 3, 5, 7, 0, 0, acts: 1, range: 3, uuid: stormbird);
        Check("dive arc: a Spread diver's victim may stand one square aside, at any range up to its own",
              MoveDetector.InDiveArc(diver, Pc(9, 2, 4, 5, 2, 1))
              && MoveDetector.InDiveArc(diver, Pc(9, 4, 3, 5, 2, 1))
              && MoveDetector.InDiveArc(diver, Pc(9, 3, 2, 5, 2, 1)));
        Check("dive arc: not two aside, not past the range, not behind, not without Spread, not for a Dash",
              !MoveDetector.InDiveArc(diver, Pc(9, 1, 4, 5, 2, 1))
              && !MoveDetector.InDiveArc(diver, Pc(9, 3, 1, 5, 2, 1))
              && !MoveDetector.InDiveArc(diver, Pc(9, 2, 6, 5, 2, 1))
              && !MoveDetector.InDiveArc(diver with { Uuid = skydrifter }, Pc(9, 2, 4, 5, 2, 1))
              && !MoveDetector.InDiveArc(diver with { Uuid = tremortusk, Range = 2 }, Pc(9, 2, 4, 5, 2, 1)));
        static List<BoardSnapshot> ArcOvercharge(string uuid)
        {
            return
            [
                Frame(Pc(4, 2, 6, 7, 0, 0, range: 3, uuid: uuid), Pc(9, 2, 4, 5, 2, 1, range: 2)),
                Frame(Pc(4, 3, 5, 7, 0, 0, acts: 1, range: 3, uuid: uuid), Pc(9, 2, 4, 5, 2, 1, range: 2)),
                Frame(Pc(4, 3, 5, 7, 0, 0, acts: 1, range: 3, uuid: uuid), Pc(9, 2, 4, 0, 2, 1, range: 2)),
                Frame(Pc(4, 3, 5, 7, 0, 0, acts: 1, bursts: 1, burst: true, range: 3, uuid: uuid)),
                Frame(Pc(4, 3, 5, 5, 0, 0, acts: 1, bursts: 1, burst: true, range: 3, uuid: uuid)),
            ];
        }

        Check("a Spread diver's Overcharge on a machine one square aside reads as the move and a burst attack in place",
              MoveDetector.DetectSequence(ArcOvercharge(stormbird), 0) is { Ok: true } arcRead &&
              arcRead.Moves.Count == 2 &&
              arcRead.Moves[0] is { Attack: false, SrcX: 2, SrcY: 6, DstX: 3, DstY: 5 } &&
              arcRead.Moves[1] is { Attack: true, SrcX: 3, SrcY: 5, DstX: 3, DstY: 5, TargetX: 2, TargetY: 4, Facing: 0, Burst: true });
        Check("the same Overcharge REFUSES for a Dive machine without Spread",
              MoveDetector.DetectSequence(ArcOvercharge(skydrifter), 0) is { Ok: false } lineDive &&
              lineDive.Refusal!.Contains("facing line"));

        var cancelledPreview = new List<BoardSnapshot>
        {
            Frame(Pc(3, 5, 7, 6, 0, 0), Pc(0, 7, 5, 8, 2, 1)),
            Frame(Pc(3, 7, 7, 6, 0, 0, acts: 1), Pc(0, 7, 5, 6, 2, 1))
                with { Act = new ActRecord(3, 5, 7, 7, 7, false) },
            Frame(Pc(3, 7, 7, 6, 3, 0, acts: 1), Pc(0, 7, 5, 6, 2, 1))
                with { Act = new ActRecord(3, 7, 7, 7, 6, true) },
            Frame(Pc(3, 7, 7, 6, 0, 0, acts: 1), Pc(0, 7, 5, 6, 2, 1))
                with { Act = new ActRecord(3, 7, 7, 7, 6, false) },
            Frame(Pc(3, 7, 7, 6, 0, 0), Pc(0, 7, 5, 6, 2, 1)),
        };
        var cancelled = MoveDetector.DetectSequence(cancelledPreview, 0);
        Check("a cancelled preview on the strike line is not read as a dive's firing square",
              cancelled is { Ok: true } && cancelled.Moves.All(m => m.AtkX == -1));

        var dyingTrade = new List<BoardSnapshot>
        {
            Frame(Pc(1, 3, 6, 10, 0, 0), Pc(5, 7, 5, 1, 0, 0), Pc(0, 3, 4, 4, 2, 1)),
            Frame(Pc(1, 3, 6, 10, 0, 0), Pc(5, 7, 5, 1, 0, 0), Pc(0, 3, 4, 2, 2, 1))
                with { Act = new ActRecord(1, 3, 6, 3, 6, true) },
            Frame(Pc(1, 5, 7, 10, 0, 0, acts: 1), Pc(5, 7, 5, 1, 0, 0), Pc(0, 3, 4, 2, 2, 1))
                with { Act = new ActRecord(1, 3, 6, 5, 7, false) },
            Frame(Pc(1, 5, 7, 10, 0, 0, acts: 1), Pc(5, 6, 4, 1, 0, 0), Pc(0, 3, 4, 2, 2, 1))
                with { Act = new ActRecord(5, 7, 5, 6, 4, true) },
            Frame(Pc(1, 5, 7, 10, 0, 0, acts: 1), Pc(5, 4, 4, 1, 0, 0), Pc(0, 3, 4, 2, 2, 1))
                with { Act = new ActRecord(5, 7, 5, 6, 4, true) },
            Frame(Pc(1, 5, 7, 10, 0, 0, acts: 1), Pc(5, 4, 4, 0, 0, 0), Pc(0, 3, 4, 1, 2, 1))
                with { Act = new ActRecord(5, 7, 5, 6, 4, true) },
            Frame(Pc(1, 5, 7, 10, 0, 0, acts: 1), Pc(5, 4, 4, 0, 0, 0), Pc(0, 3, 4, 0, 2, 1))
                with { Act = new ActRecord(5, 7, 5, 6, 4, true) },
            Frame(Pc(0, 5, 7, 10, 0, 0, acts: 1))
                with { Act = new ActRecord(-1, 4, 4, 6, 4, false) },
            Frame(Pc(0, 5, 7, 10, 0, 0)),
        };
        var trade = MoveDetector.DetectSequence(dyingTrade, 0);
        Check("a machine that dies in its own attack is reconstructed from the orphan record",
              trade is { Ok: true, Moves.Count: 3 } &&
              trade.Moves[2] is { Attack: true, SrcX: 7, SrcY: 5, DstX: 4, DstY: 4,
                                  TargetX: 3, TargetY: 4, Facing: 3, AtkX: 6, AtkY: 4 });

        Check("the recovery says so out loud rather than reading silently",
              trade.Warning?.Contains("died in its own attack") == true);

        var noRecord = dyingTrade.Select((s, i) => i == 7 ? s with { Act = null } : s).ToList();
        Check("a death with no committed record reconstructs nothing",
              MoveDetector.DetectSequence(noRecord, 0) is { Ok: true, Moves.Count: 2 });

        var twoVictims = dyingTrade.Select(s =>
        {
            var extraEnemy = Pc(3, 6, 6, s.Pieces.Count == 1 ? 3 : 4, 0, 1);
            return s.Pieces.Count == 0 ? s : s with { Pieces = [.. s.Pieces, extraEnemy] };
        }).ToList();
        Check("two enemies losing health in the death window refuse rather than guess",
              MoveDetector.DetectSequence(twoVictims, 0) is { Ok: false, Moves.Count: 0 });

        var sprintThenOvercharge = new List<BoardSnapshot>
        {
            Frame(Pc(5, 2, 5, 2, 0, 0), Pc(7, 3, 3, 8, 3, 1), Pc(0, 5, 4, 8, 3, 1)),
            Frame(Pc(5, 2, 4, 2, 0, 0), Pc(7, 3, 3, 8, 3, 1), Pc(0, 5, 4, 8, 3, 1))
                with { Act = new ActRecord(5, 2, 5, 2, 4, true) },
            Frame(Pc(5, 1, 3, 2, 0, 0, acts: 1), Pc(7, 3, 3, 8, 3, 1), Pc(0, 5, 4, 8, 3, 1))
                with { Act = new ActRecord(5, 2, 5, 1, 3, false) },
            Frame(Pc(5, 1, 3, 2, 1, 0, acts: 1), Pc(7, 3, 3, 8, 3, 1), Pc(0, 5, 4, 8, 3, 1))
                with { Act = new ActRecord(5, 1, 3, 1, 3, true) },
            Frame(Pc(5, 1, 3, 2, 1, 0, acts: 1), Pc(7, 3, 3, 4, 3, 1), Pc(0, 5, 4, 8, 3, 1))
                with { Act = new ActRecord(5, 1, 3, 1, 3, true) },
            Frame(Pc(7, 3, 3, 4, 3, 1), Pc(0, 5, 4, 8, 3, 1))
                with { Act = new ActRecord(-1, 1, 3, 1, 3, false) },
        };
        Check("a machine that sprinted and then died overcharging in place is read as a move and a burst attack",
              MoveDetector.DetectSequence(sprintThenOvercharge, 0) is { Ok: true } sprintRead
              && sprintRead.Moves is
              [
                  { SrcX: 2, SrcY: 5, DstX: 1, DstY: 3, Attack: false, Burst: false },
                  { SrcX: 1, SrcY: 3, DstX: 1, DstY: 3, Attack: true, TargetX: 3, TargetY: 3, Facing: 1, Burst: true },
              ]);

        var ramSuicide = new List<BoardSnapshot>
        {
            Frame(Pc(6, 4, 6, 5, 0, 0), Pc(2, 3, 6, 1, 0, 0), Pc(4, 4, 5, 2, 2, 1), Pc(0, 3, 4, 5, 2, 1)),
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 3, 6, 1, 0, 0), Pc(4, 4, 5, 2, 2, 1),
                  Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 3, 6, 1, 0, 0), Pc(4, 4, 5, 0, 2, 1),
                  Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 3, 6, 1, 0, 0), Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 3, 5, 1, 0, 0), Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(2, 3, 6, 3, 5, true) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 3, 5, 0, 0, 0), Pc(0, 3, 4, 4, 2, 1))
                with { Act = new ActRecord(2, 3, 6, 3, 5, true) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 3, 5, 0, 0, 0), Pc(0, 3, 3, 4, 2, 1))
                with { Act = new ActRecord(2, 3, 6, 3, 5, true) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(0, 3, 3, 4, 2, 1))
                with { Act = new ActRecord(-1, 3, 4, 3, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0), Pc(0, 3, 3, 4, 2, 1)),
        };
        var suicideRead = MoveDetector.DetectSequence(ramSuicide, 0);
        Check("a ram whose recoil kills the attacker is recovered instead of refused",
              suicideRead is { Ok: true, Moves.Count: 2 } &&
              suicideRead.Moves[1] is { Attack: true, SrcX: 3, SrcY: 6, DstX: 3, DstY: 5,
                                TargetX: 3, TargetY: 4, Facing: 0, AtkX: -1 } &&
              suicideRead.Warning?.Contains("died in its own attack") == true);

        Check("the recovered victim square is pre-push, not the pushed square",
              suicideRead is { Ok: true } && suicideRead.Moves[1] is not { TargetX: 3, TargetY: 3 });

        var ramNoRecord = ramSuicide.Select((s, i) => i == 7 ? s with { Act = null } : s).ToList();
        Check("a deferred refusal stands when no orphan record explains the victim",
              MoveDetector.DetectSequence(ramNoRecord, 0) is { Ok: false } ramNr &&
              ramNr.Refusal?.Contains("lost health") == true);

        var ramNoCandidate = new List<BoardSnapshot>
        {
            Frame(Pc(6, 4, 6, 5, 0, 0), Pc(2, 1, 7, 1, 0, 0), Pc(4, 4, 5, 2, 2, 1), Pc(0, 3, 4, 5, 2, 1)),
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 1, 7, 1, 0, 0), Pc(4, 4, 5, 2, 2, 1),
                  Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 1, 7, 1, 0, 0), Pc(4, 4, 5, 0, 2, 1),
                  Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 1, 7, 1, 0, 0), Pc(0, 3, 4, 5, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 1, 7, 1, 0, 0), Pc(0, 3, 4, 4, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0, acts: 1), Pc(2, 1, 7, 1, 0, 0), Pc(0, 3, 3, 4, 2, 1))
                with { Act = new ActRecord(6, 4, 6, 5, 5, false) },
            Frame(Pc(6, 5, 5, 5, 3, 0), Pc(2, 1, 7, 1, 0, 0), Pc(0, 3, 3, 4, 2, 1)),
        };
        Check("an unattributable drop with no dying attacker still refuses on the spot",
              MoveDetector.DetectSequence(ramNoCandidate, 0) is { Ok: false } ramNc &&
              ramNc.Refusal?.Contains("lost health") == true);

        Check("the firing square rotates into the peer's frame",
              new Move { DstX = 4, DstY = 2, AtkX = 4, AtkY = 4, Attack = true }.Rotated()
                  is { AtkX: 3, AtkY: 3, DstX: 3, DstY: 5 });
        Check("an unset firing square survives rotation unset",
              new Move { DstX = 4, DstY = 2, Attack = true }.Rotated() is { AtkX: -1, AtkY: -1 });

        var diveThenBurst = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 5, 4, 0, 0), Pc(1, 4, 1, 6, 2, 1)),
            Frame(Pc(0, 4, 2, 4, 0, 0, acts: 1), Pc(1, 4, 1, 4, 2, 1))
                with { Act = new ActRecord(0, 4, 2, 4, 4, false) },
            Frame(Pc(0, 4, 2, 4, 0, 0, acts: 2, bursts: 1), Pc(1, 4, 1, 2, 2, 1))
                with { Act = new ActRecord(0, 4, 2, 4, 4, false) },
        };
        var dtb = MoveDetector.DetectSequence(diveThenBurst, 0);
        Check("a dive then an Overcharge in place reads as two attacks",
              dtb.Ok && dtb.Moves.Count == 2 && dtb.Moves.All(m => m.Attack));
        Check("the dive carries the firing square",
              dtb.Ok && dtb.Moves[0] is { AtkX: 4, AtkY: 4 });
        Check("the Overcharge made in place carries NO firing square, so the peer strikes from where it stands",
              dtb.Ok && dtb.Moves[1] is { AtkX: -1, AtkY: -1 } second &&
              second.StrikeFrom == (second.DstX, second.DstY));

        var freed = new List<BoardSnapshot>
        {
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 1, 3, 2, 0), Pc(3, 1, 1, 1, 0, 1)),
            Frame(Pc(0, 1, 0, 4, 2, 0, acts: 1), Pc(1, 6, 1, 3, 2, 0)),
            Frame(Pc(0, 1, 0, 4, 2, 0, acts: 1), Pc(1, 3, 1, 3, 2, 0)),
            Frame(Pc(0, 1, 0, 4, 2, 0, acts: 1), Pc(1, 1, 1, 3, 2, 0, acts: 1)),
        };
        var s2 = MoveDetector.DetectSequence(freed, 0);
        Check("a kill that frees a square is ordered BEFORE the move onto it",
              s2.Ok && s2.Moves.Count == 2 &&
              s2.Moves[0] is { Attack: true, SrcX: 1, SrcY: 0, TargetX: 1, TargetY: 1, Facing: 2 } &&
              s2.Moves[1] is { SrcX: 6, SrcY: 1, DstX: 1, DstY: 1 });

        var s2Flat = MoveDetector.Detect(freed[0], freed[^1], 0);
        Check("the two-endpoint reading of that same turn gets the order backwards",
              s2Flat.Ok && s2Flat.Moves.Count == 2 && !s2Flat.Moves[0].Attack);

        var over = new List<BoardSnapshot>
        {
            Frame(Pc(1, 0, 7, 4, 0, 0), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 6, 4, 0, 0), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 5, 4, 0, 0), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 7, 4, 0, 0), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 5, 4, 0, 0, acts: 1), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 4, 4, 0, 0, acts: 1), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 4, 0, 0, acts: 1), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 5, 4, 0, 0, acts: 1), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0, acts: 1, bursts: 1, burst: true), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0, acts: 1, bursts: 1, burst: true), Pc(3, 0, 6, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0, acts: 1, bursts: 1, burst: true), Pc(3, 0, 5, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0, acts: 1, bursts: 1, burst: true), Pc(3, 1, 7, 4, 0, 0), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0, acts: 1, bursts: 1, burst: true), Pc(3, 0, 5, 4, 0, 0, acts: 1), Pc(0, 6, 4, 4, 3, 1), Pc(2, 5, 4, 4, 3, 1)),
        };
        var s3 = MoveDetector.DetectSequence(over, 0);
        Check("a real captured turn reads as its three actions, in order",
              s3.Ok && s3.Moves.Count == 3 &&
              s3.Moves[0] is { SrcX: 0, SrcY: 7, DstX: 0, DstY: 5, Burst: false } &&
              s3.Moves[2] is { SrcX: 1, SrcY: 7, DstX: 0, DstY: 5, Burst: false });

        Check("an Overcharge that moves the action counter NOT AT ALL is still read as an action",
              s3.Ok && s3.Moves.Count == 3 &&
              s3.Moves[1] is { SrcX: 0, SrcY: 5, DstX: 0, DstY: 3, Burst: true });

        Check("neither the preview bounces nor the second machine taking a vacated square adds an action",
              s3.Ok && s3.Moves.Count == 3);

        var ram = new List<BoardSnapshot>
        {
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 2, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 2, 2, 0, 1)),
            Frame(Pc(1, 6, 2, 3, 2, 0, acts: 1), Pc(2, 6, 3, 2, 0, 1)),
        };
        var s4 = MoveDetector.DetectSequence(ram, 0);
        Check("Ram's push is one attack, and the attacker's advance is not sent as a move",
              s4.Ok && s4.Moves is [{ Attack: true, SrcX: 6, SrcY: 1, TargetX: 6, TargetY: 2, Facing: 2 }]);

        var hitAndRun = new List<BoardSnapshot>
        {
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 2, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 2, 2, 0, 1)),
            Frame(Pc(1, 4, 1, 3, 3, 0, acts: 1), Pc(2, 6, 2, 2, 0, 1)),
        };
        var s5 = MoveDetector.DetectSequence(hitAndRun, 0);
        Check("attack-then-reposition becomes the strike and then the move, in that order",
              s5.Ok && s5.Moves.Count == 2 &&
              s5.Moves[0] is { Attack: true, TargetX: 6, TargetY: 2 } &&
              s5.Moves[1] is { SrcX: 6, SrcY: 1, DstX: 4, DstY: 1, Facing: 3 });

        var ambiguous = new List<BoardSnapshot>
        {
            Frame(Pc(1, 0, 3, 2, 0, 0), Pc(3, 0, 5, 4, 0, 0), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0), Pc(3, 2, 5, 4, 0, 0), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0), Pc(3, 3, 5, 4, 0, 0, acts: 1), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 0, 3, 2, 0, 0), Pc(3, 4, 4, 4, 0, 0, acts: 1), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 2, 3, 2, 0, 0), Pc(3, 3, 5, 4, 0, 0, acts: 1), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 3, 3, 2, 2, 0, acts: 1), Pc(3, 3, 5, 4, 0, 0, acts: 1), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 3, 3, 2, 2, 0, acts: 1), Pc(3, 3, 4, 4, 0, 0, acts: 1), Pc(0, 3, 4, 4, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 3, 3, 2, 2, 0, acts: 1), Pc(3, 3, 5, 4, 0, 0, acts: 1), Pc(0, 3, 4, 3, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
            Frame(Pc(1, 3, 3, 2, 2, 0, acts: 1), Pc(3, 3, 5, 2, 0, 0, acts: 1, bursts: 1, burst: true), Pc(0, 3, 4, 3, 3, 1), Pc(2, 2, 4, 4, 3, 1)),
        };
        var s7 = MoveDetector.DetectSequence(ambiguous, 0);
        Check("with two machines adjacent and facing one victim, the window picks the real attacker",
              s7.Ok && s7.Moves.Count == 3 &&
              s7.Moves[1] is { Attack: false, SrcX: 0, SrcY: 3, DstX: 3, DstY: 3 } &&
              s7.Moves[2] is { Attack: true, SrcX: 3, SrcY: 5, TargetX: 3, TargetY: 4, Facing: 0, Burst: true });

        var killMidTurn = new List<BoardSnapshot>
        {
            Frame(Pc(0, 3, 4, 1, 3, 1), Pc(1, 3, 3, 4, 2, 0), Pc(2, 2, 4, 4, 3, 1), Pc(3, 5, 5, 4, 0, 0)),
            Frame(Pc(0, 3, 4, 0, 3, 1), Pc(1, 3, 3, 4, 2, 0), Pc(2, 2, 4, 4, 3, 1), Pc(3, 5, 5, 4, 0, 0)),
            Frame(Pc(0, 3, 3, 4, 2, 0), Pc(1, 2, 4, 4, 3, 1), Pc(2, 5, 5, 4, 0, 0)),
            Frame(Pc(0, 3, 3, 4, 2, 0, acts: 1), Pc(1, 2, 4, 4, 3, 1), Pc(2, 5, 5, 4, 0, 0)),
            Frame(Pc(0, 3, 3, 4, 2, 0, acts: 1), Pc(1, 2, 4, 4, 3, 1), Pc(2, 4, 5, 4, 0, 0)),
            Frame(Pc(0, 3, 3, 4, 2, 0, acts: 1), Pc(1, 2, 4, 4, 3, 1), Pc(2, 4, 4, 4, 0, 0, acts: 1)),
        };
        var s8 = MoveDetector.DetectSequence(killMidTurn, 0);
        Check("a kill that renumbers the piece list mid-turn is read, not fabricated around",
              s8.Ok && s8.Moves.Count == 2 &&
              s8.Moves[0] is { Attack: true, SrcX: 3, SrcY: 3, TargetX: 3, TargetY: 4, Facing: 2 } &&
              s8.Moves[1] is { Attack: false, SrcX: 5, SrcY: 5, DstX: 4, DstY: 4 });

        var badRenumber = new List<BoardSnapshot>
        {
            Frame(Pc(0, 3, 4, 1, 3, 1), Pc(1, 3, 3, 4, 2, 0), Pc(2, 2, 4, 4, 3, 1), Pc(3, 5, 5, 4, 0, 0)),
            Frame(Pc(0, 3, 3, 4, 2, 0), Pc(1, 2, 4, 4, 3, 1), Pc(2, 6, 6, 4, 0, 0)),
        };
        Check("a renumbering whose survivors cannot be matched by square is refused",
              MoveDetector.DetectSequence(badRenumber, 0) is { Ok: false } sk && sk.Refusal!.Contains("renumbered"));

        var closingBlow = new List<BoardSnapshot>
        {
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 0, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0)),
        };
        var s9 = MoveDetector.DetectSequence(closingBlow, 0);
        Check("the match-ending strike is read even though its counter tick is never sampled",
              s9.Ok && s9.Moves.Count == 1 &&
              s9.Moves[0] is { Attack: true, SrcX: 2, SrcY: 4, DstX: 2, DstY: 4, TargetX: 2, TargetY: 3, Facing: 0 });
        Check("reading a turn with no tick says so, so a later halted hash is legible",
              s9.Warning is not null && s9.Warning.Contains("no counter tick"));

        var sprayEndsIt = new List<BoardSnapshot>
        {
            Frame(Pc(0, 2, 4, 1, 0, 0, skill: 15, range: 2), Pc(1, 2, 3, 1, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0, skill: 15, range: 2), Pc(1, 2, 3, 0, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0, skill: 15, range: 2)),
        };
        Check("a match ending because our own Spray killed the last enemy invents no attack",
              MoveDetector.DetectSequence(sprayEndsIt, 0) is { Ok: false } sse &&
              sse.Refusal!.Contains("no machine of ours acted"));

        var sprayPlusBlow = new List<BoardSnapshot>
        {
            Frame(Pc(0, 2, 4, 1, 0, 0, skill: 15, range: 2), Pc(1, 2, 3, 3, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0, skill: 15, range: 2), Pc(1, 2, 3, 0, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0, skill: 15, range: 2)),
        };
        var spb = MoveDetector.DetectSequence(sprayPlusBlow, 0);
        Check("a match-ending strike is still read when Spray explains only part of the damage",
              spb.Ok && spb.Moves.Count == 1 &&
              spb.Moves[0] is { Attack: true, SrcX: 2, SrcY: 4, TargetX: 2, TargetY: 3 });

        var notOver = new List<BoardSnapshot>
        {
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1), Pc(2, 6, 6, 4, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 0, 2, 1), Pc(2, 6, 6, 4, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 6, 6, 4, 2, 1)),
        };
        Check("a kill with no tick is still refused while the enemy has pieces left",
              MoveDetector.DetectSequence(notOver, 0) is { Ok: false } sn && sn.Refusal!.Contains("no machine of ours acted"));

        var placedSoFar = new List<(int X, int Y)> { (3, 6) };
        var placeBefore = Frame(Pc(0, 3, 6, 5, 0, 0), Pc(1, 4, 1, 5, 2, 1), Pc(2, 3, 7, 5, 0, 0))
            with { Placing = new PlacingState(1, 1, 0) };
        var placeAfter = Frame(Pc(0, 3, 6, 5, 0, 0), Pc(1, 4, 1, 5, 2, 1), Pc(2, 4, 6, 5, 1, 0))
            with { Placing = new PlacingState(1, 1, 1) };
        Check("a committed placement is read with its square, facing and index",
              TurnBoundary.CommittedPlacements(placeBefore, placeAfter, 0, placedSoFar) is
                  [{ Index: 1, Square.X: 4, Square.Y: 6, Square.Dir: 1 }]);

        Check("the last placement commits when the phase itself ends",
              TurnBoundary.CommittedPlacements(placeBefore,
                                              Frame(Pc(0, 3, 6, 5, 0, 0), Pc(1, 4, 1, 5, 2, 1),
                                                    Pc(2, 4, 6, 5, 1, 0)),
                                              0, placedSoFar) is
                  [{ Index: 1, Square.X: 4, Square.Y: 6, Square.Dir: 1 }]);

        Check("a placing state that did not read, or names no seat, proves no placement",
              TurnBoundary.CommittedPlacements(placeBefore, placeAfter with { Placing = null, PlacingUnreadable = true },
                                              0, placedSoFar) is []
              && TurnBoundary.CommittedPlacements(placeBefore, placeAfter with { PlacingUnreadable = false, Placing = new PlacingState(1, 1, -1) },
                                                 0, placedSoFar) is []
              && TurnBoundary.CommittedPlacements(placeBefore with { PlacingUnreadable = true }, placeAfter, 0, placedSoFar) is []
              && SnapshotJson.TryParse(AsProbeJson(placeAfter)[..^1] + ",\"placing\":{\"unreadable\":true}}",
                                       out var unreadPlacing, out _)
              && unreadPlacing.PlacingUnreadable && unreadPlacing.Placing is null);

        Check("the cursor wandering with no commit places nothing",
              TurnBoundary.CommittedPlacements(placeBefore,
                                              Frame(Pc(0, 3, 6, 5, 0, 0), Pc(1, 4, 1, 5, 2, 1),
                                                    Pc(2, 0, 7, 5, 0, 0))
                                                  with { Placing = new PlacingState(1, 1, 0) },
                                              0, placedSoFar) is []);

        Check("the opponent's commit is not read as ours",
              TurnBoundary.CommittedPlacements(Frame(Pc(0, 3, 6, 5, 0, 0), Pc(1, 4, 1, 5, 2, 1))
                                                  with { Placing = new PlacingState(1, 1, 1) },
                                              Frame(Pc(0, 3, 6, 5, 0, 0), Pc(1, 4, 1, 5, 2, 1),
                                                    Pc(3, 3, 1, 5, 2, 1))
                                                  with { Placing = new PlacingState(1, 1, 0) },
                                              0, placedSoFar) is []);

        Check("a snapshot without placing state places nothing",
              TurnBoundary.CommittedPlacements(Frame(Pc(0, 3, 6, 5, 0, 0)),
                                              Frame(Pc(0, 3, 6, 5, 0, 0), Pc(2, 4, 6, 5, 1, 0)),
                                              0, []) is []);

        Check("D-233: every committed piece at the commit edge is sent, in the order they spawned",
              TurnBoundary.CommittedPlacements(Frame(Pc(0, 3, 6, 5, 0, 0))
                                                  with { Placing = new PlacingState(2, 2, 0) },
                                              Frame(Pc(0, 3, 6, 5, 0, 0), Pc(3, 5, 6, 5, 1, 0),
                                                    Pc(2, 4, 6, 5, 1, 0))
                                                  with { Placing = new PlacingState(2, 2, 1) },
                                              0, []) is
                  [{ Index: 0, Square.X: 3, Square.Y: 6 }, { Index: 1, Square.X: 4, Square.Y: 6 },
                   { Index: 2, Square.X: 5, Square.Y: 6 }]);

        var tailPlaced = new List<(int X, int Y)> { (0, 7), (3, 7) };
        var tailBefore = Frame(Pc(0, 0, 7, 4, 0, 0), Pc(1, 3, 7, 4, 0, 0), Pc(2, 3, 6, 3, 0, 0))
            with { Placing = new PlacingState(3, 0, 0) };
        var tailAfter = Frame(Pc(0, 0, 7, 4, 0, 0), Pc(1, 3, 7, 4, 0, 0), Pc(2, 3, 6, 3, 0, 0),
                              Pc(3, 4, 7, 5, 0, 0))
            with { Placing = new PlacingState(2, 0, 0) };
        Check("a placement commits on the next selection when the phase never hands over",
              TurnBoundary.CommittedPlacements(tailBefore, tailAfter, 0, tailPlaced) is
                  [{ Index: 2, Square.X: 3, Square.Y: 6, Square.Dir: 0 }]);

        Check("the newly selected cursor is not read as the commit",
              !TurnBoundary.CommittedPlacements(tailBefore, tailAfter, 0, tailPlaced)
                  .Any(p => p.Square is { X: 4, Y: 7 }));

        Check("a selection with nothing pending places nothing",
              TurnBoundary.CommittedPlacements(
                  Frame(Pc(0, 0, 7, 4, 0, 0)) with { Placing = new PlacingState(2, 2, 0) },
                  Frame(Pc(0, 0, 7, 4, 0, 0), Pc(1, 4, 7, 5, 0, 0)) with { Placing = new PlacingState(1, 2, 0) },
                  0, [(0, 7)]) is []);

        Check("D-233: two pending pieces at a selection are both sent, and the new cursor is not",
              TurnBoundary.CommittedPlacements(
                  Frame(Pc(0, 0, 7, 4, 0, 0), Pc(1, 3, 6, 4, 0, 0), Pc(2, 2, 6, 4, 0, 0))
                      with { Placing = new PlacingState(3, 0, 0) },
                  Frame(Pc(0, 0, 7, 4, 0, 0), Pc(1, 3, 6, 4, 0, 0), Pc(2, 2, 6, 4, 0, 0),
                        Pc(3, 4, 7, 5, 0, 0)) with { Placing = new PlacingState(2, 0, 0) },
                  0, [(0, 7)]) is
                  [{ Index: 1, Square.X: 3, Square.Y: 6 }, { Index: 2, Square.X: 2, Square.Y: 6 }]);

        Check("a selection edge with no piece identity places nothing",
              TurnBoundary.CommittedPlacements(
                  Frame(Pc(-1, 0, 7, 4, 0, 0), Pc(-1, 3, 6, 4, 0, 0))
                      with { Placing = new PlacingState(3, 0, 0) },
                  Frame(Pc(-1, 0, 7, 4, 0, 0), Pc(-1, 3, 6, 4, 0, 0))
                      with { Placing = new PlacingState(2, 0, 0) },
                  0, [(0, 7)]) is []);

        Check("the left count rising places nothing",
              TurnBoundary.CommittedPlacements(
                  tailAfter with { Placing = new PlacingState(2, 0, 0) },
                  tailBefore with { Placing = new PlacingState(3, 0, 0) },
                  0, tailPlaced) is []);

        var army4 = new List<string> { "AAAA", "BBBB", "CCCC", "DDDD" };
        Check("a machine placed out of army order is sent to ITS army slot, not the placement step",
              TurnBoundary.ArmySlotFor("DDDD", army4, [0, 1], fallback: 2, out _) == 3);
        Check("the next machine then takes the slot that was skipped",
              TurnBoundary.ArmySlotFor("CCCC", army4, [0, 1, 3], fallback: 3, out _) == 2);
        Check("placing in army order is unaffected, slot equals step",
              TurnBoundary.ArmySlotFor("AAAA", army4, [], fallback: 0, out _) == 0);

        var dupArmy = new List<string> { "AAAA", "AAAA", "BBBB" };
        Check("a duplicated machine takes the first free slot for it",
              TurnBoundary.ArmySlotFor("AAAA", dupArmy, [0], fallback: 1, out _) == 1);

        Check("no machine id falls back to the placement step and complains",
              TurnBoundary.ArmySlotFor("", army4, [], fallback: 2, out var noIdSaid) == 2 &&
              noIdSaid is not null);
        Check("no known army falls back to the placement step and complains",
              TurnBoundary.ArmySlotFor("AAAA", [], [], fallback: 2, out var noArmySaid) == 2 &&
              noArmySaid is not null);
        Check("a machine that is not in the army complains rather than guessing a slot",
              TurnBoundary.ArmySlotFor("ZZZZ", army4, [], fallback: 1, out var strangerSaid) == 1 &&
              strangerSaid is not null);

        Check("the k-th square to arrive is written into record k whatever machine it names",
              TurnBoundary.PlacementRecordFor(0) == 0 && TurnBoundary.PlacementRecordFor(1) == 1
              && TurnBoundary.PlacementRecordFor(3) == 3);

        Check("the move-bounds patch is wanted for every shape but 8x8",
              !TurnBoundary.MoveBoundsPatchWanted(8, 8) && TurnBoundary.MoveBoundsPatchWanted(5, 8)
              && TurnBoundary.MoveBoundsPatchWanted(8, 5) && TurnBoundary.MoveBoundsPatchWanted(6, 6));

        Check("D-244: the closing hash is sent when the match-over sample turned auto off before the final turn's injection returned",
              SendsAppliedHash(auto: false, halted: false, final: true, peerEndedMatch: true, autoOffByUser: false));

        Check("D-244: a halted match, a player's auto off and a non-final turn with auto off send no hash",
              !SendsAppliedHash(auto: true, halted: true, final: true, peerEndedMatch: true, autoOffByUser: false)
              && !SendsAppliedHash(auto: false, halted: false, final: true, peerEndedMatch: true, autoOffByUser: true)
              && !SendsAppliedHash(auto: false, halted: false, final: false, peerEndedMatch: false, autoOffByUser: false)
              && SendsAppliedHash(auto: true, halted: false, final: false, peerEndedMatch: false, autoOffByUser: false));

        Check("a match whose first sample finds the last match's move-bounds holder alive waits, then applies",
              MoveBoundsStep(firstSample: true, pending: false, holderAlive: true) == BoundsStep.Wait
              && MoveBoundsStep(firstSample: false, pending: true, holderAlive: true) == BoundsStep.Wait
              && MoveBoundsStep(firstSample: false, pending: true, holderAlive: false) == BoundsStep.Apply
              && MoveBoundsStep(firstSample: true, pending: false, holderAlive: false) == BoundsStep.Apply
              && MoveBoundsStep(firstSample: false, pending: false, holderAlive: true) == BoundsStep.Nothing
              && MoveBoundsStep(firstSample: false, pending: false, holderAlive: false) == BoundsStep.Nothing);

        Check("--play puts the coin flip back at its start, with the move bounds and the commit ring",
              StartClears.Any(c => c.SequenceEqual(["--force-first", "clear", "--yes"]))
              && StartClears.Any(c => c.SequenceEqual(["--patch-move-bounds", "--clear"]))
              && StartClears.Any(c => c.SequenceEqual(["--commit-ring", "--clear"])));

        Check("live-probe's answer that it does not know the game build becomes the launcher's refusal line, " +
              "keeping the build for the other PC, and no other answer does",
              ProbeRefusedBuild(ProbeUnknownBuild) is { } unknownLine
              && unknownLine.StartsWith("REFUSED: game build not supported: ", StringComparison.Ordinal)
              && !unknownLine.Contains(';')
              && new[] { 0, 1, 2, 5, 6, -1 }.All(code => ProbeRefusedBuild(code) is null)
              && BuildFrom(ProbeUnknownBuild, "667B1778-949F000") == "667B1778-949F000"
              && BuildFrom(0, "667B1777-949F000") == "667B1777-949F000"
              && BuildFrom(1, "HorizonForbiddenWest.exe is not running.") == "unknown"
              && BuildFrom(0, "") == "unknown");

        Check("Set up the match answers a game build this Strikers does not know with its own refusal, even when " +
              "the old build's offsets read as a live match, and says the game is still in a match only on a known one",
              SetupRefusal(ProbeUnknownBuild, 0) == UnknownBuildRefusal
              && SetupRefusal(ProbeUnknownBuild, 2) == UnknownBuildRefusal
              && SetupRefusal(0, 0) == StillInMatch
              && SetupRefusal(-1, 0) == StillInMatch
              && SetupRefusal(0, 2) is null
              && SetupRefusal(1, 1) is null);

        Check("a committed placement reports which machine it was",
              TurnBoundary.CommittedPlacements(
                  Frame(Pc(0, 3, 6, 5, 0, 0), Pc(2, 3, 7, 5, 0, 0) with { Uuid = "DDDD" })
                      with { Placing = new PlacingState(1, 1, 0) },
                  Frame(Pc(0, 3, 6, 5, 0, 0), Pc(2, 4, 6, 5, 1, 0) with { Uuid = "DDDD" })
                      with { Placing = new PlacingState(1, 1, 1) },
                  0, new List<(int X, int Y)> { (3, 6) }) is [{ Uuid: "DDDD" }]);

        Check("D-233: a machine committed before the watcher's first sample is sent from that sample",
              TurnBoundary.CommittedPlacements(null,
                  Frame(Pc(0, 2, 3, 5, 0, 0) with { Uuid = "AAAA" }) with { Placing = new PlacingState(1, 2, 1) },
                  0, []) is [{ Index: 0, Square.X: 2, Square.Y: 3, Uuid: "AAAA" }]);

        Check("D-233: a first sample with the phase ours sends nothing, the piece may be the cursor",
              TurnBoundary.CommittedPlacements(null,
                  Frame(Pc(0, 2, 4, 5, 0, 0)) with { Placing = new PlacingState(1, 2, 0) }, 0, []) is []);
        Check("D-233: a first sample with no placing state sends nothing",
              TurnBoundary.CommittedPlacements(null, Frame(Pc(0, 2, 4, 5, 0, 0)), 0, []) is []);

        Check("D-233: a placement waits until auto is on, and is written directly after",
              PlacementWaits(auto: false, autoOffByUser: false, held: 0)
              && !PlacementWaits(auto: true, autoOffByUser: false, held: 0));
        Check("D-233: once one is waiting the next waits behind it, so arrival order is write order",
              PlacementWaits(auto: true, autoOffByUser: false, held: 1));
        Check("D-233: auto turned off by hand still writes as it always did",
              !PlacementWaits(auto: false, autoOffByUser: true, held: 0));

        Check("a placement rotates 180 degrees, square and facing together",
              new Placement { X = 4, Y = 6, Dir = 1 }.Rotated() is { X: 3, Y: 1, Dir: 3 });
        Check("rotating a placement twice returns it unchanged",
              new Placement { X = 4, Y = 6, Dir = 1 }.Rotated().Rotated() is { X: 4, Y: 6, Dir: 1 });

        Check("a placement rotates in the board's shape, not in a default 8x8",
              new Placement { X = 3, Y = 4, Dir = 0 }.Rotated(8, 5) is { X: 4, Y: 0, Dir: 2 });

        Check("rotating a placement twice in a rectangle returns it unchanged",
              new Placement { X = 3, Y = 4, Dir = 0 }.Rotated(8, 5).Rotated(8, 5)
                  is { X: 3, Y: 4, Dir: 0 });

        Check("a move rotates in the board's shape too",
              new Move { SrcX = 1, SrcY = 4, DstX = 2, DstY = 3, Facing = 0 }.Rotated(8, 5)
                  is { SrcX: 6, SrcY: 0, DstX: 5, DstY: 1, Facing: 2 });

        Check("an attack's target rotates in the board's shape",
              new Move { DstX = 0, DstY = 0, TargetX = 7, TargetY = 4, Attack = true }.Rotated(8, 5)
                  is { DstX: 7, DstY: 4, TargetX: 0, TargetY: 0 });

        var rectHost = new Lobby("B", isHost: true);
        rectHost.ChooseBoard([.. new int[40]], Preset.RuleNotSet, Preset.RuleNotSet, 5, 8, 2);
        rectHost.SetArmy(["1C96A39FFE37791F8AE07BD49A2230FF"]);
        rectHost.Local.Placements = [new Placement { X = 3, Y = 7, Dir = 0 }];
        var rectGuest = new Lobby("B", isHost: false);
        rectGuest.OnSetup(rectHost.Setup());

        Check("the lobby rotates the peer's squares in the board it was told about",
              rectGuest.Refusal is null && rectGuest.Remote is { } peerSeat && peerSeat.Placements.Count == 1 &&
              peerSeat.Placements[0] is { X: 1, Y: 0 });

        var tailTracker = new TurnTracker(0);
        tailTracker.Push(Frame(Pc(0, 2, 4, 5, 0, 0), Pc(1, 2, 2, 5, 2, 1, acts: 1), Pc(2, 6, 6, 5, 0, 1)));
        tailTracker.Push(Frame(Pc(0, 2, 4, 5, 0, 0), Pc(1, 2, 2, 3, 2, 1, acts: 1), Pc(2, 6, 6, 5, 0, 1)));
        tailTracker.Push(Frame(Pc(0, 2, 4, 5, 0, 0), Pc(1, 2, 2, 3, 2, 1), Pc(2, 6, 6, 5, 0, 1)));
        tailTracker.Push(Frame(Pc(0, 2, 4, 5, 0, 0), Pc(1, 2, 3, 3, 2, 1), Pc(2, 6, 6, 5, 0, 1)));

        var tail = tailTracker.Flush();
        Check("a flushed tail starts at the peer's turn end, not at the top of the buffer",
              tail is { Count: 2 });
        Check("the peer's own Overcharge cost falls OUTSIDE the flushed tail",
              tail is not null && tail.All(s => s.Pieces.Single(p => p.Idx == 1).Health == 3));

        var noEdge = new TurnTracker(0);
        noEdge.Push(Frame(Pc(0, 2, 4, 5, 0, 0), Pc(1, 2, 2, 5, 2, 1)));
        noEdge.Push(Frame(Pc(0, 2, 4, 5, 0, 0), Pc(1, 2, 2, 3, 2, 1)));
        Check("a tail with no peer turn-end in it is handed over whole, as before",
              noEdge.Flush() is { Count: 2 });

        var pointsEnd = notOver
            .Select(s => s with { Match = new MatchState(Over: true, Winner: 0, 2, 0, 2) })
            .ToList();
        var pe = MoveDetector.DetectSequence(pointsEnd, 0);
        Check("a match-ending strike IS read when the game says the match ended on points",
              pe.Ok && pe.Moves.Count == 1 &&
              pe.Moves[0] is { Attack: true, SrcX: 2, SrcY: 4, TargetX: 2, TargetY: 3 });

        var pointsNotOver = notOver
            .Select(s => s with { Match = new MatchState(Over: false, Winner: -1, 1, 0, 2) })
            .ToList();
        Check("the same board with the flag CLEAR still refuses",
              MoveDetector.DetectSequence(pointsNotOver, 0) is { Ok: false } pno &&
              pno.Refusal!.Contains("no machine of ours acted"));

        var twoDeaths = new List<BoardSnapshot>
        {
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1), Pc(2, 2, 5, 1, 0, 1), Pc(3, 6, 6, 4, 2, 1)),
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 6, 6, 4, 2, 1)),
        }
            .Select(s => s with { Match = new MatchState(Over: true, Winner: 0, 2, 0, 2) })
            .ToList();
        Check("two enemy deaths in one untracked slice still refuse",
              MoveDetector.DetectSequence(twoDeaths, 0) is { Ok: false });

        var twoCandidates = new List<BoardSnapshot>
        {
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1), Pc(2, 1, 3, 4, 1, 0)),
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 0, 2, 1), Pc(2, 1, 3, 4, 1, 0)),
            Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 1, 3, 4, 1, 0)),
        };
        Check("a match-ending kill with two machines of ours able to have struck is refused",
              MoveDetector.DetectSequence(twoCandidates, 0) is { Ok: false } s2c &&
              s2c.Refusal!.Contains("no machine of ours acted"));

        var twoKills = new List<BoardSnapshot>
        {
            Frame(Pc(0, 3, 3, 4, 0, 0), Pc(1, 3, 1, 3, 0, 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1)),
            Frame(Pc(0, 3, 2, 4, 0, 0), Pc(1, 3, 1, 3, 0, 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1)),
            Frame(Pc(0, 3, 2, 4, 0, 0), Pc(1, 3, 1, 0, 0, 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1)),
            Frame(Pc(0, 3, 2, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1)),
            Frame(Pc(0, 3, 2, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 0, 0, 1)),
        };
        var tk = MoveDetector.DetectSequence(twoKills, 0);
        Check("a turn that kills twice, the last blow untracked, reads both actions",
              tk.Ok && tk.Moves.Count == 2 &&
              tk.Moves[0] is { Attack: true, SrcX: 3, SrcY: 3, DstX: 3, DstY: 2, TargetX: 3, TargetY: 1 } &&
              tk.Moves[1] is { Attack: true, SrcX: 4, SrcY: 2, DstX: 4, DstY: 2, TargetX: 4, TargetY: 1 });
        Check("the untracked half of a two-kill turn says it had no tick",
              tk.Warning is not null && tk.Warning.Contains("no counter tick"));

        var secondBlow = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 4, 3, 3, 0), Pc(1, 5, 5, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 2, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 2, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 0, 0, 1)),
        };
        var sb = MoveDetector.DetectSequence(secondBlow, 0);
        Check("a traceless second blow that ends the match reads as a stationary burst attack",
              sb.Ok && sb.Moves.Count == 2 &&
              sb.Moves[0] is { Attack: true, SrcX: 5, SrcY: 4, DstX: 4, DstY: 5, TargetX: 5, TargetY: 5, Burst: false } &&
              sb.Moves[1] is { Attack: true, SrcX: 4, SrcY: 5, DstX: 4, DstY: 5, TargetX: 5, TargetY: 5, Facing: 1, Burst: true });
        Check("the inferred second blow says what it is, so a halted hash stays legible",
              sb.Warning is not null && sb.Warning.Contains("second blow"));

        var sbNotOver = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 4, 3, 3, 0), Pc(1, 5, 5, 4, 0, 1), Pc(2, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 4, 0, 1), Pc(2, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 2, 0, 1), Pc(2, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 2, 0, 1), Pc(2, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 0, 0, 1), Pc(2, 0, 0, 4, 0, 1)),
        };
        Check("the same shape with an enemy still standing stays one action, nothing invented",
              MoveDetector.DetectSequence(sbNotOver, 0) is { Ok: true, Moves.Count: 1 });

        var sbSurvives = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 4, 3, 3, 0), Pc(1, 5, 5, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 2, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 2, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 1, 0, 1)),
        };
        Check("the same shape with the victim surviving stays one action, nothing invented",
              MoveDetector.DetectSequence(sbSurvives, 0) is { Ok: true, Moves.Count: 1 });

        var thirdAction = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(1, 5, 4, 1, 2, 1), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(1, 5, 4, 0, 2, 1), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 5, 6, 2, 0, 0), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 0, 1, 1)),

            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1)),
        };
        var ta = MoveDetector.DetectSequence(thirdAction, 0);
        Check("a match-ending third action whose tick the teardown wiped is read, not dropped as a consequence",
              ta.Ok && ta.Moves.Count == 3 &&
              ta.Moves[2] is { Attack: true, SrcX: 5, SrcY: 6, DstX: 4, DstY: 6, TargetX: 4, TargetY: 5 });
        Check("the recovered third action says no counter tick explained it",
              ta.Warning is not null && ta.Warning.Contains("no counter tick"));

        var thirdNotOver = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(1, 5, 4, 1, 2, 1), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(1, 5, 4, 0, 2, 1), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 5, 6, 2, 0, 0), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 5, 5, 2, 0, 0), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 5, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 1, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
            Frame(Pc(0, 4, 6, 2, 0, 0, acts: 1), Pc(2, 4, 5, 0, 1, 1), Pc(3, 0, 0, 4, 0, 1)),
        };
        Check("the same third-action shape with an enemy still standing invents nothing",
              MoveDetector.DetectSequence(thirdNotOver, 0) is { Ok: true, Moves.Count: 2 });

        var ramKill = new List<BoardSnapshot>
        {
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 3, 1, 2, 1), Pc(2, 4, 4, 4, 0, 0), Pc(3, 3, 2, 3, 2, 1)),
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 3, 0, 2, 1), Pc(2, 4, 4, 3, 0, 0), Pc(3, 3, 2, 3, 2, 1)),
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 4, 3, 0, 0), Pc(2, 3, 2, 3, 2, 1)),
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 2, 3, 3, 0, acts: 1), Pc(2, 3, 2, 3, 2, 1)),
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 2, 3, 3, 0, acts: 1, bursts: 1, burst: true), Pc(2, 3, 2, 1, 2, 1)),
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 2, 1, 3, 0, acts: 1, bursts: 1, burst: true), Pc(2, 3, 2, 1, 2, 1)),
            Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 2, 1, 3, 0, acts: 1, bursts: 1, burst: true), Pc(2, 3, 2, 0, 2, 1)),
        };
        var rk = MoveDetector.DetectSequence(ramKill, 0);
        Check("a mid-turn knockback kill reads as the attack it was, ahead of the actor's own move",
              rk.Ok && rk.Moves.Count == 4 &&
              rk.Moves[0] is { Attack: true, TargetX: 4, TargetY: 3, Burst: false } &&
              rk.Moves[1] is { Attack: false, SrcX: 4, SrcY: 4, DstX: 4, DstY: 2 });

        Check("a traceless blow goes to the one machine with its use unspent, a PLAIN attack",
              rk.Ok && rk.Moves[^1] is { Attack: true, TargetX: 3, TargetY: 2, SrcX: 3, SrcY: 3, Burst: false } &&
              rk.Warning is not null && rk.Warning.Contains("PLAIN") && rk.Warning.Contains("(3,3)"));

        var loneRepeat = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 4, 0, 1)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 2, 0, 1)),
            Frame(Pc(0, 4, 5, 1, 1, 0, acts: 1, bursts: 1, burst: true), Pc(1, 5, 5, 1, 0, 1)),
            Frame(Pc(0, 4, 5, 1, 1, 0, acts: 1, bursts: 1, burst: true), Pc(1, 5, 5, 0, 0, 1)),
        };
        var lr = MoveDetector.DetectSequence(loneRepeat, 0);
        Check("our lone machine's traceless repeat blow stays with it, plain, its second use",
              lr.Ok && lr.Moves[^1] is { Attack: true, SrcX: 4, SrcY: 5, TargetX: 5, TargetY: 5, Burst: false });

        var noCandidate = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 5, 3, 1, 0), Pc(1, 5, 5, 4, 0, 1), Pc(2, 0, 0, 4, 0, 0)),
            Frame(Pc(0, 4, 5, 3, 1, 0, acts: 1), Pc(1, 5, 5, 2, 0, 1), Pc(2, 0, 0, 4, 0, 0)),
            Frame(Pc(0, 4, 5, 1, 1, 0, acts: 1, bursts: 1, burst: true), Pc(1, 5, 5, 1, 0, 1), Pc(2, 0, 0, 4, 0, 0)),
            Frame(Pc(0, 4, 5, 1, 1, 0, acts: 1, bursts: 1, burst: true), Pc(1, 5, 5, 0, 0, 1), Pc(2, 0, 0, 4, 0, 0)),
        };
        Check("a teammate alive but out of position licenses nothing, the drop folds",
              MoveDetector.DetectSequence(noCandidate, 0) is { Ok: true } nc &&
              nc.Moves.Count(m => m.Attack) == 2);

        Check("a still-living enemy elsewhere keeps the untracked kill refused",
              MoveDetector.DetectSequence(
              [
                  Frame(Pc(0, 3, 3, 4, 0, 0), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1), Pc(4, 7, 7, 4, 0, 1)),
                  Frame(Pc(0, 3, 3, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1), Pc(4, 7, 7, 4, 0, 1)),
                  Frame(Pc(0, 3, 3, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 0, 0, 1), Pc(4, 7, 7, 4, 0, 1)),
              ], 0) is { Ok: false } alive && alive.Refusal!.Contains("on its facing line"));

        Check("two of ours able to have struck the last enemy is still refused",
              MoveDetector.DetectSequence(
              [
                  Frame(Pc(0, 3, 3, 4, 0, 0), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1), Pc(4, 4, 0, 4, 2, 0)),
                  Frame(Pc(0, 3, 2, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 2, 0, 1), Pc(4, 4, 0, 4, 2, 0)),
                  Frame(Pc(0, 3, 2, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(3, 4, 1, 0, 0, 1), Pc(4, 4, 0, 4, 2, 0)),
              ], 0) is { Ok: false } twoAble && twoAble.Refusal!.Contains("on its facing line"));

        var together = new List<BoardSnapshot>
        {
            Frame(Pc(0, 1, 0, 4, 2, 0), Pc(1, 6, 1, 3, 2, 0)),
            Frame(Pc(0, 1, 1, 4, 2, 0, acts: 1), Pc(1, 6, 2, 3, 2, 0, acts: 1)),
        };
        Check("two machines acting in one sample are refused, not ordered by guess",
              MoveDetector.DetectSequence(together, 0) is { Ok: false } st && st.Refusal!.Contains("which acted first"));

        var noCounters = walk.Select(s => s with
        {
            Pieces = s.Pieces.Select(p => p with { Acts = -1, Bursts = -1 }).ToList(),
        }).ToList();
        Check("a stream with no action counters is refused, not read as a turn of zero actions",
              MoveDetector.DetectSequence(noCounters, 0) is { Ok: false } sc && sc.Refusal!.Contains("action counters"));

        var noIdxStream = walk.Select(s => s with { Pieces = s.Pieces.Select(p => p with { Idx = -1 }).ToList() }).ToList();
        Check("a stream with no piece indices is refused, identity is what the whole method rests on",
              MoveDetector.DetectSequence(noIdxStream, 0) is { Ok: false } si && si.Refusal!.Contains("indices"));

        var ranged = new List<BoardSnapshot>
        {
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 4, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 3, 2, 0, acts: 1), Pc(2, 6, 4, 2, 0, 1)),
        };
        Check("a ranged hit with the victim on the actor's facing line is read as the attack it was",
              MoveDetector.DetectSequence(ranged, 0) is { Ok: true } sr &&
              sr.Moves is [{ Attack: true, SrcX: 6, SrcY: 1, DstX: 6, DstY: 1, TargetX: 6, TargetY: 4, Facing: 2 }]);

        var beyondRange = new List<BoardSnapshot>
        {
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 5, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 3, 2, 0, acts: 1), Pc(2, 6, 5, 2, 0, 1)),
        };
        Check("damage four squares out is beyond every machine's range and still refuses",
              MoveDetector.DetectSequence(beyondRange, 0) is { Ok: false } sbr &&
              sbr.Refusal!.Contains("on its facing line"));

        Check("an untracked kill with a second candidate deep on the same line is refused",
              MoveDetector.DetectSequence(
              [
                  Frame(Pc(0, 3, 3, 4, 0, 0), Pc(2, 4, 2, 5, 0, 0), Pc(4, 4, 4, 4, 0, 0), Pc(3, 4, 1, 2, 0, 1)),
                  Frame(Pc(0, 3, 2, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(4, 4, 4, 4, 0, 0), Pc(3, 4, 1, 2, 0, 1)),
                  Frame(Pc(0, 3, 2, 4, 0, 0, acts: 1), Pc(2, 4, 2, 5, 0, 0), Pc(4, 4, 4, 4, 0, 0), Pc(3, 4, 1, 0, 0, 1)),
              ], 0) is { Ok: false } deepTwo && deepTwo.Refusal!.Contains("on its facing line"));

        var gunnerShot = new List<BoardSnapshot>
        {
            Frame(Pc(0, 6, 4, 4, 2, 1), Pc(1, 4, 6, 5, 0, 0), Pc(2, 4, 3, 4, 2, 1), Pc(3, 3, 5, 4, 0, 0, acts: 1)),
            Frame(Pc(0, 6, 4, 4, 2, 1), Pc(1, 6, 6, 5, 0, 0), Pc(2, 4, 3, 4, 2, 1), Pc(3, 3, 5, 4, 0, 0, acts: 1)),
            Frame(Pc(0, 6, 4, 1, 2, 1), Pc(1, 6, 6, 5, 0, 0), Pc(2, 4, 3, 4, 2, 1), Pc(3, 3, 5, 4, 0, 0, acts: 1)),
            Frame(Pc(0, 6, 4, 1, 2, 1), Pc(1, 6, 6, 5, 0, 0, acts: 1), Pc(2, 4, 3, 4, 2, 1), Pc(3, 3, 5, 4, 0, 0, acts: 1)),
        };
        var gs = MoveDetector.DetectSequence(gunnerShot, 0);
        Check("the live Gunner shot reads as a move-and-attack at range 2",
              gs.Ok && gs.Moves is [{ Attack: true, SrcX: 4, SrcY: 6, DstX: 6, DstY: 6, TargetX: 6, TargetY: 4, Facing: 0 }]);

        var gunnerKill = new List<BoardSnapshot>
        {
            Frame(Pc(0, 6, 4, 1, 2, 1), Pc(1, 6, 5, 5, 0, 0), Pc(2, 4, 3, 4, 2, 1), Pc(3, 5, 4, 4, 1, 0)),
            Frame(Pc(0, 6, 4, 1, 2, 1), Pc(1, 6, 6, 5, 0, 0), Pc(2, 4, 3, 4, 2, 1), Pc(3, 5, 4, 4, 1, 0)),
            Frame(Pc(0, 6, 4, 0, 2, 1), Pc(1, 6, 6, 5, 0, 0), Pc(2, 4, 3, 4, 2, 1), Pc(3, 5, 4, 4, 1, 0)),
            Frame(Pc(1, 6, 6, 5, 0, 0, acts: 1), Pc(2, 4, 3, 4, 2, 1), Pc(3, 5, 4, 4, 1, 0)),
        };
        var gk = MoveDetector.DetectSequence(gunnerKill, 0);
        Check("a landing shot that kills at range 2 goes to the machine that ticked, not the nearer bystander",
              gk.Ok && gk.Moves is [{ Attack: true, SrcX: 6, SrcY: 5, DstX: 6, DstY: 6, TargetX: 6, TargetY: 4, Facing: 0 }]);

        var retal = new List<BoardSnapshot>
        {
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 2, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 2, 2, 0, acts: 1), Pc(2, 6, 2, 2, 0, 1)),
        };
        var s6 = MoveDetector.DetectSequence(retal, 0);
        Check("our own machine losing health in a stream is retaliation, not a burst",
              s6.Ok && s6.Moves is [{ Attack: true, Burst: false }]);

        var lateDamage = new List<BoardSnapshot>
        {
            Frame(Pc(1, 6, 1, 3, 2, 0), Pc(2, 6, 2, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 3, 2, 0, acts: 1), Pc(2, 6, 2, 4, 0, 1)),
            Frame(Pc(1, 6, 1, 3, 2, 0, acts: 1), Pc(2, 6, 2, 2, 0, 1)),
        };
        Check("damage arriving a sample after its own counter tick still reaches that action",
              MoveDetector.DetectSequence(lateDamage, 0) is { Ok: true } sl &&
              sl.Moves is [{ Attack: true, TargetX: 6, TargetY: 2 }]);

        var theirs = new Move { SrcX = 1, SrcY = 0, DstX = 1, DstY = 2, Facing = 2 };
        var ours = theirs.Rotated();
        Check("a received move is rotated into the receiver's frame",
              ours is { SrcX: 6, SrcY: 7, DstX: 6, DstY: 5, Facing: 0 });
        Check("rotating twice returns the original",
              ours.Rotated() is { SrcX: 1, SrcY: 0, DstX: 1, DstY: 2, Facing: 2 });

        var theirAttack = new Move { SrcX = 5, SrcY = 3, DstX = 5, DstY = 3, TargetX = 5, TargetY = 4, Facing = 2, Attack = true };
        var ourAttack = theirAttack.Rotated();
        Check("an attack keeps the attacker on its own square after rotation",
              ourAttack.SrcX == ourAttack.DstX && ourAttack.SrcY == ourAttack.DstY);
        Check("an attack's victim stays adjacent in the direction of facing",
              ourAttack is { SrcX: 2, SrcY: 4, TargetX: 2, TargetY: 3, Facing: 0 });
        Check("a move with no target keeps -1 rather than rotating it into a real square",
              ours is { TargetX: -1, TargetY: -1 });

        var orphan = new Peer("127.0.0.1", port, "NOROOM");
        Check("hashing before pairing halts instead of guessing a seat",
              !orphan.CheckHash(hashFrame, viewA, 4) && orphan.HaltReason?.Contains("before pairing") == true);

        var sameBuild = new Lobby("6A1B2C3D-4E5F000", isHost: true);
        Check("identical game builds pair without complaint",
              sameBuild.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "6A1B2C3D-4E5F000" }) is null);

        var crossBuild = new Lobby("6A1B2C3D-4E5F000", isHost: true);
        var buildRefusal = crossBuild.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "99999999-4E5F000" });
        Check("a game build mismatch refuses the pair and names both builds",
              buildRefusal is not null && buildRefusal.Contains("6A1B2C3D") && buildRefusal.Contains("99999999"));
        Check("a refused lobby stays refused when a later frame arrives",
              crossBuild.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "6A1B2C3D-4E5F000" }) is not null);

        var unknownBuild = new Lobby("unknown", isHost: false);
        Check("an unknown build warns rather than refusing",
              unknownBuild.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "6A1B2C3D-4E5F000" }) is null);

        var sameStrikers = new Lobby("B", isHost: true, localNetplay: "aaaa", localProbe: "pppp");
        Check("identical netplay and live-probe versions pair without complaint",
              sameStrikers.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B",
                                               NetplayVersion = "aaaa", ProbeVersion = "pppp" }) is null);

        var crossNetplay = new Lobby("B", isHost: true, localNetplay: "aaaa", localProbe: "pppp");
        var netplayRefusal = crossNetplay.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B",
                                                              NetplayVersion = "bbbb", ProbeVersion = "pppp" });
        Check("a netplay version mismatch refuses the pair and says update to the same version",
              netplayRefusal is not null && netplayRefusal.Contains("aaaa")
              && netplayRefusal.Contains("bbbb") && netplayRefusal.Contains("version"));

        var crossProbe = new Lobby("B", isHost: false, localNetplay: "aaaa", localProbe: "pppp");
        var probeRefusal = crossProbe.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B",
                                                          NetplayVersion = "aaaa", ProbeVersion = "qqqq" });
        Check("a live-probe version mismatch refuses the pair on its own",
              probeRefusal is not null && probeRefusal.Contains("live-probe"));

        var noLocal = new Lobby("B", isHost: true);
        Check("a lobby with no versions of its own does not refuse a peer that has them",
              noLocal.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B",
                                          NetplayVersion = "bbbb", ProbeVersion = "qqqq" }) is null);

        var noPeer = new Lobby("B", isHost: true, localNetplay: "aaaa", localProbe: "pppp");
        Check("a peer sending no versions is missing information, not a mismatch",
              noPeer.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B" }) is null);

        var gameDownStrikers = new Lobby("unknown", isHost: true, localNetplay: "aaaa", localProbe: "pppp");
        Check("the netplay guard still runs while the game build is unknown",
              gameDownStrikers.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "unknown",
                                                   NetplayVersion = "bbbb", ProbeVersion = "pppp" }) is not null);

        const string beginnerHard = "74771C9B66D9441AA4CBD5E47D4851AD";

        var guestLobby = new Lobby("B", isHost: false) { Seat = 1 };
        guestLobby.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Challenge = beginnerHard,
            Army = [MachineA, MachineB],
            Placements = [new Placement { X = 1, Y = 0, Dir = 1 }, new Placement { X = 6, Y = 0, Dir = 3 }],
        });
        Check("a received placement is rotated into the receiver's frame",
              guestLobby.Remote!.Placements[0] is { X: 6, Y: 7, Dir: 3 } &&
              guestLobby.Remote.Placements[1] is { X: 1, Y: 7, Dir: 1 });
        Check("a received army is not rotated, UUIDs are not coordinates",
              guestLobby.Remote.Army is [MachineA, MachineB]);
        Check("the guest is told the challenge and does not choose one",
              guestLobby.Challenge == beginnerHard);

        var namePicker = new Lobby("B", isHost: true) { Seat = 0 };
        var rejectedName = false;
        try { namePicker.Choose("Beginner's Practice: Hard"); }
        catch (InvalidOperationException) { rejectedName = true; }
        Check("a challenge NAME is refused, nothing downstream could resolve one", rejectedName);
        Check("refusing a name leaves the challenge unset", namePicker.Challenge is null);

        namePicker.Choose("74771c9b-66d9-441a-a4cb-d5e47d4851ad");
        Check("a dashed lower-case uuid is accepted and normalised to live-probe's form",
              namePicker.Challenge == beginnerHard);

        var shortUuid = false;
        try { namePicker.Choose("74771C9B66D9441AA4CBD5E47D4851"); }
        catch (InvalidOperationException) { shortUuid = true; }
        Check("a uuid of the wrong length is refused", shortUuid);

        var badFromHost = new Lobby("B", isHost: false) { Seat = 1 };
        badFromHost.OnSetup(new Frame { Kind = MsgKind.Setup, Challenge = "not-a-uuid", Army = [MachineA] });
        Check("a guest refuses a challenge id from the host that is not a uuid",
              badFromHost.Refusal is not null && badFromHost.Challenge is null);
        Check("that refusal survives a later well-formed Setup",
              !RefusedGuestRecovers(badFromHost, beginnerHard));

        var armyLobby = new Lobby("B", isHost: true) { Seat = 0 };
        Check("a machine NAME is refused as an army entry",
              armyLobby.SetArmy(["Bristleback"]) is not null && armyLobby.Local.Army.Count == 0);
        Check("a --survey ADDRESS is refused as an army entry, the peer cannot resolve one",
              armyLobby.SetArmy(["0x1F2A3B4C5D6"]) is not null && armyLobby.Local.Army.Count == 0);
        Check("a dashed lower-case machine uuid is accepted and normalised",
              armyLobby.SetArmy(["74771c9b-66d9-441a-a4cb-d5e47d4851ad"]) is null &&
              armyLobby.Local.Army is [beginnerHard]);

        var badArmy = new Lobby("B", isHost: false) { Seat = 1 };
        badArmy.OnSetup(new Frame { Kind = MsgKind.Setup, Challenge = beginnerHard, Army = ["Bristleback"] });
        Check("a guest refuses a machine id from the host that is not a uuid",
              badArmy.Refusal is not null);

        var builtin = Presets.Builtin();
        Check("the built-in preset validates", builtin.Problem() is null);
        Check("the built-in preset names its challenge in words as well as hex",
              builtin.ChallengeName.Length > 0 && Lobby.NormaliseUuid(builtin.Challenge) is not null);
        Check("the built-in preset has one placement per machine",
              builtin.Army.Count == builtin.Placements.Count && builtin.Army.Count == 2);

        var rangedPreset = Presets.Ranged();
        Check("the ranged preset validates", rangedPreset.Problem() is null);
        Check("the ranged preset fields the Gunner, and on the same board as the gate preset",
              rangedPreset.Army[0].Name == "Scrapper" && rangedPreset.Challenge == builtin.Challenge);

        Check("both built-in presets are found by name with no presets.json",
              Presets.Load(Path.GetTempPath(), out _) is var builtins &&
              Presets.Find(builtins, "gate") is not null && Presets.Find(builtins, "ranged") is not null);

        var brokenPresets = Directory.CreateTempSubdirectory("strikers-selftest-presets-");
        try
        {
            var presetsPath = Path.Combine(brokenPresets.FullName, Presets.FileName);

            var nullEntryHeld = true;
            List<Preset> nullEntry = [];
            try
            {
                File.WriteAllText(presetsPath, "[{\"name\":\"gate\"},null]");
                nullEntry = Presets.Load(brokenPresets.FullName, out _);
            }
            catch (Exception)
            {
                nullEntryHeld = false;
            }

            Check("a null entry in presets.json costs the entry, not the process",
                  nullEntryHeld && nullEntry.Count == 5 && Presets.Find(nullEntry, "ranged") is not null);

            var nullListHeld = true;
            List<Preset> nullList = [];
            try
            {
                File.WriteAllText(presetsPath, "[{\"name\":\"nulled\",\"army\":null,\"placements\":null}]");
                nullList = Presets.Load(brokenPresets.FullName, out _);
            }
            catch (Exception)
            {
                nullListHeld = false;
            }

            Check("a null list in presets.json is read as an empty one, not handed on as null",
                  nullListHeld && Presets.Find(nullList, "nulled") is { } nulled
                  && nulled.Army is { Count: 0 } && nulled.Placements is { Count: 0 });
        }
        finally
        {
            brokenPresets.Delete(recursive: true);
        }

        var loneSurrogate = ((char)0xD800).ToString();

        var decodeHeld = true;
        Frame? decoded = null;
        try
        {
            decoded = Protocol.Decode("{\"v\":24,\"k\":1,\"room\":\"" + loneSurrogate + "\"}");
        }
        catch (Exception)
        {
            decodeHeld = false;
        }

        Check("a frame line holding an unpaired surrogate is refused, not thrown on",
              decodeHeld && decoded is null);

        var snapshotHeld = true;
        var snapshotParsed = true;
        try
        {
            snapshotParsed = SnapshotJson.TryParse(
                "{\"width\":8,\"height\":8,\"pieces\":[],\"note\":\"" + loneSurrogate + "\"}", out _, out _);
        }
        catch (Exception)
        {
            snapshotHeld = false;
        }

        Check("a snapshot holding an unpaired surrogate is refused, not thrown on",
              snapshotHeld && snapshotParsed is false);

        var previewPreset = Presets.Preview();
        Check("the preview preset validates, board and rules included", previewPreset.Problem() is null);
        Check("the preview preset is a 4v4 with one placement per machine",
              previewPreset.Army.Count == 4 && previewPreset.Placements.Count == 4);

        Check("the two seats field different armies of the same size",
              previewPreset.ArmyGuest.Count == previewPreset.Army.Count &&
              !previewPreset.ArmyFor(isHost: true).Select(m => m.Uuid)
                  .SequenceEqual(previewPreset.ArmyFor(isHost: false).Select(m => m.Uuid)));
        Check("the host takes the host list and the guest takes the guest list",
              previewPreset.ArmyFor(isHost: true)[0].Name == "Slaughterspine" &&
              previewPreset.ArmyFor(isHost: false)[0].Name == "Fireclaw");

        var flyers = new[] { "Sunwing", "Skydrifter", "Dreadwing", "Glinthawk" };
        Check("each seat fields two machines that can cross the abyss",
              previewPreset.ArmyFor(isHost: true).Count(m => flyers.Contains(m.Name)) == 2 &&
              previewPreset.ArmyFor(isHost: false).Count(m => flyers.Contains(m.Name)) == 2);

        Check("a preset with no guest army gives both seats the same one",
              Presets.Builtin() is var plainPreset &&
              plainPreset.ArmyFor(isHost: true).Select(m => m.Uuid)
                  .SequenceEqual(plainPreset.ArmyFor(isHost: false).Select(m => m.Uuid)));

        var shortGuest = Presets.Preview();
        shortGuest.ArmyGuest = [.. previewPreset.ArmyGuest.Take(3)];
        Check("seats fielding different NUMBERS of machines is refused",
              shortGuest.Problem() is { } shortBad && shortBad.Contains("same number"));
        Check("the preview board is 64 cells and 180-degree rotationally symmetric",
              previewPreset.Board.Count == 64 &&
              Enumerable.Range(0, 64).All(i => previewPreset.Board[i] == previewPreset.Board[63 - i]));
        Check("the preview board keeps Chasm out of every placing row",
              Enumerable.Range(0, 64).Where(i => i / 8 is 0 or 1 or 6 or 7).All(i => previewPreset.Board[i] != -2));

        Check("the preview raises the victory threshold well past a single machine's cost",
              previewPreset.VictoryPoints is >= 20 && previewPreset.DraftPoints is >= 26);

        var asym = Presets.Preview();
        asym.Board = [.. previewPreset.Board];

        asym.Board[0] = asym.Board[0] == 0 ? 1 : 0;
        Check("an asymmetric board is ACCEPTED, because the guest writes the rotation",
              asym.Problem() is null);

        Check("Rotate180 maps every cell to its 180-degree opposite",
              Preset.Rotate180(asym.Board, 8, 8) is var turnedBoard && turnedBoard.Count == 64 &&
              Enumerable.Range(0, 64).All(i => turnedBoard[i] == asym.Board[63 - i]));

        Check("rotating twice returns the original board",
              Preset.Rotate180(Preset.Rotate180(asym.Board, 8, 8), 8, 8).SequenceEqual(asym.Board));

        Check("rotating a SYMMETRIC board is the identity, so nothing that worked changes",
              Preset.Rotate180(previewPreset.Board, 8, 8).SequenceEqual(previewPreset.Board));

        List<int> wideStrip = [0, 1, 2, 3, 4, 5, 6, 7];
        Check("Rotate180 turns a rectangle, rather than guessing a square side",
              Preset.Rotate180(wideStrip, 4, 2).SequenceEqual([7, 6, 5, 4, 3, 2, 1, 0]));

        Check("rotating a rectangle twice returns it unchanged",
              Preset.Rotate180(Preset.Rotate180(wideStrip, 4, 2), 4, 2).SequenceEqual(wideStrip));

        var chasmRow = Presets.Preview();
        chasmRow.Board = [.. previewPreset.Board];
        chasmRow.Board[3] = -2;
        chasmRow.Board[60] = -2;
        Check("a Chasm in a placing row is ALLOWED even when the board stays symmetric",
              chasmRow.Problem() is null);

        var badTile = Presets.Preview();
        badTile.Board = [.. previewPreset.Board];
        badTile.Board[0] = 9;
        badTile.Board[63] = 9;
        Check("a terrain value outside -2..3 is refused",
              badTile.Problem() is { } tileBad && tileBad.Contains("range is -2"));

        var pv = new Lobby("B", isHost: true) { Seat = 0 };
        pv.Choose(previewPreset.Challenge);
        pv.SetArmy([.. previewPreset.Army.Select(m => m.Uuid)]);
        pv.Local.Placements = [.. previewPreset.Placements];
        pv.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = [.. previewPreset.Army.Select(m => m.Uuid)],
            Placements = [.. previewPreset.Placements],
        });

        var pvSteps = WriteSteps(pv, pv.Remote!, interactivePlacement: true, preset: previewPreset);
        Check("a preview write is rules, then board, then armies, and no placement pre-write",
              pvSteps.Length == 3 &&
              pvSteps[0].Args[0] == "--set-rules" &&
              pvSteps[1].Args[0] == "--set-board" &&
              pvSteps[2].Args[0] == "--set-units" &&
              !pvSteps.Any(s => s.Args.Contains("--set-placement")));
        Check("the rule write names both numbers and the challenge",
              pvSteps[0].Args.Contains("--victory-points") && pvSteps[0].Args.Contains("--draft-points") &&
              pvSteps[0].Args.Contains(previewPreset.Challenge));
        Check("the board write sends all 64 cells in row-major order",
              pvSteps[1].Args.Count(a => int.TryParse(a, out _)) == 64 &&
              pvSteps[1].Args[1] == $"{previewPreset.Board[0]}");

        var seven = Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 7).ToList();
        var free = new Lobby("F", isHost: true) { Seat = 0 };
        free.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        free.SetArmy(seven);
        free.Local.Placements = Lobby.DefaultSquares(7, Lobby.BoardSide, Lobby.BoardSide, 2);
        free.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = seven,
            Placements = Lobby.DefaultSquares(7, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        var freeUnits = WriteSteps(free, free.Remote!, interactivePlacement: true).First(s => s.Args[0] == "--set-units").Args;
        Check("a free draft writes both seats, each sized to its own army",
              freeUnits.Contains("--ai") && freeUnits.Contains("--human") &&
              freeUnits.Count(a => a == "1C96A39FFE37791F8AE07BD49A2230FF") == 14);
        Check("a free draft sizes the seats and does NOT hand them back (D-232)",
              freeUnits.Contains("--allocate") && !freeUnits.Contains("--restore-when-live"));
        Check("a fixed-slot challenge still writes both seats, and sizes neither",
              pvSteps[2].Args.Contains("--human") && pvSteps[2].Args.Contains("--ai") &&
              !pvSteps[2].Args.Contains("--allocate") && !pvSteps[2].Args.Contains("--restore-when-live"));

        var sides = new Lobby("L", isHost: true) { Seat = 0 };
        sides.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        sides.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 3).ToList());
        sides.Local.Placements = Lobby.DefaultSquares(3, Lobby.BoardSide, Lobby.BoardSide, 2);
        sides.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("2B34B3566FC1ED50071517AE89C5252F", 9).ToList(),
            Placements = Lobby.DefaultSquares(9, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        var lopsidedUnits = WriteSteps(sides, sides.Remote!, interactivePlacement: true)
            .First(s => s.Args[0] == "--set-units").Args;
        var humanAt = Array.IndexOf(lopsidedUnits, "--human");
        var aiAt = Array.IndexOf(lopsidedUnits, "--ai");
        Check("our three go into the human seat and their nine into the ai seat",
              aiAt - humanAt - 1 == 3 && lopsidedUnits.Length - aiAt - 1 == 9 &&
              lopsidedUnits[humanAt + 1] == "1C96A39FFE37791F8AE07BD49A2230FF" &&
              lopsidedUnits[aiAt + 1] == "2B34B3566FC1ED50071517AE89C5252F");

        var limit = new Lobby("S", isHost: true) { Seat = 0 };
        limit.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        limit.ChooseBoard(null, 7, 10, Preset.BoardSide, Preset.BoardSide, Preset.RuleNotSet);
        limit.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 3).ToList());
        limit.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 6).ToList(),
            Placements = Lobby.DefaultSquares(6, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        Check("a peer fielding six against our three is a match, not a refusal",
              limit.Refusal is null);

        var overspent = new Lobby("O", isHost: true) { Seat = 0 };
        overspent.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        overspent.ChooseBoard(null, 7, 10, Preset.BoardSide, Preset.BoardSide, Preset.RuleNotSet);
        overspent.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 2).ToList());
        overspent.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("82F13F6222FCF687A5684CC1F9B96F4F", 2).ToList(),
            Placements = Lobby.DefaultSquares(2, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        Check("a peer's army over the cost limit is refused on receipt",
              overspent.Refusal is not null && overspent.Refusal.Contains("budget"));

        var narrow = new Lobby("N", isHost: true) { Seat = 0 };
        narrow.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        narrow.ChooseBoard(Enumerable.Repeat(0, 12).ToList(), Preset.RuleNotSet, Preset.RuleNotSet, 4, 3, 1);
        narrow.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 9).ToList());
        narrow.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 4).ToList(),
            Placements = Lobby.DefaultSquares(4, 4, 3, 1),
        });
        Check("nine machines are refused on a board with four squares to place them on",
              narrow.Refusal is not null && narrow.Refusal.Contains("square"));

        var roomy = new Lobby("R", isHost: true) { Seat = 0 };
        roomy.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        roomy.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 9).ToList());
        roomy.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 9).ToList(),
            Placements = Lobby.DefaultSquares(9, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        Check("nine a side is fine on the default board, which has sixteen",
              roomy.Refusal is null);

        var oneMachine = Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 1).ToList();
        var offEdge = new Lobby("E", isHost: true) { Seat = 0 };
        offEdge.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        offEdge.ChooseBoard(Enumerable.Repeat(0, 12).ToList(), Preset.RuleNotSet, Preset.RuleNotSet, 4, 3, 1);
        offEdge.SetArmy(oneMachine);
        offEdge.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = oneMachine,
            Placements = [new Placement { X = 7, Y = 2, Dir = 0 }],
        });
        Check("a starting square past a shrunken board's edge is refused on arrival",
              offEdge.Refusal is not null && offEdge.Refusal.Contains("starting square"));

        var ownRows = new Lobby("O", isHost: true) { Seat = 0 };
        ownRows.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        ownRows.SetArmy(oneMachine);
        ownRows.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = oneMachine,
            Placements = [new Placement { X = 3, Y = 0, Dir = 0 }],
        });
        Check("a starting square on the sender's far edge lands in this player's rows and is refused",
              ownRows.Refusal is not null && ownRows.Refusal.Contains("placing rows"));
        Check("a square in the sender's near rows is inside their rows once rotated",
              Lobby.PlacementProblem(new Placement { X = 3, Y = 6 }.Rotated(8, 8), 8, 2) is null);

        Check("a free draft takes nine a side and refuses ten",
              Machines.ArmyProblem(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 9).ToList(),
                                   0, 0, allowUnknown: true) is null &&
              Machines.ArmyProblem(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 10).ToList(),
                                   0, 0, allowUnknown: true) is not null);
        Check("a fixed-slot challenge still demands exactly its own size",
              Machines.ArmyProblem(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 3).ToList(),
                                   4, 0, allowUnknown: true) is not null);

        var undeclared = new Lobby("U", isHost: true) { Seat = 0 };
        undeclared.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        undeclared.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 2).ToList());
        undeclared.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("82F13F6222FCF687A5684CC1F9B96F4F", 2).ToList(),
            Placements = Lobby.DefaultSquares(2, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        Check("an over-cost army is refused even when the host declared no rules",
              undeclared.Refusal is not null && undeclared.Refusal.Contains("budget"));

        var shallow = new Lobby("H", isHost: true) { Seat = 0 };
        shallow.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        shallow.ChooseBoard(Enumerable.Repeat(0, 4).ToList(), Preset.RuleNotSet, Preset.RuleNotSet,
                            4, 1, Preset.RuleNotSet);
        shallow.SetArmy(Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 5).ToList());
        shallow.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = Enumerable.Repeat("1C96A39FFE37791F8AE07BD49A2230FF", 4).ToList(),
            Placements = Lobby.DefaultSquares(4, 4, 1, 1),
        });
        Check("a shallow board's placing zone is clamped to its height",
              shallow.Refusal is not null && shallow.Refusal.Contains("1 deep"));

        Check("the relay strips a newline from the room key it prints",
              !Relay.SafeRoomKey("AAA\nBOTH READY").Contains('\n') &&
              Relay.SafeRoomKey("AAA\nBOTH READY") == "AAABOTH READY");
        Check("the relay leaves an ordinary room code untouched",
              Relay.SafeRoomKey("MSX8FR") == "MSX8FR" &&
              Relay.SafeRoomKey("  msx8fr  ") == "MSX8FR");

        var plainSteps = WriteSteps(pv, pv.Remote!, interactivePlacement: false, preset: Presets.Builtin());
        Check("a preset with no board and no rules still writes armies then placements, unchanged",
              plainSteps.Length == 2 &&
              plainSteps[0].Args[0] == "--set-units" && plainSteps[1].Args[0] == "--set-placement");

        var hostPreset = new Lobby("B", isHost: true) { Seat = 0 };
        Check("a host applying a preset gets the challenge, the army and the squares",
              hostPreset.ApplyPreset(builtin) is null &&
              hostPreset.Challenge == builtin.Challenge &&
              hostPreset.Local.Army.Count == 2 &&
              hostPreset.Local.Placements.Count == 2 &&
              hostPreset.Local.Complete);

        var guestPreset = new Lobby("B", isHost: false) { Seat = 1 };
        Check("a guest applying a preset does NOT set the challenge itself",
              guestPreset.ApplyPreset(builtin) is null && guestPreset.Challenge is null &&
              guestPreset.Local.Complete);

        var agreeing = new Lobby("B", isHost: false) { Seat = 1 };
        agreeing.OnSetup(new Frame { Kind = MsgKind.Setup, Challenge = builtin.Challenge, Army = [MachineA] });
        Check("a guest preset naming the challenge the host chose is accepted",
              agreeing.ApplyPreset(builtin) is null);

        var clashing = new Lobby("B", isHost: false) { Seat = 1 };
        clashing.OnSetup(new Frame { Kind = MsgKind.Setup, Challenge = beginnerHard, Army = [MachineA] });
        Check("a guest preset for a DIFFERENT challenge than the host chose is refused",
              clashing.ApplyPreset(builtin) is not null);
        Check("that refusal names the challenge in words, the only part a player can act on",
              clashing.ApplyPreset(builtin)!.Contains(builtin.ChallengeName));
        Check("a refused preset writes nothing at all, not even the army",
              clashing.Local.Army.Count == 0 && clashing.Local.Placements.Count == 0);

        var lopsided = new Preset
        {
            Name = "lopsided",
            ChallengeName = "Beginner's Practice: Easy",
            Challenge = builtin.Challenge,
            Army = [.. builtin.Army],
            Placements = [builtin.Placements[0]],
        };
        Check("a preset with more machines than squares is refused", lopsided.Problem() is not null);

        var halfApplied = new Lobby("B", isHost: true) { Seat = 0 };
        Check("that refusal leaves the lobby untouched",
              halfApplied.ApplyPreset(lopsided) is not null &&
              halfApplied.Challenge is null && halfApplied.Local.Army.Count == 0);

        var badUuidPreset = new Preset
        {
            Name = "bad",
            ChallengeName = "somewhere",
            Challenge = builtin.Challenge,
            Army = [new PresetMachine { Name = "Bristleback", Uuid = "Bristleback" }],
            Placements = [builtin.Placements[0]],
        };
        Check("a preset whose machine is a name rather than a uuid is refused",
              badUuidPreset.Problem() is not null);

        var namelessChallenge = new Preset { Name = "x", Challenge = "not-a-uuid", Army = [.. builtin.Army] };
        Check("a preset with no valid challenge uuid is refused", namelessChallenge.Problem() is not null);

        Check("presets are found by name, case-insensitively",
              Presets.Find([builtin], "GATE") is not null && Presets.Find([builtin], "nope") is null);

        var rematchLine = Protocol.Encode(new Frame { Kind = MsgKind.Rematch });



        const string tjaw261 = "F9433C1448F8F052BD457978CD0BFEC5";
        const string roller261 = "EC24DE233B69D5702EC68D058C0DF9ED";
        var charge261 = new Move
        {
            SrcX = 2, SrcY = 0, DstX = 2, DstY = 0, TargetX = 3, TargetY = 1,
            Facing = 2, Attack = true, LandX = 2, LandY = 2,
        };
        Check("a charge strikes from the square it charged from and the game runs the charge, never from its landing",
              charge261.Charged && charge261.StrikeFrom == (2, 0));

        var liveCharge = new Move
        {
            SrcX = 2, SrcY = 1, DstX = 2, DstY = 1, TargetX = 2, TargetY = 2,
            Facing = 2, Attack = true, LandX = 2, LandY = 3,
        };
        var chargeOwed = new Move { SrcX = 2, SrcY = 3, DstX = 3, DstY = 3, Facing = 2, LandX = 3, LandY = 3 };
        Check("the charge that exited a game on 2026-09-22 is armed from the Charger's own square, then its owed move",
              new Injector("live-probe.exe", armed: false) is var injCharge
              && injCharge.Apply([liveCharge, chargeOwed]).Result
              && injCharge.LastCommand!.Contains("attack,2,1,2,2,2,2,1;move,2,3,3,3,2"));

        Check("D-261: a plain attack, a dive and a move still strike where they always did",
              !new Move { SrcX = 1, SrcY = 1, DstX = 1, DstY = 1, Attack = true, LandX = 1, LandY = 1 }.Charged
              && new Move { SrcX = 1, SrcY = 1, DstX = 1, DstY = 1, Attack = true, LandX = 1, LandY = 1 }
                  .StrikeFrom == (1, 1)
              && new Move { SrcX = 1, SrcY = 1, DstX = 4, DstY = 4, Attack = true, AtkX = 4, AtkY = 4, LandX = 4, LandY = 4 }
                  .StrikeFrom == (4, 4)
              && !new Move { SrcX = 1, SrcY = 1, DstX = 2, DstY = 1, LandX = 2, LandY = 1 }.Charged);

        var charging = new BoardSnapshot(4, 5,
            [
                new Piece(2, 0, 10, 1, 1) { Uuid = tjaw261, Range = 2 },
                new Piece(3, 1, 12, 3, 0) { Uuid = "F5172B344148EE7E5DB47BD7A23AF9F9", Range = 3 },
            ], []) { AiSeat = 1 };
        Check("D-261: the peer's claimed landing must be where that machine's charge ends",
              Machines.TurnProblem([charge261], charging, 1) is null
              && Machines.TurnProblem([Landing(charge261, 1, 3)], charging, 1) is { } wrongLanding
              && wrongLanding.Contains("charge")
              && Machines.TurnProblem([Landing(charge261, 2, 4)], charging, 1) is not null);

        var rolling = new BoardSnapshot(4, 5, [new Piece(2, 0, 5, 1, 1) { Uuid = roller261, Range = 2 }], [])
        {
            AiSeat = 1,
        };
        Check("D-261: a machine that is not a Dash cannot claim a charge at all",
              Machines.TurnProblem([charge261], rolling, 1) is { } notADash
              && notADash.Contains("Rollerback"));

        var second202 = new Move
        {
            SrcX = 2, SrcY = 2, DstX = 2, DstY = 2, TargetX = 3, TargetY = 3,
            Facing = 2, Attack = true, LandX = 2, LandY = 4,
        };
        Check("a second charge in the same turn is checked from where the first one landed",
              Machines.TurnProblem([charge261, second202], charging, 1) is null
              && Machines.TurnProblem([charge261, Landing(second202, 2, 3)], charging, 1) is { } secondWrong
              && secondWrong.Contains("action 2") && secondWrong.Contains("charge")
              && Machines.TurnProblem([charge261, Landing(second202, 3, 2)], charging, 1) is not null);

        Check("the square a charge left holds nothing, and an action from it is passed over untested",
              Machines.TurnProblem([charge261, new Move { SrcX = 2, SrcY = 0, DstX = 2, DstY = 1, Facing = 2 }],
                                   charging, 1) is null);

        const string scrounger267 = "B78EF94227B7D36454715138E9A17144";
        const string burrower267 = "1C96A39FFE37791F8AE07BD49A2230FF";
        const string grazer267 = "2B34B3566FC1ED50071517AE89C5252F";
        const string charger268 = "FDC39FDFF4CE9C5B8190CE46AAFCF7B5";
        var reach271 = new BoardSnapshot(8, 8,
            [
                new Piece(1, 1, 5, 2, 1, Range: 1, Uuid: scrounger267),
                new Piece(5, 5, 6, 0, 1, Range: 3, Uuid: "36791A8338E8498ACD11B127EB75CF82"),
                new Piece(6, 1, 4, 2, 1, Range: 2, Uuid: charger268),
                new Piece(1, 3, 8, 0, 0, Range: 2, Uuid: "ED68990FF5589A2DA853F0D163682C40"),
                new Piece(5, 2, 5, 2, 0, Range: 1, Uuid: burrower267),
                new Piece(6, 2, 4, 0, 0, Range: 1, Uuid: burrower267),
            ], new sbyte[64]) { AiSeat = 1 };
        static Move Struck(int srcX, int srcY, int atkX, int atkY, int dstX, int dstY, int targetX, int targetY,
                           byte facing)
        {
            return new Move
            {
                SrcX = srcX, SrcY = srcY, AtkX = atkX, AtkY = atkY, DstX = dstX, DstY = dstY,
                TargetX = targetX, TargetY = targetY, Facing = facing, Attack = true,
            };
        }

        Check("an attack that leaves its machine away from the square it strikes from is refused, unless it " +
              "is a Dive landing next to its victim or a Dash landing its charge",
              Machines.TurnProblem([Struck(1, 1, 1, 2, 0, 1, 1, 3, 2)], reach271, 1) is { } strikeAway
              && strikeAway.Contains("only a Dive or a Dash")
              && Machines.TurnProblem([Struck(1, 1, 1, 2, 4, 1, 1, 3, 2),
                                       new Move
                                       {
                                           SrcX = 1, SrcY = 2, DstX = 1, DstY = 2, Facing = 2, Attack = true,
                                           TargetX = 1, TargetY = 3, Burst = true,
                                       }],
                                      reach271, 1) is { } n1Shape
              && n1Shape.Contains("action 1")
              && Machines.TurnProblem([Struck(5, 5, 5, 5, 2, 5, 5, 2, 0)], reach271, 1) is { } diveAway
              && diveAway.Contains("next to its victim")
              && Machines.TurnProblem([Struck(5, 5, 5, 5, 5, 3, 5, 2, 0)], reach271, 1) is null
              && Machines.TurnProblem([Struck(6, 1, 6, 1, 6, 3, 6, 2, 2)], reach271, 1) is null
              && Machines.TurnProblem([Struck(6, 1, 6, 1, 6, 5, 6, 2, 2)], reach271, 1) is { } dashAway
              && dashAway.Contains("charge"));

        var threeOfOurs = new BoardSnapshot(8, 8,
            [
                new Piece(1, 6, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(3, 6, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(5, 6, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(3, 4, 4, 2, 0, Range: 1, Uuid: burrower267),
            ], new sbyte[64]) { AiSeat = 1 };
        var inLine270 = new BoardSnapshot(8, 8,
            [
                new Piece(1, 6, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(3, 6, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(3, 1, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(3, 4, 4, 2, 0, Range: 1, Uuid: burrower267),
            ], new sbyte[64]) { AiSeat = 1 };
        Check("a turn with more activations than the rules allow is refused, and an attack with the move it " +
              "owes, a move with the Rotate attack it fires, and an Overcharge each add none, while a Rotate attack " +
              "owes no move, so another machine's move after it starts an activation",
              Machines.TurnProblem([new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 5, Facing = 0 },
                                    new Move { SrcX = 3, SrcY = 5, DstX = 3, DstY = 5, Facing = 0, Attack = true, TargetX = 3, TargetY = 4 },
                                    new Move { SrcX = 5, SrcY = 6, DstX = 5, DstY = 5, Facing = 0 },
                                    new Move { SrcX = 1, SrcY = 6, DstX = 1, DstY = 5, Facing = 0 }],
                                   threeOfOurs, 1) is { } rotateThenTwo
              && rotateThenTwo.Contains("3 activations")
              && Machines.TurnProblem([new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 5, Facing = 0 },
                                       new Move { SrcX = 3, SrcY = 5, DstX = 3, DstY = 5, Facing = 0, Attack = true, TargetX = 3, TargetY = 4 },
                                       new Move { SrcX = 3, SrcY = 1, DstX = 2, DstY = 1, Facing = 3 },
                                       new Move { SrcX = 1, SrcY = 6, DstX = 1, DstY = 5, Facing = 0 }],
                                      inLine270, 1) is { } rotateThenInLine
              && rotateThenInLine.Contains("3 activations")
              && Machines.TurnProblem([new Move { SrcX = 1, SrcY = 6, DstX = 1, DstY = 5, Facing = 0 },
                                       new Move { SrcX = 3, SrcY = 6, DstX = 2, DstY = 6, Facing = 3 },
                                       new Move { SrcX = 5, SrcY = 6, DstX = 5, DstY = 5, Facing = 0 }],
                                      threeOfOurs, 1) is { } threeUsed
              && threeUsed.Contains("3 activations")
              && Machines.TurnProblem([new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 6, Facing = 0, Attack = true, TargetX = 3, TargetY = 4 },
                                       new Move { SrcX = 3, SrcY = 6, DstX = 2, DstY = 6, Facing = 3 },
                                       new Move { SrcX = 1, SrcY = 6, DstX = 1, DstY = 5, Facing = 0 },
                                       new Move { SrcX = 5, SrcY = 6, DstX = 5, DstY = 5, Facing = 0 }],
                                      threeOfOurs, 1) is { } threeWithStrike
              && threeWithStrike.Contains("3 activations")
              && Machines.TurnProblem([new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 6, Facing = 0, Attack = true, TargetX = 3, TargetY = 4 },
                                       new Move { SrcX = 3, SrcY = 6, DstX = 2, DstY = 6, Facing = 3 },
                                       new Move { SrcX = 1, SrcY = 6, DstX = 1, DstY = 5, Facing = 0 },
                                       new Move { SrcX = 1, SrcY = 5, DstX = 2, DstY = 5, Facing = 1, Burst = true }],
                                      threeOfOurs, 1) is null
              && Machines.TurnProblem([new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 5, Facing = 0 },
                                       new Move { SrcX = 3, SrcY = 5, DstX = 3, DstY = 5, Facing = 0, Attack = true, TargetX = 3, TargetY = 4 },
                                       new Move { SrcX = 5, SrcY = 6, DstX = 5, DstY = 5, Facing = 0 }],
                                      threeOfOurs, 1) is null);

        var gateV12 = new BoardSnapshot(8, 8,
            [
                new Piece(4, 4, 4, 2, 1, Range: 1, Uuid: burrower267),
                new Piece(3, 6, 4, 0, 0, Range: 1, Uuid: burrower267),
                new Piece(3, 4, 4, 2, 1, Range: 1, Uuid: grazer267),
                new Piece(4, 6, 4, 0, 0, Range: 1, Uuid: grazer267),
            ], new sbyte[64]) { AiSeat = 0 };
        Check("a Ram's push and advance, its Overcharge move back from its victim's square, and a strike then an " +
              "Overcharge move onto the struck square still pass (gate-v12, turn 1)",
              Machines.TurnProblem([new Move { SrcX = 4, SrcY = 6, DstX = 4, DstY = 5, Facing = 0, Attack = true, TargetX = 4, TargetY = 4 },
                                    new Move { SrcX = 4, SrcY = 4, DstX = 4, DstY = 5, Facing = 0, Burst = true },
                                    new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 5, Facing = 0, Attack = true, TargetX = 3, TargetY = 4 },
                                    new Move { SrcX = 3, SrcY = 5, DstX = 3, DstY = 4, Facing = 0, Burst = true }],
                                   gateV12, 0) is null);

        var towPull = new BoardSnapshot(8, 8,
            [
                new Piece(2, 6, 7, 0, 1, Range: 3, Uuid: "4B962F6770CF0C4B905C3E2782E46B24"),
                new Piece(3, 3, 4, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(2, 3, 4, 2, 0, Range: 1, Uuid: grazer267),
            ], new sbyte[64]) { AiSeat = 1 };
        var towStrike = new Move { SrcX = 2, SrcY = 6, DstX = 2, DstY = 6, Facing = 0, Attack = true, TargetX = 2, TargetY = 3 };
        var towOwed = new Move { SrcX = 2, SrcY = 6, DstX = 1, DstY = 6, Facing = 3 };
        var ontoLeft = new Move { SrcX = 3, SrcY = 3, DstX = 2, DstY = 3, Facing = 3 };
        Check("a Tow that pulls its victim, its own move after, and a machine then moving onto the square the " +
              "victim left still pass",
              Machines.TurnProblem([towStrike, towOwed, ontoLeft], towPull, 1) is null);

        Check("after an attack in place, a move counts as the one it owes only from a square the attacker can " +
              "be on, so another machine's move between the attack and its owed move starts an activation",
              Machines.Activations([towStrike, ontoLeft, towOwed], 8) == 3
              && Machines.Activations([towStrike, towOwed, ontoLeft], 8) == 2
              && Machines.Activations([towStrike, towOwed], 8) == 1
              && Machines.Activations([new Move { SrcX = 4, SrcY = 3, DstX = 4, DstY = 3, Facing = 0, Attack = true, TargetX = 4, TargetY = 2 },
                                       new Move { SrcX = 4, SrcY = 1, DstX = 5, DstY = 1, Facing = 3 }], 6) == 1);

        var haltHunter = new BoardSnapshot(6, 5,
            [
                new Piece(4, 3, 10, 0, 0, Range: 2, Skill: 5, Uuid: "23C2AA3CCB2680F418CA1D7E9F179E9D"),
                new Piece(5, 0, 4, 2, 1, Range: 1, Uuid: burrower267),
                new Piece(5, 2, 8, 0, 0, Range: 3, Skill: 5, Uuid: "7875B4B22E79B8BF0996D4B74BCC0477"),
                new Piece(5, 4, 4, 0, 0, Range: 1, Uuid: "C5A08FEFF4757D3A3BD4A614A0B5E948"),
                new Piece(4, 2, 3, 2, 1, Range: 1, Uuid: "76D3C3711B67863E82D8C703AAB1A815"),
                new Piece(3, 3, 7, 0, 0, Range: 3, Uuid: "4B962F6770CF0C4B905C3E2782E46B24"),
                new Piece(3, 0, 7, 2, 1, Range: 1, Uuid: "05662ED22BB56D93305986970E81E6C4"),
                new Piece(4, 4, 2, 0, 0, Range: 1, Uuid: "A70D3B57757E0A4692DF0D4511231EDE"),
                new Piece(4, 0, 10, 2, 1, Range: 2, Uuid: "370A5C1EE3F50A95FE7A04BE068355FC"),
            ], new sbyte[30]) { AiSeat = 0 };
        Check("a charge read without its landing, the charger's next move from where it landed, and moves onto the " +
              "square it charged from and onto its victim's square still pass (halt-hunter, turn 2)",
              Machines.TurnProblem([new Move { SrcX = 4, SrcY = 3, DstX = 4, DstY = 3, Facing = 0, Attack = true, TargetX = 4, TargetY = 2 },
                                    new Move { SrcX = 4, SrcY = 1, DstX = 5, DstY = 1, Facing = 3 },
                                    new Move { SrcX = 4, SrcY = 4, DstX = 4, DstY = 3, Facing = 1 },
                                    new Move { SrcX = 4, SrcY = 3, DstX = 4, DstY = 2, Facing = 1, Burst = true }],
                                   haltHunter, 0) is null);

        var walkCharge268 = new BoardSnapshot(4, 4,
            [
                new Piece(3, 1, 2, 0, 1, Range: 2, Uuid: charger268),
                new Piece(1, 0, 4, 2, 0, Range: 2, Uuid: charger268),
                new Piece(0, 2, 1, 0, 1, Range: 1, Uuid: burrower267),
                new Piece(1, 2, 2, 0, 0, Range: 1, Uuid: burrower267),
            ], new sbyte[16]) { AiSeat = 0 };
        var inPlaceCharge268 = new BoardSnapshot(4, 4,
            [
                new Piece(1, 2, 4, 0, 0, Range: 2, Uuid: charger268),
                new Piece(3, 1, 4, 2, 1, Range: 2, Uuid: charger268),
                new Piece(2, 2, 4, 0, 0, Range: 1, Uuid: burrower267),
                new Piece(1, 1, 4, 2, 1, Range: 1, Uuid: burrower267),
            ], new sbyte[16]) { AiSeat = 0 };
        Check("a walk then a charge, and a charge in place, each followed by the charger's move from its landing, " +
              "still pass (walk-then-charge-ending, PC1's last turn and PC2's turn 1)",
              Machines.TurnProblem([new Move
                                    {
                                        SrcX = 1, SrcY = 0, DstX = 3, DstY = 0, Facing = 2, Attack = true,
                                        TargetX = 3, TargetY = 1, LandX = 3, LandY = 2,
                                    },
                                    new Move { SrcX = 3, SrcY = 2, DstX = 2, DstY = 2, Facing = 3, Burst = true }],
                                   walkCharge268, 0) is null
              && Machines.TurnProblem([new Move
                                       {
                                           SrcX = 1, SrcY = 2, DstX = 1, DstY = 2, Facing = 0, Attack = true,
                                           TargetX = 1, TargetY = 1, LandX = 1, LandY = 0,
                                       },
                                       new Move { SrcX = 1, SrcY = 0, DstX = 0, DstY = 0, Facing = 0, LandX = 0, LandY = 0 },
                                       new Move { SrcX = 2, SrcY = 2, DstX = 3, DstY = 2, Facing = 1, LandX = 3, LandY = 2 }],
                                      inPlaceCharge268, 0) is null);

        var dyingAttacker = new BoardSnapshot(8, 8,
            [
                new Piece(3, 5, 8, 0, 1, Range: 1, Uuid: "82F13F6222FCF687A5684CC1F9B96F4F"),
                new Piece(5, 7, 10, 0, 0, Range: 2, Uuid: "D132E56AAEF7AC7F24DBC79886876512"),
                new Piece(4, 3, 8, 2, 1, Range: 2, Uuid: "373D8F477EAEAF6BE80EFF08610BBA8F"),
                new Piece(7, 4, 5, 2, 1, Range: 2, Uuid: "D7570C9AAEC2D94C677452D1861A99DA"),
                new Piece(1, 1, 6, 2, 1, Range: 3, Uuid: "36791A8338E8498ACD11B127EB75CF82"),
                new Piece(5, 6, 5, 0, 0, Range: 3, Uuid: "4726C13DD5722AF80595854DA7E2FCBF"),
            ], new sbyte[64]) { AiSeat = 0 };
        Check("a Dive with its firing square on the wire, its Overcharge move onto its victim's square after the " +
              "knockback, and another machine's walk, strike and Overcharge strike still pass (dying-attacker, turn 2)",
              Machines.TurnProblem([Struck(5, 6, 7, 6, 7, 5, 7, 4, 0),
                                    new Move { SrcX = 7, SrcY = 5, DstX = 7, DstY = 4, Facing = 0, Burst = true },
                                    new Move { SrcX = 5, SrcY = 7, DstX = 3, DstY = 6, Facing = 0, Attack = true, TargetX = 3, TargetY = 5 },
                                    new Move
                                    {
                                        SrcX = 3, SrcY = 6, DstX = 3, DstY = 6, Facing = 0, Attack = true,
                                        TargetX = 3, TargetY = 5, Burst = true,
                                    }],
                                   dyingAttacker, 0) is null);

        var relocatedDive = new BoardSnapshot(8, 8,
            [
                new Piece(4, 4, 2, 0, 0, Range: 3, Uuid: "4726C13DD5722AF80595854DA7E2FCBF"),
                new Piece(5, 3, 6, 2, 1, Range: 2, Uuid: "D132E56AAEF7AC7F24DBC79886876512"),
                new Piece(2, 5, 1, 0, 0, Range: 3, Skill: 5, Uuid: "7875B4B22E79B8BF0996D4B74BCC0477"),
                new Piece(4, 5, 11, 2, 1, Range: 3, Uuid: "F5172B344148EE7E5DB47BD7A23AF9F9"),
                new Piece(3, 4, 7, 0, 0, Range: 2, Uuid: "D7570C9AAEC2D94C677452D1861A99DA"),
                new Piece(2, 4, 9, 0, 0, Range: 3, Uuid: "435534A445562BA16633AF4B908D83B2"),
                new Piece(2, 3, 5, 2, 1, Range: 2, Uuid: "ADEA18E33DA2CAF15010D29CA12FE1F3"),
                new Piece(4, 6, 7, 0, 0, Range: 2, Uuid: "D7570C9AAEC2D94C677452D1861A99DA"),
            ], new sbyte[64]) { AiSeat = 0 };
        Check("a Dive that relocated beside its victim with no firing square on the wire, then moved back from " +
              "there, still passes (inplace-shot-facing, PC2's turn 4)",
              Machines.TurnProblem([new Move
                                    {
                                        SrcX = 4, SrcY = 4, DstX = 5, DstY = 5, Facing = 0, Attack = true,
                                        TargetX = 5, TargetY = 3, LandX = 5, LandY = 5,
                                    },
                                    new Move { SrcX = 5, SrcY = 4, DstX = 5, DstY = 5, Facing = 0, Burst = true, LandX = 5, LandY = 5 },
                                    new Move { SrcX = 2, SrcY = 5, DstX = 1, DstY = 5, Facing = 2, LandX = 1, LandY = 5 }],
                                   relocatedDive, 0) is null);

        Check("a turn the live board check stopped part way halts with its own reason, worded for an action that " +
              "could not be played or could not be checked, which says the actions before it are already on this board",
              Injector.HaltReason(Injector.GateRefused, 3) is { } gateHalt
              && gateHalt.Contains("could not play or could not check") && gateHalt.Contains("already on this board")
              && !gateHalt.Contains("injection failed")
              && Injector.HaltReason(6, 3) is { } otherHalt
              && otherHalt.Contains("injection failed") && otherHalt.Contains("3 action(s)")
              && !gateHalt.Contains(';') && !otherHalt.Contains(';')
              && Injector.GateSummaryOf(["  game time 4.2 s", "  gate: 4 checkpoints, least margin 270 ms"])
                 == "gate: 4 checkpoints, least margin 270 ms");

        var staleExit = new Injector("live-probe.exe", armed: false);
        staleExit.LastExit = Injector.GateRefused;
        var staleApplied = staleExit.Apply([new Move { SrcX = 1, SrcY = 6, DstX = 1, DstY = 5, Facing = 0 }]).Result;
        Check("only the live board check's own halt adds that a release of the freeze can close the game, and a turn " +
              "whose live-probe never ran does not reuse the last turn's exit as the check's halt",
              FrozenLineFor(Injector.GateHaltText) is { } gateFrozen
              && gateFrozen.StartsWith(FrozenLine, StringComparison.Ordinal) && gateFrozen.Contains("can close the game")
              && FrozenLine.Contains("Only a person releases it") && !FrozenLine.Contains("close the game")
              && FrozenLineFor(Injector.HaltReason(6, 3)) == FrozenLine
              && FrozenLineFor(Peer.RelayedHaltReason(Injector.GateHaltText)) == FrozenLine
              && !gateFrozen.Contains(';')
              && staleApplied && staleExit.LastExit == Injector.NoExit
              && Injector.HaltReason(staleExit.LastExit, 1) != Injector.GateHaltText);

        var buffed204 = new BoardSnapshot(4, 5, [new Piece(2, 0, 10, 1, 1) { Uuid = tjaw261, Range = 3 }], [])
        {
            AiSeat = 1,
        };
        var unknown204 = new BoardSnapshot(4, 5, [new Piece(2, 0, 10, 1, 1) { Uuid = tjaw261 }], [])
        {
            AiSeat = 1,
        };
        Check("a charge is measured by the live range the board carries, and the table's only without one",
              Machines.TurnProblem([Landing(charge261, 2, 3)], buffed204, 1) is null
              && Machines.TurnProblem([charge261], buffed204, 1) is { } byTable
              && byTable.Contains("(2,3)")
              && Machines.TurnProblem([Landing(charge261, 2, 4)], buffed204, 1) is not null
              && unknown204.Pieces[0].Range < 1
              && Machines.TurnProblem([charge261], unknown204, 1) is null);
        Check("a halt keeps the link open for the files alone, for a bounded time",
              Peer.DrainCarries(MsgKind.Recording)
              && !Peer.DrainCarries(MsgKind.Move) && !Peer.DrainCarries(MsgKind.Halt)
              && !Peer.DrainCarries(MsgKind.Place) && !Peer.DrainCarries(MsgKind.Hash)
              && !Peer.DrainCarries(MsgKind.Rematch) && !Peer.DrainCarries(MsgKind.Sealed)
              && Peer.HaltDrain > TimeSpan.Zero && Peer.HaltDrain <= TimeSpan.FromMinutes(1));

        Check("D-263: the drain carries the three files a halt sends and nothing else",
              Peer.DrainCarries(MsgKind.Recording) && Peer.DrainCarries(MsgKind.RecordingStart)
              && Peer.DrainCarries(MsgKind.Log)
              && !Peer.DrainCarries(MsgKind.Halt) && !Peer.DrainCarries(MsgKind.Sealed)
              && !Peer.DrainCarries(MsgKind.Move) && !Peer.DrainCarries(MsgKind.Hash));

        Check("the drain outlasts the three paced sends and the wait before them",
              Program.RecordingPace.TotalSeconds * FrameLimits.MaxHaltParts
                  + Program.HaltSettle.TotalSeconds < Peer.HaltDrain.TotalSeconds
              && Program.HaltSettle > TimeSpan.Zero
              && Program.RecordingPace.TotalSeconds * FrameLimits.MaxFramesPerWindow
                 > FrameLimits.RateWindow.TotalSeconds);

        Check("D-263: the three files a halt sends add up to the parts a halt may send",
              FrameLimits.MaxRecordingStartParts + FrameLimits.MaxRecordingTailParts + FrameLimits.MaxLogParts
                  == FrameLimits.MaxHaltParts
              && FrameLimits.MaxHaltParts <= FrameLimits.MaxRecordingParts
              && FrameLimits.MaxRecordingStartBytes < FrameLimits.MaxRecordingTailBytes
              && FrameLimits.MaxRecordingTailBytes <= FrameLimits.MaxRecordingBytes);

        var biggest = new Frame
        {
            Kind = MsgKind.Recording,
            Part = FrameLimits.MaxRecordingParts - 1,
            Parts = FrameLimits.MaxRecordingParts,
            Data = new string('A', FrameLimits.MaxRecordingPartChars),
        };
        var keys = KeyExchange.Complete("CODE", KeyExchange.Begin(), KeyExchange.PublicBlob(KeyExchange.Begin()),
                                        isHost: true);
        var sealedBiggest = new SecureChannel(keys!).Seal(Protocol.Encode(biggest));
        Check("a part at its cap still fits the line cap once it is sealed and enveloped",
              Protocol.Encode(new Frame { Kind = MsgKind.Sealed, Box = sealedBiggest }).Length
                  < FrameLimits.MaxLine);

        var biggestLog = new Frame
        {
            Kind = MsgKind.Log,
            Part = FrameLimits.MaxLogParts - 1,
            Parts = FrameLimits.MaxLogParts,
            Data = new string('A', FrameLimits.MaxRecordingPartChars),
        };
        var biggestStart = new Frame
        {
            Kind = MsgKind.RecordingStart,
            Part = FrameLimits.MaxRecordingStartParts - 1,
            Parts = FrameLimits.MaxRecordingStartParts,
            Data = new string('A', FrameLimits.MaxRecordingPartChars),
        };
        var sealedLog = new SecureChannel(keys!).Seal(Protocol.Encode(biggestLog));
        var sealedStart = new SecureChannel(keys!).Seal(Protocol.Encode(biggestStart));
        Check("D-263: a log part and a start part at their cap fit the line cap once sealed and enveloped",
              Protocol.Encode(new Frame { Kind = MsgKind.Sealed, Box = sealedLog }).Length < FrameLimits.MaxLine
              && Protocol.Encode(new Frame { Kind = MsgKind.Sealed, Box = sealedStart }).Length
                 < FrameLimits.MaxLine);

        Check("D-263: a Log frame and a RecordingStart frame round-trip as themselves",
              Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Log, Part = 2, Parts = 3, Data = "AAAA" }))
                  is { Kind: MsgKind.Log, Part: 2, Parts: 3, Data: "AAAA" }
              && Protocol.Decode(Protocol.Encode(new Frame
                 {
                     Kind = MsgKind.RecordingStart,
                     Part = 0,
                     Parts = 1,
                     Data = "AAAA",
                 })) is { Kind: MsgKind.RecordingStart, Part: 0, Parts: 1, Data: "AAAA" });

        Check("a name crosses as hex, so no name can spell a halt on a console line",
              Names.Hex("HALT") == "48414C54"
              && !Names.Hex("HALT").Contains("HALT", StringComparison.OrdinalIgnoreCase)
              && !Names.Hex("ASPHALT").Contains("HALT", StringComparison.OrdinalIgnoreCase)
              && Names.Hex("BOB") == "424F42");
        var recordingBytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 100 * 1024));
        var recordingParts = Recordings.Parts(recordingBytes);
        Check("D-260: a recording crosses as parts inside the frame cap, each numbered and counted",
              recordingParts.Count == 5
              && recordingParts.All(p => p.Kind == MsgKind.Recording && p.Parts == 5)
              && recordingParts.Select(p => p.Part).SequenceEqual([0, 1, 2, 3, 4])
              && recordingParts.All(p => (p.Data ?? "").Length <= FrameLimits.MaxRecordingPartChars)
              && recordingParts.All(p => Protocol.Encode(p).Length < FrameLimits.MaxLine)
              && Recordings.Parts([]).Count == 0);

        var takenIn = NewTailFile();
        string? feedProblem = null;
        foreach (var part in recordingParts)
        {
            feedProblem ??= takenIn.Offer(part, halted: true);
        }

        Check("D-260: the parts of a halted peer's recording assemble back into the same bytes",
              feedProblem is null && takenIn.Done && takenIn.Taken is not null
              && takenIn.Taken!.SequenceEqual(recordingBytes));

        Check("D-260: a recording is taken only while this PC is halted, and only once",
              NewTailFile().Offer(recordingParts[0], halted: false) is not null
              && takenIn.Offer(recordingParts[0], halted: true) is not null);

        var outOfOrder = NewTailFile();
        var overCount = NewTailFile();
        var junk = NewTailFile();
        Check("D-260: a recording out of order, over the part count, or not readable is refused",
              outOfOrder.Offer(recordingParts[1], halted: true) is not null
              && overCount.Offer(new Frame
              {
                  Kind = MsgKind.Recording,
                  Part = 0,
                  Parts = FrameLimits.MaxRecordingParts + 1,
                  Data = "AAAA",
              }, halted: true) is not null
              && junk.Offer(new Frame { Kind = MsgKind.Recording, Part = 0, Parts = 1, Data = "not base64!" },
                            halted: true) is not null);

        var oversize = NewTailFile();
        var oversizePart = new string('A', FrameLimits.MaxRecordingPartChars + 4);
        Check("D-260: a part over the size a part may be is refused before it is decoded",
              oversize.Offer(new Frame { Kind = MsgKind.Recording, Part = 0, Parts = 2, Data = oversizePart },
                             halted: true) is not null);

        var halfway = NewTailFile();
        _ = halfway.Offer(recordingParts[0], halted: true);
        _ = halfway.Offer(recordingParts[1], halted: true);
        Check("D-262: a recording that never finished says how far it got, and a finished one says nothing",
              takenIn.Unfinished() is null
              && NewTailFile().Unfinished() is { } never && never.Contains("did not arrive")
              && halfway.Unfinished() is { } half && half.Contains("2 of 5")
              && outOfOrder.Unfinished() is { } dropped && dropped.Contains("refused"));

        Check("D-262: the other PC's recording is named after this PC's recording of the same match",
              Recordings.NameFor(@"C:\Other\recordings\match-20260921-175500.jsonl", new DateTime(2026, 9, 21, 18, 0, 0))
                  == "opponent-20260921-175500.jsonl"
              && Recordings.NameFor("match-20260921-175500.jsonl", DateTime.Now) == "opponent-20260921-175500.jsonl"
              && Recordings.NameFor(null, new DateTime(2026, 9, 21, 18, 0, 0)) == "opponent-20260921-180000.jsonl"
              && Recordings.NameFor(@"C:\Other\stale.jsonl", new DateTime(2026, 9, 21, 18, 0, 0))
                  == "opponent-20260921-180000.jsonl"
              && Recordings.NameFor("match-.jsonl", new DateTime(2026, 9, 21, 18, 0, 0))
                  == "opponent-20260921-180000.jsonl");

        Check("D-263: the start and the log are named after this PC's recording of the same match",
              Recordings.StartNameFor(@"C:\Other\recordings\match-20260921-175500.jsonl",
                                      new DateTime(2026, 9, 21, 18, 0, 0))
                  == "opponent-20260921-175500-start.jsonl"
              && Recordings.LogNameFor(@"C:\Other\recordings\match-20260921-175500.jsonl",
                                       new DateTime(2026, 9, 21, 18, 0, 0))
                 == "opponent-20260921-175500.log"
              && Recordings.StartNameFor(null, new DateTime(2026, 9, 21, 18, 0, 0))
                 == "opponent-20260921-180000-start.jsonl"
              && Recordings.LogNameFor(null, new DateTime(2026, 9, 21, 18, 0, 0))
                 == "opponent-20260921-180000.log");

        var nasty = new byte[]
        {
            0x1B, (byte)'[', (byte)'3', (byte)'1', (byte)'m', 0xE2, 0x80, 0xAE,
            (byte)'a', 0x09, 0x0A, 0x0D, 0x7F, 0x00,
        };
        var scrubbed = Recordings.Scrubbed(nasty);
        Check("D-263: what the other PC sends is scrubbed to plain text before it is written",
              scrubbed.Length == nasty.Length
              && scrubbed[0] == (byte)'?' && scrubbed[5] == (byte)'?' && scrubbed[6] == (byte)'?'
              && scrubbed[7] == (byte)'?' && scrubbed[12] == (byte)'?' && scrubbed[13] == (byte)'?'
              && scrubbed[1] == (byte)'[' && scrubbed[8] == (byte)'a'
              && scrubbed[9] == 0x09 && scrubbed[10] == 0x0A && scrubbed[11] == 0x0D
              && !scrubbed.Contains((byte)0x1B) && !scrubbed.Contains((byte)0xE2));

        var lineDir = Directory.CreateTempSubdirectory("strikers-selftest-lines-");
        try
        {
            var linePath = Path.Combine(lineDir.FullName, "match-20260921-175500.jsonl");
            var made = new System.Text.StringBuilder();
            for (var i = 0; i < 40; i++)
            {
                made.Append(new string((char)('a' + (i % 26)), 99));
                made.Append('\n');
            }

            File.WriteAllText(linePath, made.ToString());
            var headRead = Recordings.Head(linePath, 250);
            var tailRead = Recordings.Tail(linePath, 250);
            Check("D-263: the start of a recording is cut back to the last whole line",
                  headRead is { Length: 200 } && headRead[^1] == (byte)'\n' && headRead[0] == (byte)'a'
                  && Recordings.Head(linePath, 8000) is { Length: 4000 });

            Check("D-263: the tail of a recording is cut forward to the first whole line",
                  tailRead is { Length: 200 } && tailRead[^1] == (byte)'\n' && tailRead[0] == (byte)'m'
                  && Recordings.Tail(linePath, 8000) is { Length: 4000 });

            var emptyPath = Path.Combine(lineDir.FullName, "empty.jsonl");
            File.WriteAllText(emptyPath, "");
            Check("D-263: a file that is missing or empty is read as nothing to send",
                  Recordings.Head(Path.Combine(lineDir.FullName, "gone.jsonl"), 250) is null
                  && Recordings.Tail(Path.Combine(lineDir.FullName, "gone.jsonl"), 250) is null
                  && Recordings.Head(emptyPath, 250) is null && Recordings.Tail(emptyPath, 250) is null
                  && Recordings.LogTail(lineDir.FullName) is null);

            File.WriteAllText(Path.Combine(lineDir.FullName, Recordings.LogName), "one line\ntwo lines\n");
            using var held = new FileStream(Path.Combine(lineDir.FullName, Recordings.LogName),
                                            FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Check("D-263: the log is read from netplay's own folder while the launcher still holds it open",
                  Recordings.LogTail(lineDir.FullName) is { Length: 19 });
        }
        finally
        {
            Directory.Delete(lineDir.FullName, recursive: true);
        }

        var oneLine = System.Text.Encoding.UTF8.GetBytes("{\"sample\":1}\n");
        var startParts = Recordings.Parts(oneLine, MsgKind.RecordingStart, FrameLimits.MaxRecordingStartParts);
        var logParts = Recordings.Parts(oneLine, MsgKind.Log, FrameLimits.MaxLogParts);
        var tailParts = Recordings.Parts(oneLine, MsgKind.Recording, FrameLimits.MaxRecordingTailParts);
        var intoStart = NewStartFile();
        var intoLog = NewLogFile();
        var intoTail = NewTailFile();
        var startProblem = intoStart.Offer(startParts[0], halted: true);
        var logProblem = intoLog.Offer(logParts[0], halted: true);
        var tailProblem = intoTail.Offer(tailParts[0], halted: true);
        var secondLog = intoLog.Offer(logParts[0], halted: true);
        Check("D-263: the three files land in three receivers, and a second log is dropped",
              startParts[0].Kind == MsgKind.RecordingStart && logParts[0].Kind == MsgKind.Log
              && tailParts[0].Kind == MsgKind.Recording
              && startProblem is null && logProblem is null && tailProblem is null
              && intoStart.Done && intoLog.Done && intoTail.Done
              && intoStart.Taken!.SequenceEqual(oneLine) && intoLog.Taken!.SequenceEqual(oneLine)
              && intoTail.Taken!.SequenceEqual(oneLine)
              && secondLog is { } dropped263 && dropped263.Contains("a second time"));

        Check("D-263: a file over the parts its kind may send is not sent at all",
              Recordings.Parts(new byte[FrameLimits.MaxRecordingStartBytes * 2], MsgKind.RecordingStart,
                               FrameLimits.MaxRecordingStartParts) is []
              && Recordings.Parts(new byte[FrameLimits.MaxLogBytes * 2], MsgKind.Log, FrameLimits.MaxLogParts) is []
              && Recordings.Parts([], MsgKind.Log, FrameLimits.MaxLogParts) is []);

        var nineLogs = new Frame
        {
            Kind = MsgKind.Log,
            Part = 0,
            Parts = FrameLimits.MaxLogParts + 1,
            Data = "AAAA",
        };
        var sevenStarts = new Frame
        {
            Kind = MsgKind.RecordingStart,
            Part = 0,
            Parts = FrameLimits.MaxRecordingStartParts + 1,
            Data = "AAAA",
        };
        Check("a file is held to the parts its own kind may send, not the parts a whole halt may send",
              NewLogFile().Offer(nineLogs, halted: true) is { } tooManyLogs
              && tooManyLogs.Contains($"claimed {FrameLimits.MaxLogParts + 1} parts")
              && NewStartFile().Offer(sevenStarts, halted: true) is { } tooManyStarts
              && tooManyStarts.Contains($"claimed {FrameLimits.MaxRecordingStartParts + 1} parts")
              && NewTailFile().Offer(new Frame
                 {
                     Kind = MsgKind.Recording,
                     Part = 0,
                     Parts = FrameLimits.MaxRecordingTailParts + 1,
                     Data = "AAAA",
                 }, halted: true) is not null
              && nineLogs.Parts <= FrameLimits.MaxRecordingParts
              && sevenStarts.Parts <= FrameLimits.MaxRecordingParts);

        var fullPart198 = new string('A', FrameLimits.MaxRecordingPartChars);
        var fillingStart = NewStartFile();
        string? startOverfull = null;
        for (var i = 0; i < FrameLimits.MaxRecordingStartParts && startOverfull is null; i++)
        {
            startOverfull = fillingStart.Offer(new Frame
            {
                Kind = MsgKind.RecordingStart,
                Part = i,
                Parts = FrameLimits.MaxRecordingStartParts,
                Data = fullPart198,
            }, halted: true);
        }

        var partBytes198 = FrameLimits.MaxRecordingPartChars / 4 * 3;
        Check("a file is held to the bytes its own kind may send, inside the parts it may send",
              startOverfull is { } overfull && overfull.Contains("over the size a file may be")
              && FrameLimits.MaxRecordingStartParts * partBytes198 > FrameLimits.MaxRecordingStartBytes
              && FrameLimits.MaxRecordingTailParts * partBytes198 == FrameLimits.MaxRecordingTailBytes
              && FrameLimits.MaxLogParts * partBytes198 == FrameLimits.MaxLogBytes);

        Check("the three kinds' caps each sit under the ceiling a file may claim, and add up to a halt's",
              FrameLimits.MaxRecordingStartParts <= FrameLimits.MaxRecordingParts
              && FrameLimits.MaxRecordingTailParts <= FrameLimits.MaxRecordingParts
              && FrameLimits.MaxLogParts <= FrameLimits.MaxRecordingParts
              && FrameLimits.MaxRecordingStartBytes <= FrameLimits.MaxRecordingBytes
              && FrameLimits.MaxRecordingTailBytes <= FrameLimits.MaxRecordingBytes
              && FrameLimits.MaxLogBytes <= FrameLimits.MaxRecordingBytes
              && FrameLimits.MaxRecordingStartParts + FrameLimits.MaxRecordingTailParts + FrameLimits.MaxLogParts
                 == FrameLimits.MaxHaltParts);

        var wentOut = new[]
        {
            WentOut(OurStartWhat, 3, 3), WentOut(OurRecordingWhat, 4, 4), WentOut(OurLogWhat, 2, 2),
            WentOut(OurStartWhat, 1, 3), WentOut(OurRecordingWhat, 2, 4), WentOut(OurLogWhat, 1, 2),
        };
        var neverCame = new[]
        {
            NewStartFile().Unfinished()!,
            NewTailFile().Unfinished()!,
            NewLogFile().Unfinished()!,
        };
        Check("D-263: each file says it went out, whole or in part, in a sentence with no semicolon",
              wentOut[0] == "  -> the start of this PC's recording went out, 3 part(s)"
              && wentOut[1] == "  -> this PC's recording of the match went out, 4 part(s)"
              && wentOut[2] == "  -> this PC's log went out, 2 part(s)"
              && wentOut[3] == "  -> the start of this PC's recording went out in part, 1 of 3 part(s)"
              && wentOut[5] == "  -> this PC's log went out in part, 1 of 2 part(s)"
              && wentOut.All(l => !l.Contains(';')));

        Check("D-263: each file that never came says so by name, in a sentence with no semicolon",
              neverCame[0] == "the start of the other player's recording did not arrive"
              && neverCame[1] == "the other player's recording of the match did not arrive"
              && neverCame[2] == "the other player's log did not arrive"
              && neverCame.All(l => !l.Contains(';')));

        var neverOpens = new Peer("127.0.0.1", port, "HELDRM", 0);
        Check("a part still waiting on a channel that never opened does not count as sent",
              !neverOpens.TrySend(tailParts[0])
              && !neverOpens.Halted
              && WentOut(OurRecordingWhat, 0, 3)
                 == "  -> this PC's recording of the match went out in part, 0 of 3 part(s)");

        ForgetRecordings();
        Check("no stamp is taken before the first file frame of a session",
              StampTaken() is null);

        var named200 = new[]
        {
            NameForKind(MsgKind.RecordingStart, null)!,
            NameForKind(MsgKind.Recording, null)!,
            NameForKind(MsgKind.Log, null)!,
        };
        Check("with no recording of our own the three files still take one stamp between them",
              StampTaken() is { } stamp200
              && named200[0] == Recordings.StartNameFor(null, stamp200)
              && named200[1] == Recordings.NameFor(null, stamp200)
              && named200[2] == Recordings.LogNameFor(null, stamp200)
              && named200.All(n => Captures.Family(n, Recordings.OpponentPrefix)
                                   == $"{stamp200:yyyyMMdd-HHmmss}")
              && NameForKind(MsgKind.Recording, @"C:\Other\recordings\match-20260921-175500.jsonl")
                 == "opponent-20260921-175500.jsonl"
              && NameForKind(MsgKind.Move, null) is null);

        ForgetRecordings();
        Check("the stamp is forgotten with the receivers, so the next halt names its own family",
              StampTaken() is null);

        var opponentNames = new[]
        {
            "opponent-20260920-100000.jsonl", "opponent-20260920-110000.jsonl",
            "opponent-20260920-120000.jsonl", "opponent-20260920-130000.jsonl",
            "opponent-20260920-140000.jsonl", "opponent-20260920-150000.jsonl",
            "match-20260920-090000.jsonl", "notes.txt",
        };
        Check("the recordings the other PC sends are pruned to five like our own, and nothing else is touched",
              Captures.Stale(opponentNames, Captures.Keep, Recordings.OpponentPrefix)
                  is ["opponent-20260920-100000.jsonl", "opponent-20260920-110000.jsonl"]
              && Captures.Stale(opponentNames, Captures.Keep) is []
              && Captures.Stale(["opponent-1.txt"], 1, Recordings.OpponentPrefix) is []);

        var families = new[]
        {
            "opponent-20260921-100000.jsonl", "opponent-20260921-100000-start.jsonl", "opponent-20260921-100000.log",
            "opponent-20260921-110000.jsonl", "opponent-20260921-110000.log",
            "opponent-20260921-120000.jsonl", "opponent-20260921-130000.jsonl",
            "opponent-20260921-140000-start.jsonl", "opponent-20260921-150000.log",
            "opponent-notes.txt", "match-20260921-100000.jsonl",
        };
        Check("D-263: the other PC's three files of one match are pruned as one family, a second file of the same match "
              + "costs no older match, and a file that is not one of the three is left alone",
              Captures.Family("opponent-20260921-100000-start.jsonl", Recordings.OpponentPrefix) == "20260921-100000"
              && Captures.Family("opponent-20260921-100000.log", Recordings.OpponentPrefix) == "20260921-100000"
              && Captures.Family("opponent-20260921-100000.jsonl", Recordings.OpponentPrefix) == "20260921-100000"
              && Captures.Stale(families, Captures.Keep, Recordings.OpponentPrefix)
                  is ["opponent-20260921-100000-start.jsonl", "opponent-20260921-100000.jsonl",
                      "opponent-20260921-100000.log", "opponent-20260921-110000.jsonl", "opponent-20260921-110000.log"]
              && Captures.Stale(families, Captures.Keep, Recordings.OpponentPrefix, "20260921-150000")
                  is ["opponent-20260921-100000-start.jsonl", "opponent-20260921-100000.jsonl",
                      "opponent-20260921-100000.log"]
              && Captures.Stale(families, Captures.Keep, Recordings.OpponentPrefix, "20260921-160000")
                  is ["opponent-20260921-100000-start.jsonl", "opponent-20260921-100000.jsonl",
                      "opponent-20260921-100000.log", "opponent-20260921-110000.jsonl", "opponent-20260921-110000.log"]
              && Captures.Stale(families, Captures.Keep) is []
              && !Captures.Stale(families, 1, Recordings.OpponentPrefix).Contains("opponent-notes.txt"));

        Check("D-260: the file the other PC's recording lands in is named by this PC, in the recordings folder",
              Recordings.Name(new DateTime(2026, 9, 20, 14, 5, 6)) == "opponent-20260920-140506.jsonl"
              && Recordings.Name(DateTime.Now).StartsWith(Recordings.OpponentPrefix, StringComparison.Ordinal)
              && Recordings.Name(DateTime.Now).EndsWith(".jsonl", StringComparison.Ordinal));

        var landing = new Move { SrcX = 2, SrcY = 0, DstX = 2, DstY = 0, LandX = 2, LandY = 2, Attack = true };
        var landedBack = landing.Rotated(4, 5);
        Check("D-260: the square a machine stood on after its action crosses and rotates with the rest",
              landedBack.LandX == 1 && landedBack.LandY == 2
              && new Move { LandX = -1, LandY = -1 }.Rotated(4, 5) is { LandX: -1, LandY: -1 }
              && FrameLimits.SquaresOff([new Move { LandX = 4, LandY = 0 }], 4, 5) is not null
              && FrameLimits.SquaresOff([new Move { LandX = -2, LandY = 0 }], 4, 5) is not null
              && FrameLimits.SquaresOff([landing], 4, 5) is null);
        var rematchBack = Protocol.Decode(rematchLine);
        Check("a Rematch frame round-trips as a Rematch",
              rematchBack is { Kind: MsgKind.Rematch });
        Check("a Rematch frame carries the current protocol version",
              rematchBack!.Version == Protocol.Version && Protocol.Version == 30);

        Check("a Confirmed frame round-trips and is never refused by the phase gate",
              Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Confirmed })) is { Kind: MsgKind.Confirmed }
              && FrameGate.Refuse(SessionPhase.NoMatch, new Frame { Kind = MsgKind.Confirmed }, new HashSet<int>(), new HashSet<(int X, int Y)>()) is null
              && FrameGate.Refuse(SessionPhase.Playing, new Frame { Kind = MsgKind.Confirmed }, new HashSet<int>(), new HashSet<(int X, int Y)>()) is null);

        var queued = new List<Frame>
        {
            new() { Kind = MsgKind.Confirmed },
            new() { Kind = MsgKind.Hash },
            new() { Kind = MsgKind.Ready },
        };
        var keptOnNewKey = Peer.KeepOnNewKey(queued);
        Check("a confirmation queued through a link drop does not cross onto the new key",
              keptOnNewKey.Count == 2 && !keptOnNewKey.Any(f => f.Kind == MsgKind.Confirmed));
        Check("a turn's own frames queued through the same drop are kept",
              keptOnNewKey[0].Kind == MsgKind.Hash && keptOnNewKey[1].Kind == MsgKind.Ready);

        Check("a Left frame round-trips with its seat and is never refused by the phase gate",
              Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Left, Seat = 1 })) is { Kind: MsgKind.Left, Seat: 1 }
              && FrameGate.Refuse(SessionPhase.NoMatch, new Frame { Kind = MsgKind.Left }, new HashSet<int>(), new HashSet<(int X, int Y)>()) is null
              && FrameGate.Refuse(SessionPhase.Playing, new Frame { Kind = MsgKind.Left }, new HashSet<int>(), new HashSet<(int X, int Y)>()) is null);

        var shapeKeeper = new Lobby("BUILD", isHost: true);
        shapeKeeper.ChooseBoard([.. new int[18]], Preset.RuleNotSet, Preset.RuleNotSet, 3, 6, 1);
        shapeKeeper.ChooseBoard(shapeKeeper.Board, 20, 40,
                                shapeKeeper.BoardWidth, shapeKeeper.BoardHeight, shapeKeeper.PlacementRows);
        var keptShape = shapeKeeper.Setup();
        Check("setting the rule numbers leaves the board's shape alone",
              keptShape.BoardWidth == 3 && keptShape.BoardHeight == 6 && keptShape.PlacementRows == 1);

        var shapedGuest = new Lobby("BUILD", isHost: false);
        shapedGuest.OnSetup(keptShape);
        Check("a guest told a 3x6 board keeps the shape it was sent",
              shapedGuest.Refusal is null && shapedGuest.BoardWidth == 3 && shapedGuest.BoardHeight == 6);

        var recording = Captures.Path("match-x.jsonl");
        Check("a recording is written into the recordings folder beside the exe",
              recording == Path.Combine(AppContext.BaseDirectory, "recordings", "match-x.jsonl"));
        Check("asking for the recordings folder creates it",
              Directory.Exists(Captures.Dir()));

        var blocker = Path.GetTempFileName();
        try
        {
            Check("a recordings folder that cannot be made turns the recording off instead of throwing",
                  Captures.NewRecording(DateTime.Now, blocker) is null);
        }
        finally
        {
            File.Delete(blocker);
        }

        List<string> recordingNames = ["match-20260901-1200.jsonl", "match-20260903-2105.jsonl", "notes.txt",
                              "match-20260902-0900.jsonl", "match-20260902-1800.jsonl",
                              "match-20260903-1000.jsonl", "match-20260901-0800.jsonl",
                              "netplay-auto-20260807-0102.jsonl", "match-20260903-1200.jsonl"];
        Check("the oldest recordings past the kept five are the ones named, strangers untouched",
              Captures.Stale(recordingNames, Captures.Keep).SequenceEqual(
                  ["match-20260901-0800.jsonl", "match-20260901-1200.jsonl", "match-20260902-0900.jsonl"]));
        Check("four or fewer recordings leave nothing to prune",
              Captures.Stale(recordingNames.Take(5), Captures.Keep).Count == 0);

        var lobbyRole = Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Role, Role = SessionRole.Lobby }));
        var playRole = Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Role, Role = SessionRole.Play }));
        Check("a Role frame round-trips as the sender's role, lobby",
              lobbyRole is { Kind: MsgKind.Role, Role: SessionRole.Lobby });
        Check("a Role frame round-trips as the sender's role, play",
              playRole is { Kind: MsgKind.Role, Role: SessionRole.Play });

        var namedRole = Protocol.Decode(Protocol.Encode(
            new Frame { Kind = MsgKind.Role, Role = SessionRole.Play, Name = "EXAMPLE" }));
        Check("a Role frame carries the sender's display name",
              namedRole is { Kind: MsgKind.Role, Role: SessionRole.Play, Name: "EXAMPLE" });

        Check("a peer's name is cut to the buffer and stripped on the way in",
              Names.FromPeer("exam ple!!!-2") == "EXAMPLE2"
              && Names.FromPeer("VERYLONGNAMEINDEED") == "VERYLONG"
              && Names.FromPeer("!!!") is null
              && Names.FromPeer(null) is null);

        Check("a name never reaches a console line, so a legal ASPHALT cannot spell a halt",
              !Names.SetLine("ASPHALT").Contains("halt", StringComparison.OrdinalIgnoreCase)
              && Names.SetLine("ASPHALT").Contains('7')
              && Names.Problem("ASPHALTER") is { } tooLong
              && !tooLong.Contains("halt", StringComparison.OrdinalIgnoreCase));

        Check("no names means no --set-names command",
              NameArgs(null, null) is { Length: 0 } && NameArgs("", "") is { Length: 0 });

        Check("one side named still writes that one",
              NameArgs("EXAMPLE", null) is ["--set-names", "--me", "EXAMPLE"]
              && NameArgs(null, "LAPTOP") is ["--set-names", "--them", "LAPTOP"]);

        Check("the army's name goes to the names hold as hex, alone or beside the player names, and a blank one is left out",
              NameArgs(null, null, "Halt Hunter") is ["--set-names", ArmyNameHexFlag, "48616C742048756E746572"]
              && NameArgs("EXAMPLE", null, "Halt Hunter") is ["--set-names", ArmyNameHexFlag, "48616C742048756E746572", "--me", "EXAMPLE"]
              && NameArgs("EXAMPLE", null, " \t ") is ["--set-names", "--me", "EXAMPLE"]
              && NameArgs(null, null, "") is { Length: 0 }
              && ArmyNameHexFlag == "--army-name-hex");

        var flagNamed = NameArgs("EXAMPLE", null, "--no-names");
        Check("an army named like a flag reaches live-probe as hex, so it can never switch a flag on there",
              !flagNamed.Contains("--no-names")
              && flagNamed.Count(a => a.StartsWith('-')) == 3
              && ArmyNameFromHex(flagNamed[2]) == "--no-names"
              && NamesHoldArgs(flagNamed, 4242).Count(a => a == "--no-names") == 0);

        var accentedArmy = "Caf" + (char)0xE9 + " Gr" + (char)0xF6 + (char)0xDF + "e";
        Check("the army name read from hex keeps accents, drops control characters, stops at 32 characters, and refuses bad hex",
              ArmyNameFromHex(ArmyNameHex(accentedArmy)) == accentedArmy
              && ArmyNameFromHex(ArmyNameHex(" Halt\tHunter\n ")) == "HaltHunter"
              && ArmyNameFromHex(ArmyNameHex(new string('a', 40)))!.Length == MaxArmyNameChars
              && ArmyNameFromHex("4G") is null && ArmyNameFromHex("486") is null && ArmyNameFromHex(null) is null
              && ArmyNameFromHex(new string('4', MaxArmyNameChars * 8 + 2)) is null);

        Move OneAction()
        {
            return new Move { SrcX = 3, SrcY = 6, DstX = 3, DstY = 5, Facing = 0 };
        }

        var overLong = new Frame
        {
            Kind = MsgKind.Move,
            Moves = [.. Enumerable.Range(0, FrameLimits.MaxMoves + 1).Select(_ => OneAction())],
        };
        Check("a turn with more actions than a legal turn can hold is refused",
              FrameLimits.Refuse(overLong) is not null);

        var atLimit = new Frame
        {
            Kind = MsgKind.Move,
            Moves = [.. Enumerable.Range(0, FrameLimits.MaxMoves).Select(_ => OneAction())],
        };
        Check("a turn exactly at the cap is allowed through",
              FrameLimits.Refuse(atLimit) is null);

        Check("an ordinary two-action turn is allowed through",
              FrameLimits.Refuse(new Frame { Kind = MsgKind.Move, Moves = [OneAction(), OneAction()] }) is null);

        Check("a turn naming a square off this board is refused on receipt, before any arithmetic",
              FrameLimits.SquaresOff([new Move { SrcX = 0, SrcY = 0, DstX = -2147483641, DstY = 0 }], 8, 8) is not null
              && FrameLimits.SquaresOff([new Move { SrcX = 0, SrcY = 0, DstX = 6, DstY = 4 }], 6, 5) is not null
              && FrameLimits.SquaresOff([new Move { SrcX = 0, SrcY = 0, DstX = 5, DstY = 4, TargetX = 6, TargetY = 1 }], 6, 5) is not null
              && FrameLimits.SquaresOff([new Move { SrcX = 0, SrcY = 0, DstX = 5, DstY = 4 }], 6, 5) is null);
        Check("a placement off this board is refused on receipt",
              FrameLimits.SquareOff(new Placement { X = 6, Y = 0 }, 6, 5) is not null
              && FrameLimits.SquareOff(new Placement { X = 0, Y = -1 }, 6, 5) is not null
              && FrameLimits.SquareOff(new Placement { X = 5, Y = 4 }, 6, 5) is null);

        string? applierHalt = null;
        try
        {
            Guarded(() => throw new OverflowException(), reason => applierHalt = reason, "turn applier")
                .GetAwaiter().GetResult();
        }
        catch (OverflowException)
        {
        }

        Check("an error inside the apply loop halts, where it used to end the loop in silence",
              applierHalt is not null && applierHalt.Contains("OverflowException"));

        string? handlerFault = null;
        var linkFailurePassed = false;
        try
        {
            handlerFault = Peer.HandlerFault(() => throw new InvalidOperationException());
            Peer.HandlerFault(() => throw new IOException());
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
            linkFailurePassed = true;
        }

        Check("the read loop halts on a handler's own fault and still reconnects on a link failure",
              handlerFault is not null && linkFailurePassed && Peer.HandlerFault(() => { }) is null);

        var lastSampled = new BoardSnapshot(8, 8, [], []) { AiSeat = 1 };
        var readFresh = new BoardSnapshot(6, 5, [], []) { AiSeat = 1 };
        Check("the pre-flight reads a fresh board when none has been sampled, and has none only when that fails",
              PreflightBoard(null, () => readFresh) == readFresh
              && PreflightBoard(null, () => null) is null
              && PreflightBoard(lastSampled, () => throw new InvalidOperationException("no fresh read wanted")) == lastSampled);

        var seatless = new BoardSnapshot(8, 8, [], []);
        Check("the pre-flight takes no board that does not name the computer's seat",
              PreflightBoard(seatless, () => readFresh) == readFresh
              && PreflightBoard(seatless, () => seatless) is null);

        var bigArmy = new Frame
        {
            Kind = MsgKind.Setup,
            Army = [.. Enumerable.Range(0, FrameLimits.MaxPlacements + 1).Select(_ => "1C96A39FFE37791F8AE07BD49A2230FF")],
        };
        Check("a setup naming more machines than a seat can hold is refused",
              FrameLimits.Refuse(bigArmy) is not null);

        var bigPlacements = new Frame
        {
            Kind = MsgKind.Setup,
            Placements = [.. Enumerable.Range(0, FrameLimits.MaxPlacements + 1).Select(_ => new Placement { X = 3, Y = 6 })],
        };
        Check("a setup carrying more placements than a seat can hold is refused",
              FrameLimits.Refuse(bigPlacements) is not null);

        Check("a frame carrying no bounded list at all is allowed through",
              FrameLimits.Refuse(new Frame { Kind = MsgKind.Ready }) is null);

        var noSlots = new HashSet<int>();
        var noWrittenSquares = new HashSet<(int X, int Y)>();
        var rematch = new Frame { Kind = MsgKind.Rematch };
        Check("a rematch arriving mid-match is refused",
              FrameGate.Refuse(SessionPhase.Playing, rematch, noSlots, noWrittenSquares) is not null);
        Check("a rematch arriving during placement is refused",
              FrameGate.Refuse(SessionPhase.Placing, rematch, noSlots, noWrittenSquares) is not null);
        Check("a rematch after the match ended is allowed, which is the whole point of it",
              FrameGate.Refuse(SessionPhase.Over, rematch, noSlots, noWrittenSquares) is null);
        Check("a rematch with no match live is allowed",
              FrameGate.Refuse(SessionPhase.NoMatch, rematch, noSlots, noWrittenSquares) is null);

        var place3 = new Frame { Kind = MsgKind.Place, PlaceIdx = 3, Place = new Placement { X = 3, Y = 6 } };
        Check("a placement for a slot already written is refused",
              FrameGate.Refuse(SessionPhase.Placing, place3, new HashSet<int> { 3 }, noWrittenSquares) is not null);
        Check("a placement for a slot not yet written is allowed",
              FrameGate.Refuse(SessionPhase.Placing, place3, new HashSet<int> { 0, 1 }, noWrittenSquares) is null);

        var twoSquares = new Lobby("T", isHost: true) { Seat = 0 };
        twoSquares.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        twoSquares.SetArmy(["1C96A39FFE37791F8AE07BD49A2230FF", "1C96A39FFE37791F8AE07BD49A2230FF"]);
        twoSquares.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "1C96A39FFE37791F8AE07BD49A2230FF"],
            Placements = [new Placement { X = 3, Y = 6 }, new Placement { X = 3, Y = 6 }],
        });
        var twoApart = new Lobby("T", isHost: true) { Seat = 0 };
        twoApart.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        twoApart.SetArmy(["1C96A39FFE37791F8AE07BD49A2230FF", "1C96A39FFE37791F8AE07BD49A2230FF"]);
        twoApart.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "1C96A39FFE37791F8AE07BD49A2230FF"],
            Placements = [new Placement { X = 3, Y = 6 }, new Placement { X = 4, Y = 6 }],
        });
        Check("a second machine placed on a square one of theirs already holds is refused, frame by frame and in a setup",
              FrameGate.Refuse(SessionPhase.Placing, place3, new HashSet<int> { 0 },
                               new HashSet<(int X, int Y)> { (3, 6) }) is { } sameSquare
              && sameSquare.Contains("(3,6)")
              && FrameGate.Refuse(SessionPhase.Placing, place3, new HashSet<int> { 0 },
                                  new HashSet<(int X, int Y)> { (4, 6) }) is null
              && twoSquares.Refusal is { } setupTwice && setupTwice.Contains("one starting square")
              && twoApart.Refusal is null);

        var lastHeld = new BoardSnapshot(8, 8, [], new sbyte[64])
        {
            AiSeat = 1, Placing = new PlacingState(0, 0, 1),
            Match = new MatchState(false, -1, 0, 0, 7),
        };
        Check("both seats at zero left with the placing object still live is STILL the placing phase",
              PhaseOf(lastHeld, false) == SessionPhase.Placing);
        Check("the placing object gone is the playing phase, and a finished match is Over",
              PhaseOf(lastHeld with { Placing = null }, false) == SessionPhase.Playing &&
              PhaseOf(lastHeld with { Placing = null, Match = new MatchState(true, 0, 7, 0, 7) }, false) ==
                  SessionPhase.Over &&
              PhaseOf(null, false) == SessionPhase.NoMatch &&
              PhaseOf(lastHeld, true) == SessionPhase.Over);
        Check("a placement arriving mid-match is refused",
              FrameGate.Refuse(SessionPhase.Playing, place3, noSlots, noWrittenSquares) is not null);

        Check("a placement arriving before our watcher sees the match is ALLOWED",
              FrameGate.Refuse(SessionPhase.NoMatch, place3, noSlots, noWrittenSquares) is null);

        var turn = new Frame { Kind = MsgKind.Move, Moves = [OneAction()] };
        Check("a turn arriving after the match ended is refused",
              FrameGate.Refuse(SessionPhase.Over, turn, noSlots, noWrittenSquares) is not null);
        Check("a turn arriving before our watcher sees the match is ALLOWED",
              FrameGate.Refuse(SessionPhase.NoMatch, turn, noSlots, noWrittenSquares) is null);
        Check("an ordinary turn mid-match is allowed",
              FrameGate.Refuse(SessionPhase.Playing, turn, noSlots, noWrittenSquares) is null);

        Check("a hash is allowed in every phase",
              FrameGate.Refuse(SessionPhase.Over, new Frame { Kind = MsgKind.Hash }, noSlots, noWrittenSquares) is null
              && FrameGate.Refuse(SessionPhase.Playing, new Frame { Kind = MsgKind.Hash }, noSlots, noWrittenSquares) is null
              && FrameGate.Refuse(SessionPhase.Placing, new Frame { Kind = MsgKind.Hash }, noSlots, noWrittenSquares) is null);

        Check("a halt reason cannot carry escape sequences to the terminal",
              !FrameLimits.Safe("desync[2J[Hall fine actually").Contains(''));
        Check("a halt reason cannot carry a newline to forge a second line",
              !FrameLimits.Safe("line one\nREFUSED: nothing wrong").Contains('\n'));
        Check("a halt reason keeps its readable text",
              FrameLimits.Safe("desync[2Jafter turn 4").Contains("after turn 4"));
        Check("an over-long halt reason is cut to the cap",
              FrameLimits.Safe(new string('x', 5000)).Length <= FrameLimits.MaxReasonChars + 3);
        Check("a null or empty string sanitises to empty rather than throwing",
              FrameLimits.Safe(null) == "" && FrameLimits.Safe("") == "");

        var onlyPrintableAscii = true;
        for (var code = 0; code <= 0xFFFF; code++)
        {
            var cleaned = FrameLimits.Safe("a" + (char)code + "b");
            if (cleaned.Any(ch => ch is < ' ' or > '~'))
            {
                onlyPrintableAscii = false;
                break;
            }
        }

        Check("no character a peer sends survives as anything but printable ASCII, so no code page makes a line break",
              onlyPrintableAscii && FrameLimits.Safe("A" + (char)0x25D9 + "B" + (char)0x266A + "C") == "A?B?C");

        Check("the Hash log line strips control characters from the peer's hashes",
              !HashLine(new Frame { Kind = MsgKind.Hash, Turn = 1, PieceHash = "aa[2Jbb", TerrainHash = "cc\ndd" })
                  .Any(char.IsControl));
        const string realHash = "0123456789abcdef";
        Check("a peer's board hash is refused unless it is sixteen lower-case hex digits",
              FrameLimits.Refuse(new Frame { Kind = MsgKind.Hash, PieceHash = realHash, TerrainHash = realHash }) is null
              && FrameLimits.Refuse(new Frame { Kind = MsgKind.Hash, PieceHash = "HALT the match!!", TerrainHash = realHash }) is not null
              && FrameLimits.Refuse(new Frame { Kind = MsgKind.Hash, PieceHash = realHash, TerrainHash = "0123456789ABCDEF" }) is not null
              && FrameLimits.Refuse(new Frame { Kind = MsgKind.Hash, PieceHash = realHash + "0", TerrainHash = realHash }) is not null
              && FrameLimits.Refuse(new Frame { Kind = MsgKind.Hash, PieceHash = realHash }) is not null
              && FrameLimits.Refuse(new Frame { Kind = MsgKind.Move, TerrainHash = "halt" }) is not null
              && FrameLimits.Refuse(new Frame { Kind = MsgKind.Move }) is null);
        Check("the refusal of a hash names no text the peer chose",
              FrameLimits.Refuse(new Frame { Kind = MsgKind.Hash, PieceHash = "HALT", TerrainHash = realHash })
                  is { } hashRefusal && !hashRefusal.Contains("HALT"));

        var relayed = ChildLines("a\nREFUSED: x\r\nsetup written", stderr: false).ToList();
        Check("a child's output is relayed one marked line per line it wrote",
              relayed.Count == 3 && relayed.All(l => l.StartsWith("    | "))
              && ChildLines("oops\nBOTH READY, challenge", stderr: true).All(l => l.StartsWith("    ! "))
              && !ChildLines(null, stderr: false).Any());

        static bool RefusesChallenge(string challenge)
        {
            var g = new Lobby("BUILD", isHost: false);
            g.OnSetup(new Frame { Kind = MsgKind.Setup, Challenge = challenge, Army = [] });
            return g.Challenge is null && g.Refusal is not null;
        }

        Check("a challenge carrying control characters is refused, never printed",
              RefusesChallenge("dead[2Jbeef"));

        var t0 = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var burst = new RateLimiter(FrameLimits.MaxFramesPerWindow, FrameLimits.RateWindow);
        var allowedInBurst = 0;
        for (var i = 0; i < FrameLimits.MaxFramesPerWindow + 20; i++)
        {
            if (burst.Allow(t0))
            {
                allowedInBurst++;
            }
        }

        Check("a flood is cut off at the frame cap",
              allowedInBurst == FrameLimits.MaxFramesPerWindow);

        var trickle = new RateLimiter(FrameLimits.MaxFramesPerWindow, FrameLimits.RateWindow);
        var allowedSlowly = 0;
        for (var i = 0; i < 500; i++)
        {
            if (trickle.Allow(t0.AddSeconds(i * 2)))
            {
                allowedSlowly++;
            }
        }

        Check("a frame every two seconds forever is never refused", allowedSlowly == 500);

        Check("the window really slides, so a flood is forgiven once it stops",
              burst.Allow(t0.Add(FrameLimits.RateWindow).AddSeconds(1)));

        var mismatched = new Lobby("6A1B2C3D-4E5F000", isHost: false);
        mismatched.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "52739299954A231229FD4BE0818ADBB0"],
            Placements = [new Placement { X = 3, Y = 6 }],
        });
        Check("a setup whose placements do not match its army is refused",
              mismatched.Refusal is not null);

        var matched = new Lobby("6A1B2C3D-4E5F000", isHost: false);
        matched.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "52739299954A231229FD4BE0818ADBB0"],
            Placements = [new Placement { X = 3, Y = 6 }, new Placement { X = 4, Y = 6 }],
        });
        Check("a setup whose placements match its army is accepted", matched.Refusal is null);

        var hostKey = KeyExchange.Begin();
        var guestKey = KeyExchange.Begin();
        var hostPub = KeyExchange.PublicBlob(hostKey);
        var guestPub = KeyExchange.PublicBlob(guestKey);

        var hostSide = KeyExchange.Complete("GATE01", hostKey, guestPub, isHost: true);
        var guestSide = KeyExchange.Complete("GATE01", guestKey, hostPub, isHost: false);

        Check("both sides complete the handshake",
              hostSide is not null && guestSide is not null);
        Check("the two sides derive the SAME fingerprint",
              hostSide!.Fingerprint == guestSide!.Fingerprint);
        Check("one side's send key is the other's receive key",
              hostSide.Send.SequenceEqual(guestSide.Recv) && hostSide.Recv.SequenceEqual(guestSide.Send));

        var hostChan = new SecureChannel(hostSide);
        var guestChan = new SecureChannel(guestSide);
        var turnLine = Protocol.Encode(new Frame { Kind = MsgKind.Move, Moves = [OneAction()] });
        var sealed1 = hostChan.Seal(turnLine);

        Check("a sealed frame does not carry its contents in the clear",
              !sealed1.Contains("Move") && !sealed1.Contains("srcX"));
        Check("the peer opens what we sealed, byte for byte",
              guestChan.Open(sealed1) == turnLine);

        var tampered = sealed1.ToCharArray();
        var flipAt = tampered.Length / 2;
        tampered[flipAt] = tampered[flipAt] == 'A' ? 'B' : 'A';
        Check("a frame altered in transit is refused",
              new SecureChannel(guestSide).Open(new string(tampered)) is null);

        Check("a frame that is not base64 at all is refused, not thrown on",
              guestChan.Open("this is not base64 !!!") is null);
        Check("a truncated frame is refused",
              guestChan.Open(Convert.ToBase64String(new byte[4])) is null);

        var replayTarget = new SecureChannel(guestSide);
        var first = new SecureChannel(hostSide).Seal(turnLine);
        Check("a frame opens once", replayTarget.Open(first) is not null);
        Check("the SAME frame played back is refused", replayTarget.Open(first) is null);

        var wrongCode = KeyExchange.Complete("WRONG1", guestKey, hostPub, isHost: false);
        Check("a peer with the wrong room code derives different keys",
              wrongCode is not null && !wrongCode.Send.SequenceEqual(guestSide.Send));
        Check("a peer with the wrong room code cannot open our frames",
              new SecureChannel(wrongCode!).Open(new SecureChannel(hostSide).Seal(turnLine)) is null);

        var attackerKey = KeyExchange.Begin();
        var attackerSide = KeyExchange.Complete("GATE01", attackerKey, hostPub, isHost: false);
        Check("knowing the room code does not open a session between two other people",
              attackerSide is not null
              && new SecureChannel(attackerSide!).Open(new SecureChannel(hostSide).Seal(turnLine)) is null);

        var mitmToHost = KeyExchange.Complete("GATE01", attackerKey, hostPub, isHost: false);
        var hostVsMitm = KeyExchange.Complete("GATE01", hostKey, KeyExchange.PublicBlob(attackerKey), isHost: true);
        Check("a middleman cannot make the two fingerprints match",
              hostVsMitm!.Fingerprint != guestSide.Fingerprint
              && mitmToHost!.Fingerprint != guestSide.Fingerprint);

        var steady = new SecureChannel(hostSide);
        var listener = new SecureChannel(guestSide);
        var one = steady.Seal(turnLine);
        var two = steady.Seal(turnLine);
        Check("consecutive frames on ONE channel open in order",
              listener.Open(one) is not null && listener.Open(two) is not null);

        var rebuilt = new SecureChannel(hostSide);
        Check("a channel rebuilt from the same keys restarts its counter and IS refused as a replay, "
              + "which is why a repeated key must not re-derive",
              listener.Open(rebuilt.Seal(turnLine)) is null);

        var gapSender = new SecureChannel(hostSide);
        var gapReceiver = new SecureChannel(guestSide);
        var atZero = gapSender.Seal(turnLine);
        var atOne = gapSender.Seal(turnLine);
        var atTwo = gapSender.Seal(turnLine);
        Check("a sealed frame after a lost one is refused, and so is a channel's first frame if it was not sealed first",
              gapReceiver.Open(atZero) is not null && gapReceiver.Open(atTwo) is null
              && new SecureChannel(guestSide).Open(atOne) is null);

        var keyOne = KeyExchange.PublicBlob(hostKey);
        var keyTwo = KeyExchange.PublicBlob(guestKey);
        var oneSpelled = Convert.ToBase64String(keyOne);
        var spelledWithSpace = oneSpelled.Insert(8, " ");

        Check("base64 has more than one spelling for one key, which is what defeated the string compare",
              oneSpelled != spelledWithSpace
              && Convert.FromBase64String(spelledWithSpace).SequenceEqual(keyOne));

        Check("the same key respelled with whitespace is SameAsCurrent, so it does not rebuild",
              Peer.JudgeKey(spelledWithSpace, keyOne, [keyOne], out _) == Peer.KeyOffer.SameAsCurrent);

        Check("going back to a retired key is Retired, which is the alternating attack",
              Peer.JudgeKey(oneSpelled, keyTwo, [keyOne, keyTwo], out _) == Peer.KeyOffer.Retired);

        Check("an honest re-key onto a key never seen on this connection is Fresh",
              Peer.JudgeKey(Convert.ToBase64String(KeyExchange.PublicBlob(KeyExchange.Begin())),
                            keyOne, [keyOne], out _) == Peer.KeyOffer.Fresh);

        Check("the first key exchange of a connection is Fresh, nothing being retired yet",
              Peer.JudgeKey(oneSpelled, null, [], out _) == Peer.KeyOffer.Fresh);

        Check("a key that does not decode is Malformed rather than throwing",
              Peer.JudgeKey("not base64 at all!!", keyOne, [keyOne], out _) == Peer.KeyOffer.Malformed
              && Peer.JudgeKey(null, keyOne, [keyOne], out _) == Peer.KeyOffer.Malformed);

        var sealer = new SecureChannel(hostSide);
        var counters = new System.Collections.Concurrent.ConcurrentBag<ulong>();
        const int Threads = 8;
        const int Each = 500;

        Parallel.For(0, Threads, _ =>
        {
            for (var i = 0; i < Each; i++)
            {
                var wire = Convert.FromBase64String(sealer.Seal("turn"));
                counters.Add(BitConverter.ToUInt64(wire, 0));
            }
        });

        Check($"{Threads} threads sealing at once take {Threads * Each} DISTINCT nonces",
              counters.Count == Threads * Each
              && counters.Distinct().Count() == Threads * Each);

        Check("those counters are the whole unbroken run, so none was skipped either",
              counters.Min() == 0 && counters.Max() == (ulong)(Threads * Each) - 1);

        Check("a hash with no board of ours to compare against is refused, not indexed",
              Peer.HashCompareProblem(seat: 0, candidateCount: 0, turn: 3) is { } why
              && why.Contains("no board of ours"));

        Check("an unpaired seat still reports the older reason, which is the more specific one",
              Peer.HashCompareProblem(seat: -1, candidateCount: 0, turn: 3) is { } unpaired
              && unpaired.Contains("before pairing"));

        Check("a seat with candidates has nothing to refuse",
              Peer.HashCompareProblem(seat: 1, candidateCount: 2, turn: 3) is null);

        Check("a boundary crossing carries its count and its closing flag as one value",
              Crossed(new Boundary(4, false), closing: true) == new Boundary(5, true)
              && Crossed(new Boundary(5, true), closing: false) == new Boundary(6, false));

        Check("a frame waits for the new key while this side answers an exchange over the old channel",
              Peer.SendHolds(haveChannel: true, answeringNewKey: true)
              && Peer.SendHolds(haveChannel: false, answeringNewKey: false)
              && !Peer.SendHolds(haveChannel: true, answeringNewKey: false));

        Check("a sealed frame before this connection's exchange completes is dropped as a straggler, not halted on",
              Peer.UnopenedIsStraggler(haveChannel: false, Peer.Exchange.Initiating)
              && Peer.UnopenedIsStraggler(haveChannel: false, Peer.Exchange.Responding)
              && !Peer.UnopenedIsStraggler(haveChannel: false, Peer.Exchange.Idle)
              && !Peer.UnopenedIsStraggler(haveChannel: true, Peer.Exchange.Idle));

        Check("a write into a link disposed under it is a link error, not a fault that ends --play",
              Peer.IsLinkError(new ObjectDisposedException("stream")) && Peer.IsLinkError(new IOException("pipe"))
              && !Peer.IsLinkError(new InvalidOperationException("other")));

        Check("a connection that has not said Hello is read at the Hello cap, one that has at the frame cap",
              FrameLimits.LineCapFor(saidHello: false) == FrameLimits.MaxHelloLine
              && FrameLimits.LineCapFor(saidHello: true) == FrameLimits.MaxLine
              && FrameLimits.MaxHelloLine < FrameLimits.MaxLine);

        static async Task<Frame?> HaltWithin(StreamReader reader, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var heard = await reader.ReadLineAsync(deadline.Token);
                    if (heard is null)
                    {
                        return null;
                    }

                    if (Protocol.Decode(heard) is { Kind: MsgKind.Halt } halt)
                    {
                        return halt;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            return null;
        }

        using (var stranger = new TcpClient())
        {
            await stranger.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var strangerStream = stranger.GetStream();
            var strangerWriter = new StreamWriter(strangerStream, new UTF8Encoding(false)) { AutoFlush = true };
            using var strangerReader = new StreamReader(strangerStream, Encoding.UTF8);
            await strangerWriter.WriteLineAsync(new string('x', FrameLimits.MaxHelloLine + 64));
            var refusal = await HaltWithin(strangerReader, cts.Token);
            Check("the relay refuses a first line past the Hello cap, before it decodes anything",
                  refusal is not null && (refusal.Reason ?? "").Contains("no end of line"));
        }

        using (var bulkSender = new TcpClient())
        {
            await bulkSender.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var bulkStream = bulkSender.GetStream();
            var bulkWriter = new StreamWriter(bulkStream, new UTF8Encoding(false)) { AutoFlush = true };
            using var bulkReader = new StreamReader(bulkStream, Encoding.UTF8);
            await bulkWriter.WriteLineAsync(Protocol.Encode(new Frame
            {
                Kind = MsgKind.Hello, Room = Relay.NewCode(), Seat = 0, ResumeFrom = -1,
            }));
            await bulkWriter.WriteLineAsync(Protocol.Encode(new Frame
            {
                Kind = MsgKind.Move,
                Moves = [.. Enumerable.Range(0, FrameLimits.MaxMoves + 1).Select(_ => OneAction())],
            }));

            var refusedFrame = await HaltWithin(bulkReader, cts.Token);
            Check("the relay refuses a frame carrying more than the per-frame caps, as the peer does",
                  refusedFrame is not null && (refusedFrame.Reason ?? "").Contains("actions"));
        }

        Check("a hold on the key exchange has a ceiling and a deadline, and an honest one trips neither",
              Peer.HoldProblem(held: 1, TimeSpan.FromSeconds(1)) is null
              && Peer.HoldProblem(Peer.MaxHeldFrames, TimeSpan.FromSeconds(1)) is not null
              && Peer.HoldProblem(held: 1, Peer.HoldsFramesFor) is not null);

        Check("one connection gets a bounded number of key exchanges",
              !Peer.TooManyExchanges(Peer.MaxExchangesPerConnection - 1)
              && Peer.TooManyExchanges(Peer.MaxExchangesPerConnection));

        Check("D-245: the watcher's no-match line halts a match that was being played, and nothing else",
              IsNoMatchLine("{\"nomatch\":true}") && !IsNoMatchLine("{\"width\":5,\"height\":5,\"pieces\":[]}")
              && MatchLeftHalts(auto: true, sawBoard: true, halted: false, summarised: false)
              && !MatchLeftHalts(auto: false, sawBoard: true, halted: false, summarised: false)
              && !MatchLeftHalts(auto: true, sawBoard: false, halted: false, summarised: false)
              && !MatchLeftHalts(auto: true, sawBoard: true, halted: true, summarised: false));

        Check("the leave reason keeps the words the launcher reads it by",
              MatchLeftReason.StartsWith("a player left the match before it ended", StringComparison.Ordinal)
              && !MatchLeftReason.Contains(';'));

        Check("a match gone while the game process is gone too halts as the game closing, and as a leave while it runs",
              LeftOrClosedReason(gameStillRunning: false) == GameClosedReason
              && LeftOrClosedReason(gameStillRunning: true) == MatchLeftReason
              && GameClosedReason.StartsWith("the game on this PC closed before the match ended", StringComparison.Ordinal)
              && !GameClosedReason.Contains(';')
              && GameGoneChecks * GameGoneCheckDelay.TotalSeconds >= 12
              && GameGoneChecks * GameGoneCheckDelay.TotalSeconds < Peer.HaltDrain.TotalSeconds);

        Check("the names are written under a hold that waits for the match itself and watches the parent, so a Glossary visit's rebuild is written again",
              NamesHoldArgs(["--set-names", "--me", "EXAMPLE"], 4242)
                  is ["--set-names", "--me", "EXAMPLE", "--yes", "--hold-names", "--wait", "600", "--parent-pid", "4242"] && !NamesHoldArgs(["--set-names"], 1).Contains("--hold"));

        Check("pressing Continue after our own match-ending turn was sent and summarised halts nothing",
              !MatchLeftHalts(auto: true, sawBoard: true, halted: false, summarised: true));

        Check("D-245: the applied line takes the injector's game time and nothing else",
              Injector.GameTimeOf(["  turn APPLIED IN FULL, 3 action record(s)", "  game time 4.9 s"]) == "4.9 s"
              && Injector.GameTimeOf(["  game timer 4.9 s", "  count 11 -> 10"]) is null);

        Check("D-245: the hold and the auto watcher are started with no wall-clock end",
              HoldArgs(42).SkipWhile(a => a != "--secs").Skip(1).FirstOrDefault() == "0"
              && HoldArgs(42).Contains("--parent-pid")
              && WatcherSeconds(autoOn: true) == "0" && WatcherSeconds(autoOn: false) == "900");

        Check("the watcher's stream ending halts while auto is on, unless we stopped it ourselves",
              WatcherEndHalts(auto: true, weStoppedIt: false)
              && !WatcherEndHalts(auto: true, weStoppedIt: true)
              && !WatcherEndHalts(auto: false, weStoppedIt: false));

        Check("a confirmation names the code that was compared, and one that does not is refused",
              Lobby.ConfirmProblem("A1B2C3D4E5F60718", "A1B2C3D4E5F60718") is null
              && Lobby.ConfirmProblem("a1b2c3d4e5f60718", "A1B2C3D4E5F60718") is null
              && Lobby.ConfirmProblem("A1B2C3D4E5F60718", "0000000000000000") is not null
              && Lobby.ConfirmProblem(null, "A1B2C3D4E5F60718") is not null
              && Lobby.ConfirmProblem("A1B2C3D4E5F60718", null) is not null);

        static (Lobby Host, Lobby Guest) AgreedLobbies(List<int> terrain)
        {
            var host = new Lobby("X", isHost: true) { Seat = 0 };
            host.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
            host.ChooseBoard(terrain, 3, 30, 6, 5, 1);
            host.ChooseFirst(Lobby.FirstJoiner);
            host.SetName("HOSTA");
            host.SetArmy([MachineA, MachineB]);
            host.Local.Placements = [new Placement { X = 1, Y = 4, Dir = 0 }, new Placement { X = 4, Y = 4, Dir = 1 }];
            var guest = new Lobby("X", isHost: false) { Seat = 1 };
            guest.SetName("GUESTB");
            guest.SetArmy([MachineB]);
            guest.Local.Placements = [new Placement { X = 2, Y = 4, Dir = 3 }];
            guest.OnSetup(host.Setup());
            host.OnSetup(guest.Setup());
            host.Ready();
            guest.Ready();
            host.OnReady(new Frame { Kind = MsgKind.Ready });
            guest.OnReady(new Frame { Kind = MsgKind.Ready });
            return (host, guest);
        }

        var agreedTerrain = Enumerable.Range(0, 30).Select(i => i % 5 == 0 ? 1 : 0).ToList();
        var otherTerrain = agreedTerrain.Select((t, i) => i == 7 ? 2 : t).ToList();
        var (agreedHost, agreedGuest) = AgreedLobbies(agreedTerrain);
        var (otherHost, _) = AgreedLobbies(otherTerrain);
        var hostSays = agreedHost.SetupDigest();
        var guestHolds = agreedGuest.SetupDigest();
        var otherSays = otherHost.SetupDigest();

        var otherPeer = new Peer("127.0.0.1", 1, "GATE01");
        var otherTaken = TakeTheirConfirmation(otherPeer, agreedGuest,
                                               new Frame { Kind = MsgKind.Confirmed, SetupDigest = otherSays });
        var blankPeer = new Peer("127.0.0.1", 1, "GATE02");
        var blankTaken = TakeTheirConfirmation(blankPeer, agreedGuest, new Frame { Kind = MsgKind.Confirmed });
        var (_, honestGuest) = AgreedLobbies(agreedTerrain);
        var honestPeer = new Peer("127.0.0.1", 1, "GATE03");
        var honestTaken = TakeTheirConfirmation(honestPeer, honestGuest,
                                                new Frame { Kind = MsgKind.Confirmed, SetupDigest = hostSays });
        Check("a Confirmed frame carrying another setup's digest, or none, halts the lobby and records nothing",
              agreedGuest.Refusal is null && agreedHost.Refusal is null && hostSays == guestHolds
              && otherSays != guestHolds
              && !otherTaken && otherPeer.Halted && otherPeer.HaltReason == Lobby.SetupsDiffer
              && !blankTaken && blankPeer.Halted
              && agreedGuest.ConfirmedSetup is null
              && honestTaken && !honestPeer.Halted && honestGuest.ConfirmedSetup == hostSays);

        static string DigestAfter(List<int> terrain, bool onHost, bool joinerPlaces, Action<Lobby>? change)
        {
            var (host, guest) = AgreedLobbies(terrain);
            if (!joinerPlaces)
            {
                host.Remote!.Placements = [];
            }

            var side = onHost ? host : guest;
            change?.Invoke(side);
            return side.SetupDigest();
        }

        List<Action<Lobby>> hostChanges =
        [
            l => l.Choose("8BBC182B83FC495AA2150021217530D5"),
            l => l.ChooseBoard(otherTerrain, 3, 30, 6, 5, 1),
            l => l.ChooseBoard(agreedTerrain, 3, 30, 6, 5, 2),
            l => l.ChooseBoard(agreedTerrain, 4, 30, 6, 5, 1),
            l => l.ChooseBoard(agreedTerrain, 3, 31, 6, 5, 1),
            l => l.ChooseFirst(Lobby.FirstHost),
            l => l.SetArmy([MachineB, MachineB]),
            l => l.Local.Placements = [new Placement { X = 1, Y = 4, Dir = 0 }, new Placement { X = 5, Y = 4, Dir = 1 }],
            l => l.SetName("HOSTC"),
        ];
        List<Action<Lobby>> joinerChanges =
        [
            l => l.SetArmy([MachineA]),
            l => l.Local.Placements = [new Placement { X = 3, Y = 4, Dir = 3 }],
            l => l.SetName("GUESTC"),
        ];
        List<Action<Lobby>> shapeChanges =
        [
            l => l.ChooseBoard(agreedTerrain, 3, 30, 7, 5, 1),
            l => l.ChooseBoard(agreedTerrain, 3, 30, 6, 6, 1),
        ];
        var unplaced = DigestAfter(agreedTerrain, onHost: true, joinerPlaces: false, null);
        Check("the setup digest changes when any one thing write puts in either game differs",
              hostChanges.All(c => DigestAfter(agreedTerrain, onHost: true, joinerPlaces: true, c) != hostSays)
              && joinerChanges.All(c => DigestAfter(agreedTerrain, onHost: false, joinerPlaces: true, c) != guestHolds)
              && shapeChanges.All(c => DigestAfter(agreedTerrain, onHost: true, joinerPlaces: false, c) != unplaced)
              && hostChanges.Count + joinerChanges.Count + shapeChanges.Count
                 == WireJson.Default.WrittenSetup.Properties.Count);

        static void ConfirmedOnOne(Lobby lobby, string theirs, int channel)
        {
            lobby.ConfirmedLocally(channel, "A1B2C3D4E5F60718");
            lobby.ConfirmedByPeer(channel, theirs, lobby.SetupDigest());
            lobby.BoundOn(channel);
        }

        var (_, changedGuest) = AgreedLobbies(agreedTerrain);
        ConfirmedOnOne(changedGuest, hostSays, 3);
        changedGuest.Local.Army = [MachineA];
        var changedPeer = new Peer("127.0.0.1", 1, "GATE04") { Binding = new byte[32] };
        var changedWrite = LobbyWrite(changedPeer, changedGuest, "live-probe", yes: false);
        Check("write halts rather than write a setup changed after both confirmed",
              changedWrite == 1 && changedPeer.Halted && changedPeer.HaltReason == Lobby.SetupsDiffer);

        var (_, peerOnlyGuest) = AgreedLobbies(agreedTerrain);
        peerOnlyGuest.ConfirmedByPeer(3, hostSays, peerOnlyGuest.SetupDigest());
        var peerOnlyPeer = new Peer("127.0.0.1", 1, "GATE05") { Binding = new byte[32] };
        var peerOnlyWrite = LobbyWrite(peerOnlyPeer, peerOnlyGuest, "live-probe", yes: false);
        var (_, splitGuest) = AgreedLobbies(agreedTerrain);
        splitGuest.ConfirmedLocally(3, "A1B2C3D4E5F60718");
        splitGuest.ConfirmedByPeer(4, hostSays, splitGuest.SetupDigest());
        splitGuest.BoundOn(3);
        var splitPeer = new Peer("127.0.0.1", 1, "GATE06") { Binding = new byte[32] };
        var splitWrite = LobbyWrite(splitPeer, splitGuest, "live-probe", yes: false);
        var (_, droppedGuest) = AgreedLobbies(agreedTerrain);
        ConfirmedOnOne(droppedGuest, hostSays, 3);
        var droppedPeer = new Peer("127.0.0.1", 1, "GATE07");
        var droppedWrite = LobbyWrite(droppedPeer, droppedGuest, "live-probe", yes: false);
        var (_, writableGuest) = AgreedLobbies(agreedTerrain);
        ConfirmedOnOne(writableGuest, hostSays, 3);
        var writablePeer = new Peer("127.0.0.1", 1, "GATE08") { Binding = new byte[32] };
        var writableWrite = LobbyWrite(writablePeer, writableGuest, "live-probe", yes: false);
        var (_, forgottenGuest) = AgreedLobbies(agreedTerrain);
        ConfirmedOnOne(forgottenGuest, hostSays, 3);
        forgottenGuest.ForgetPeer();
        Check("write needs both confirmations and the bind on one channel, a session still bound, and a peer not forgotten since",
              peerOnlyWrite == 1 && splitWrite == 1 && droppedWrite == 1 && writableWrite == 0
              && !peerOnlyPeer.Halted && !splitPeer.Halted && !droppedPeer.Halted && !writablePeer.Halted
              && forgottenGuest.ConfirmedSetup is null && forgottenGuest.BoundSetup is null);

        var (_, unboundGuest) = AgreedLobbies(agreedTerrain);
        unboundGuest.ConfirmedLocally(3, "A1B2C3D4E5F60718");
        unboundGuest.ConfirmedByPeer(3, hostSays, unboundGuest.SetupDigest());
        var unboundPeer = new Peer("127.0.0.1", 1, "GATE09") { Binding = new byte[32] };
        var unboundWrite = LobbyWrite(unboundPeer, unboundGuest, "live-probe", yes: false);
        var (_, elsewhereGuest) = AgreedLobbies(agreedTerrain);
        elsewhereGuest.ConfirmedLocally(3, "A1B2C3D4E5F60718");
        elsewhereGuest.ConfirmedByPeer(3, hostSays, elsewhereGuest.SetupDigest());
        elsewhereGuest.BoundOn(2);
        var elsewherePeer = new Peer("127.0.0.1", 1, "GATE10") { Binding = new byte[32] };
        var elsewhereWrite = LobbyWrite(elsewherePeer, elsewhereGuest, "live-probe", yes: false);
        Check("write refuses two confirmations on one channel when the bind happened on another channel or not at all",
              unboundWrite == 1 && elsewhereWrite == 1
              && !unboundPeer.Halted && !elsewherePeer.Halted
              && unboundGuest.BoundSetup is null && elsewhereGuest.BoundSetup is null);

        string[] noListSays =
        [
            "no loaded BoardGame carries uuid 0ECEA5D9B9F841908D9716A1421F0AEC, or it has no settings.",
            "  no loaded BoardGame carries uuid 0ECEA5D9B9F841908D9716A1421F0AEC.",
            "  no loaded BoardGame carries that uuid (0 found). Are you in the right menu, on the agreed challenge?",
        ];
        var noListLines = noListSays
            .Select(s => WriteStepFailed(2, [.. ChildLines("  scanned 6615 MB in 7s (8 threads)", stderr: false),
                                             .. ChildLines(s, stderr: true)]))
            .ToList();
        var otherStepLine = WriteStepFailed(2, [.. ChildLines("  write of MaxVictoryPoints failed.", stderr: true)]);
        var silentStepLine = WriteStepFailed(1, []);
        Check("a write step that fails because the game has no challenge list open says so and says to open it and write again, while any other failed step still says the setup is incomplete and not to start",
              noListLines.All(l => l == NoChallengeListLine)
              && NoChallengeListLine.Contains("challenge list") && NoChallengeListLine.Contains("write again")
              && otherStepLine.StartsWith("live-probe exited 2. ", StringComparison.Ordinal)
              && otherStepLine.EndsWith("Do not start the match.", StringComparison.Ordinal)
              && silentStepLine.StartsWith("live-probe exited 1. ", StringComparison.Ordinal));

        string[] rulesStep = ["--set-rules", "--board-game", "0ECEA5D9B9F841908D9716A1421F0AEC", "--victory-points", "7",
                              "--yes"];
        var stepHanded = new List<string[]>();
        Func<string, string[], List<string>, int[], int> ProbeSays(int exit, string said)
        {
            return (_, args, lines, _) =>
            {
                stepHanded.Add(args);
                lines.AddRange(ChildLines("  scanned 6615 MB in 7s (8 threads)", stderr: false));
                lines.AddRange(ChildLines(said, stderr: true));
                return exit;
            };
        }

        var noListStep = RunWriteStep("live-probe", rulesStep, ProbeSays(2, noListSays[0]));
        var otherStep = RunWriteStep("live-probe", rulesStep, ProbeSays(2, "  write of MaxVictoryPoints failed."));
        var passedStep = RunWriteStep("live-probe", rulesStep, ProbeSays(0, noListSays[0]));
        Check("a write step's own output reaches the line the write stops with: live-probe saying no loaded BoardGame carries the challenge ends it with the challenge-list line, any other output with the step-incomplete line, and a step that passed with nothing",
              noListStep is [var noListWas, var noListSaid]
              && noListWas == $"  the step was: live-probe {string.Join(' ', rulesStep)}"
              && noListSaid == $"\n  {NoChallengeListLine}"
              && otherStep is [_, var otherSaid]
              && otherSaid.StartsWith("\n  live-probe exited 2. ", StringComparison.Ordinal)
              && passedStep is null
              && stepHanded.Count == 3 && stepHanded.All(s => s.SequenceEqual(rulesStep)));

        var namingGuest = new Lobby("X", isHost: false);
        var namingSetup = namingGuest.SetupDigest();
        namingGuest.ConfirmedLocally(5, "A1B2C3D4E5F60718");
        namingGuest.ConfirmedByPeer(5, namingSetup, namingSetup);
        Check("a confirmation binds only while the channel in use still carries the code it named",
              namingGuest.BindingChannel("0000000000000000", 5) is null
              && namingGuest.BindingChannel("A1B2C3D4E5F60718", 5) == 5);

        Check("an exchange that finds the key unchanged still counts and still releases the held frames",
              Peer.CountsAndReleases(Peer.KeyOffer.SameAsCurrent)
              && !Peer.CountsAndReleases(Peer.KeyOffer.Fresh)
              && !Peer.CountsAndReleases(Peer.KeyOffer.Retired)
              && !Peer.CountsAndReleases(Peer.KeyOffer.Malformed));

        Check("the pre-match pollers keep their cadence for ten misses and then back off",
              SnapshotPollDelay(0, 1000) == 1000 && SnapshotPollDelay(PollSlowsAfter - 1, 2000) == 2000
              && SnapshotPollDelay(PollSlowsAfter, 1000) == SlowPollMs
              && SnapshotPollDelay(100, 2000) == SlowPollMs);

        var commitOne = KeyExchange.Commitment(KeyExchange.PublicBlob(hostKey), bound: false);
        var commitTwo = KeyExchange.Commitment(KeyExchange.PublicBlob(guestKey), bound: false);
        Check("D-219: an initiating host keeps its exchange, an initiating guest answers the host's",
              Peer.JudgeCommitment(Peer.Exchange.Initiating, 0, null, commitOne) == Peer.CommitVerdict.Ignore
              && Peer.JudgeCommitment(Peer.Exchange.Initiating, 1, null, commitOne) == Peer.CommitVerdict.Respond);
        Check("D-219: the resend of the commitment being answered is ignored, a new one is answered afresh",
              Peer.JudgeCommitment(Peer.Exchange.Responding, 1, commitOne, [.. commitOne]) == Peer.CommitVerdict.Ignore
              && Peer.JudgeCommitment(Peer.Exchange.Responding, 1, commitOne, commitTwo) == Peer.CommitVerdict.Respond
              && Peer.JudgeCommitment(Peer.Exchange.Idle, 0, null, commitOne) == Peer.CommitVerdict.Respond);

        var hostPubText = Convert.ToBase64String(KeyExchange.PublicBlob(hostKey));
        Check("D-219: a reveal is taken only as the key and the bound flag that were committed to",
              Peer.RevealProblem(hostPubText, bound: false, commitOne) is null
              && Peer.RevealProblem(Convert.ToBase64String(KeyExchange.PublicBlob(guestKey)), false, commitOne) is not null
              && Peer.RevealProblem(hostPubText, bound: true, commitOne) is not null
              && Peer.RevealProblem(hostPubText, bound: false, null) is not null
              && Peer.RevealProblem("not base64 at all!!", bound: false, commitOne) is not null);

        Check("D-219: bound only when both are, --play never open, a bound lobby meeting an open peer drops its own",
              Peer.JudgeBinding(true, true, required: true) == Peer.BindVerdict.Bound
              && Peer.JudgeBinding(true, true, required: false) == Peer.BindVerdict.Bound
              && Peer.JudgeBinding(true, false, required: true) == Peer.BindVerdict.Refuse
              && Peer.JudgeBinding(false, true, required: true) == Peer.BindVerdict.Refuse
              && Peer.JudgeBinding(true, false, required: false) == Peer.BindVerdict.OpenDroppingBinding
              && Peer.JudgeBinding(false, true, required: false) == Peer.BindVerdict.Open
              && Peer.JudgeBinding(false, false, required: false) == Peer.BindVerdict.Open);

        var bindingOne = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var boundHost = KeyExchange.Complete("GATE01", hostKey, guestPub, isHost: true, bindingOne)!;
        var boundGuest = KeyExchange.Complete("GATE01", guestKey, hostPub, isHost: false, bindingOne)!;
        var otherBinding = KeyExchange.Complete("GATE01", guestKey, hostPub, isHost: false,
                                                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))!;
        var openGuest = KeyExchange.Complete("GATE01", guestKey, hostPub, isHost: false)!;
        Check("D-219: a shared binding derives one session, a different binding or none derives another",
              boundHost.Send.SequenceEqual(boundGuest.Recv) && boundHost.Fingerprint == boundGuest.Fingerprint
              && !boundHost.Send.SequenceEqual(otherBinding.Recv) && !boundHost.Send.SequenceEqual(openGuest.Recv)
              && boundHost.Binding.SequenceEqual(boundGuest.Binding)
              && !boundGuest.Binding.SequenceEqual(openGuest.Binding));

        var sixByFive = new BoardSnapshot(6, 5, [], []);
        var unreadable = new BoardSnapshot(0, 0, [], []);

        Check("the cached sample decides the shape when it has one",
              Program.ShapeFrom(sixByFive, null) == (6, 5));

        Check("a fresh read decides it when the cache has none",
              Program.ShapeFrom(null, sixByFive) == (6, 5)
              && Program.ShapeFrom(unreadable, sixByFive) == (6, 5));

        Check("a board that cannot be read yields NO shape rather than a plausible one",
              Program.ShapeFrom(null, null) is null
              && Program.ShapeFrom(unreadable, unreadable) is null);

        Check("a malformed public key is refused rather than throwing",
              KeyExchange.Complete("GATE01", hostKey, [1, 2, 3, 4], isHost: true) is null);

        Check("a room id does not contain the room code",
              !RoomId.For("GATE01").Contains("GATE01"));
        Check("a room id is stable for the same code",
              RoomId.For("GATE01") == RoomId.For("GATE01"));
        Check("a room id ignores case and surrounding space, as a typed code must",
              RoomId.For(" gate01 ") == RoomId.For("GATE01"));
        Check("different codes give different room ids",
              RoomId.For("GATE01") != RoomId.For("GATE02"));
        Check("the room code still decides the keys despite the id being a hash",
              KeyExchange.Complete("gate01", guestKey, hostPub, isHost: false)!.Send
                  .SequenceEqual(guestSide.Send));

        var noSquares = new Lobby("6A1B2C3D-4E5F000", isHost: false);
        noSquares.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF"],
        });
        Check("a setup carrying no placements at all is accepted, which interactive placement needs",
              noSquares.Refusal is null);

        _auto = true;
        _tracker = new TurnTracker(0);
        _autoTurns = 7;
        _sawEnemy = true;
        _capturePath = "stale.jsonl";
        _forceMode = null;
        StartRematch("live-probe", fromPeer: true);
        Check("a rematch clears the finished match's auto state",
              !_auto && _tracker is null && _autoTurns == 0 && !_sawEnemy && _capturePath is null);

        var advice = Presets.PlacementAdvice(
            [.. builtin.Army.Select(m => m.Uuid)], builtin.Placements, [builtin]);
        Check("the placement advice names the machine on each square",
              advice == "Burrower on (3,6) dir 0, Scrounger on (4,6) dir 0");

        var swapped = Presets.PlacementAdvice(
            [.. builtin.Army.Select(m => m.Uuid).Reverse()], builtin.Placements, [builtin]);
        Check("reversing the army reverses which square each machine is named on",
              swapped == "Scrounger on (3,6) dir 0, Burrower on (4,6) dir 0");

        Check("a machine no preset knows still gets a square, named by a short uuid",
              Presets.PlacementAdvice([MachineA], [builtin.Placements[0]], [builtin])
                     .StartsWith("0B7E1F3C... on (3,6)"));

        var advised = new Lobby("B", isHost: true) { Seat = 0 };
        advised.ApplyPreset(builtin);
        advised.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Army = [.. builtin.Army.Select(m => m.Uuid)],
            Placements = [.. builtin.Placements],
        });
        Check("the both-ready instructions name the machine per square, not just the squares",
              BothReadyInstructions(advised, [builtin]).Contains("Burrower on (3,6) dir 0"));

        var presetDir = Path.Combine(Path.GetTempPath(), "strikers-preset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(presetDir);
        try
        {
            var builtinCount = Presets.Load(Path.GetTempPath(), out _).Count;

            var noFile = Presets.Load(presetDir, out var noComplaint);
            Check("with no presets.json the built-ins are still there",
                  noComplaint is null && noFile.Count == builtinCount &&
                  Presets.Find(noFile, "gate") is not null && Presets.Find(noFile, "ranged") is not null);

            File.WriteAllText(Path.Combine(presetDir, Presets.FileName), "{ not json");
            var broken = Presets.Load(presetDir, out var brokenComplaint);
            Check("a broken presets.json complains and keeps the built-ins",
                  brokenComplaint is not null && broken.Count == builtinCount &&
                  Presets.Find(broken, "gate") is not null &&
                  Presets.Find(broken, "whiplash") is not null);

            File.WriteAllText(Path.Combine(presetDir, Presets.FileName),
                              """
                              [{ "name": "gate", "challengeName": "Overridden", "challenge":
                                 "74771C9B66D9441AA4CBD5E47D4851AD",
                                 "army": [{ "name": "Burrower", "uuid": "1C96A39FFE37791F8AE07BD49A2230FF" }],
                                 "placements": [{ "x": 1, "y": 2, "dir": 3 }] },
                               { "name": "extra", "challengeName": "Another", "challenge":
                                 "74771C9B66D9441AA4CBD5E47D4851AD",
                                 "army": [{ "name": "Burrower", "uuid": "1C96A39FFE37791F8AE07BD49A2230FF" }],
                                 "placements": [{ "x": 4, "y": 5, "dir": 1 }] }]
                              """);
            var loaded = Presets.Load(presetDir, out var loadedComplaint);
            Check("a file entry of the same name replaces the built-in rather than duplicating it",
                  loadedComplaint is null && loaded.Count == builtinCount + 1 &&
                  Presets.Find(loaded, "gate")!.ChallengeName == "Overridden");
            Check("a file can add presets the build does not know about",
                  Presets.Find(loaded, "extra") is not null);
        }
        finally
        {
            Directory.Delete(presetDir, recursive: true);
        }

        var writer = new Lobby("B", isHost: true) { Seat = 0 };
        writer.Choose(beginnerHard);
        writer.SetArmy([MachineA, MachineB]);
        writer.Local.Placements = [new Placement { X = 1, Y = 7, Dir = 0 }, new Placement { X = 2, Y = 7, Dir = 0 }];
        writer.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = [MachineB, MachineA],
            Placements = [new Placement { X = 1, Y = 7, Dir = 0 }, new Placement { X = 2, Y = 7, Dir = 0 }],
        });
        var steps = WriteSteps(writer, writer.Remote!);
        Check("write issues one --set-units carrying both seats",
              steps.Count(s => s.Args[0] == "--set-units") == 1);
        Check("that call names the challenge and both seats' armies in order",
              steps[0].Args is ["--set-units", "--board-game", beginnerHard,
                                "--human", MachineA, MachineB, "--ai", MachineB, MachineA]);
        Check("their placements are passed through already rotated, not rotated again",
              steps[1].Args is ["--set-placement", "6", "0", "2", "5", "0", "2"]);

        var interactive = WriteSteps(writer, writer.Remote!, interactivePlacement: true);
        Check("interactive placement writes the armies and nothing else",
              interactive.Length == 1 && interactive[0].Args[0] == "--set-units");
        Check("interactive placement never pre-writes the AI seat's squares",
              !interactive.Any(s => s.Args.Contains("--set-placement")));
        Check("the army write is identical either way, only the placement step differs",
              interactive[0].Args.SequenceEqual(steps[0].Args));

        var pl = new Placement { X = 3, Y = 6, Dir = 2 };
        Check("rotating a placement twice returns the original",
              pl.Rotated().Rotated() is { X: 3, Y: 6, Dir: 2 });

        var shortSetup = new Lobby("B", isHost: true) { Seat = 0 };
        shortSetup.Local.Army = ["a", "b"];
        shortSetup.Local.Placements = [new Placement { X = 1, Y = 0, Dir = 0 }];
        Check("an army with fewer placements than machines is incomplete", !shortSetup.Local.Complete);
        shortSetup.Local.Placements.Add(new Placement { X = 2, Y = 0, Dir = 0 });
        Check("an army with one placement per machine is complete", shortSetup.Local.Complete);

        Check("both ready needs both sides", !shortSetup.BothReady);
        shortSetup.Ready();
        Check("one side ready is not both ready", !shortSetup.BothReady);
        shortSetup.OnReady(new Frame { Kind = MsgKind.Ready });
        Check("both sides ready is both ready", shortSetup.BothReady);

        Check("status names a next action while unpaired",
              new Lobby("B", isHost: true).Status().Contains("room code"));
        Check("status tells the host to pick a challenge once identified",
              PickStage().Contains("pick the challenge"));
        Check("status points at the command that lists challenge uuids",
              PickStage().Contains("challenges"));
        shortSetup.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B" });

        shortSetup.Choose("8BBC182B83FC495AA2150021217530D5");
        shortSetup.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Army = [MachineA, MachineB],
            Placements = Lobby.DefaultSquares(2, Lobby.BoardSide, Lobby.BoardSide, 2),
        });
        var unconfirmedStatus = shortSetup.Status();
        var unconfirmedSteps = BothReadyInstructions(shortSetup, []);
        shortSetup.ConfirmedLocally(3, "A1B2C3D4E5F60718");
        shortSetup.ConfirmedByPeer(3, shortSetup.SetupDigest(), shortSetup.SetupDigest());
        shortSetup.BoundOn(3);
        Check("status and the both-ready steps send a ready pair to confirm the safety code before write, and a bound pair to write",
              unconfirmedStatus.Contains("run: confirm <code>, then") && unconfirmedStatus.Contains("run: write")
              && unconfirmedSteps.Contains("run: confirm <code>") && unconfirmedSteps.Contains("run: write")
              && LobbyCommandList.Contains("confirm <code>")
              && shortSetup.Status().Contains("Machine Strike menu and run: write")
              && !shortSetup.Status().Contains("confirm <code>")
              && !BothReadyInstructions(shortSetup, []).Contains("confirm <code>"));
        Check("status surfaces a build refusal instead of a next action",
              crossBuild.Status().Contains("REFUSED") && !crossBuild.Status().Contains("next       :"));

        static async Task<JsonNode?> RoundTrip(JsonNode msg)
        {
            var (a, b) = LoopbackPair();
            using var writer = new TunnelClient.Framed(a);
            using var reader = new TunnelClient.Framed(b);
            await writer.SendAsync(msg, CancellationToken.None);
            return await reader.RecvAsync(CancellationToken.None);
        }

        Check("a Hello frame round-trips as serde's one-key object",
              TunnelClient.Decode(await RoundTrip(TunnelClient.Msg("Hello", 0))) is ("Hello", not null));
        Check("an Accept frame carries the connection uuid",
              TunnelClient.Decode(await RoundTrip(TunnelClient.Msg("Accept", "b0a1c2d3-0000-0000-0000-000000000000")))
                  is ("Accept", { } id) && id.GetValue<string>().StartsWith("b0a1c2d3"));
        Check("a unit variant decodes from a bare string, not an object",
              TunnelClient.Decode(JsonNode.Parse("\"Heartbeat\"")) is ("Heartbeat", null));
        Check("a payload variant decodes to its value",
              TunnelClient.Decode(JsonNode.Parse("{\"Hello\":32456}")) is ("Hello", { } p) &&
              p.GetValue<int>() == 32456);
        Check("garbage decodes to nothing rather than to a plausible message",
              TunnelClient.Decode(JsonNode.Parse("{\"Hello\":1,\"Error\":\"x\"}")) is ("", null));

        var forgedKind = TunnelClient.Decode(
            JsonNode.Parse("{\"x\\n  tunnel open: --server evil.example:9000\":0}"));

        Check("a server can still name a kind carrying a line break, so the decode is not the guard",
              forgedKind.Kind.Contains((char)0x000A));

        Check("the raw kind would have produced a second line that reads as an address",
              forgedKind.Kind.Split((char)0x000A) is [_, "  tunnel open: --server evil.example:9000"]);

        foreach (var hostile in new object?[]
                 {
                     forgedKind.Kind,
                     JsonNode.Parse("\"a\\r\\nb\""),
                     JsonNode.Parse("{\"deep\":{\"nested\":\"x\\ny\"}}"),
                     null,
                 })
        {
            Check($"a tunnel line built from server text is one line ({hostile?.GetType().Name ?? "null"})",
                  TunnelClient.Says("  tunnel: ignoring", hostile).Split((char)0x000A).Length == 1);
        }

        var forgedDeath = TunnelClient.DiedLine(new TunnelClient.Fault(
            TunnelClient.Says("server error", "HALT: the other PC stopped the match: hash differs")));
        var resetDeath = TunnelClient.DiedLine(new IOException("Unable to read data: HALT, the board diverged",
                                                               new SocketException((int)SocketError.ConnectionReset)));
        var otherEnd = TunnelClient.EndedLine(Guid.Empty, new InvalidOperationException("HALT desync"));
        Check("the tunnel's DIED and ended lines carry no word the tunnel server or the socket chose, so neither can spell a halt",
              forgedDeath.StartsWith("  Warning: the tunnel DIED, server error: ", StringComparison.Ordinal)
              && resetDeath == "  Warning: the tunnel DIED, IOException ConnectionReset"
              && otherEnd.EndsWith(" ended, InvalidOperationException", StringComparison.Ordinal)
              && !new[] { forgedDeath, resetDeath, otherEnd }.Any(
                  line => line.Contains("halt", StringComparison.OrdinalIgnoreCase)));

        var (proveOurs, proveServer) = LoopbackPair();
        var proveFailed = false;
        var proveClosed = false;
        using (var proveTheirs = new TunnelClient.Framed(proveServer))
        {
            await proveTheirs.SendAsync(TunnelClient.Msg("Hello", 1), cts.Token);
            try
            {
                await TunnelClient.Prove(new TunnelClient.Framed(proveOurs), "secret", TimeSpan.FromSeconds(2),
                                         cts.Token);
            }
            catch (TunnelClient.Fault)
            {
                proveFailed = true;
            }

            using var proveWindow = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            proveWindow.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                proveClosed = await proveServer.GetStream().ReadAsync(new byte[16], proveWindow.Token) == 0;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                proveClosed = true;
            }
        }

        GC.KeepAlive(proveOurs);
        Check("a tunnel dial that fails its secret's challenge closes its socket at once, not at the finaliser",
              proveFailed && proveClosed);
        proveOurs.Dispose();

        Check("a challenge uuid converts to bytes in RFC order, not .NET's",
              TunnelClient.ChallengeBytes("00112233-4455-6677-8899-aabbccddeeff")
                  is [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff]);
        Check("Guid.ToByteArray would have got that wrong",
              Guid.Parse("00112233-4455-6677-8899-aabbccddeeff").ToByteArray()[0] != 0x00);
        Check("an auth answer is lowercase hex of a sha-256 mac",
              TunnelClient.Answer("secret", "00112233-4455-6677-8899-aabbccddeeff") is { Length: 64 } ans &&
              ans.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));

        var (big, sink) = LoopbackPair();
        using (var flood = new TunnelClient.Framed(sink))
        {
            await big.GetStream().WriteAsync(Enumerable.Repeat((byte)'x', 600).ToArray());
            var refused = false;
            try { await flood.RecvAsync(CancellationToken.None); }
            catch (InvalidOperationException) { refused = true; }
            Check("a frame with no delimiter is refused past the 256-byte cap", refused);
        }
        big.Dispose();

        Check("only a Heartbeat is idle and only a Connection is work, every other kind ends the tunnel",
              TunnelClient.Judge("Heartbeat") == TunnelClient.Heard.Ignore
              && TunnelClient.Judge("Connection") == TunnelClient.Heard.Forward
              && TunnelClient.Judge("Error") == TunnelClient.Heard.Die
              && TunnelClient.Judge("HALT: forged") == TunnelClient.Heard.Die
              && TunnelClient.Judge("") == TunnelClient.Heard.Die);
        Check("the public port is taken only as a number from 1 to 65535",
              TunnelClient.PublicPort(JsonNode.Parse("24877")) == 24877
              && TunnelClient.PublicPort(JsonNode.Parse("65535")) == 65535
              && TunnelClient.PublicPort(JsonNode.Parse("0")) is null
              && TunnelClient.PublicPort(JsonNode.Parse("70000")) is null
              && TunnelClient.PublicPort(JsonNode.Parse("\"24877\"")) is null
              && TunnelClient.PublicPort(null) is null);

        var mute = new TcpListener(IPAddress.Loopback, 0);
        mute.Start();
        var muteAccept = mute.AcceptTcpClientAsync(cts.Token);
        var muteRun = TunnelClient.RunAgainst(((IPEndPoint)mute.LocalEndpoint).Port,
                                              TimeSpan.FromMilliseconds(300), cts.Token);
        Check("a server that says nothing ends the tunnel instead of holding it open for ever",
              await Task.WhenAny(muteRun, Task.Delay(TimeSpan.FromSeconds(2))) == muteRun);
        (await muteAccept).Dispose();
        mute.Stop();

        var chatty = new TcpListener(IPAddress.Loopback, 0);
        chatty.Start();
        var chattyRun = TunnelClient.RunAgainst(((IPEndPoint)chatty.LocalEndpoint).Port,
                                                TimeSpan.FromSeconds(20), cts.Token);
        using (var standIn = new TunnelClient.Framed(await chatty.AcceptTcpClientAsync(cts.Token)))
        {
            await standIn.SendAsync(TunnelClient.Msg("Hello", 24877), cts.Token);
            await standIn.SendAsync(TunnelClient.Msg("HALT", 1), cts.Token);
            Check("a message kind the protocol does not have ends the tunnel at once",
                  await Task.WhenAny(chattyRun, Task.Delay(TimeSpan.FromSeconds(2))) == chattyRun);
        }
        chatty.Stop();

        var identRound = Protocol.Decode(Protocol.Encode(new Frame { Kind = MsgKind.Ident, Build = "abc-123",
                                                                     NetplayVersion = "nv1", ProbeVersion = "pv1" }));
        Check("an Ident frame round-trips through the wire format",
              identRound is { Kind: MsgKind.Ident, Build: "abc-123", NetplayVersion: "nv1", ProbeVersion: "pv1" });

        var setupRound = Protocol.Decode(Protocol.Encode(new Frame
        {
            Kind = MsgKind.Setup, Challenge = "c", Army = ["u"],
            Placements = [new Placement { X = 4, Y = 5, Dir = 3 }],
        }));
        Check("a Setup frame round-trips army, challenge and placements",
              setupRound is { Kind: MsgKind.Setup, Challenge: "c" } &&
              setupRound.Army is ["u"] && setupRound.Placements![0] is { X: 4, Y: 5, Dir: 3 });

        var (lh, lg) = await Pair(port, cts.Token);
        var lobbyInbox = new List<Frame>();
        lg.Received += f => { lock (lobbyInbox) { lobbyInbox.Add(f); } };
        lh.Send(new Frame { Kind = MsgKind.Ident, Build = "relayed-build" });
        lh.Send(new Frame { Kind = MsgKind.Setup, Challenge = "relayed-challenge", Army = ["u1"], Placements = [new Placement { X = 0, Y = 0, Dir = 0 }] });
        await Task.Delay(400, cts.Token);
        lock (lobbyInbox)
        {
            Check("an Ident crosses the relay to the other seat",
                  lobbyInbox.Any(f => f.Kind == MsgKind.Ident && f.Build == "relayed-build"));
            Check("a Setup crosses the relay to the other seat",
                  lobbyInbox.Any(f => f.Kind == MsgKind.Setup && f.Challenge == "relayed-challenge"));
        }

        var tracker = new TurnTracker(0);

        BoardSnapshot Two(int x1, int y1, int a1, int x2, int y2, int a2, int ex, int ey)
        {
            return Frame(Pc(0, x1, y1, 4, 0, 0, acts: a1), Pc(1, x2, y2, 4, 0, 0, acts: a2), Pc(2, ex, ey, 4, 2, 1));
        }

        Check("an opening sample is not a completed turn", tracker.Push(Two(3, 6, 0, 4, 6, 0, 6, 1)) is null);
        Check("a quiet sample is not a completed turn", tracker.Push(Two(3, 6, 0, 4, 6, 0, 6, 1)) is null);
        Check("our first action does not end the turn", tracker.Push(Two(0, 6, 1, 4, 6, 0, 6, 1)) is null);
        Check("our second action does not end the turn", tracker.Push(Two(0, 6, 1, 1, 6, 1, 6, 1)) is null);

        var turn1 = tracker.Push(Two(0, 6, 0, 1, 6, 0, 6, 1));
        Check("the counters clearing is what ends the turn", turn1 is not null);
        Check("the slice opens before our first mark and stops before the clear",
              turn1 is { Count: >= 3 } && !TurnBoundary.AnyMarked(turn1[0], 0) && TurnBoundary.AnyMarked(turn1[^1], 0));
        Check("the sliced turn reads as the two actions that were played",
              MoveDetector.DetectSequence(turn1!, 0) is { Ok: true, Moves.Count: 2 });

        Check("the opponent moving does not end our turn",
              tracker.Push(Two(0, 6, 0, 1, 6, 0, 6, 3)) is null &&
              tracker.Push(Two(0, 6, 0, 1, 6, 0, 6, 5)) is null);

        Check("our next action does not end the turn either", tracker.Push(Two(0, 4, 1, 1, 6, 0, 6, 5)) is null);

        var actedThenDied = Frame(Pc(0, 3, 4, 2, 0, 0, acts: 1), Pc(1, 4, 3, 5, 0, 0), Pc(2, 5, 2, 2, 0, 1));
        var itIsGone = Frame(Pc(1, 4, 3, 5, 0, 0), Pc(2, 5, 2, 2, 0, 1));
        Check("our only marked machine dying is NOT a turn end",
              !TurnBoundary.Ended(actedThenDied, itIsGone, 0));

        var bothStanding = Frame(Pc(0, 3, 4, 2, 0, 0), Pc(1, 4, 3, 5, 0, 0), Pc(2, 5, 2, 2, 0, 1));
        Check("a wipe with the marked machine still standing IS a turn end",
              TurnBoundary.Ended(actedThenDied, bothStanding, 0));

        var twoMarked = Frame(Pc(0, 3, 4, 2, 0, 0, acts: 1), Pc(1, 4, 3, 5, 0, 0, acts: 1), Pc(2, 5, 2, 2, 0, 1));
        var oneDiedRestWiped = Frame(Pc(1, 4, 3, 5, 0, 0), Pc(2, 5, 2, 2, 0, 1));
        Check("one marked machine dying while another survives unmarked IS a turn end",
              TurnBoundary.Ended(twoMarked, oneDiedRestWiped, 0));

        var compacted = Frame(Pc(0, 4, 3, 5, 0, 0), Pc(1, 5, 2, 2, 0, 1));
        Check("the survivor is found across a list compaction, which renumbers idx",
              TurnBoundary.Ended(twoMarked, compacted, 0));

        var theirsMarked = Frame(Pc(0, 3, 4, 2, 0, 0), Pc(1, 4, 3, 5, 0, 1, acts: 1));
        var theirsWiped = Frame(Pc(0, 3, 4, 2, 0, 0), Pc(1, 4, 3, 5, 0, 1));
        Check("the PEER's turn ending is a boundary, which our own-seat edge cannot see",
              TurnBoundary.AnyTurnEnded(theirsMarked, theirsWiped) &&
              !TurnBoundary.Ended(theirsMarked, theirsWiped, 0));

        Check("a marked machine dying is not a boundary either, whichever seat owned it",
              !TurnBoundary.AnyTurnEnded(actedThenDied, itIsGone));

        Check("two samples with no marks at all hold no boundary",
              !TurnBoundary.AnyTurnEnded(theirsWiped, theirsWiped));

        List<BoardSnapshot> DyingPair(int turnSeat)
        {
            var enemy = Pc(2, 5, 2, 12, 2, 1);
            var hurt = Pc(2, 5, 2, 8, 2, 1);
            return
            [
                Frame(Pc(0, 3, 6, 2, 0, 0), Pc(1, 4, 7, 2, 0, 0), enemy) with { Turn = turnSeat },
                Frame(Pc(0, 3, 5, 2, 0, 0, acts: 1), Pc(1, 4, 7, 2, 0, 0), enemy) with { Turn = turnSeat },
                Frame(Pc(1, 4, 7, 2, 0, 0), enemy) with { Turn = turnSeat },
                Frame(Pc(1, 4, 5, 2, 0, 0, acts: 1), enemy) with { Turn = turnSeat },
                Frame(hurt) with { Turn = turnSeat },
                Frame(hurt) with { Turn = turnSeat == 0 ? 1 : -1 },
            ];
        }

        IReadOnlyList<BoardSnapshot>? Feed(TurnTracker t, IEnumerable<BoardSnapshot> samples, List<int> cutAt)
        {
            IReadOnlyList<BoardSnapshot>? cut = null;
            var i = 0;
            foreach (var s in samples)
            {
                if (t.Push(s) is { } c)
                {
                    cut ??= c;
                    cutAt.Add(i);
                }

                i++;
            }

            return cut;
        }

        var stallCuts = new List<int>();
        var stallTurn = Feed(new TurnTracker(0), DyingPair(0), stallCuts);
        Check("D-248: a turn whose every acting machine died ends when the game hands the turn over, at that sample only",
              stallCuts is [5] && stallTurn is { Count: >= 4 } && stallTurn[^1].Pieces.Count == 1
              && stallTurn.Any(s => TurnBoundary.AnyMarked(s, 0)));

        var oldCuts = new List<int>();
        Feed(new TurnTracker(0), DyingPair(-1), oldCuts);
        Check("D-248: a stream with no turn field keeps the old rule, so the same turn never closes there",
              oldCuts.Count == 0);

        var earlyFlip = new List<BoardSnapshot>(DyingPair(0).Take(4))
        {
            Frame(Pc(1, 4, 5, 2, 0, 0, acts: 1), Pc(2, 5, 2, 8, 2, 1)) with { Turn = 1 },
            Frame(Pc(2, 5, 2, 8, 2, 1)) with { Turn = 1 },
            Frame(Pc(2, 5, 2, 8, 2, 1)) with { Turn = 1 },
        };
        var earlyCuts = new List<int>();
        Feed(new TurnTracker(0), earlyFlip, earlyCuts);
        Check("D-248: the turn handed over a sample before the last actor's death still closes once, at the death",
              earlyCuts is [5]);

        var survivorLate = new List<BoardSnapshot>
        {
            Frame(Pc(0, 3, 6, 4, 0, 0), Pc(2, 5, 2, 4, 2, 1)) with { Turn = 0 },
            Frame(Pc(0, 3, 5, 4, 0, 0, acts: 1), Pc(2, 5, 2, 4, 2, 1)) with { Turn = 0 },
            Frame(Pc(0, 3, 5, 4, 0, 0), Pc(2, 5, 2, 4, 2, 1)) with { Turn = 0 },
            Frame(Pc(0, 3, 5, 4, 0, 0), Pc(2, 5, 2, 4, 2, 1)) with { Turn = 1 },
        };
        var survivorEarly = new List<BoardSnapshot>
        {
            survivorLate[0],
            survivorLate[1],
            survivorLate[1] with { Turn = 1 },
            survivorLate[3],
            survivorLate[3],
        };
        var lateCuts = new List<int>();
        var earlyHandCuts = new List<int>();
        Feed(new TurnTracker(0), survivorLate, lateCuts);
        Feed(new TurnTracker(0), survivorEarly, earlyHandCuts);
        Check("D-248: a turn with a surviving actor closes once, whether the marks clear before or after the hand-over",
              lateCuts is [2] && earlyHandCuts is [3]);

        List<int> BoundaryFires(List<BoardSnapshot> samples)
        {
            var watch = new TurnPassWatch(-1);
            var fires = new List<int>();
            for (var k = 1; k < samples.Count; k++)
            {
                var marks = TurnBoundary.AnyTurnEnded(samples[k - 1], samples[k]);
                if (watch.Observe(samples[k - 1], samples[k], marks) || marks)
                {
                    fires.Add(k);
                }
            }

            return fires;
        }

        Check("D-248: the boundary count never counts one turn twice when the marks and the hand-over land apart",
              BoundaryFires(survivorEarly) is [3] && BoundaryFires(survivorLate) is [2]);

        var peerDying = DyingPair(0).Select(s => s with
        {
            Pieces = s.Pieces.Select(p => p with { Owner = 1 - p.Owner }).Append(Pc(3, 1, 1, 3, 0, 1)).ToList(),
            Turn = s.Turn == 0 ? 1 : 0,
        }).ToList();
        Check("D-248: the boundary count sees the PEER's all-dead turn hand over too, once",
              BoundaryFires(peerDying) is [5]);

        Check("D-248: the turn field parses as a seat, and anything else or nothing reads as unknown",
              SnapshotJson.TryParse("{\"width\":1,\"height\":1,\"pieces\":[],\"terrain\":[0],\"turn\":1}", out var withTurn, out _)
              && withTurn.Turn == 1
              && SnapshotJson.TryParse("{\"width\":1,\"height\":1,\"pieces\":[],\"terrain\":[0]}", out var noTurn, out _)
              && noTurn.Turn == -1
              && SnapshotJson.TryParse("{\"width\":1,\"height\":1,\"pieces\":[],\"terrain\":[0],\"turn\":5}", out var badTurn, out _)
              && badTurn.Turn == -1);

        var wrongSeat = DyingPair(0);
        wrongSeat[1] = wrongSeat[1] with { Turn = 1 };
        var wrongSeatCuts = new List<int>();
        Feed(new TurnTracker(0), wrongSeat, wrongSeatCuts);
        Check("a marked sample whose turn names the other seat is not believed, so the first actor's death does not close the turn",
              wrongSeatCuts is [5] && BoundaryFires(wrongSeat) is [5]);

        var overBeforeHandOver = DyingPair(0);
        overBeforeHandOver[4] = overBeforeHandOver[4] with { Match = new MatchState(true, 1, 0, 0, 2) };
        overBeforeHandOver[5] = overBeforeHandOver[5] with { Match = new MatchState(true, 1, 0, 0, 2) };
        var overCuts = new List<int>();
        Feed(new TurnTracker(0), overBeforeHandOver, overCuts);
        Check("a hand-over read after the match is over is no turn boundary, for the tracker or the count",
              overCuts.Count == 0 && BoundaryFires(overBeforeHandOver).Count == 0);

        var mountain = new sbyte[25];
        mountain[12] = 3;
        BoardSnapshot MountainBoard(int turn, params Piece[] ps)
        {
            return new BoardSnapshot(5, 5, ps.ToList(), mountain) { AiSeat = 1, Turn = turn, Commits = new CommitBatch(19, 0, []) };
        }

        var theirGrazer = Pc(0, 1, 3, 4, 2, 1, skill: 1, range: 1, uuid: grazer);
        var theirBurrower = Pc(1, 2, 2, 3, 2, 1, range: 1, uuid: burrowerUuid);
        var ourBurrower = Pc(2, 2, 3, 1, 0, 0, range: 1, uuid: burrowerUuid);
        var theirCharger = Pc(3, 3, 0, 2, 2, 1, skill: 1, range: 2, uuid: chargerUuid);
        var ourCharger = Pc(4, 0, 4, 4, 0, 0, skill: 1, range: 2, uuid: chargerUuid);
        var knocked = theirBurrower with { Y = 1, Health = 2 };
        var theirChargerAfter = theirCharger with { Idx = 2 };
        var ourChargerAfter = ourCharger with { Idx = 3 };
        var knockedLast = knocked with { Idx = 0 };
        var theirChargerLast = theirCharger with { Idx = 1 };
        var landedCharger = ourCharger with { Idx = 2, X = 2, Y = 3, Facing = 1, Acts = 1 };
        List<BoardSnapshot> opensWithSelfKill =
        [
            MountainBoard(1, theirGrazer, theirBurrower with { Acts = 1 }, ourBurrower,
                          theirCharger with { Acts = 1, Bursts = 1, Burst = true }, ourCharger),
            MountainBoard(0, theirGrazer, theirBurrower, ourBurrower, theirCharger, ourCharger),
            WithCommits(MountainBoard(0, theirGrazer, theirBurrower with { Health = 2 }, ourBurrower with { Health = 0 },
                                      theirCharger, ourCharger),
                        Rec(20, "activate", "14E3C210100", -1, -1, -1, -1, unit: 2),
                        Rec(21, "attack", "14E3C210100", 2, 3, 2, 3, unit: 2)),
            MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter),
            WithCommits(MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter with { Y = 3 }),
                        Rec(22, "activate", "14F83DF0780", -1, -1, -1, -1, unit: 3)),
            MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter with { Y = 3, Facing = 1 }),
            WithCommits(MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter),
                        Rec(23, "attack", "14F83DF0780", 0, 4, 0, 3, unit: 3)),
            MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter with { Y = 3 }),
            MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter with { Y = 3, Facing = 1 }),
            MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter with { X = 2, Y = 3, Facing = 1 }),
            MountainBoard(0, theirGrazer with { Health = 0 }, knocked, theirChargerAfter,
                          ourChargerAfter with { X = 2, Y = 3, Facing = 1 }),
            MountainBoard(0, knockedLast, theirChargerLast, landedCharger),
            WithCommits(MountainBoard(0, knockedLast, theirChargerLast, landedCharger with { X = 3 }),
                        Rec(24, "burst", "14F83DF0780", -1, -1, -1, -1, unit: 2)),
            MountainBoard(0, knockedLast, theirChargerLast, landedCharger with { X = 3, Y = 2 }),
            WithCommits(MountainBoard(0, knockedLast, theirChargerLast, landedCharger),
                        Rec(25, "move", "14F83DF0780", 2, 3, 3, 2, unit: 2)),
            MountainBoard(0, knockedLast, theirChargerLast, landedCharger with { X = 3, Y = 2, Bursts = 1, Burst = true }),
            MountainBoard(0, knockedLast, theirChargerLast,
                          landedCharger with { X = 3, Y = 2, Health = 2, Bursts = 1, Burst = true }),
            MountainBoard(1, knockedLast, theirChargerLast, landedCharger with { X = 3, Y = 2, Health = 2, Acts = 0 }),
        ];
        var selfKillCuts = new List<int>();
        var selfKillTurn = Feed(new TurnTracker(0), opensWithSelfKill, selfKillCuts);
        var selfKillTail = new TurnTracker(0);
        foreach (var selfKillSample in opensWithSelfKill.Take(opensWithSelfKill.Count - 1))
        {
            selfKillTail.Push(selfKillSample);
        }

        bool ReadsSelfKillFirst(IReadOnlyList<BoardSnapshot>? slice)
        {
            if (slice is not { Count: > 0 } || slice[0].Turn != 0)
            {
                return false;
            }

            var selfKillRead = MoveDetector.ReadTurn(slice, 0, out _);
            return selfKillRead is
            {
                Ok: true,
                Moves:
                [
                    { Attack: true, SrcX: 2, SrcY: 3, DstX: 2, DstY: 3, TargetX: 2, TargetY: 2, Burst: false },
                    { Attack: true, SrcX: 0, SrcY: 4, DstX: 0, DstY: 3, TargetX: 1, TargetY: 3, LandX: 2, LandY: 3, Burst: false },
                    { Attack: false, SrcX: 2, SrcY: 3, DstX: 3, DstY: 2, Burst: true },
                ],
            } && Machines.TurnProblem(selfKillRead.Moves, slice[0], 0) is null;
        }

        var theirChargerMarked = theirChargerAfter with { Acts = 1, Bursts = 1, Burst = true };
        var handedOverEarly = new List<BoardSnapshot>
        {
            MountainBoard(1, theirGrazer, knocked, theirChargerMarked, ourChargerAfter),
            MountainBoard(0, theirGrazer, knocked, theirChargerMarked, ourChargerAfter),
            MountainBoard(0, theirGrazer, knocked, theirChargerAfter, ourChargerAfter),
        };
        handedOverEarly.AddRange(opensWithSelfKill.Skip(4));
        var earlyHandOverCuts = new List<int>();
        var earlyHandOverTurn = Feed(new TurnTracker(0), handedOverEarly, earlyHandOverCuts);
        var earlyHandOverRead = earlyHandOverTurn is null ? null : MoveDetector.ReadTurn(earlyHandOverTurn, 0, out _);

        Check("a turn that opens with an in-place attack whose attacker dies of its own blow is read from the " +
              "hand-over, the attack first and then the turn's later actions, at the turn's end and at the match's end, " +
              "while a turn with no record of ours before its first mark still starts where the opponent's marks cleared " +
              "(dying-attacker-opens-turn, PC2's turn 2)",
              selfKillCuts is [17] && ReadsSelfKillFirst(selfKillTurn) && ReadsSelfKillFirst(selfKillTail.Flush())
              && earlyHandOverCuts is [16] && earlyHandOverTurn is { Count: 14 } && !TurnBoundary.AnyMarkedAtAll(earlyHandOverTurn[0])
              && earlyHandOverRead is { Ok: true, Moves: [{ Attack: true, SrcX: 0, SrcY: 4 }, { Attack: false, Burst: true }] });

        var dyingStrike = new Move { SrcX = 2, SrcY = 3, DstX = 2, DstY = 3, Facing = 0, Attack = true, TargetX = 2, TargetY = 2 };
        var chargeOntoTheDead = new Move
        {
            SrcX = 0, SrcY = 4, DstX = 0, DstY = 3, Facing = 1, Attack = true, TargetX = 1, TargetY = 3, LandX = 2, LandY = 3,
        };
        var overchargeCharge = new Move
        {
            SrcX = 2, SrcY = 3, DstX = 2, DstY = 3, Facing = 1, Attack = true, TargetX = 3, TargetY = 3, LandX = 4, LandY = 3,
            Burst = true,
        };
        var overchargeWalk = new Move { SrcX = 2, SrcY = 3, DstX = 3, DstY = 2, Facing = 1, Burst = true };
        var overLongWalk = new Move { SrcX = 2, SrcY = 3, DstX = 4, DstY = 0, Facing = 1, Burst = true };
        var chargerInLine = opensWithSelfKill[1] with
        {
            Pieces = opensWithSelfKill[1].Pieces.Select(p => p.Idx == theirCharger.Idx ? p with { Y = 3 } : p).ToList(),
        };
        var chargerListedFirst = chargerInLine with
        {
            Pieces = chargerInLine.Pieces.OrderBy(p => p.Idx == ourCharger.Idx ? 0 : 1).ToList(),
        };
        Check("after an attacker of ours dies of its own blow, a later action from its square is judged by the " +
              "machine that arrived there last, so the Charger's Overcharge charge from the dead Burrower's square " +
              "passes whichever of the two the board lists first, its Overcharge move still passes, and a walk too long " +
              "for a Charger is still refused (dying-attacker-opens-turn, PC2's turn 2)",
              Machines.TurnProblem([dyingStrike, chargeOntoTheDead, overchargeCharge], chargerInLine, 0) is null
              && Machines.TurnProblem([dyingStrike, chargeOntoTheDead, overchargeCharge], chargerListedFirst, 0) is null
              && Machines.TurnProblem([dyingStrike, chargeOntoTheDead, overchargeWalk], opensWithSelfKill[1], 0) is null
              && Machines.TurnProblem([dyingStrike, chargeOntoTheDead, overLongWalk], opensWithSelfKill[1], 0) is { } tooFar
              && tooFar.Contains("action 3 walks a Charger 5 squares"));

        var atBoundary = SampleBoard();
        var theirView = RotateByHand(atBoundary);
        var afterTheirSpin = theirView with
        {
            Pieces = theirView.Pieces
                .Select((p, i) => i < 2 ? p with { Facing = (byte)((p.Facing + 2) % 4) } : p)
                .ToList(),
        };

        Check("the two views agree at the boundary sample",
              BoardHash.Pieces(atBoundary, 0) == BoardHash.Pieces(theirView, 1));
        Check("a turn-start spin one sample later makes them disagree, which is the halt that was seen",
              BoardHash.Pieces(atBoundary, 0) != BoardHash.Pieces(afterTheirSpin, 1));

        var spunOurSide = atBoundary with
        {
            Pieces = atBoundary.Pieces
                .Select((p, i) => i < 2 ? p with { Facing = (byte)((p.Facing + 2) % 4) } : p)
                .ToList(),
        };

        var theirBoundarySet = new List<BoardSnapshot> { theirView, theirView, afterTheirSpin };
        var set1 = new Peer("127.0.0.1", port, "SETHASH", 0);
        var set2 = new Peer("127.0.0.1", port, "SETHASH", 1);
        _ = set1.Run(cts.Token);
        _ = set2.Run(cts.Token);
        await Until(() => set1.Seat >= 0 && set2.Seat >= 0, cts.Token);

        var spunFrame = new Frame
        {
            Kind = MsgKind.Hash,
            PieceHash = BoardHash.Pieces(spunOurSide, 0),
            TerrainHash = BoardHash.Terrain(spunOurSide, 0),
        };

        Check("a hash taken one sample later still matches, because the set holds that board too",
              set2.CheckHash(spunFrame, theirBoundarySet, 1) && !set2.Halted);

        var reallyMoved = spunOurSide with
        {
            Pieces = [.. spunOurSide.Pieces.Skip(1), spunOurSide.Pieces[0] with { X = 0, Y = 0 }],
        };
        var movedFrame = new Frame
        {
            Kind = MsgKind.Hash,
            PieceHash = BoardHash.Pieces(reallyMoved, 0),
            TerrainHash = BoardHash.Terrain(reallyMoved, 0),
        };

        Check("a genuinely different board still halts, set or no set",
              !set2.CheckHash(movedFrame, theirBoundarySet, 1) && set2.Halted);

        var turn2 = tracker.Push(Two(0, 4, 0, 1, 6, 0, 6, 5));
        Check("a second turn is cut the same way",
              turn2 is not null && MoveDetector.DetectSequence(turn2!, 0) is { Ok: true, Moves.Count: 1 });
        Check("the second slice leaves the opponent's move outside it",
              turn2 is { Count: 2 } && turn2[0].Pieces.Single(p => p.Owner == 1) is { X: 6, Y: 5 });

        var preview = new TurnTracker(0);
        BoardSnapshot Pv(int x, int y, int f, int a, int sx, int sy, int sf, int sa)
        {
            return Frame(Pc(0, x, y, 4, f, 0, acts: a), Pc(1, 3, 1, 4, 2, 1),
                  Pc(2, sx, sy, 5, sf, 0, acts: sa), Pc(3, 4, 1, 5, 2, 1));
        }

        preview.Push(Pv(4, 6, 0, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 5, 0, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 4, 0, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 3, 0, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 3, 3, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 6, 0, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 3, 0, 0, 3, 6, 0, 0));
        preview.Push(Pv(4, 3, 3, 1, 3, 6, 0, 0));
        preview.Push(Pv(4, 3, 3, 1, 4, 4, 0, 0));
        preview.Push(Pv(4, 3, 3, 1, 4, 4, 3, 1));
        var previewTurn = preview.Push(Pv(4, 3, 3, 0, 4, 4, 3, 0));

        Check("the slice opens before our own preview began, not one sample before the commit",
              previewTurn is not null && previewTurn[0].Pieces.Single(p => p is { Owner: 0, Idx: 0 }) is { X: 4, Y: 6 });
        Check("a previewed square never becomes the committed one it is read back as",
              previewTurn is not null &&
              MoveDetector.DetectSequence(previewTurn!, 0) is { Ok: true, Moves.Count: 2 } spv &&
              spv.Moves[0] is { SrcX: 4, SrcY: 6, DstX: 4, DstY: 3, Attack: false } &&
              spv.Moves[1] is { SrcX: 3, SrcY: 6, DstX: 4, DstY: 4, Attack: false });

        var attackFirst = new TurnTracker(0);
        BoardSnapshot Af(int enemyHp, int enemyActs, int ourX, int ourY, int ourActs)
        {
            return Frame(Pc(0, 4, 3, enemyHp, 2, 1, acts: enemyActs), Pc(1, ourX, ourY, 4, 0, 0, acts: ourActs));
        }

        attackFirst.Push(Af(4, 1, 4, 6, 0));
        attackFirst.Push(Af(4, 0, 4, 6, 0));
        attackFirst.Push(Af(4, 0, 4, 5, 0));
        attackFirst.Push(Af(4, 0, 4, 4, 0));
        attackFirst.Push(Af(3, 0, 4, 4, 0));
        attackFirst.Push(Af(3, 0, 4, 4, 1));
        var af = attackFirst.Push(Af(3, 0, 4, 4, 0));

        Check("enemy damage does not end the walk, it is our own attack landing early",
              af is not null && af[0].Pieces.Single(p => p.Owner == 0) is { X: 4, Y: 6 });
        Check("a turn whose first action is an attack still reads its true source square",
              af is not null && MoveDetector.DetectSequence(af!, 0) is { Ok: true, Moves.Count: 1 } saf &&
              saf.Moves[0] is { SrcX: 4, SrcY: 6, Attack: true, TargetX: 4, TargetY: 3 });

        var dying = new TurnTracker(0);
        var stillOpen = dying.Push(Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1)));
        dying.Push(Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 0, 2, 1)));
        var last = Frame(Pc(0, 2, 4, 1, 0, 0));
        Check("no closing edge is ever produced by the turn that ends the match",
              stillOpen is null && dying.Push(last) is null);
        Check("a match ending yields nothing until it is flushed",
              dying.Flush() is { Count: 3 } flushed &&
              MoveDetector.DetectSequence(flushed, 0) is { Ok: true, Moves.Count: 1 });

        Check("an empty opposing side is what says the match is over",
              TurnBoundary.NoOpponentLeft(last, 0));
        Check("an opponent still on the board is not a finished match",
              !TurnBoundary.NoOpponentLeft(Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1)), 0));
        Check("an empty board also reads as no opponent, which is why the caller needs a seen-them guard",
              TurnBoundary.NoOpponentLeft(Frame(Pc(0, 2, 4, 1, 0, 0)), 0));

        Check("a turn that leaves the opponent with nothing ends the match",
              TurnBoundary.EndsTheMatch(last, 0, sawEnemy: true));
        Check("an opponent still standing is not a match-ending turn",
              !TurnBoundary.EndsTheMatch(Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1)), 0, sawEnemy: true));
        Check("an empty board before the opponent has ever been seen is NOT a match-ending turn",
              !TurnBoundary.EndsTheMatch(Frame(Pc(0, 2, 4, 1, 0, 0)), 0, sawEnemy: false));

        var pointsBoard = Frame(Pc(0, 2, 4, 1, 0, 0), Pc(1, 2, 3, 1, 2, 1));
        var pointsWon = pointsBoard with { Match = new MatchState(Over: true, Winner: 0, 2, 0, 2) };
        var pointsRunning = pointsBoard with { Match = new MatchState(Over: false, Winner: -1, 1, 0, 2) };

        Check("a match won on victory points is over even with the loser still on the board",
              TurnBoundary.MatchOver(pointsWon, 0));
        Check("the same board without the flag set is NOT over",
              !TurnBoundary.MatchOver(pointsRunning, 0));
        Check("a victory-points win is a match-ending turn, so it goes on the wire as final",
              TurnBoundary.EndsTheMatch(pointsWon, 0, sawEnemy: true));
        Check("the same board still running is not a match-ending turn",
              !TurnBoundary.EndsTheMatch(pointsRunning, 0, sawEnemy: true));

        Check("the game's own flag outranks an empty-looking board",
              !TurnBoundary.MatchOver(last with { Match = new MatchState(false, -1, 0, 0, 2) }, 0));

        Check("with no match field at all the elimination test still decides it",
              TurnBoundary.MatchOver(last, 0) &&
              !TurnBoundary.MatchOver(pointsBoard, 0));

        Check("a match snapshot parses its score and threshold",
              SnapshotJson.TryParse(
                  "{\"width\":2,\"height\":2,\"aiSeat\":1,\"pieces\":[],\"terrain\":[0,0,0,0]," +
                  "\"match\":{\"over\":true,\"winner\":0,\"vp\":[5,0],\"vpMax\":10}}",
                  out var vpSnap, out _) &&
              vpSnap.Match is { Over: true, Winner: 0, Vp0: 5, Vp1: 0, VpMax: 10 });
        Check("a snapshot with no match field parses to no opinion rather than a false one",
              SnapshotJson.TryParse(
                  "{\"width\":2,\"height\":2,\"aiSeat\":1,\"pieces\":[],\"terrain\":[0,0,0,0]}",
                  out var noMatchSnap, out _) &&
              noMatchSnap.Match is null);

        var wipe = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 3, 3, 1, 0), Pc(1, 5, 3, 3, 2, 1), Pc(2, 5, 4, 3, 0, 0, acts: 1, bursts: 1, burst: true)),
            Frame(Pc(0, 4, 3, 3, 1, 0), Pc(1, 5, 3, 0, 2, 1), Pc(2, 5, 4, 3, 0, 0, acts: 1, bursts: 1, burst: true)),
            Frame(Pc(0, 4, 3, 3, 1, 0, acts: 1), Pc(1, 5, 4, 3, 0, 0)),
        };

        Check("the counters clearing as the match ends is not a second machine acting",
              MoveDetector.DetectSequence(wipe, 0) is { Ok: true, Moves.Count: 1 } wp &&
              wp.Moves[0] is { SrcX: 4, SrcY: 3, DstX: 4, DstY: 3, Attack: true, TargetX: 5, TargetY: 3 });

        var sweep = new TurnTracker(0);
        BoardSnapshot Sw(int ourX, int ourY, int ourF, int ourActs, int enemyHp, int enemyActs, bool enemyBurst)
        {
            return enemyHp < 0
                ? Frame(Pc(0, ourX, ourY, 3, ourF, 0, acts: ourActs))
                : Frame(Pc(0, ourX, ourY, 3, ourF, 0, acts: ourActs),
                        Pc(1, 5, 3, enemyHp, 2, 1, acts: enemyActs, bursts: enemyBurst ? 1 : 0, burst: enemyBurst));
        }

        sweep.Push(Sw(3, 4, 0, 0, 4, 1, true));
        sweep.Push(Sw(3, 4, 0, 0, 2, 1, true));
        sweep.Push(Sw(3, 4, 0, 0, 2, 0, false));
        sweep.Push(Sw(4, 3, 0, 0, 2, 0, false));
        sweep.Push(Sw(4, 3, 1, 0, 0, 0, false));
        sweep.Push(Sw(4, 3, 1, 1, -1, 0, false));

        var swept = sweep.Flush();
        Check("a flush stops at the end of the opponent's turn instead of swallowing it",
              swept is { Count: 4 } && swept[0].Pieces.Single(p => p.Owner == 1).Health == 2);
        Check("the opponent's own Overcharge cost is not left inside our slice to be blamed on us",
              swept is not null && MoveDetector.DetectSequence(swept, 0) is { Ok: true, Moves.Count: 1 } sw &&
              sw.Moves[0] is { SrcX: 3, SrcY: 4, DstX: 4, DstY: 3, Attack: true, TargetX: 5, TargetY: 3 });

        var opening = new TurnTracker(0);
        opening.Push(Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 3, 1, 2, 1, acts: 1, bursts: 1, burst: true),
                           Pc(2, 4, 4, 4, 0, 0), Pc(3, 3, 2, 3, 2, 1, acts: 1, bursts: 1, burst: true)));
        opening.Push(Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 3, 1, 2, 1),
                           Pc(2, 4, 4, 4, 0, 0), Pc(3, 3, 2, 3, 2, 1)));
        opening.Push(Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 3, 0, 2, 1),
                           Pc(2, 4, 4, 3, 0, 0), Pc(3, 3, 2, 3, 2, 1)));
        opening.Push(Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 4, 3, 0, 0), Pc(2, 3, 2, 3, 2, 1)));
        opening.Push(Frame(Pc(0, 3, 3, 2, 0, 0), Pc(1, 4, 2, 3, 3, 0, acts: 1), Pc(2, 3, 2, 3, 2, 1)));
        var openingSlice = opening.Flush();
        Check("the walk continues through our opening attack's kill to the opponent's wipe",
              openingSlice is { Count: 4 } && openingSlice[0].Pieces.Count == 4 &&
              openingSlice[0].Pieces.Single(p => p.Idx == 1).Health == 1);
        Check("the opening kill sits inside the slice and reads as an attack, not a move",
              openingSlice is not null && MoveDetector.DetectSequence(openingSlice, 0) is { Ok: true } op &&
              op.Moves[0] is { Attack: true, TargetX: 4, TargetY: 3 });

        var dash = new TurnTracker(0);
        dash.Push(Frame(Pc(0, 3, 4, 4, 0, 0), Pc(3, 3, 3, 4, 2, 1, acts: 1)));
        dash.Push(Frame(Pc(0, 3, 4, 4, 0, 0), Pc(3, 3, 3, 4, 2, 1)));
        dash.Push(Frame(Pc(0, 3, 2, 4, 0, 0), Pc(3, 3, 3, 4, 2, 1)));
        dash.Push(Frame(Pc(0, 3, 2, 4, 0, 0), Pc(3, 3, 3, 2, 2, 1)));
        dash.Push(Frame(Pc(0, 3, 2, 4, 0, 0), Pc(3, 3, 3, 2, 0, 1)));
        dash.Push(Frame(Pc(0, 3, 1, 4, 0, 0, acts: 1), Pc(3, 3, 3, 2, 0, 1)));
        var dashSlice = dash.Flush();
        Check("the walk continues through a Dash victim's spin to the opponent's wipe",
              dashSlice is { Count: 5 } && dashSlice[0].Pieces.Single(p => p.Idx == 0) is { X: 3, Y: 4 });

        Check("a dash on a board with no uuid still refuses, because nothing licenses the reading",
              dashSlice is not null && MoveDetector.DetectSequence(dashSlice, 0) is { Ok: false } dashRead &&
              dashRead.Refusal!.Contains("facing line"));

        var ramPush = new TurnTracker(0);
        ramPush.Push(Frame(Pc(0, 3, 4, 5, 0, 0), Pc(1, 4, 3, 5, 2, 1, acts: 1)));
        ramPush.Push(Frame(Pc(0, 3, 4, 5, 0, 0), Pc(1, 4, 3, 5, 2, 1)));
        ramPush.Push(Frame(Pc(0, 3, 3, 5, 1, 0), Pc(1, 4, 3, 5, 2, 1)));
        ramPush.Push(Frame(Pc(0, 3, 3, 5, 1, 0), Pc(1, 4, 3, 4, 2, 1)));
        ramPush.Push(Frame(Pc(0, 3, 3, 5, 1, 0), Pc(1, 5, 3, 4, 2, 1)));
        ramPush.Push(Frame(Pc(0, 4, 3, 5, 1, 0, acts: 1), Pc(1, 5, 3, 4, 2, 1)));
        var ramSlice = ramPush.Flush();
        Check("the walk continues through a Ram victim's push to the opponent's wipe",
              ramSlice is { Count: 5 } && ramSlice[0].Pieces.Single(p => p.Idx == 0) is { X: 3, Y: 4 });

        Check("a Ram attack after a step reads as an ATTACK, not as a plain move onto its victim",
              ramSlice is not null && MoveDetector.DetectSequence(ramSlice, 0) is { Ok: true } ramRead &&
              ramRead.Moves.Any(m => m.Attack && m.SrcX == 3 && m.SrcY == 4 &&
                                     m.DstX == 3 && m.DstY == 3 && m.TargetX == 4 && m.TargetY == 3));

        var pullCost = new TurnTracker(0);
        pullCost.Push(Frame(Pc(4, 2, 5, 5, 0, 0, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(8, 2, 4, 2, 2, 1, acts: 1, skill: 15, range: 1), Pc(5, 4, 4, 1, 3, 1, acts: 1)));
        pullCost.Push(Frame(Pc(4, 2, 5, 5, 0, 0, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(8, 2, 4, 2, 2, 1, skill: 15, range: 1), Pc(5, 4, 4, 1, 3, 1)));
        pullCost.Push(Frame(Pc(4, 2, 5, 4, 0, 0, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(8, 2, 4, 2, 2, 1, skill: 15, range: 1), Pc(5, 4, 4, 1, 3, 1)));
        pullCost.Push(Frame(Pc(4, 2, 5, 4, 0, 0, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(8, 2, 4, 0, 2, 1, skill: 15, range: 1), Pc(5, 4, 4, 1, 3, 1)));
        pullCost.Push(Frame(Pc(4, 2, 5, 3, 0, 0, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(8, 2, 4, 0, 2, 1, skill: 15, range: 1), Pc(5, 4, 4, 1, 3, 1)));
        pullCost.Push(Frame(Pc(4, 2, 5, 3, 0, 0, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(5, 4, 4, 1, 3, 1)));
        pullCost.Push(Frame(Pc(4, 4, 5, 3, 0, 0, acts: 1, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(5, 4, 4, 1, 3, 1)));
        pullCost.Push(Frame(Pc(4, 4, 5, 3, 0, 0, acts: 1, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(5, 4, 4, 0, 3, 1)));
        pullCost.Push(Frame(Pc(4, 4, 5, 2, 0, 0, acts: 1, range: 3), Pc(2, 5, 6, 5, 1, 0, range: 2),
                            Pc(5, 4, 4, 0, 3, 1)));
        pullCost.Push(Frame(Pc(4, 4, 5, 2, 0, 0, acts: 1, bursts: 1, burst: true, range: 3),
                            Pc(2, 5, 6, 5, 1, 0, range: 2)));
        pullCost.Push(Frame(Pc(2, 5, 6, 5, 1, 0, range: 2)));
        pullCost.Push(Frame(Pc(2, 5, 3, 5, 1, 0, acts: 1, range: 2)));
        var pullSlice = pullCost.Push(Frame(Pc(2, 5, 3, 5, 1, 0, range: 2)));
        Check("the walk continues through a Pull attacker's own cost, which lands one sample after its victim's",
              pullSlice is not null && pullSlice[0].Pieces.Any(p => p.Idx == 8 && p.Health == 2));
        Check("the excuse reaches back one step only: a Spray tick on ours with no enemy loss before it still stops the walk",
              pullSlice is not null && pullSlice[0].Pieces.Single(p => p.Idx == 4) is { Health: 4, X: 2, Y: 5 });
        Check("an opening attack made in place by a Pull machine reads as the attack, its owed move, and the Overcharge strike",
              pullSlice is not null && MoveDetector.DetectSequence(pullSlice, 0) is { Ok: true } pullRead &&
              pullRead.Moves.Count == 4 &&
              pullRead.Moves[0] is { Attack: true, SrcX: 2, SrcY: 5, DstX: 2, DstY: 5, TargetX: 2, TargetY: 4, Burst: false } &&
              pullRead.Moves[1] is { Attack: false, SrcX: 2, SrcY: 5, DstX: 4, DstY: 5 } &&
              pullRead.Moves[2] is { Attack: true, DstX: 4, DstY: 5, TargetX: 4, TargetY: 4, Burst: true } &&
              pullRead.Moves[3] is { Attack: false, SrcX: 5, SrcY: 6, DstX: 5, DstY: 3 });

        static BoardSnapshot SplashFrame(int stormX, int stormY, int stormActs, int jawHp, int tusk25Hp,
                                         int tusk34Hp, int behemothHp, bool theirMarks)
        {
            var behemoth = theirMarks
                ? Pc(9, 2, 4, behemothHp, 2, 1, acts: 1, bursts: 1, burst: true, range: 2)
                : Pc(9, 2, 4, behemothHp, 2, 1, range: 2);
            return Frame(Pc(4, stormX, stormY, 7, 0, 0, acts: stormActs, range: 3, uuid: "7875B4B22E79B8BF0996D4B74BCC0477"),
                         Pc(2, 1, 5, jawHp, 0, 0, range: 2), Pc(8, 2, 5, tusk25Hp, 0, 0, range: 2),
                         Pc(6, 3, 4, tusk34Hp, 0, 0, range: 2), behemoth);
        }

        var splash = new TurnTracker(0);
        splash.Push(SplashFrame(2, 6, 0, 10, 8, 10, 7, theirMarks: true));
        splash.Push(SplashFrame(2, 6, 0, 10, 8, 10, 7, theirMarks: false));
        splash.Push(SplashFrame(2, 6, 0, 10, 8, 10, 5, theirMarks: false));
        splash.Push(SplashFrame(2, 6, 0, 7, 8, 10, 5, theirMarks: false));
        splash.Push(SplashFrame(2, 6, 0, 7, 6, 10, 5, theirMarks: false));
        splash.Push(SplashFrame(2, 6, 0, 7, 6, 4, 5, theirMarks: false));
        splash.Push(SplashFrame(4, 7, 0, 7, 6, 4, 5, theirMarks: false));
        splash.Push(SplashFrame(3, 5, 0, 7, 6, 4, 5, theirMarks: false));
        splash.Push(SplashFrame(3, 5, 1, 7, 6, 4, 5, theirMarks: false));
        var splashSlice = splash.Push(SplashFrame(3, 5, 0, 7, 6, 4, 5, theirMarks: false));
        Check("the walk continues through a run of our own splash losses to the enemy's hit behind them",
              splashSlice is not null && splashSlice[0].Pieces.Any(p => p.Idx == 9 && p.Health == 7));
        Check("an opening Dive made in place, splashing three of ours, reads as the attack and its owed move",
              splashSlice is not null && MoveDetector.DetectSequence(splashSlice, 0) is { Ok: true } splashRead &&
              splashRead.Moves.Count == 2 &&
              splashRead.Moves[0] is { Attack: true, SrcX: 2, SrcY: 6, DstX: 2, DstY: 6, TargetX: 2, TargetY: 4, Burst: false } &&
              splashRead.Moves[1] is { Attack: false, SrcX: 2, SrcY: 6, DstX: 3, DstY: 5 });

        var noHit = new TurnTracker(0);
        noHit.Push(SplashFrame(2, 6, 0, 10, 8, 10, 7, theirMarks: true));
        noHit.Push(SplashFrame(2, 6, 0, 10, 8, 10, 7, theirMarks: false));
        noHit.Push(SplashFrame(2, 6, 0, 7, 8, 10, 7, theirMarks: false));
        noHit.Push(SplashFrame(2, 6, 0, 7, 6, 10, 7, theirMarks: false));
        noHit.Push(SplashFrame(2, 6, 0, 7, 6, 4, 7, theirMarks: false));
        noHit.Push(SplashFrame(4, 7, 0, 7, 6, 4, 7, theirMarks: false));
        noHit.Push(SplashFrame(3, 5, 1, 7, 6, 4, 7, theirMarks: false));
        var noHitSlice = noHit.Push(SplashFrame(3, 5, 0, 7, 6, 4, 7, theirMarks: false));
        Check("a run of own losses with no enemy loss behind it still stops the walk: the opponent's blows stay theirs",
              noHitSlice is not null && noHitSlice[0].Pieces.Single(p => p.Idx == 6).Health == 4);

        static BoardSnapshot ChargeFrame(int tuskX, int tuskY, int tuskActs, int clawHp, byte clawFacing,
                                         bool clawGone, int stormHp, int markerX, bool theirMarks)
        {
            var pieces = new List<Piece>
            {
                Pc(6, tuskX, tuskY, 2, 0, 0, acts: tuskActs, range: 2, uuid: "23C2AA3CCB2680F418CA1D7E9F179E9D"),
                Pc(4, 3, 2, stormHp, 0, 0, range: 3, uuid: "7875B4B22E79B8BF0996D4B74BCC0477"),
                theirMarks ? Pc(9, markerX, 1, 12, 2, 1, acts: 1, bursts: 1, burst: true, range: 2)
                           : Pc(9, markerX, 1, 12, 2, 1, range: 2),
            };
            if (!clawGone)
            {
                pieces.Add(Pc(3, 4, 2, clawHp, clawFacing, 1, range: 2));
            }

            return new BoardSnapshot(8, 8, pieces, new sbyte[64]) { AiSeat = 1 };
        }

        var chargeKill = new TurnTracker(0);
        chargeKill.Push(ChargeFrame(4, 3, 0, 1, 2, false, 7, 0, theirMarks: true));
        chargeKill.Push(ChargeFrame(4, 3, 0, 1, 2, false, 7, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(4, 1, 0, 1, 2, false, 7, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(4, 1, 0, 0, 2, false, 7, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(4, 1, 0, 0, 0, false, 7, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(4, 1, 0, 0, 0, false, 4, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(4, 1, 0, 0, 0, true, 4, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(5, 2, 0, 0, 0, true, 4, 0, theirMarks: false));
        chargeKill.Push(ChargeFrame(5, 2, 1, 0, 0, true, 4, 0, theirMarks: false)
                            with { Act = new ActRecord(6, 4, 1, 5, 2, false) });
        var chargeSlice = chargeKill.Push(ChargeFrame(5, 2, 0, 0, 0, true, 4, 0, theirMarks: false));
        Check("D-199: the splash run crosses the dying victim's spin to the kill behind it",
              chargeSlice is not null && chargeSlice[0].Pieces.Any(p => p.Idx == 6 && p.X == 4 && p.Y == 3));
        Check("D-199: a Dash that kills and splashes our own machine reads as the charge and its owed move from the landing",
              chargeSlice is not null && MoveDetector.DetectSequence(chargeSlice, 0) is { Ok: true } chargeRead &&
              chargeRead.Moves is [{ Attack: true, SrcX: 4, SrcY: 3, DstX: 4, DstY: 3, TargetX: 4, TargetY: 2 },
                                   { Attack: false, SrcX: 4, SrcY: 1, DstX: 5, DstY: 2 }]);

        var strayStep = new TurnTracker(0);
        strayStep.Push(ChargeFrame(4, 3, 0, 1, 2, false, 7, 0, theirMarks: true));
        strayStep.Push(ChargeFrame(4, 3, 0, 1, 2, false, 7, 0, theirMarks: false));
        strayStep.Push(ChargeFrame(4, 1, 0, 1, 2, false, 7, 0, theirMarks: false));
        strayStep.Push(ChargeFrame(4, 1, 0, 0, 2, false, 7, 0, theirMarks: false));
        strayStep.Push(ChargeFrame(4, 1, 0, 0, 2, false, 7, 1, theirMarks: false));
        strayStep.Push(ChargeFrame(4, 1, 0, 0, 2, false, 4, 1, theirMarks: false));
        strayStep.Push(ChargeFrame(4, 1, 0, 0, 2, true, 4, 1, theirMarks: false));
        strayStep.Push(ChargeFrame(5, 2, 1, 0, 2, true, 4, 1, theirMarks: false)
                           with { Act = new ActRecord(6, 4, 1, 5, 2, false) });
        var straySlice = strayStep.Push(ChargeFrame(5, 2, 0, 0, 2, true, 4, 1, theirMarks: false));
        Check("D-199: a step the walk refuses still breaks the run, so the slice opens after it as before",
              straySlice is not null && !straySlice[0].Pieces.Any(p => p.Idx == 6 && p.X == 4 && p.Y == 3));

        var tornA = Pc(1, 4, 5, 4, 0, 0, range: 1, uuid: "AAAA");
        var tornB = Pc(2, 6, 6, 4, 0, 0, uuid: "BBBB");
        var tornE = Pc(0, 4, 3, 2, 2, 1, uuid: "EEEE");
        var beforeKill = Frame(tornE with { Health = 0 }, tornA with { X = 4, Y = 4 }, tornB);
        var tornShape = Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 }, tornB with { Idx = 2 });
        Check("the torn shape: same count, the last two entries one unit, a dead piece gone",
              tornShape.IsTornAfter(beforeKill));
        Check("a twin at one square with the count grown is a spawn, not a tear",
              !Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 }, tornB with { Idx = 2 })
                   .IsTornAfter(Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 })));
        Check("a twin at one square with nothing dead before it is a preview, not a tear",
              !tornShape.IsTornAfter(Frame(tornE, tornA with { X = 4, Y = 4 }, tornB)));

        var tornTracker = new TurnTracker(0);
        tornTracker.Push(Frame(tornE with { Acts = 1, Acted = true }, tornA, tornB));
        tornTracker.Push(Frame(tornE, tornA, tornB));
        tornTracker.Push(Frame(tornE, tornA with { X = 4, Y = 4 }, tornB));
        tornTracker.Push(Frame(tornE with { Health = 0 }, tornA with { X = 4, Y = 4 }, tornB));
        var beforeTorn = tornTracker.Buffered;
        tornTracker.Push(Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 },
                               tornB with { Idx = 2 }));
        Check("the tracker never buffers a torn sample",
              tornTracker.Buffered == beforeTorn);
        tornTracker.Push(Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 }));
        tornTracker.Push(Frame(tornA with { Idx = 0, X = 4, Y = 4, Acts = 1, Acted = true },
                               tornB with { Idx = 1 }));
        var tornSlice = tornTracker.Push(Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 }));
        Check("a kill whose compaction was sampled torn still reads as the attack it was",
              tornSlice is not null && MoveDetector.DetectSequence(tornSlice, 0) is { Ok: true } tornRead &&
              tornRead.Moves.Any(m => m.Attack && m.TargetX == 4 && m.TargetY == 3));

        var tornDirect = new List<BoardSnapshot>
        {
            Frame(tornE, tornA, tornB),
            Frame(tornE, tornA with { X = 4, Y = 4 }, tornB),
            Frame(tornE with { Health = 0 }, tornA with { X = 4, Y = 4 }, tornB),
            Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 }, tornB with { Idx = 2 }),
            Frame(tornA with { Idx = 0, X = 4, Y = 4 }, tornB with { Idx = 1 }),
            Frame(tornA with { Idx = 0, X = 4, Y = 4, Acts = 1, Acted = true }, tornB with { Idx = 1 }),
        };
        Check("the detector drops a torn sample from a slice handed to it directly",
              MoveDetector.DetectSequence(tornDirect, 0) is { Ok: true } tornDirectRead &&
              tornDirectRead.Moves.Any(m => m.Attack && m.TargetX == 4 && m.TargetY == 3));

        var owedMove = new List<BoardSnapshot>
        {
            Frame(Pc(0, 6, 1, 7, 3, 0), Pc(1, 5, 1, 4, 2, 1)),
            Frame(Pc(0, 6, 1, 7, 3, 0), Pc(1, 5, 1, 2, 2, 1)),
            Frame(Pc(0, 5, 1, 7, 3, 0), Pc(1, 4, 1, 2, 2, 1)),
            Frame(Pc(0, 5, 3, 7, 3, 0, acts: 1), Pc(1, 4, 1, 2, 2, 1))
                with { Act = new ActRecord(0, 5, 1, 5, 3, false) },
        };

        Check("an attack's owed move starts where the controller says, not at the strike square",
              MoveDetector.DetectSequence(owedMove, 0) is { Ok: true } owedRead &&
              owedRead.Moves.Any(m => !m.Attack && m.SrcX == 5 && m.SrcY == 1 &&
                                      m.DstX == 5 && m.DstY == 3));

        var owedNoAct = owedMove.ToList();
        owedNoAct[^1] = owedNoAct[^1] with { Act = null };

        Check("with no controller record the owed move falls back to the strike square",
              MoveDetector.DetectSequence(owedNoAct, 0) is { Ok: true } owedOld &&
              owedOld.Moves.Any(m => !m.Attack && m.SrcX == 6 && m.SrcY == 1));

        var sprayTurn = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 2, 7, 0, 0, skill: 15, range: 2), Pc(1, 5, 1, 5, 2, 1)),
            Frame(Pc(0, 5, 2, 7, 0, 0, skill: 15, range: 2), Pc(1, 5, 1, 4, 2, 1)),
            Frame(Pc(0, 4, 2, 7, 0, 0, acts: 1, skill: 15, range: 2), Pc(1, 5, 1, 4, 2, 1)),
        };

        Check("Spray's turn-start damage is subtracted, not read as an attack",
              MoveDetector.DetectSequence(sprayTurn, 0) is { Ok: true } sprayRead &&
              sprayRead.Moves.Count == 1 && !sprayRead.Moves[0].Attack);

        var sprayPlusHit = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 2, 7, 0, 0, skill: 15, range: 2), Pc(1, 5, 1, 5, 2, 1)),
            Frame(Pc(0, 5, 2, 7, 0, 0, skill: 15, range: 2), Pc(1, 5, 1, 2, 2, 1)),
            Frame(Pc(0, 5, 2, 7, 0, 0, acts: 1, skill: 15, range: 2), Pc(1, 5, 1, 2, 2, 1)),
        };

        Check("damage beyond Spray's share is still read as an attack",
              MoveDetector.DetectSequence(sprayPlusHit, 0) is { Ok: true } sprayHit &&
              sprayHit.Moves.Any(m => m.Attack && m.TargetX == 5 && m.TargetY == 1));

        var sprayNoFields = new List<BoardSnapshot>
        {
            Frame(Pc(0, 5, 2, 7, 0, 0), Pc(1, 5, 1, 5, 2, 1)),
            Frame(Pc(0, 5, 2, 7, 0, 0), Pc(1, 5, 1, 4, 2, 1)),
            Frame(Pc(0, 4, 2, 7, 0, 0, acts: 1), Pc(1, 5, 1, 4, 2, 1)),
        };

        Check("a snapshot with no skill field predicts no Spray and reads as it always did",
              MoveDetector.DetectSequence(sprayNoFields, 0) is { Ok: true } sprayOld &&
              sprayOld.Moves.Any(m => m.Attack));

        var ramCounter = new TurnTracker(0);
        ramCounter.Push(Frame(Pc(0, 3, 4, 5, 0, 0), Pc(1, 4, 3, 5, 2, 1, acts: 1)));
        ramCounter.Push(Frame(Pc(0, 3, 4, 5, 0, 0), Pc(1, 4, 3, 5, 2, 1)));
        ramCounter.Push(Frame(Pc(0, 3, 3, 5, 1, 0), Pc(1, 4, 3, 5, 2, 1)));
        ramCounter.Push(Frame(Pc(0, 3, 3, 5, 1, 0), Pc(1, 4, 3, 4, 2, 1)));
        ramCounter.Push(Frame(Pc(0, 3, 3, 5, 1, 0), Pc(1, 5, 3, 4, 2, 1, acts: 1)));
        ramCounter.Push(Frame(Pc(0, 4, 3, 5, 1, 0, acts: 1), Pc(1, 5, 3, 4, 2, 1, acts: 1)));
        var ramCounterSlice = ramCounter.Flush();
        Check("an enemy that moves AND ticks still ends the walk, even one sample after losing health",
              ramCounterSlice is { Count: 2 });

        var facing = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 6, 4, 0, 0), Pc(1, 3, 6, 5, 0, 0), Pc(2, 4, 1, 4, 2, 1)),
            Frame(Pc(0, 7, 5, 4, 0, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0), Pc(2, 4, 1, 4, 2, 1)),
            Frame(Pc(0, 7, 5, 4, 1, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0), Pc(2, 4, 1, 4, 2, 1)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0), Pc(2, 4, 1, 4, 2, 1)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0), Pc(2, 4, 1, 4, 2, 1)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0, acts: 1), Pc(2, 4, 1, 4, 2, 1)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 5, 5, 5, 2, 0, acts: 1), Pc(2, 4, 1, 4, 2, 1)),
        };

        Check("a move carries the facing the machine SETTLES on, not the one it ticked with",
              MoveDetector.DetectSequence(facing, 0) is { Ok: true, Moves.Count: 2 } fc &&
              fc.Moves[0] is { SrcX: 4, SrcY: 6, DstX: 7, DstY: 5, Facing: 3 } &&
              fc.Moves[1] is { SrcX: 3, SrcY: 6, DstX: 5, DstY: 5, Facing: 2 });

        var leak = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 6, 4, 0, 0), Pc(1, 3, 6, 5, 0, 0)),
            Frame(Pc(0, 7, 5, 4, 0, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0, acts: 1)),
            Frame(Pc(0, 7, 5, 4, 1, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0, acts: 1)),
            Frame(Pc(0, 7, 4, 4, 1, 0, acts: 1, bursts: 1, burst: true), Pc(1, 5, 5, 5, 0, 0, acts: 1)),
        };

        Check("the facing window stops at the next action instead of reading a later preview",
              MoveDetector.DetectSequence(leak, 0) is { Ok: true, Moves.Count: 3 } lk &&
              lk.Moves[0] is { SrcX: 4, SrcY: 6, DstX: 7, DstY: 5, Facing: 3 } &&
              lk.Moves[1] is { SrcX: 3, SrcY: 6, DstX: 5, DstY: 5, Facing: 0 } &&
              lk.Moves[2] is { SrcX: 7, SrcY: 5, DstX: 7, DstY: 4, Facing: 1, Burst: true });

        var comeback = new List<BoardSnapshot>
        {
            Frame(Pc(0, 4, 6, 4, 0, 0), Pc(1, 3, 6, 5, 0, 0)),
            Frame(Pc(0, 7, 5, 4, 0, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 3, 6, 5, 0, 0)),
            Frame(Pc(0, 7, 5, 4, 3, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0, acts: 1)),
            Frame(Pc(0, 7, 5, 4, 1, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0, acts: 1)),
            Frame(Pc(0, 7, 5, 4, 2, 0, acts: 1), Pc(1, 5, 5, 5, 0, 0, acts: 1)),
        };

        Check("a machine turned AFTER another one acts still carries its settled facing",
              MoveDetector.DetectSequence(comeback, 0) is { Ok: true, Moves.Count: 2 } cb &&
              cb.Moves[0] is { SrcX: 4, SrcY: 6, DstX: 7, DstY: 5, Facing: 2 } &&
              cb.Moves[1] is { SrcX: 3, SrcY: 6, DstX: 5, DstY: 5, Facing: 0 });

        Check("a lobby that has heard nothing owes no answer",
              new Lobby("X", isHost: true).TakeAnswer() is null);

        var answerer = new Lobby("X", isHost: true);
        answerer.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "X" });
        Check("a lobby answers the first Ident it receives",
              answerer.TakeAnswer() is { Kind: MsgKind.Ident, Build: "X" });
        Check("a lobby answers only once, so two peers cannot answer each other forever",
              answerer.TakeAnswer() is null);

        var forgetful = new Lobby("X", isHost: true);
        forgetful.Choose(builtin.Challenge!);
        forgetful.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "X" });
        forgetful.TakeAnswer();
        forgetful.OnSetup(new Frame { Kind = MsgKind.Setup, Army = [.. builtin.Army.Select(m => m.Uuid)] });
        forgetful.OnReady(new Frame { Kind = MsgKind.Ready });
        forgetful.SetArmy(builtin.Army.Select(m => m.Uuid).ToArray());
        forgetful.Ready();
        var wasReady = forgetful.BothReady && forgetful.Remote is not null && forgetful.PeerBuild == "X";
        forgetful.ForgetPeer();
        Check("forgetting the peer clears its build, its setup and its ready, and keeps our own",
              wasReady && forgetful.PeerBuild is null && forgetful.Remote is null && !forgetful.RemoteReady
              && !forgetful.BothReady && forgetful.LocalReady && forgetful.Local.Army.Count > 0);
        forgetful.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "X" });
        Check("the next peer on the seat is answered again",
              forgetful.TakeAnswer() is { Kind: MsgKind.Ident } && forgetful.TakeAnswer() is null);

        var refusedOnce = new Lobby("BUILD-A", isHost: true);
        refusedOnce.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "BUILD-B" });
        refusedOnce.ForgetPeer();
        refusedOnce.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "BUILD-A" });
        Check("forgetting the peer never lifts a refusal",
              refusedOnce.Refusal is not null && !refusedOnce.BothReady);

        var hostFirst = await Handshake(port, cts.Token, hostPairsFirst: true);
        Check("both peers learn the other's build when the HOST pairs first",
              hostFirst is { Host: "SAME-BUILD", Guest: "SAME-BUILD" });

        var guestFirst = await Handshake(port, cts.Token, hostPairsFirst: false);
        Check("both peers learn the other's build when the GUEST pairs first",
              guestFirst is { Host: "SAME-BUILD", Guest: "SAME-BUILD" });

        var firedOnNoParent = false;
        ParentWatch.Start(0, () => firedOnNoParent = true);
        Check("parent-watch: pid 0 (no parent given) never triggers shutdown", !firedOnNoParent);

        var firedOnDeadParent = false;
        ParentWatch.Start(int.MaxValue, () => firedOnDeadParent = true);
        Check("parent-watch: a parent that is already gone triggers shutdown at once", firedOnDeadParent);

        var firedOnLiveParent = false;
        ParentWatch.Start(Environment.ProcessId, () => firedOnLiveParent = true);
        await Task.Delay(150, cts.Token);
        Check("parent-watch: a still-running parent does not trigger shutdown", !firedOnLiveParent);

        var everyField = new Frame
        {
            Kind = MsgKind.Setup, Room = "ROOM", Seat = 0, ResumeFrom = 0, Build = "BUILD",
            Challenge = "8BBC182B83FC495AA2150021217530D5", Army = ["8BBC182B83FC495AA2150021217530D5"],
            Placements = [new Placement { X = 1, Y = 1, Dir = 0 }],
            Board = [.. new int[64]], VictoryPoints = 2, DraftPoints = 40,
            Place = new Placement { X = 1, Y = 1, Dir = 0 }, PlaceIdx = 0,
            Moves = [new Move { SrcX = 1, SrcY = 1, DstX = 1, DstY = 2 }], Final = true,
            Turn = 1, PieceHash = "aa", TerrainHash = "bb", Reason = "why", Role = SessionRole.Play,
        };
        Check("a Frame with every field set encodes and round-trips, so no two fields share a JSON name",
              Protocol.Decode(Protocol.Encode(everyField)) is
              { Kind: MsgKind.Setup, Board.Count: 64, TerrainHash: "bb", VictoryPoints: 2 });

        var hostsBoard = new List<int>(new int[64]);
        hostsBoard[9] = 3;
        hostsBoard[54] = 3;

        var guestWithOwnPreset = new Lobby("BUILD", isHost: false);
        guestWithOwnPreset.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF"], Placements = [],
            Board = hostsBoard, VictoryPoints = 3, DraftPoints = 44,
        });

        var rivalPreset = Presets.Builtin();
        rivalPreset.Board = [.. new int[64]];
        rivalPreset.Board[0] = 1;
        rivalPreset.VictoryPoints = 9;
        rivalPreset.DraftPoints = 99;

        var guestSteps = WriteSteps(guestWithOwnPreset, guestWithOwnPreset.Remote!, interactivePlacement: true, preset: rivalPreset);
        var guestBoardStep = guestSteps.FirstOrDefault(s => s.Args[0] == "--set-board");
        var guestRuleStep = guestSteps.FirstOrDefault(s => s.Args[0] == "--set-rules");
        Check("a guest writes the HOST's board, not the one in its own presets.json",
              guestBoardStep.Args is not null && guestBoardStep.Args[10] == "3" && guestBoardStep.Args[1] == "0");
        Check("a guest writes the HOST's rule numbers, not its own",
              guestRuleStep.Args is not null &&
              guestRuleStep.Args.Contains("3") && guestRuleStep.Args.Contains("44") &&
              !guestRuleStep.Args.Contains("9") && !guestRuleStep.Args.Contains("99"));

        var oneSided = new List<int>(new int[64]);
        oneSided[9] = 3;

        var oneSidedGuest = new Lobby("BUILD", isHost: false);
        oneSidedGuest.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144"],
            Placements = [], Board = oneSided,
            VictoryPoints = Preset.RuleNotSet, DraftPoints = Preset.RuleNotSet,
        });

        Check("an asymmetric board from the peer is accepted rather than refused",
              oneSidedGuest.Refusal is null && oneSidedGuest.Board is { Count: 64 });

        var oneSidedStep = WriteSteps(oneSidedGuest, oneSidedGuest.Remote!, interactivePlacement: true, preset: rivalPreset)
            .FirstOrDefault(s => s.Args[0] == "--set-board");
        Check("a guest writes the host's asymmetric board ROTATED into its own frame",
              oneSidedStep.Args is not null && oneSidedStep.Args[55] == "3" && oneSidedStep.Args[10] == "0");

        string[] twoLegal = ["1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144"];
        var oneSidedHost = new Lobby("BUILD", isHost: true) { Seat = 0 };
        oneSidedHost.Choose("8BBC182B83FC495AA2150021217530D5");
        oneSidedHost.SetArmy(twoLegal);
        oneSidedHost.ChooseBoard(oneSided, Preset.RuleNotSet, Preset.RuleNotSet, 8, 8, Preset.RuleNotSet);
        oneSidedHost.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Army = [.. twoLegal], Placements = [],
            VictoryPoints = Preset.RuleNotSet, DraftPoints = Preset.RuleNotSet,
        });
        var oneSidedHostStep = WriteSteps(oneSidedHost, oneSidedHost.Remote!, interactivePlacement: true, preset: rivalPreset)
            .FirstOrDefault(s => s.Args[0] == "--set-board");
        Check("the HOST writes the same board unrotated, so the two grids are opposites",
              oneSidedHostStep.Args is not null &&
              oneSidedHostStep.Args[10] == "3" && oneSidedHostStep.Args[55] == "0");

        var goodBoard = new List<int>(new int[64]);
        var boardHost = new Lobby("BUILD", isHost: true);
        boardHost.ChooseBoard(goodBoard, 2, 40, 8, 8, Preset.RuleNotSet);
        var sentSetup = boardHost.Setup();
        Check("the host's Setup carries the board and the rule numbers",
              sentSetup.Board is { Count: 64 } && sentSetup.VictoryPoints == 2 && sentSetup.DraftPoints == 40);

        var guestGiven = new Lobby("BUILD", isHost: false);
        guestGiven.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = [], Placements = [], Board = goodBoard, VictoryPoints = 2, DraftPoints = 40,
        });
        Check("a guest takes the host's board rather than its own presets.json",
              guestGiven.Board is { Count: 64 } && guestGiven.VictoryPoints == 2 &&
              guestGiven.DraftPoints == 40 && guestGiven.Refusal is null);

        var guestNoBoard = new Lobby("BUILD", isHost: false);
        guestNoBoard.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5", Army = [], Placements = [],
        });
        Check("a Setup with no board is the stock board, not a refusal",
              guestNoBoard.Board is null && guestNoBoard.Refusal is null);

        static Lobby GuestToldBoard(List<int>? terrain, int vp = 2, int dp = 40)
        {
            var g = new Lobby("BUILD", isHost: false);
            g.OnSetup(new Frame
            {
                Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5",
                Army = [], Placements = [], Board = terrain, VictoryPoints = vp, DraftPoints = dp,
            });
            return g;
        }

        Check("a board that is not 64 squares is refused before it reaches a write",
              GuestToldBoard([.. new int[63]]).Refusal is not null);

        static Lobby GuestToldSetup(List<string> army, int placementRows)
        {
            var g = new Lobby("BUILD", isHost: false);
            g.OnSetup(new Frame
            {
                Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5",
                Army = army, Placements = [], Board = null, VictoryPoints = 2, DraftPoints = 40,
                PlacementRows = placementRows,
            });
            return g;
        }

        var injected = GuestToldSetup(["aaaa\nboth players are in the match"], Preset.RuleNotSet);
        Check("a peer's machine id cannot carry a newline into the refusal that quotes it",
              injected.Refusal is { } dirty && !dirty.Contains('\n') && !dirty.Contains('\r'));

        Check("an absurdly long machine id is cut rather than quoted whole",
              GuestToldSetup([new string('x', 4000)], Preset.RuleNotSet).Refusal is { Length: < 200 });

        Check("a placing depth sent without a board is taken, not dropped",
              GuestToldSetup([], 3) is { Refusal: null } depthKept && depthKept.PlacementRows == 3);

        Check("a placing depth sent without a board is still bounded",
              GuestToldSetup([], 9).Refusal is not null);

        static Frame ShapedSetup(int width, int height, int placementRows)
        {
            return new Frame
            {
                Kind = MsgKind.Setup, Challenge = "8BBC182B83FC495AA2150021217530D5",
                Army = [], Placements = [], VictoryPoints = 2, DraftPoints = 40,
                Board = width > 0 ? [.. new int[width * height]] : null,
                BoardWidth = width > 0 ? width : Preset.RuleNotSet,
                BoardHeight = width > 0 ? height : Preset.RuleNotSet,
                PlacementRows = placementRows,
            };
        }

        var twoFrames = new Lobby("BUILD", isHost: false);
        twoFrames.OnSetup(ShapedSetup(8, 3, 1));
        var twoFramesFirst = twoFrames.Refusal is null;
        twoFrames.OnSetup(ShapedSetup(0, 0, 8));
        var tooDeep = new Lobby("BUILD", isHost: false);
        tooDeep.OnSetup(ShapedSetup(8, 3, 1));
        tooDeep.OnSetup(ShapedSetup(0, 0, 9));
        Check("a depth sent after a board is judged against the board the lobby then holds",
              twoFramesFirst
              && (twoFrames.Refusal is not null
                  || Lobby.DepthOffBoard(twoFrames.PlacementRows, twoFrames.BoardHeight) is null)
              && tooDeep.Refusal is not null);

        var oneRow = new Lobby("BUILD", isHost: false);
        oneRow.OnSetup(ShapedSetup(8, 1, Preset.RuleNotSet));
        Check("a board too shallow for the challenge's own depth is refused when no depth is sent",
              oneRow.Refusal is not null);

        var depthFits = new Lobby("BUILD", isHost: false);
        depthFits.OnSetup(ShapedSetup(8, 3, 1));
        var stockFits = new Lobby("BUILD", isHost: false);
        stockFits.OnSetup(ShapedSetup(8, 2, Preset.RuleNotSet));
        Check("a depth that fits its board, and the challenge's own on a board two deep, still pass",
              depthFits.Refusal is null && stockFits.Refusal is null);

        var settled = new Lobby("BUILD", isHost: false);
        settled.OnSetup(ShapedSetup(8, 3, 1));
        settled.Ready();
        settled.OnReady(new Frame { Kind = MsgKind.Ready });
        settled.OnSetup(ShapedSetup(8, 3, 1));
        var resendPassed = settled.Refusal is null;
        settled.OnSetup(ShapedSetup(8, 3, 2));
        Check("a setup changed after both players were ready is refused, and the accepted one stands",
              resendPassed && settled.Refusal is not null && settled.PlacementRows == 1);

        var restarted = new Lobby("BUILD", isHost: false);
        restarted.OnSetup(ShapedSetup(8, 3, 1));
        restarted.Ready();
        restarted.OnReady(new Frame { Kind = MsgKind.Ready });
        restarted.ForgetPeer();
        restarted.OnSetup(ShapedSetup(8, 3, 2));
        Check("a player who started over may send a different setup",
              restarted.Refusal is null && restarted.PlacementRows == 2);

        var handedOver = GuestToldSetup([MachineA], Preset.RuleNotSet);
        var checkedRemote = handedOver.Remote!;
        handedOver.ForgetPeer();
        bool builtFromChecked;
        try
        {
            builtFromChecked = WriteSteps(handedOver, checkedRemote)
                .First(s => s.Args[0] == "--set-units").Args.Contains(MachineA);
        }
        catch (NullReferenceException)
        {
            builtFromChecked = false;
        }

        Check("the write is built from the setup the caller checked, even once the peer is forgotten",
              builtFromChecked);

        var toldHost = new Lobby("X", isHost: true) { Seat = 0 };
        toldHost.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        toldHost.ChooseBoard([.. new int[36]], 3, 30, 6, 6, 1);
        toldHost.SetName("HOSTA");
        toldHost.SetArmy([MachineA]);
        toldHost.Local.Placements = [new Placement { X = 2, Y = 5, Dir = 0 }];
        var toldGuest = new Lobby("X", isHost: false) { Seat = 1 };
        toldGuest.OnSetup(toldHost.Setup());
        var tookWhatWasTold = toldGuest.Refusal is null && toldGuest.Board is not null && toldGuest.BoardWidth == 6
                              && toldGuest.PlacementRows == 1 && toldGuest.Challenge is not null
                              && toldGuest.PeerName == "HOSTA";
        var frozenGuest = toldGuest.Frozen();
        toldGuest.ForgetPeer();
        toldGuest.OnSetup(new Frame
        {
            Kind = MsgKind.Setup, Challenge = "0ECEA5D9B9F841908D9716A1421F0AEC", Army = [MachineA], Placements = [],
        });
        toldHost.ForgetPeer();
        var unnamedGuest = new Lobby("X", isHost: false) { Seat = 1 };
        unnamedGuest.OnSetup(toldHost.Setup());
        unnamedGuest.OnSetup(new Frame { Kind = MsgKind.Setup, Army = [MachineA], Placements = [] });
        Check("a host Setup with no board, depth or name after one with them leaves the stock 8x8 board, no depth and no name",
              tookWhatWasTold && toldGuest.Refusal is null && toldGuest.Board is null
              && toldGuest.BoardWidth == Preset.BoardSide && toldGuest.BoardHeight == Preset.BoardSide
              && toldGuest.PlacementRows == Preset.RuleNotSet && toldGuest.PeerName is null
              && unnamedGuest.Challenge is null && unnamedGuest.Board is null
              && toldHost.Board is not null && toldHost.BoardWidth == 6 && toldHost.Challenge is not null
              && toldHost.LocalName == "HOSTA"
              && frozenGuest.Board is not null && frozenGuest.Challenge is not null && frozenGuest.PeerName == "HOSTA"
              && frozenGuest.Remote is { Army.Count: 1 });

        var awayHost = new Lobby("X", isHost: true) { Seat = 0 };
        awayHost.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        awayHost.ChooseBoard([.. new int[30]], 3, 30, 6, 5, 1);
        awayHost.SetArmy([MachineA]);
        awayHost.Local.Placements = [new Placement { X = 2, Y = 4, Dir = 0 }];
        var awayGuest = new Lobby("X", isHost: false) { Seat = 1 };
        awayGuest.OnSetup(awayHost.Setup());
        awayHost.Ready();
        awayGuest.OnReady(new Frame { Kind = MsgKind.Ready });
        awayGuest.ForgetPeer();
        awayGuest.SetArmy([MachineB]);
        awayGuest.Local.Placements = awayGuest.AutoSquares(1);
        var awaySent = TrySendReady(new Peer("127.0.0.1", 1, "AWAY01"), awayGuest);
        var awayArmed = awayGuest.LocalReady;
        awayGuest.ForgetPeer();
        awayHost.ForgetPeer();
        awayGuest.OnSetup(awayHost.Setup());
        awayGuest.OnReady(new Frame { Kind = MsgKind.Ready });
        awayHost.OnSetup(awayGuest.Setup());
        awayHost.OnReady(new Frame { Kind = MsgKind.Ready });
        Check("a joiner who readies while the host is away still reaches both ready once the host is back",
              awaySent is null && awayArmed && awayGuest.BothReady && awayHost.BothReady
              && awayGuest.Refusal is null && awayHost.Refusal is null);

        var oneRowHost = new Lobby("H", isHost: true) { Seat = 0 };
        oneRowHost.Choose("0ECEA5D9B9F841908D9716A1421F0AEC");
        oneRowHost.ChooseBoard(Enumerable.Repeat(0, 8).ToList(), Preset.RuleNotSet, Preset.RuleNotSet,
                               8, 1, Preset.RuleNotSet);
        Check("place auto stays on a board shallower than the challenge's own depth",
              oneRowHost.AutoSquares(3) is { Count: 3 } autoSquares && autoSquares.All(s => s.Y == 0));

        var outOfRange = new List<int>(new int[64]);
        outOfRange[0] = 9;
        Check("a terrain value outside -2..3 is refused",
              GuestToldBoard(outOfRange).Refusal is not null);

        var asymmetric = new List<int>(new int[64]);
        asymmetric[0] = 1;
        Check("an asymmetric board reaches the write, because the guest rotates it",
              GuestToldBoard(asymmetric) is { Refusal: null } asymOk && asymOk.Board is { Count: 64 });

        var chasmInPlacingRow = new List<int>(new int[64]);
        chasmInPlacingRow[0] = -2;
        chasmInPlacingRow[63] = -2;
        Check("a Chasm in a placing row reaches the write",
              GuestToldBoard(chasmInPlacingRow) is { Refusal: null } chasmOk
              && chasmOk.Board is { Count: 64 });

        Check("an out-of-range victory-point target is refused",
              GuestToldBoard(goodBoard, vp: 0).Refusal is not null &&
              GuestToldBoard(goodBoard, dp: 999).Refusal is not null);

        Check("the roster is the four supported challenges",
              Challenges.Known.Count == 4 &&
              Challenges.Known.All(c => Lobby.NormaliseUuid(c.Uuid) == c.Uuid));
        Check("Hard is 6 draft slots and Regular Challenge is a free draft",
              Challenges.Find("74771C9B66D9441AA4CBD5E47D4851AD")?.Slots == 6 &&
              Challenges.Find("0ECEA5D9B9F841908D9716A1421F0AEC")?.Slots == 0);
        Check("a supported challenge passes however it is punctuated",
              Challenges.Problem("8bbc182b-83fc-495a-a215-0021217530d5") is null);

        var hostMatch = LobbyMatchCommands(
        [
            "--lobby", "--challenge", "8BBC182B83FC495AA2150021217530D5",
            "--board", "0", "0", "-2", "3", "--victory-points", "20", "--draft-points", "40",
        ]);

        Check("the host's flags become the challenge, the board and the rules, in that order",
              hostMatch.Length == 3 &&
              hostMatch[0] == "challenge 8BBC182B83FC495AA2150021217530D5" &&
              hostMatch[1] == "board 0 0 -2 3" &&
              hostMatch[2] == "rules 20 40");

        Check("a negative terrain value is read as a value, not as the next flag",
              LobbyMatchCommands(["--board", "-2", "-1", "0", "--victory-points", "6"])[0] == "board -2 -1 0");

        Check("a guest's command line configures nothing at all",
              LobbyMatchCommands(["--lobby", "--join", "GATE01"]).Length == 0);

        Check("a host that changed no rules sends no rules command",
              LobbyMatchCommands(["--challenge", "8BBC182B83FC495AA2150021217530D5"]).Length == 1);

        Check("D-235: the host's --first becomes the lobby's first command, after the rules",
              LobbyMatchCommands(["--challenge", "8BBC182B83FC495AA2150021217530D5", "--victory-points", "7",
                                  "--draft-points", "10", "--first", "joiner"]) is
                  ["challenge 8BBC182B83FC495AA2150021217530D5", "rules 7 10", "first joiner"]);

        Check("D-235: host first forces the host's human and the joiner's AI seat",
              ForceModeFor(joining: false, Lobby.FirstHost) == "human"
              && ForceModeFor(joining: true, Lobby.FirstHost) == "ai");
        Check("D-235: joiner first forces the joiner's human and the host's AI seat",
              ForceModeFor(joining: true, Lobby.FirstJoiner) == "human"
              && ForceModeFor(joining: false, Lobby.FirstJoiner) == "ai");

        var firstHost = new Lobby("F", isHost: true) { Seat = 0 };
        firstHost.ChooseFirst(Lobby.FirstJoiner);
        var firstGuest = new Lobby("F", isHost: false) { Seat = 1 };
        firstGuest.OnSetup(firstHost.Setup());
        var badFirst = new Lobby("F", isHost: false) { Seat = 1 };
        badFirst.OnSetup(new Frame { Kind = MsgKind.Setup, Army = [], First = "both" });
        Check("D-235: the host's first player crosses on Setup, and the guest sends none of its own",
              firstHost.Setup().First == Lobby.FirstJoiner && firstGuest.First == Lobby.FirstJoiner
              && firstGuest.Refusal is null && firstGuest.Setup().First is null);
        Check("D-235: a first player that is neither word is refused, and silence is the host",
              badFirst.Refusal is not null && badFirst.First == Lobby.FirstHost
              && SummaryOf(new Frame { Kind = MsgKind.Setup, Army = [] }).EndsWith(", first host"));

        var armyGuest = new Lobby("A", isHost: false) { Seat = 1 };
        armyGuest.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1c96a39f-fe37-791f-8ae0-7bd49a2230ff", "B78EF94227B7D36454715138E9A17144"],
        });
        Check("D-236: the other player's army is printed from the lobby, normalised, and absent is no line",
              TheirArmyLine(armyGuest) == "  <- their army: 1C96A39FFE37791F8AE07BD49A2230FF B78EF94227B7D36454715138E9A17144"
              && TheirArmyLine(new Lobby("A", isHost: false)) is null);

        static string SummaryOf(Frame f)
        {
            var g = new Lobby("BUILD", isHost: false);
            g.OnSetup(f);
            return SetupSummary(f, g);
        }

        Check("the roster is this save's 43 machines, each with a real uuid",
              Machines.Known.Count == 43 &&
              Machines.Known.All(m => Lobby.NormaliseUuid(m.Uuid) == m.Uuid));

        var unplayable = Machines.Known.Where(m => Machines.CannotPlay(m) is not null).Select(m => m.Name).ToList();
        Check("all 43 machines can be fielded, the Dash and Sweep carriers included",
              unplayable.Count == 0);

        Check("the seven that used to be refused are each fieldable by uuid",
              new[] { "F9433C1448F8F052BD457978CD0BFEC5", "F5172B344148EE7E5DB47BD7A23AF9F9",
                      "7875B4B22E79B8BF0996D4B74BCC0477", "E1301D6553C24EDD4CFBE9E21EC99D1C",
                      "23C2AA3CCB2680F418CA1D7E9F179E9D", "FDC39FDFF4CE9C5B8190CE46AAFCF7B5",
                      "8218A7A4485CED3F9DBFF00447CA94A0" }
                  .All(u => Machines.Find(u) is { } m && Machines.CannotPlay(m) is null));

        Check("a machine with neither Dash nor Sweep is fieldable",
              Machines.CannotPlay(Machines.Find("1C96A39FFE37791F8AE07BD49A2230FF")!) is null);

        string[] twoMachines = ["1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144"];
        Check("an army that fills the challenge's slots inside the budget is accepted",
              Machines.ArmyProblem(twoMachines, slots: 2, budget: 10) is null);

        Check("an army of the wrong size is refused before it can be written",
              Machines.ArmyProblem(twoMachines, slots: 4, budget: 10) is not null &&
              Machines.ArmyProblem([twoMachines[0]], slots: 2, budget: 10) is not null);

        Check("a Thunderjaw is now accepted in an army, inside the budget",
              Machines.ArmyProblem(["F9433C1448F8F052BD457978CD0BFEC5"], slots: 1, budget: 10) is null);

        Check("an over-budget army is refused",
              Machines.ArmyProblem(["82F13F6222FCF687A5684CC1F9B96F4F"], slots: 1, budget: 4) is not null);

        Check("a machine this save does not have is refused",
              Machines.ArmyProblem(["00000000000000000000000000000000"], slots: 1, budget: 10) is not null);

        const string watcherId = "0E44B98882BA9AFD876C0DB6144D35F5";
        const string fireclawId = "D132E56AAEF7AC7F24DBC79886876512";
        const string chargerId = "FDC39FDFF4CE9C5B8190CE46AAFCF7B5";
        const string grazerId = "2B34B3566FC1ED50071517AE89C5252F";
        var reachBoard = new BoardSnapshot(8, 8,
                                           [
                                               new Piece(5, 2, 2, 2, 1, Uuid: watcherId),
                                               new Piece(4, 4, 8, 1, 0, Uuid: fireclawId),
                                               new Piece(5, 6, 10, 0, 1, Uuid: fireclawId),
                                               new Piece(1, 1, 4, 1, 1, Uuid: chargerId),
                                               new Piece(4, 0, 4, 2, 1, Uuid: grazerId),
                                               new Piece(0, 0, 4, 2, 1, Uuid: ""),
                                           ],
                                           new sbyte[64])
        {
            AiSeat = 1,
        };
        var watcherShot = new Move
        {
            SrcX = 5, SrcY = 2, DstX = 6, DstY = 4, Facing = 3, Attack = true, TargetX = 4, TargetY = 4,
        };
        Check("the 2026-09-05 crash: a move-2 machine walking three squares and striking is refused, by name",
              Machines.TurnProblem([watcherShot], reachBoard, 1) is { } overReach
              && overReach.Contains("Redeye Watcher") && overReach.Contains("3 squares")
              && overReach.Contains("move is 2"));

        var sprintThenRotate = new Move { SrcX = 5, SrcY = 2, DstX = 6, DstY = 4, Facing = 3 };
        var rotateStrike = new Move
        {
            SrcX = 6, SrcY = 4, DstX = 6, DstY = 4, Facing = 3, Attack = true, TargetX = 5, TargetY = 4,
        };
        Check("a sprint then the same machine's attack in place, which live-probe folds into one attack, is refused",
              Machines.TurnProblem([sprintThenRotate, rotateStrike], reachBoard, 1) is { } folded
              && folded.Contains("Redeye Watcher") && folded.Contains("3 squares") && folded.Contains("one attack"));

        Check("a walk within the move then an attack in place, or a sprint followed by that machine's own move, is accepted",
              Machines.TurnProblem([new Move { SrcX = 5, SrcY = 2, DstX = 5, DstY = 4, Facing = 3 },
                                    new Move
                                    {
                                        SrcX = 5, SrcY = 4, DstX = 5, DstY = 4, Facing = 3, Attack = true,
                                        TargetX = 4, TargetY = 4,
                                    }], reachBoard, 1) is null
              && Machines.TurnProblem([sprintThenRotate, rotateStrike,
                                       new Move { SrcX = 6, SrcY = 4, DstX = 6, DstY = 5, Facing = 2 }],
                                      reachBoard, 1) is null);

        Check("three squares with no strike is a sprint and is accepted; four is not",
              Machines.TurnProblem([new Move { SrcX = 5, SrcY = 2, DstX = 6, DstY = 4, Facing = 3 }],
                                   reachBoard, 1) is null
              && Machines.TurnProblem([new Move { SrcX = 5, SrcY = 2, DstX = 7, DstY = 4, Facing = 3 }],
                                      reachBoard, 1) is not null);

        Check("a move-3 machine walking three squares and striking is accepted",
              Machines.TurnProblem([new Move
                                    {
                                        SrcX = 5, SrcY = 6, DstX = 6, DstY = 4, Facing = 1, Attack = true,
                                        TargetX = 7, TargetY = 4,
                                    }],
                                   reachBoard, 1) is null);

        Check("a Dash's charge and a Ram's advance are not counted as the walk",
              Machines.TurnProblem([new Move
                                    {
                                        SrcX = 1, SrcY = 1, DstX = 1, DstY = 6, Facing = 2, Attack = true,
                                        TargetX = 1, TargetY = 5,
                                    }],
                                   reachBoard, 1) is null
              && Machines.TurnProblem([new Move
                                       {
                                           SrcX = 4, SrcY = 0, DstX = 4, DstY = 3, Facing = 2, Attack = true,
                                           TargetX = 4, TargetY = 3,
                                       }],
                                      reachBoard, 1) is null
              && Machines.TurnProblem([new Move
                                       {
                                           SrcX = 4, SrcY = 0, DstX = 4, DstY = 4, Facing = 2, Attack = true,
                                           TargetX = 4, TargetY = 4,
                                       }],
                                      reachBoard, 1) is not null);

        Check("a later action is judged on the board the earlier ones changed",
              Machines.TurnProblem([new Move { SrcX = 5, SrcY = 2, DstX = 5, DstY = 3, Facing = 2 },
                                    new Move
                                    {
                                        SrcX = 5, SrcY = 3, DstX = 5, DstY = 4, Facing = 2, Attack = true,
                                        TargetX = 5, TargetY = 6,
                                    }],
                                   reachBoard, 1) is null
              && Machines.TurnProblem([new Move { SrcX = 5, SrcY = 2, DstX = 5, DstY = 3, Facing = 2 },
                                       new Move
                                       {
                                           SrcX = 5, SrcY = 3, DstX = 6, DstY = 5, Facing = 2, Attack = true,
                                           TargetX = 6, TargetY = 7,
                                       }],
                                      reachBoard, 1) is not null);

        Check("a piece the roster does not know, or a square holding none of the seat's, is not judged",
              Machines.TurnProblem([new Move { SrcX = 0, SrcY = 0, DstX = 5, DstY = 5, Facing = 2 }],
                                   reachBoard, 1) is null
              && Machines.TurnProblem([new Move { SrcX = 4, SrcY = 4, DstX = 0, DstY = 0, Facing = 2 }],
                                      reachBoard, 1) is null);

        var squares = Lobby.DefaultSquares(2, Lobby.BoardSide, Lobby.BoardSide, 2);
        Check("the auto squares are the near placing row, centred, one per machine",
              squares.Count == 2 && squares[0].X == 3 && squares[1].X == 4 &&
              squares.All(p => p.Y == 6));

        Check("a seven-machine free draft still gets a square each, all in a placing row",
              Lobby.DefaultSquares(7, Lobby.BoardSide, Lobby.BoardSide, 2) is { Count: 7 } wide &&
              wide.All(p => p.Y is 6 or 7) &&
              wide.Select(p => (p.X, p.Y)).Distinct().Count() == 7);

        static (string Line, Lobby Lobby) SummaryFor(Frame f)
        {
            var g = new Lobby("BUILD", isHost: false);
            g.OnSetup(f);
            return (SetupSummary(f, g), g);
        }

        var told = new Frame
        {
            Kind = MsgKind.Setup,
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144"],
            Board = [.. new int[64]],
            VictoryPoints = 20,
            DraftPoints = 40,
        };

        var summary = SummaryFor(told).Line;
        Check("the setup summary names the challenge and both rule numbers",
              summary.Contains("challenge 8BBC182B83FC495AA2150021217530D5") &&
              summary.Contains("rules 20/40") && summary.Contains("custom board"));

        Check("a stock match's summary carries no rule numbers to misread",
              !SummaryFor(new Frame { Kind = MsgKind.Setup, Army = [] }).Line.Contains("rules"));

        var absurd = SummaryFor(new Frame
        {
            Kind = MsgKind.Setup,
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = [],
            DraftPoints = 2000000000,
        });

        Check("a refused rule number is not announced as though it were accepted",
              absurd.Lobby.Refusal is not null && !absurd.Line.Contains("2000000000"));

        Check("a board of 100 squares is refused, not accepted as a 10x10",
              Preset.BoardProblem([.. new int[100]], "a board", 8, 8, Preset.RuleNotSet) is not null);

        Check("a board too big to be a board is refused at the frame boundary",
              FrameLimits.Refuse(new Frame { Kind = MsgKind.Setup, Board = [.. new int[400]] }) is not null);

        Check("a real board still passes both",
              Preset.BoardProblem([.. new int[64]], "a board", 8, 8, Preset.RuleNotSet) is null &&
              FrameLimits.Refuse(new Frame { Kind = MsgKind.Setup, Board = [.. new int[64]] }) is null);

        Check("a board wider than it is deep is ALLOWED",
              Preset.BoardProblem([.. new int[40]], "a board", 8, 5, Preset.RuleNotSet) is null &&
              Preset.BoardProblem([.. new int[18]], "a board", 6, 3, Preset.RuleNotSet) is null);

        Check("the same board turned on its side is accepted",
              Preset.BoardProblem([.. new int[40]], "a board", 5, 8, Preset.RuleNotSet) is null &&
              Preset.BoardProblem([.. new int[18]], "a board", 3, 6, Preset.RuleNotSet) is null);

        Check("a square board is unaffected by the width rule",
              Preset.BoardProblem([.. new int[64]], "a board", 8, 8, Preset.RuleNotSet) is null &&
              Preset.BoardProblem([.. new int[16]], "a board", 4, 4, Preset.RuleNotSet) is null);

        Check("a rectangle is accepted when the cell count matches its shape",
              Preset.BoardProblem([.. new int[40]], "a board", 5, 8, Preset.RuleNotSet) is null);

        Check("a cell count that disagrees with the shape is refused",
              Preset.BoardProblem([.. new int[64]], "a board", 5, 8, Preset.RuleNotSet) is not null);

        Check("a board wider than the game allocates is refused",
              Preset.BoardProblem([.. new int[81]], "a board", 9, 9, Preset.RuleNotSet) is not null);

        Check("a board with no cells at all is refused",
              Preset.BoardProblem([], "a board", 0, 0, Preset.RuleNotSet) is not null);

        Check("a placing depth that fits is accepted",
              Preset.BoardProblem([.. new int[40]], "a board", 5, 8, 2) is null);

        Check("a placing depth that would overlap the two zones is ALLOWED",
              Preset.BoardProblem([.. new int[25]], "a board", 5, 5, 3) is null);

        Check("a placing depth of zero is refused, because it is outside the range",
              Preset.BoardProblem([.. new int[40]], "a board", 5, 8, 0) is not null);

        Check("a placing depth past the board's maximum side is refused",
              Preset.BoardProblem([.. new int[40]], "a board", 5, 8, 9) is not null);

        var deepChasm = new int[40].ToList();
        deepChasm[5 * 2] = -2;
        Check("a Chasm in a placing row is ALLOWED at any depth",
              Preset.BoardProblem(deepChasm, "a board", 5, 8, 2) is null &&
              Preset.BoardProblem(deepChasm, "a board", 5, 8, 3) is null);

        var rectChasmFree = new int[40].Select((_, i) => i % 3).ToList();
        Check("a board that passes still passes after the rotation the guest applies",
              Preset.BoardProblem(rectChasmFree, "a board", 5, 8, 2) is null ==
              (Preset.BoardProblem(Preset.Rotate180(rectChasmFree, 5, 8), "a board", 5, 8, 2) is null));

        Check("default squares sit in the near placing zone of a resized board",
              Lobby.DefaultSquares(2, 8, 5, 2).All(p => p.Y is 3 or 4) &&
              Lobby.DefaultSquares(2, 8, 5, 2).Count == 2);

        Check("default squares on a 6x3 board with placing depth 1 sit on its own near row",
              Lobby.DefaultSquares(2, 6, 3, 1) is { Count: 2 } small &&
              small.All(p => p.Y == 2) &&
              small[0].X == 2 && small[1].X == 3);

        var autoLobby = new Lobby("B", isHost: true);
        autoLobby.ChooseBoard([.. new int[18]], Preset.RuleNotSet, Preset.RuleNotSet, 6, 3, 1);
        Check("the lobby's auto squares follow the board it chose, not the stock 8x8",
              autoLobby.AutoSquares(2) is { Count: 2 } autoPicked &&
              autoPicked.All(p => p.Y == 2 && p.Y < autoLobby.BoardHeight) &&
              autoPicked[0].X == 2 && autoPicked[1].X == 3);

        var stockLobby = new Lobby("B", isHost: true);
        stockLobby.ChooseBoard(null, Preset.RuleNotSet, Preset.RuleNotSet,
                               Lobby.BoardSide, Lobby.BoardSide, Preset.RuleNotSet);
        Check("the lobby's auto squares on a stock board are the near row it always used",
              stockLobby.AutoSquares(2) is { Count: 2 } stock &&
              stock.All(p => p.Y == 6) && stock[0].X == 3 && stock[1].X == 4 &&
              stockLobby.AutoDepth == 2);

        Check("more machines than the placing zone holds gets no squares, rather than duplicates",
              Lobby.DefaultSquares(9, 4, 5, 2).Count == 0);

        var emptyBoard = new Lobby("BUILD", isHost: false);
        emptyBoard.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = [],
            Board = [],
        });
        Check("an empty board from the peer is 'no board', not a board of nothing",
              emptyBoard.Board is null && emptyBoard.Refusal is null);

        static Lobby GuestToldArmy(params string[] army)
        {
            var g = new Lobby("BUILD", isHost: false);
            g.OnSetup(new Frame
            {
                Kind = MsgKind.Setup,
                Challenge = "8BBC182B83FC495AA2150021217530D5",
                Army = [.. army],
                Placements = [],
            });
            return g;
        }

        Check("an army that fits the challenge's AI draft is accepted from the peer",
              GuestToldArmy("1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144")
                  .Refusal is null);

        Check("an army too big for the AI draft is refused on receipt",
              GuestToldArmy("1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144",
                            "44333B622E88297526F3BAEFE049E1E6").Refusal is not null);

        Check("a peer's army holding a Thunderjaw is accepted on receipt",
              GuestToldArmy("F9433C1448F8F052BD457978CD0BFEC5", "B78EF94227B7D36454715138E9A17144")
                  .Refusal is null);

        Check("a machine this save has not seen is left for live-probe to judge",
              GuestToldArmy("AAAABBBBCCCCDDDDEEEEFFFF00001111", "B78EF94227B7D36454715138E9A17144")
                  .Refusal is null);

        var guestOwnRules = new Lobby("BUILD", isHost: false);
        guestOwnRules.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army = [],
            VictoryPoints = Preset.RuleNotSet,
            DraftPoints = Preset.RuleNotSet,
        });

        var richPreset = Presets.Preview();
        var guestSteps2 = WriteSteps(guestOwnRules, guestOwnRules.Remote!, interactivePlacement: true, preset: richPreset);
        Check("a guest never falls back to its own preset for the host's rules or board",
              !guestSteps2.Any(s => s.Args[0] is "--set-rules" or "--set-board"));

        var hostNoBoard = new Lobby("BUILD", isHost: true);
        hostNoBoard.Choose("8BBC182B83FC495AA2150021217530D5");
        hostNoBoard.OnSetup(new Frame
        {
            Kind = MsgKind.Setup,
            Army = ["1C96A39FFE37791F8AE07BD49A2230FF", "B78EF94227B7D36454715138E9A17144"],
            Placements = [],
        });
        Check("a HOST still uses its own preset, which is where its board came from",
              WriteSteps(hostNoBoard, hostNoBoard.Remote!, interactivePlacement: true, preset: richPreset)
                  .Any(s => s.Args[0] == "--set-board"));

        Check("the auto squares refuse an army wider than the placing rows",
              Lobby.DefaultSquares(Lobby.MostSquares + 1, Lobby.BoardSide, Lobby.BoardSide, 2).Count == 0 &&
              Lobby.DefaultSquares(Lobby.MostSquares, Lobby.BoardSide, Lobby.BoardSide, 2).Count
                  == Lobby.MostSquares);

        Check("no two auto squares are ever the same square",
              Lobby.DefaultSquares(Lobby.MostSquares, Lobby.BoardSide, Lobby.BoardSide, 2)
                   .Select(p => (p.X, p.Y)).Distinct().Count() == Lobby.MostSquares);

        var tutorialRefusal = Challenges.Problem("071AA8581C1F4EA69D7390BB6107D431");
        Check("a tutorial challenge is refused and the refusal says why",
              tutorialRefusal is not null && tutorialRefusal.Contains("Tutorials"));
        Check("a challenge that is not a UUID at all is refused",
              Challenges.Problem("beginner practice") is not null);

        static Frame SetupNaming(string challenge)
        {
            return new Frame { Kind = MsgKind.Setup, Challenge = challenge, Army = [], Placements = [] };
        }

        var toldATutorial = new Lobby("BUILD", isHost: false);
        toldATutorial.OnSetup(SetupNaming("071AA8581C1F4EA69D7390BB6107D431"));
        Check("a guest refuses a tutorial the host sent, rather than carrying it into the write",
              toldATutorial.Challenge is null && toldATutorial.Refusal is not null);

        var toldASupported = new Lobby("BUILD", isHost: false);
        toldASupported.OnSetup(SetupNaming("1E46038FF9804646813C959A31EAFB91"));
        Check("a guest still accepts a supported challenge",
              toldASupported.Challenge == "1E46038FF9804646813C959A31EAFB91" && toldASupported.Refusal is null);

        var savedIn = Console.In;
        var neverReads = new NeverReadsStream();
        try
        {
            Console.SetIn(new StreamReader(neverReads));

            using var readCts = new CancellationTokenSource();

            var pending = Task.Run(() => ReadLineOrCancel(readCts.Token));
            await readCts.CancelAsync();

            var landed = await Task.WhenAny(pending, Task.Delay(5000, cts.Token)) == pending;
            var threwCancelled = false;
            if (landed)
            {
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                    threwCancelled = true;
                }
            }

            Check("a stdin read already blocked still observes the parent-watch cancellation",
                  landed && threwCancelled);
        }
        finally
        {
            Console.SetIn(savedIn);
            neverReads.Release();
        }

        {
            BoardSnapshot PairBoard(int w, int h, params (string U, int X, int Y, int Hp)[] ps)
            {
                var pieces = ps.Select(p => new Piece(p.X, p.Y, (byte)p.Hp, 0, 0, Uuid: p.U)).ToList();
                return new BoardSnapshot(w, h, pieces, new sbyte[w * h]);
            }

            var sideA = new List<BoardSnapshot>
            {
                PairBoard(8, 8, ("AAAA", 2, 5, 5), ("BBBB", 2, 4, 2)),
                PairBoard(8, 8, ("AAAA", 2, 5, 4), ("BBBB", 2, 4, 2)),
                PairBoard(8, 8, ("AAAA", 2, 5, 3), ("BBBB", 2, 4, 0)),
                PairBoard(8, 8, ("AAAA", 2, 5, 3)),
            };
            var sideB = new List<BoardSnapshot>
            {
                PairBoard(8, 8, ("AAAA", 5, 2, 5), ("BBBB", 5, 3, 2)),
                PairBoard(8, 8, ("AAAA", 5, 2, 4), ("BBBB", 5, 3, 2)),
            };

            var evA = StreamPair.Events(sideA, rotate: false);
            var evB = StreamPair.Events(sideB, rotate: true);
            var res = StreamPair.Pair(evA, evB);

            Check("diff-pair: the shared tick pairs across the rotation",
                  res.Paired.Count == 1 && res.Paired[0].A.Uuid == "AAAA" && res.Paired[0].A.Delta == -1);
            Check("diff-pair: rotation folds the second stream into the first's frame",
                  evB.Count == 1 && evB[0].X == 2 && evB[0].Y == 5);
            Check("diff-pair: the one-sided remainder is the phantom, cost, duplicate tick and death",
                  res.OnlyA.Count == 3 &&
                  res.OnlyA.Any(e => e is { Uuid: "BBBB", Kind: "health", Delta: -2 }) &&
                  res.OnlyA.Any(e => e is { Uuid: "AAAA", Kind: "health", Delta: -1 }) &&
                  res.OnlyA.Any(e => e is { Uuid: "BBBB", Kind: "death" }));
            Check("diff-pair: the clean side has no remainder", res.OnlyB.Count == 0);

            var dup = StreamPair.Pair(
                [new StreamPair.Event("AAAA", 1, "health", -1, 0, 0, null),
                 new StreamPair.Event("AAAA", 5, "health", -1, 0, 0, null)],
                [new StreamPair.Event("AAAA", 2, "health", -1, 0, 0, null)]);
            Check("diff-pair: a duplicated tick pairs first-to-first and leaves the duplicate over",
                  dup.Paired.Count == 1 && dup.Paired[0].A.Sample == 1 &&
                  dup.OnlyA.Count == 1 && dup.OnlyA[0].Sample == 5);

            var smallEv = StreamPair.Events(
                [PairBoard(6, 3, ("CCCC", 5, 0, 4)), PairBoard(6, 3, ("CCCC", 5, 0, 3))], rotate: true);
            Check("diff-pair: rotation uses the sample's own board size",
                  smallEv.Count == 1 && smallEv[0].X == 0 && smallEv[0].Y == 2);
        }

        Console.WriteLine($"\n  {passed} passed, {failed} failed");
        await cts.CancelAsync();
        return failed == 0 ? 0 : 1;
    }

    private sealed class NeverReadsStream : Stream
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public void Release()
        {
            _gate.Set();
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            return new ValueTask<int>(new TaskCompletionSource<int>().Task);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            return new TaskCompletionSource<int>().Task;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _gate.Wait();
            return 0;
        }

        public override void Flush()
        {
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
    }

    private static string AsProbeJson(BoardSnapshot s)
    {
        var pieces = s.Pieces.Select(p =>
            $"{{\"idx\":{p.Idx},\"x\":{p.X},\"y\":{p.Y},\"health\":{p.Health},\"facing\":{p.Facing},\"owner\":{p.Owner}}}");
        return $"{{\"width\":{s.Width},\"height\":{s.Height},\"aiSeat\":{s.AiSeat}," +
               $"\"pieces\":[{string.Join(",", pieces)}]," +
               $"\"terrain\":[{string.Join(",", s.Terrain)}]}}";
    }

    private static async Task<(string? Host, string? Guest)> Handshake(int port, CancellationToken token,
                                                                      bool hostPairsFirst)
    {
        var room = Relay.NewCode();
        var hostLobby = new Lobby("SAME-BUILD", isHost: true);
        var guestLobby = new Lobby("SAME-BUILD", isHost: false);
        var hostPeer = new Peer("127.0.0.1", port, room);
        var guestPeer = new Peer("127.0.0.1", port, room);

        hostPeer.Received += f => { if (f.Kind == MsgKind.Ident) { hostLobby.OnIdent(f); } };
        guestPeer.Received += f => { if (f.Kind == MsgKind.Ident) { guestLobby.OnIdent(f); } };

        var first = hostPairsFirst ? (Peer: hostPeer, Lobby: hostLobby) : (Peer: guestPeer, Lobby: guestLobby);
        var second = hostPairsFirst ? (Peer: guestPeer, Lobby: guestLobby) : (Peer: hostPeer, Lobby: hostLobby);

        _ = first.Peer.Run(token);
        await Until(() => first.Peer.Seat >= 0, token);
        first.Peer.Send(first.Lobby.Ident());

        _ = second.Peer.Run(token);
        await Until(() => second.Peer.Seat >= 0, token);
        second.Peer.Send(second.Lobby.Ident());
        await Until(() => first.Lobby.PeerBuild is not null, token);

        foreach (var side in ((Peer Peer, Lobby Lobby)[])[first, second])
        {
            if (side.Lobby.TakeAnswer() is { } reply)
            {
                side.Peer.Send(reply);
            }
        }

        await Until(() => second.Lobby.PeerBuild is not null, token, 800);

        return (hostLobby.PeerBuild, guestLobby.PeerBuild);
    }

    private static async Task<(Peer Host, Peer Guest)> Pair(int port, CancellationToken token)
    {
        var room = Relay.NewCode();
        var host = new Peer("127.0.0.1", port, room);
        var guest = new Peer("127.0.0.1", port, room);
        _ = host.Run(token);
        await Until(() => host.Seat == 0, token);
        _ = guest.Run(token);
        await Until(() => guest.Seat == 1, token);
        return (host, guest);
    }

    private static string PickStage()
    {
        var l = new Lobby("B", isHost: true) { Seat = 0 };
        l.OnIdent(new Frame { Kind = MsgKind.Ident, Build = "B" });
        return l.Status();
    }

    private static BoardSnapshot SampleBoard()
    {
        var terrain = new sbyte[64];
        terrain[0] = 1;
        terrain[9] = 2;
        terrain[35] = -1;
        terrain[63] = 3;

        return new BoardSnapshot(8, 8,
        [
            new Piece(1, 0, 4, 2, 0, false, 0),
            new Piece(6, 1, 3, 2, 0, false, 1),
            new Piece(5, 6, 4, 0, 1, false, 2),
            new Piece(2, 7, 2, 1, 1, false, 3),
        ], terrain);
    }

    private static BoardSnapshot RotateByHand(BoardSnapshot s)
    {
        var pieces = s.Pieces
            .Select(p => new Piece(7 - p.X, 7 - p.Y, p.Health, p.Facing switch { 0 => 2, 1 => 3, 2 => 0, _ => 1 }, 1 - p.Owner))
            .ToList();

        var terrain = new sbyte[64];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                terrain[(7 - y) * 8 + (7 - x)] = s.Terrain[y * 8 + x];
            }
        }

        return s with { Pieces = pieces, Terrain = terrain };
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
    private static Move Landing(Move m, int x, int y)
    {
        return new Move
        {
            SrcX = m.SrcX,
            SrcY = m.SrcY,
            DstX = m.DstX,
            DstY = m.DstY,
            TargetX = m.TargetX,
            TargetY = m.TargetY,
            Facing = m.Facing,
            Attack = m.Attack,
            Burst = m.Burst,
            AtkX = m.AtkX,
            AtkY = m.AtkY,
            LandX = x,
            LandY = y,
        };
    }
}
