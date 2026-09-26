namespace Strikers.Netplay;

internal static partial class Program
{
    private static BoardSnapshot? _baseline;

    private static List<Move>? _pending;
    private static BoardSnapshot? _pendingBaseline;

    private static System.Diagnostics.Process? _watcher;

    internal const int PollSlowsAfter = 10;
    internal const int SlowPollMs = 5000;

    internal static int SnapshotPollDelay(int misses, int quickMs)
    {
        return misses < PollSlowsAfter ? quickMs : SlowPollMs;
    }

    private static volatile bool _watcherStopping;

    internal static bool WatcherEndHalts(bool auto, bool weStoppedIt)
    {
        return auto && !weStoppedIt;
    }

    internal const string MatchLeftReason = "a player left the match before it ended, so it cannot go on";

    internal const string GameClosedReason = "the game on this PC closed before the match ended, so it cannot go on";

    internal const int GameGoneChecks = 60;

    internal static readonly TimeSpan GameGoneCheckDelay = TimeSpan.FromMilliseconds(250);

    internal static string LeftOrClosedReason(bool gameStillRunning)
    {
        return gameStillRunning ? MatchLeftReason : GameClosedReason;
    }

    private static bool GameRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(AfterHalt.GameProcess).Length > 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static string StartFailedLine(string what, Exception e)
    {
        return $"  could not start {what} ({e.GetType().Name})";
    }

    internal static bool IsNoMatchLine(string line)
    {
        return line.Trim() == "{\"nomatch\":true}";
    }

    internal static bool MatchLeftHalts(bool auto, bool sawBoard, bool halted, bool summarised)
    {
        return auto && sawBoard && !halted && !summarised;
    }

    private static readonly List<string> _watched = [];

    private static bool _auto;
    private static TurnTracker? _tracker;
    private static int _autoTurns;
    private static bool _summarised;
    private static bool _sawEnemy;

    private static bool _peerEndedMatch;

    private static int _appliedTurns;

    private static string? _probePath;

    private static bool _autoOffByUser;

    private static bool _placementPhase = true;
    private static BoardSnapshot? _lastSnap;
    private static TurnPassWatch _boundaryPass = new(-1);

    internal const int ClosingTail = 3;

    internal static bool IsTeardownSample(BoardSnapshot snap, int boardsHeld)
    {
        return snap.Pieces.Count == 0 && boardsHeld > 0;
    }

    private static BoardSnapshot? _boundarySnap;

    internal readonly record struct Boundary(int Count, bool Closing);

    private static Boundary _boundary;

    internal static Boundary Crossed(Boundary before, bool closing)
    {
        return new Boundary(before.Count + 1, closing);
    }

    private static readonly List<BoardSnapshot> _boundaryBoards = [];

    private static List<BoardSnapshot> BoundaryCandidates()
    {
        lock (_closingLock)
        {
            return [.. _boundaryBoards];
        }
    }

    private static void CrossBoundary(bool closing, params BoardSnapshot[] boards)
    {
        lock (_closingLock)
        {
            _boundaryBoards.Clear();
            _boundaryBoards.AddRange(boards);
            _boundary = Crossed(_boundary, closing);
        }
    }

    private static void ResetBoundary()
    {
        lock (_closingLock)
        {
            _boundaryBoards.Clear();
            _boundary = default;
        }
    }

    private static Boundary CurrentBoundary()
    {
        lock (_closingLock)
        {
            return _boundary;
        }
    }

    private static (List<BoardSnapshot> Candidates, Boundary Boundary) BoundaryView()
    {
        lock (_closingLock)
        {
            return ([.. _boundaryBoards], _boundary);
        }
    }

    private static void AddBoundaryCandidate(BoardSnapshot board)
    {
        lock (_closingLock)
        {
            _boundaryBoards.Add(board);
        }
    }

    private static int BoundaryCandidateCount()
    {
        lock (_closingLock)
        {
            return _boundaryBoards.Count;
        }
    }

    private static int _boundaryTail;

    internal const int ClosingSettleSeconds = 30;
    private static Frame? _pendingClosingHash;
    private static readonly object _closingLock = new();

    private static bool _settling;

    internal static string MatchSummary(MatchState? match, int localOwner, int turnsSent, int turnsApplied)
    {
        string result;
        if (match is { } m)
        {
            var mine = localOwner == 0 ? m.Vp0 : m.Vp1;
            var theirs = localOwner == 0 ? m.Vp1 : m.Vp0;
            var who = m.Winner == localOwner ? "you won" : "the other player won";

            if (mine < 0 || theirs < 0)
            {
                result = $"{who}, and the score could not be read";
            }
            else
            {
                result = $"{who}, {mine} point(s) to {theirs}";
            }
        }
        else
        {
            result = "the other player has no machines left";
        }

        return $"match over: {result}; {turnsSent} turn(s) sent, {turnsApplied} applied";
    }

    private static bool _unsharedNoticed;

    internal static bool NewMatchAfterOver(BoardSnapshot snap)
    {
        return snap.Match is { Over: false } || snap.Placing is not null;
    }

    private static void NoticeUnsharedMatch(string json)
    {
        if (!SnapshotJson.TryParse(json.Trim(), out var snap, out _) || !NewMatchAfterOver(snap))
        {
            return;
        }

        _unsharedNoticed = true;
        Console.WriteLine("  a new match started on this PC after the shared one ended: it is not shared " +
                          "with the other player");
    }

    private static void Summarise(BoardSnapshot snap)
    {
        if (_summarised)
        {
            return;
        }

        _summarised = true;
        Console.WriteLine($"  {MatchSummary(snap.Match, snap.LocalOwner, _autoTurns, _appliedTurns)}");
    }

    internal static bool HoldClosingHash(Peer peer, Frame f, IReadOnlyList<BoardSnapshot> candidates)
    {
        lock (_closingLock)
        {
            if (peer.HashMatches(f, candidates))
            {
                _pendingClosingHash = null;
                return false;
            }

            _pendingClosingHash = f;
            return true;
        }
    }

    internal static bool SendsAppliedHash(bool auto, bool halted, bool final, bool peerEndedMatch, bool autoOffByUser)
    {
        if (halted)
        {
            return false;
        }

        if (auto)
        {
            return true;
        }

        return final && peerEndedMatch && !autoOffByUser;
    }

    private static void ClosingCandidateAdded(Peer peer)
    {
        lock (_closingLock)
        {
            if (_pendingClosingHash is { } pending && peer.HashMatches(pending, _boundaryBoards))
            {
                _pendingClosingHash = null;
                Console.WriteLine($"    in sync (the closing board settled, {_boundaryBoards.Count} candidate(s))");
            }
        }
    }

    internal static bool ClosingSettleStep(Peer peer, BoardSnapshot read, List<BoardSnapshot> candidates)
    {
        lock (_closingLock)
        {
            if (_pendingClosingHash is not { } pending)
            {
                return true;
            }

            if (IsTeardownSample(read, candidates.Count))
            {
                return false;
            }

            var last = candidates.Count > 0 ? candidates[^1] : null;
            var unchanged = last is not null &&
                            BoardHash.Pieces(read, 0) == BoardHash.Pieces(last, 0) &&
                            BoardHash.Terrain(read, 0) == BoardHash.Terrain(last, 0);
            if (!unchanged)
            {
                candidates.Add(read);
            }

            if (peer.HashMatches(pending, candidates))
            {
                _pendingClosingHash = null;
                Console.WriteLine($"    in sync (the closing board settled, {candidates.Count} candidate(s))");
                return true;
            }

            return false;
        }
    }

    internal static void ClosingSettleExpired(Peer peer, IReadOnlyList<BoardSnapshot> candidates)
    {
        lock (_closingLock)
        {
            if (_pendingClosingHash is not { } pending || peer.Halted)
            {
                return;
            }

            _pendingClosingHash = null;
            Console.WriteLine($"    the closing board did not settle to the peer's in {ClosingSettleSeconds} s");
            peer.CheckHash(pending, candidates, pending.Turn);
        }
    }

    internal static bool ClaimSettle()
    {
        lock (_closingLock)
        {
            if (_settling)
            {
                return false;
            }

            _settling = true;
            return true;
        }
    }

    internal static void ReleaseSettle()
    {
        lock (_closingLock)
        {
            _settling = false;
        }
    }

    internal static bool SettleWanted()
    {
        lock (_closingLock)
        {
            return _pendingClosingHash is not null;
        }
    }

    private static void StartClosingSettle(Peer peer, string probe)
    {
        if (!ClaimSettle())
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(ClosingSettleSeconds);
                while (DateTime.UtcNow < deadline && !peer.Halted)
                {
                    await Task.Delay(1000);
                    var read = ReadSnapshot(probe);
                    if (read is not null && ClosingSettleStep(peer, read, BoundaryCandidates()))
                    {
                        return;
                    }
                }

                ClosingSettleExpired(peer, BoundaryCandidates());
            }
            finally
            {
                ReleaseSettle();
            }

            if (SettleWanted() && !peer.Halted)
            {
                StartClosingSettle(peer, probe);
            }
        });
    }

    private static readonly List<(int X, int Y)> _placedSquares = [];

    private static readonly List<int> _placedSlots = [];

    private static readonly HashSet<int> _writtenSlots = [];
    private static readonly HashSet<(int X, int Y)> _writtenSquares = [];
    private static int _releasedPlacements;

    private static readonly Queue<Frame> _heldPlacements = new();
    private static readonly object _placementLock = new();

    internal static string ForceModeFor(bool joining, string first)
    {
        var weGoFirst = joining ? first == Lobby.FirstJoiner : first != Lobby.FirstJoiner;
        return weGoFirst ? "human" : "ai";
    }

    internal static bool PlacementWaits(bool auto, bool autoOffByUser, int held)
    {
        return (!auto && !autoOffByUser) || held > 0;
    }

    private static void ReceivePlacement(Peer peer, string probe, Frame f)
    {
        lock (_placementLock)
        {
            if (!PlacementWaits(_auto, _autoOffByUser, _heldPlacements.Count))
            {
                if (FrameGate.Refuse(CurrentPhase(), f, _writtenSlots, _writtenSquares) is { } written)
                {
                    peer.HaltAndTell($"the other side sent {written}");
                    return;
                }

                WritePlacement(peer, probe, f);
                return;
            }

            if (_heldPlacements.Any(h => h.PlaceIdx == f.PlaceIdx))
            {
                peer.HaltAndTell($"the other side sent a second placement for machine {f.PlaceIdx}, " +
                                 "which is already waiting to be written");
                return;
            }

            if (_heldPlacements.Count >= FrameLimits.MaxPlacements)
            {
                peer.HaltAndTell($"the other side sent more than {FrameLimits.MaxPlacements} placements " +
                                 "before this match was ready");
                return;
            }

            _heldPlacements.Enqueue(f);
            Console.WriteLine($"  <- their machine {f.PlaceIdx} arrived before this PC was watching its match, " +
                              "and goes in as soon as it is");
        }
    }

    private static void ReleaseHeldPlacements(Peer peer, string probe)
    {
        ReleaseHeldPlacements(peer, () => LocalShape(probe), probeArgs => RunProbeQuiet(probe, probeArgs));
    }

    private static void ReleaseHeldPlacements(Peer peer, Func<(int Width, int Height)?> shape,
                                               Func<string[], int> placeOne)
    {
        lock (_placementLock)
        {
            while (_heldPlacements.Count > 0 && !peer.Halted)
            {
                var f = _heldPlacements.Dequeue();

                if (FrameGate.Refuse(CurrentPhase(), f, _writtenSlots, _writtenSquares) is { } wrongPhase)
                {
                    peer.HaltAndTell($"the other side sent {wrongPhase}");
                    return;
                }

                WritePlacement(peer, f, shape, placeOne);
            }
        }
    }

    internal static void ResetPlacementBookkeeping()
    {
        lock (_placementLock)
        {
            _writtenSlots.Clear();
            _writtenSquares.Clear();
            _releasedPlacements = 0;
            _heldPlacements.Clear();
        }
    }

    internal static string? GatePeerFrame(Frame f)
    {
        lock (_placementLock)
        {
            return FrameGate.Refuse(CurrentPhase(), f, _writtenSlots, _writtenSquares);
        }
    }

    private static void WritePlacement(Peer peer, string probe, Frame f)
    {
        WritePlacement(peer, f, () => LocalShape(probe), probeArgs => RunProbeQuiet(probe, probeArgs));
    }

    private static void WritePlacement(Peer peer, Frame f, Func<(int Width, int Height)?> shape,
                                        Func<string[], int> placeOne)
    {
        if (f.Place is not { } theirs)
        {
            return;
        }

        if (shape() is not { } placeShape)
        {
            peer.HaltAndTell("the local board could not be read, so the other player's " +
                             "placement cannot be rotated into this PC's frame");
            return;
        }

        if (FrameLimits.SquareOff(theirs, placeShape.Width, placeShape.Height) is { } offPlace)
        {
            peer.HaltAndTell($"the other side sent {offPlace}. Nothing was written.");
            return;
        }

        var mine = theirs.Rotated(placeShape.Width, placeShape.Height);
        Console.WriteLine($"  <- their machine {f.PlaceIdx} at ({theirs.X},{theirs.Y}), " +
                          $"ours at ({mine.X},{mine.Y}) facing {mine.Dir}");

        var record = TurnBoundary.PlacementRecordFor(_releasedPlacements);
        if (placeOne(["--place-one", record.ToString(), mine.X.ToString(),
                      mine.Y.ToString(), mine.Dir.ToString(), "--for-slot",
                      f.PlaceIdx.ToString(), "--player", "1", "--yes"]) != 0)
        {
            peer.HaltAndTell(
                $"could not write their placement of machine {f.PlaceIdx} at ({mine.X},{mine.Y}). " +
                "Do not play on: the game would clamp a bad square silently, or place " +
                "another machine on it.");
            return;
        }

        RecordWrittenPlacement(f, theirs);
        AllowAiPlacement(_releasedPlacements, f.PlaceIdx);
    }

    private static void RecordWrittenPlacement(Frame f, Placement theirs)
    {
        _writtenSlots.Add(f.PlaceIdx);
        _writtenSquares.Add((theirs.X, theirs.Y));
        _releasedPlacements++;
    }

    private static SessionPhase CurrentPhase()
    {
        return PhaseOf(_lastSnap, _peerEndedMatch);
    }

    internal static SessionPhase PhaseOf(BoardSnapshot? lastSnap, bool peerEndedMatch)
    {
        if (peerEndedMatch)
        {
            return SessionPhase.Over;
        }

        if (lastSnap is not { } snap)
        {
            return SessionPhase.NoMatch;
        }

        if (snap.Match is { Over: true })
        {
            return SessionPhase.Over;
        }

        if (snap.Placing is not null)
        {
            return SessionPhase.Placing;
        }

        return SessionPhase.Playing;
    }

    private static Preset? _playPreset;

    private static List<string>? _playArmy;

    private static string? _playArmyName;

    private static List<string> LocalArmy(int seat)
    {
        if (_playArmy is { Count: > 0 })
        {
            return _playArmy;
        }

        if (_playPreset is null || seat is not (0 or 1))
        {
            return [];
        }

        return [.. _playPreset.ArmyFor(isHost: seat == 0).Select(m => Lobby.NormaliseUuid(m.Uuid) ?? m.Uuid)];
    }

    private static System.Diagnostics.Process? _placeHold;

    private static System.Diagnostics.Process? _forcer;
    private static string? _forceMode;

    private static string? _rematchPlacement;

    private static string? _capturePath;
    private static readonly object _captureLock = new();

    private static Peer? _livePeer;

    internal const string TheirRecordingWhat = "the other player's recording of the match";
    internal const string TheirStartWhat = "the start of the other player's recording";
    internal const string TheirLogWhat = "the other player's log";

    internal const string OurRecordingWhat = "this PC's recording of the match";
    internal const string OurStartWhat = "the start of this PC's recording";
    internal const string OurLogWhat = "this PC's log";

    private static IncomingFile _theirRecording = NewTailFile();
    private static IncomingFile _theirStart = NewStartFile();
    private static IncomingFile _theirLog = NewLogFile();

    private static int _oursSent;

    private static DateTime? _fileStamp;

    internal static IncomingFile NewTailFile()
    {
        return new IncomingFile(TheirRecordingWhat, FrameLimits.MaxRecordingTailParts,
                                FrameLimits.MaxRecordingTailBytes);
    }

    internal static IncomingFile NewStartFile()
    {
        return new IncomingFile(TheirStartWhat, FrameLimits.MaxRecordingStartParts,
                                FrameLimits.MaxRecordingStartBytes);
    }

    internal static IncomingFile NewLogFile()
    {
        return new IncomingFile(TheirLogWhat, FrameLimits.MaxLogParts, FrameLimits.MaxLogBytes);
    }

    internal static DateTime? StampTaken()
    {
        return _fileStamp;
    }

    internal static DateTime FileStamp()
    {
        _fileStamp ??= DateTime.Now;
        return _fileStamp.Value;
    }

    internal static string? NameForKind(MsgKind kind, string? ourCapture)
    {
        switch (kind)
        {
            case MsgKind.RecordingStart:
                return Recordings.StartNameFor(ourCapture, FileStamp());

            case MsgKind.Log:
                return Recordings.LogNameFor(ourCapture, FileStamp());

            case MsgKind.Recording:
                return Recordings.NameFor(ourCapture, FileStamp());

            default:
                return null;
        }
    }

    internal static void ForgetRecordings()
    {
        _theirRecording = NewTailFile();
        _theirStart = NewStartFile();
        _theirLog = NewLogFile();
        _fileStamp = null;
        Interlocked.Exchange(ref _oursSent, 0);
    }

    internal static void SendOurFiles(Peer? peer)
    {
        if (peer is null || Interlocked.Exchange(ref _oursSent, 1) == 1)
        {
            return;
        }

        string? path;
        lock (_captureLock)
        {
            path = _capturePath;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(HaltSettle);

            var head = path is null ? null : Recordings.Head(path, FrameLimits.MaxRecordingStartBytes);
            await SendOneFile(peer, head, MsgKind.RecordingStart, FrameLimits.MaxRecordingStartParts, OurStartWhat);

            var tail = path is null ? null : Recordings.Tail(path, FrameLimits.MaxRecordingTailBytes);
            await SendOneFile(peer, tail, MsgKind.Recording, FrameLimits.MaxRecordingTailParts, OurRecordingWhat);

            var log = Recordings.LogTail();
            await SendOneFile(peer, log, MsgKind.Log, FrameLimits.MaxLogParts, OurLogWhat);
        });
    }

    private static async Task SendOneFile(Peer peer, byte[]? bytes, MsgKind kind, int maxParts, string what)
    {
        if (bytes is not { Length: > 0 })
        {
            return;
        }

        var parts = Recordings.Parts(bytes, kind, maxParts);
        if (parts.Count == 0)
        {
            return;
        }

        var sent = 0;
        foreach (var part in parts)
        {
            if (peer.TrySend(part))
            {
                sent++;
            }

            await Task.Delay(RecordingPace);
        }

        Console.WriteLine(WentOut(what, sent, parts.Count));
    }

    internal static string WentOut(string what, int sent, int parts)
    {
        if (sent == parts)
        {
            return $"  -> {what} went out, {parts} part(s)";
        }

        return $"  -> {what} went out in part, {sent} of {parts} part(s)";
    }

    internal static void SayIfTheirsNeverCame()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(Peer.HaltDrain + TimeSpan.FromSeconds(1));
            foreach (var file in new[] { _theirStart, _theirRecording, _theirLog })
            {
                if (file.Unfinished() is { } unfinished)
                {
                    Console.WriteLine($"  {unfinished}");
                }
            }
        });
    }

    internal static readonly TimeSpan RecordingPace = TimeSpan.FromMilliseconds(350);

    internal static readonly TimeSpan HaltSettle = TimeSpan.FromSeconds(2);

    internal static void TakeFile(Peer peer, Frame f)
    {
        if (!peer.Halted)
        {
            peer.HaltAndTell("the other side sent a recording while the match was live");
            return;
        }

        string? ours;
        lock (_captureLock)
        {
            ours = _capturePath;
        }

        IncomingFile into;
        switch (f.Kind)
        {
            case MsgKind.RecordingStart:
                into = _theirStart;
                break;

            case MsgKind.Log:
                into = _theirLog;
                break;

            case MsgKind.Recording:
                into = _theirRecording;
                break;

            default:
                return;
        }

        if (NameForKind(f.Kind, ours) is not { } name)
        {
            return;
        }

        var problem = into.Offer(f, peer.Halted);
        if (problem is not null)
        {
            Console.Error.WriteLine($"  {problem}");
            return;
        }

        if (!into.Done || into.Taken is not { } bytes)
        {
            return;
        }

        var written = Recordings.Write(bytes, name);
        Console.WriteLine(written is null
            ? $"  {into.What} could not be written"
            : $"  <- {into.What} is in {Captures.Folder}, {Path.GetFileName(written)}");
    }

    private static void Capture(string json)
    {
        if (_capturePath is null)
        {
            return;
        }

        try
        {
            lock (_captureLock)
            {
                File.AppendAllText(_capturePath, json + Environment.NewLine);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  auto: capture stopped ({e.GetType().Name})");
            _capturePath = null;
        }
    }

    internal static bool CapturesSample(bool auto, bool tracking, bool halted)
    {
        return tracking && (auto || halted);
    }

    internal static bool SampleGoesToAuto(bool auto, bool tracking, bool halted)
    {
        return auto || CapturesSample(auto, tracking, halted);
    }

    internal static bool ReadsSample(bool auto, bool tracking, bool halted)
    {
        return auto && tracking && !halted;
    }

    private static void OnAutoSample(Peer peer, string json)
    {
        if (!CapturesSample(_auto, _tracker is not null, peer.Halted))
        {
            return;
        }

        Capture(json);
        if (!ReadsSample(_auto, _tracker is not null, peer.Halted) || _tracker is null)
        {
            return;
        }

        if (IsNoMatchLine(json))
        {
            if (MatchLeftHalts(_auto, _lastSnap is not null, peer.Halted, _summarised))
            {
                _auto = false;
                _ = Task.Run(async () =>
                {
                    var running = true;
                    for (var i = 0; i < GameGoneChecks && running; i++)
                    {
                        await Task.Delay(GameGoneCheckDelay);
                        running = GameRunning();
                    }

                    peer.HaltAndTell(LeftOrClosedReason(running));
                });
            }

            return;
        }

        if (!SnapshotJson.TryParse(json, out var snap, out _))
        {
            return;
        }

        if (_lastSnap is not null && snap.IsTornAfter(_lastSnap))
        {
            return;
        }

        if (_placementPhase && snap.PlacingUnreadable)
        {
            return;
        }

        if (_placementPhase)
        {
            if (snap.Pieces.Any(p => p.Acts > 0 || p.Bursts > 0 || p.Acted || p.Burst))
            {
                _placementPhase = false;
            }

            else
            {
                foreach (var placed in TurnBoundary.CommittedPlacements(_lastSnap, snap, snap.LocalOwner,
                                                                         _placedSquares))
                {
                    var slot = TurnBoundary.ArmySlotFor(placed.Uuid, LocalArmy(peer.Seat), _placedSlots,
                                                        placed.Index, out var complaint);
                    if (complaint is not null)
                    {
                        Console.Error.WriteLine($"  Warning: {complaint}");
                    }

                    peer.SendPlace(slot, placed.Square);
                    _placedSquares.Add((placed.Square.X, placed.Square.Y));
                    _placedSlots.Add(slot);
                    Console.WriteLine($"  -> placed machine {slot} at " +
                                      $"({placed.Square.X},{placed.Square.Y}) facing {placed.Square.Dir}, sent; " +
                                      "their board is held until it lands");
                }
            }
        }

        if (_lastSnap is { } beforeSnap)
        {
            var marksBoundary = TurnBoundary.AnyTurnEnded(beforeSnap, snap);
            var passBoundary = _boundaryPass.Observe(beforeSnap, snap, marksBoundary);
            if (marksBoundary || passBoundary)
            {
                _boundarySnap = snap;
                _boundaryTail = 3;
                CrossBoundary(closing: false, beforeSnap, snap);

                ClosingCandidateAdded(peer);
            }

            else if (TurnBoundary.EndedWithoutABoundary(beforeSnap, snap, snap.LocalOwner))
            {
                _boundarySnap = snap;
                _boundaryTail = ClosingTail;
                CrossBoundary(closing: true, snap);
                Console.WriteLine("  the match ended without a turn boundary");

                ClosingCandidateAdded(peer);
            }
            else if (_boundaryTail > 0)
            {
                if (TurnBoundary.AnyMarked(snap, snap.LocalOwner) ||
                    TurnBoundary.AnyMarked(snap, 1 - snap.LocalOwner))
                {
                    _boundaryTail = 0;
                }
                else if (IsTeardownSample(snap, BoundaryCandidateCount()))
                {
                    _boundaryTail = 0;
                }
                else
                {
                    _boundaryTail--;
                    AddBoundaryCandidate(snap);
                    if (CurrentBoundary().Closing)
                    {
                        ClosingCandidateAdded(peer);
                    }
                }
            }
        }

        if (_probePath is not null)
        {
            var holderAlive = _moveBoundsHolder is { HasExited: false };
            switch (MoveBoundsStep(_lastSnap is null, _moveBoundsPending, holderAlive))
            {
                case BoundsStep.Apply:
                    _moveBoundsPending = false;
                    ApplyMoveBoundsPatch(_probePath, snap.Width, snap.Height);
                    break;

                case BoundsStep.Wait:
                    _moveBoundsPending = true;
                    break;
            }
        }

        _lastSnap = snap;

        IReadOnlyList<BoardSnapshot>? turn;
        try
        {
            turn = _tracker.Push(snap);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  auto: a sample could not be read ({e.GetType().Name})");
            return;
        }

        var over = TurnBoundary.MatchOver(snap, snap.LocalOwner);

        if (!TurnBoundary.NoOpponentLeft(snap, snap.LocalOwner))
        {
            _sawEnemy = true;
        }

        var trustworthy = snap.Match is not null || _sawEnemy;

        if (over && TurnBoundary.LocalForfeit(snap, snap.LocalOwner))
        {
            _auto = false;
            peer.HaltAndTell(MatchLeftReason);
            return;
        }

        if (turn is null)
        {
            if (!trustworthy || !over)
            {
                return;
            }

            if (_peerEndedMatch)
            {
                _auto = false;
                Console.WriteLine("\n  the match is over; their turn ended it, nothing to send");
                Summarise(snap);
                return;
            }

            turn = _tracker.Flush();
            if (turn is null)
            {
                return;
            }

            if (TurnBoundary.NothingOfOursIn(turn, snap.LocalOwner))
            {
                _auto = false;
                var closing = _boundarySnap ?? snap;
                Console.WriteLine("\n  the match ended in nobody's turn; nothing to send, the closing hash decides");
                var crossed = CurrentBoundary().Count;
                peer.SendHash(crossed, closing);
                Console.WriteLine($"  -> closing hash sent (boundary {crossed}, {closing.Pieces.Count} pieces)");
                Summarise(snap);
                return;
            }

            _auto = false;
            Console.WriteLine(snap.Match is not null
                ? "\n  the match is over; reading the turn that ended it"
                : "\n  the opponent has no machines left; reading the turn that ended the match");
        }

        var result = MoveDetector.ReadTurn(turn, snap.LocalOwner, out var readNote);
        if (readNote is not null)
        {
            Console.Error.WriteLine($"  auto: {readNote}");
        }

        if (!result.Ok)
        {
            _auto = false;
            peer.HaltAndTell($"could not read the local turn automatically: {result.Refusal}");
            Console.Error.WriteLine("  auto is OFF and the match is halted. This turn cannot be sent faithfully.\n" +
                                    "  Rebuilding it by hand does not work either, compare both boards instead.");
            return;
        }

        if (result.Warning is not null)
        {
            Console.Error.WriteLine($"  auto: warning: {result.Warning}");
        }

        var atSend = (_probePath is null ? null : ReadSnapshot(_probePath)) ?? snap;
        var final = TurnBoundary.EndsTheMatch(atSend, snap.LocalOwner, _sawEnemy);

        peer.SendMoves(result.Moves, final);
        _autoTurns++;
        Console.WriteLine($"\n  turn {_autoTurns} read and sent automatically, {result.Moves.Count} action(s), " +
                          $"{turn.Count} samples" + (final ? " -- this turn ended the match" : ""));
        foreach (var m in result.Moves)
        {
            Console.WriteLine($"     {Describe(m)}");
        }

        if (final)
        {
            Summarise(atSend);
        }
    }

    private static void StartRematch(string probe, bool fromPeer)
    {
        StopWatcher();
        lock (_watched)
        {
            _watched.Clear();
        }

        _auto = false;
        _tracker = null;
        _autoTurns = 0;
        _summarised = false;
        _unsharedNoticed = false;
        _sawEnemy = false;
        _peerEndedMatch = false;
        _appliedTurns = 0;
        _capturePath = null;
        _placementPhase = true;
        _placedSquares.Clear();
        _placedSlots.Clear();

        _lastSnap = null;
        _boundaryPass = new TurnPassWatch(-1);
        _moveBoundsPending = false;
        _boundarySnap = null;
        ForgetRecordings();
        ResetBoundary();
        _boundaryTail = 0;
        _pendingClosingHash = null;
        ResetPlacementBookkeeping();

        if (_placeHold is null || _placeHold.HasExited)
        {
            _placeHold = StartPlacementHold(probe);
            Console.WriteLine(_placeHold is null
                ? "  Warning: could not restart the placement hold; the AI seat will re-place " +
                  "last match's squares without waiting"
                : "  placement hold restarted for the rematch");
        }

        Console.WriteLine(fromPeer
            ? "\n  <- the other player asked for a rematch"
            : "\n  -> rematch asked for");

        if (_forceMode is null)
        {
            Console.Error.WriteLine("  Warning: the coin flip was never forced on this run, so Retry will roll a " +
                                    "random first player. Check whose turn it is on BOTH PCs before moving.");
        }
        else
        {
            try
            {
                if (_forcer is { HasExited: false })
                {
                    _forcer.Kill(entireProcessTree: false);
                }
            }
            catch (Exception) { }

            _forcer = StartForceFirst(probe, _forceMode);
            Console.WriteLine(_forcer is null
                ? "  Warning: could not re-apply the coin flip, check whose turn it is on BOTH PCs before moving"
                : $"  coin flip re-applied: this seat takes `{_forceMode}` again");
        }

        Console.WriteLine("  now press Retry on BOTH PCs (the victory screen offers it too), then type 'auto' on each.");
        Console.WriteLine("  Retry keeps both armies and the AI seat's placements, so no army write is owed.");

        Console.WriteLine(_rematchPlacement is null
            ? "  Warning: you still place YOUR OWN machines by hand, and this run was started without " +
              "--preset, so there is nothing here to tell you which machine goes on which square. " +
              "Put them where you had them, or the boards desync before a move is played."
            : $"  place your own machines by hand: {_rematchPlacement}");
    }

    private static void HandleCommand(Peer peer, string probe, string line)
    {
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0)
        {
            return;
        }

        try
        {
            switch (w[0])
            {
                case "move" when w.Length >= 6:
                    peer.SendMove(new Move
                    {
                        SrcX = int.Parse(w[1]), SrcY = int.Parse(w[2]),
                        DstX = int.Parse(w[3]), DstY = int.Parse(w[4]), Facing = byte.Parse(w[5]),
                    });
                    Console.WriteLine("  -> move sent");
                    break;

                case "attack" when w.Length >= 6:
                    peer.SendMove(new Move
                    {
                        SrcX = int.Parse(w[1]), SrcY = int.Parse(w[2]),
                        DstX = int.Parse(w[1]), DstY = int.Parse(w[2]),
                        TargetX = int.Parse(w[3]), TargetY = int.Parse(w[4]),
                        Facing = byte.Parse(w[5]), Attack = true,
                    });
                    Console.WriteLine("  -> attack sent");
                    break;

                case "start":
                    _baseline = ReadSnapshot(probe);
                    StopWatcher();
                    lock (_watched)
                    {
                        _watched.Clear();
                    }

                    _watcher = _baseline is null ? null : StartWatcher(probe);
                    Console.WriteLine(_baseline is null
                        ? "  no snapshot, is a match live?"
                        : $"  baseline taken ({_baseline.Pieces.Count} pieces, you are seat {_baseline.LocalOwner})" +
                          (_watcher is null ? "" : ", watching the board"));
                    if (_baseline is not null && _watcher is null)
                    {
                        Console.Error.WriteLine("  Warning: no board watcher: this turn will be read from its two ends only, " +
                                                "so a kill or a push that frees a square can be ordered wrongly.");
                    }

                    break;

                case "done":
                    if (_baseline is null)
                    {
                        Console.Error.WriteLine("  run 'start' at the beginning of your turn first");
                        break;
                    }

                    StopWatcher();

                    var now = ReadSnapshot(probe);
                    if (now is null)
                    {
                        Console.Error.WriteLine("  no snapshot, is a match live?");
                        break;
                    }

                    var result = ReadTurn(_baseline, now);
                    if (!result.Ok)
                    {
                        Console.Error.WriteLine($"  cannot read your turn: {result.Refusal}");
                        Console.Error.WriteLine("  Warning: This turn cannot be sent faithfully, and hand-rebuilding it does not work:");
                        Console.Error.WriteLine("     'move'/'attack' send one action each, so a real turn would be split, and a");
                        Console.Error.WriteLine("     reconstruction with the right actions has still produced a different board.");
                        Console.Error.WriteLine("     Stop the match here and compare both boards rather than playing on.");
                        break;
                    }

                    if (result.Warning is not null)
                    {
                        Console.Error.WriteLine($"  warning: {result.Warning}");
                    }

                    _pending = result.Moves;
                    _pendingBaseline = now;

                    Console.WriteLine($"  read a turn of {result.Moves.Count} action(s), NOT SENT YET");
                    foreach (var m in result.Moves)
                    {
                        Console.WriteLine($"     {Describe(m)}");
                    }

                    Console.WriteLine("  end your turn in the game, then type 'send'.");
                    break;

                case "send":
                    if (_pending is null)
                    {
                        Console.Error.WriteLine("  nothing read yet, run 'done' at the end of your turn first");
                        break;
                    }

                    peer.SendMoves(_pending);
                    Console.WriteLine($"  -> turn of {_pending.Count} action(s) sent");

                    _baseline = _pendingBaseline;
                    _pending = null;
                    _pendingBaseline = null;
                    break;

                case "auto":
                    var off = w.Length > 1 && w[1] is "off" or "stop";
                    if (off)
                    {
                        _autoOffByUser = true;
                        _auto = false;
                        StopWatcher();
                        _tracker = null;
                        Console.WriteLine($"  auto OFF, back to start / done / send." +
                                          (_capturePath is null ? "" : $" Capture kept at {_capturePath}."));
                        _capturePath = null;
                        ReleaseHeldPlacements(peer, probe);
                        break;
                    }

                    var first = ReadSnapshot(probe);
                    if (first is null)
                    {
                        Console.Error.WriteLine("  no snapshot, is a match live?");
                        break;
                    }
                    if (first.LocalOwner < 0)
                    {
                        Console.Error.WriteLine("  the snapshot does not say which seat is the AI.");
                        break;
                    }

                    StopWatcher();
                    lock (_watched)
                    {
                        _watched.Clear();
                    }

                    _tracker = new TurnTracker(first.LocalOwner);
                    _autoTurns = 0;
                    _summarised = false;
                    _unsharedNoticed = false;
                    _sawEnemy = false;
                    _peerEndedMatch = false;
                    _appliedTurns = 0;
                    _placementPhase = true;
                    _placedSquares.Clear();
                    _placedSlots.Clear();
                    _lastSnap = null;
                    _boundaryPass = new TurnPassWatch(-1);
                    _moveBoundsPending = false;
                    _boundarySnap = null;
                    ResetBoundary();
                    _boundaryTail = 0;
                    _pendingClosingHash = null;
                    _capturePath = w.Length > 1 && w[1] is not ("off" or "stop")
                        ? w[1]
                        : Captures.NewRecording(DateTime.Now);

                    lock (_placementLock)
                    {
                        _writtenSlots.Clear();
                        _writtenSquares.Clear();
                        _releasedPlacements = 0;
                        _auto = true;
                    }

                    _watcher = StartWatcher(probe, peer);
                    if (_watcher is null)
                    {
                        _auto = false;
                        _tracker = null;
                        Console.Error.WriteLine("  auto needs the board watcher and it would not start.");
                        ReleaseHeldPlacements(peer, probe);
                        break;
                    }

                    Console.WriteLine($"  auto ON (seat {first.LocalOwner}).");
                    ReleaseHeldPlacements(peer, probe);
                    if (!Console.IsOutputRedirected)
                    {
                        Console.WriteLine("     'auto off' to stop.\n" +
                                          (_capturePath is null
                                              ? "     not recording (the recordings folder could not be written)"
                                              : $"     capturing to {_capturePath}, replay any turn later with: netplay --diff-turns <file>"));
                    }
                    break;

                case "hash":
                    var snap = _boundarySnap ?? ReadSnapshot(probe);
                    if (snap is null)
                    {
                        Console.Error.WriteLine("  no snapshot, is a match live?");
                        break;
                    }
                    peer.SendHash(w.Length > 1 ? int.Parse(w[1]) : 0, snap);
                    Console.WriteLine($"  -> hash sent ({snap.Pieces.Count} pieces)" +
                                      (_boundarySnap is null ? "" : ", board as at the last turn boundary"));
                    break;

                case "rematch":
                    peer.SendRematch();
                    StartRematch(probe, fromPeer: false);
                    break;

                default:
                    Console.WriteLine("  commands: auto [off] | rematch | start | done | send | " +
                                      "move <sx> <sy> <dx> <dy> <facing> | " +
                                      "attack <sx> <sy> <tx> <ty> <facing> | hash <turn> | quit");
                    break;
            }
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("  could not parse those numbers");
        }
    }

    private static MoveDetector.Result ReadTurn(BoardSnapshot baseline, BoardSnapshot now)
    {
        List<string> lines;
        lock (_watched)
        {
            lines = [.. _watched];
        }

        var samples = new List<BoardSnapshot>();
        var unreadable = 0;
        foreach (var l in lines)
        {
            if (SnapshotJson.TryParse(l, out var s, out _))
            {
                samples.Add(s);
            }
            else
            {
                unreadable++;
            }
        }

        if (samples.Count < 2)
        {
            Console.Error.WriteLine($"  Warning: only {samples.Count} usable board sample(s) from the watcher" +
                                    (unreadable > 0 ? $" ({unreadable} unparseable)" : "") +
                                    ", reading this turn from its two ends instead.");
            return MoveDetector.Detect(baseline, now, now.LocalOwner);
        }

        Console.WriteLine($"  {samples.Count} board samples taken during the turn");
        var seq = MoveDetector.ReadTurn(samples, now.LocalOwner, out var readNote);
        if (readNote is not null)
        {
            Console.Error.WriteLine($"  note: {readNote}");
        }

        var flat = MoveDetector.Detect(baseline, now, now.LocalOwner);
        if (seq.Ok != flat.Ok || (seq.Ok && flat.Ok && seq.Moves.Count != flat.Moves.Count))
        {
            Console.Error.WriteLine("  note: the two-end reading says " +
                                    (flat.Ok ? $"{flat.Moves.Count} action(s)" : $"REFUSED ({flat.Refusal})") +
                                    ", the sampled reading is the one that counts.");
        }

        return seq;
    }

    private static System.Diagnostics.Process? StartWatcher(string probe, Peer? autoPeer = null)
    {
        _watcherStopping = false;
        _probePath = probe;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("--watch-snapshot");

            psi.ArgumentList.Add(WatcherSeconds(autoPeer is not null));

            psi.ArgumentList.Add("--asymmetric-board");

            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    if (WatcherEndHalts(_auto, _watcherStopping) && autoPeer is not null)
                    {
                        autoPeer.HaltAndTell("the board watcher stopped, so this PC can no longer read its own turns");
                    }

                    return;
                }

                if (e.Data.Length == 0)
                {
                    return;
                }

                lock (_watched)
                {
                    _watched.Add(e.Data);
                }

                if (autoPeer is { } peer && SampleGoesToAuto(_auto, _tracker is not null, peer.Halted))
                {
                    OnAutoSample(peer, e.Data);
                }
                else if (_summarised && !_unsharedNoticed)
                {
                    NoticeUnsharedMatch(e.Data);
                }
            };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(StartFailedLine($"{probe} --watch-snapshot", e));
            return null;
        }
    }

    private static void StopWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcherStopping = true;
        try { if (!_watcher.HasExited) { _watcher.Kill(); } } catch (Exception) { }
        try { _watcher.WaitForExit(2000); } catch (Exception) { }
        _watcher.Dispose();
        _watcher = null;
    }

    internal static string[] HoldArgs(int parentPid)
    {
        return ["--hold", "--wait", "600", "--secs", "0", "--yes", "--parent-pid", parentPid.ToString()];
    }

    internal static string WatcherSeconds(bool autoOn)
    {
        return autoOn ? "0" : "900";
    }

    private static System.Diagnostics.Process? StartHold(string probe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                RedirectStandardInput = true, UseShellExecute = false,
            };
            foreach (var a in HoldArgs(Environment.ProcessId))
            {
                psi.ArgumentList.Add(a);
            }

            return System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(StartFailedLine($"{probe} --hold", e));
            return null;
        }
    }

    private static System.Diagnostics.Process? StartPlacementHold(string probe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                RedirectStandardInput = true, UseShellExecute = false,
            };
            foreach (var a in new[] { "--hold-placement", "--wait", "600",
                                      "--parent-pid", Environment.ProcessId.ToString() })
            {
                psi.ArgumentList.Add(a);
            }

            return System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(StartFailedLine($"{probe} --hold-placement", e));
            return null;
        }
    }

    private static void AllowAiPlacement(int n, int slot)
    {
        if (_placeHold is null || _placeHold.HasExited)
        {
            Console.Error.WriteLine($"  Warning: no placement hold to release for placement {n - 1}; " +
                                    "if the AI seat is stalled, restart --play");
            return;
        }

        try
        {
            _placeHold.StandardInput.WriteLine($"allow {n} {slot}");
            _placeHold.StandardInput.Flush();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  Warning: could not release the placement hold ({e.GetType().Name})");
        }
    }

    private static System.Diagnostics.Process? StartForceFirst(string probe, string mode)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe) { UseShellExecute = false };
            foreach (var a in new[] { "--force-first", mode, "--yes", "--wait", "600",
                                      "--parent-pid", Environment.ProcessId.ToString() })
            {
                psi.ArgumentList.Add(a);
            }

            return System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(StartFailedLine($"{probe} --force-first", e));
            return null;
        }
    }

    private static string Describe(Move m)
    {
        return (m.Attack
            ? $"attack ({m.SrcX},{m.SrcY})->({m.DstX},{m.DstY}) on ({m.TargetX},{m.TargetY}) facing {m.Facing}"
            : $"move   ({m.SrcX},{m.SrcY})->({m.DstX},{m.DstY}) facing {m.Facing}")
        + (m.AtkX >= 0 ? $"  struck from ({m.AtkX},{m.AtkY})" : "")
        + (m.Burst ? "  + burst (Overcharge, -2 health)" : "");
    }

    private static async Task<BoardSnapshot?> BoundaryBoard(int since, string probe, CancellationToken ct)
    {
        for (var waited = 0; waited < 4000; waited += 50)
        {
            if (CurrentBoundary().Count != since && _boundarySnap is { } atBoundary)
            {
                return atBoundary;
            }

            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { return null; }
        }

        Console.Error.WriteLine("  auto: no turn boundary arrived to hash, reading the board live instead. " +
                                "A turn-start ability can make that read disagree with the peer's.");
        return ReadSnapshot(probe);
    }

    internal static async Task Guarded(Func<Task> loop, Action<string> halt, string what)
    {
        try
        {
            await loop();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            halt($"the {what} stopped on an error ({e.GetType().Name}), so nothing more can be done safely. " +
                 "Do not play on: compare both boards before restarting.");
        }
    }

    internal static BoardSnapshot? PreflightBoard(BoardSnapshot? last, Func<BoardSnapshot?> fresh)
    {
        if (last is { AiSeat: 0 or 1 })
        {
            return last;
        }

        return fresh() is { AiSeat: 0 or 1 } read ? read : null;
    }

    private static BoardSnapshot? ReadSnapshot(string probe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("--snapshot");

            psi.ArgumentList.Add("--terrain-altered");

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var json = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                if (p.ExitCode != 2)
                {
                    Console.Error.WriteLine($"    live-probe --snapshot exited {p.ExitCode}:");
                    foreach (var shown in ChildLines(err, stderr: true))
                    {
                        Console.Error.WriteLine(shown);
                    }
                }

                return null;
            }

            if (SnapshotJson.TryParse(json.Trim(), out var snap, out var why))
            {
                return snap;
            }

            Console.Error.WriteLine($"    bad snapshot: {why}");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    could not run {probe} ({ex.GetType().Name})");
            return null;
        }
    }

    private static string[] ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } l)
        {
            lines.Add(l);
        }

        return lines.ToArray();
    }

    private static string DefaultProbe()
    {
        return System.IO.Path.Combine(AppContext.BaseDirectory, "live-probe.exe");
    }

    private static string? ArgStr(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[i + 1] : null;
    }

    private static int? ArgInt(string[] a, string name)
    {
        return int.TryParse(ArgStr(a, name), out var v) ? v : null;
    }

    private static string[] ArgMany(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        if (i < 0)
        {
            return [];
        }

        var values = new List<string>();
        for (var j = i + 1; j < a.Length && !a[j].StartsWith("--"); j++)
        {
            values.Add(a[j]);
        }

        return [.. values];
    }

    public static string[] LobbyMatchCommands(string[] args)
    {
        var commands = new List<string>();

        if (ArgStr(args, "--name") is { } name)
        {
            commands.Add($"name {name}");
        }

        if (ArgStr(args, "--challenge") is { } challenge)
        {
            commands.Add($"challenge {challenge}");
        }

        var shapeWidth = ArgInt(args, "--board-width");
        var shapeHeight = ArgInt(args, "--board-height");
        var shapeDepth = ArgInt(args, "--placement-rows");
        if (shapeWidth is not null || shapeHeight is not null || shapeDepth is not null)
        {
            var w = shapeWidth ?? Preset.BoardSide;
            var h = shapeHeight ?? Preset.BoardSide;
            commands.Add(shapeDepth is { } d ? $"shape {w} {h} {d}" : $"shape {w} {h}");
        }

        if (ArgMany(args, "--board") is { Length: > 0 } board)
        {
            commands.Add($"board {string.Join(' ', board)}");
        }

        var victory = ArgInt(args, "--victory-points") ?? Preset.RuleNotSet;
        var draft = ArgInt(args, "--draft-points") ?? Preset.RuleNotSet;
        if (victory != Preset.RuleNotSet || draft != Preset.RuleNotSet)
        {
            commands.Add($"rules {victory} {draft}");
        }

        if (ArgStr(args, "--first") is { } first)
        {
            commands.Add($"first {first}");
        }

        return [.. commands];
    }

    private static async Task<bool> Until(Func<bool> ok, CancellationToken token, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (ok())
            {
                return true;
            }

            await Task.Delay(15, token);
        }

        return ok();
    }

    private static string HashLine(Frame f)
    {
        return $"  <- hash for turn {f.Turn}: pieces {FrameLimits.Safe(f.PieceHash, 64)} " +
               $"terrain {FrameLimits.Safe(f.TerrainHash, 64)}";
    }

    internal static string? TheirArmyLine(Lobby lobby)
    {
        if (lobby.Remote is not { Army.Count: > 0 } remote)
        {
            return null;
        }

        return $"  <- their army: {string.Join(' ', remote.Army)}";
    }

    private static string SetupSummary(Frame f, Lobby lobby)
    {
        var challenge = lobby.Challenge is null ? "" : $", challenge {FrameLimits.Safe(lobby.Challenge, 64)}";
        var board = lobby.Board is { Count: > 0 } ? ", custom board" : "";
        var rules = lobby.VictoryPoints != Preset.RuleNotSet || lobby.DraftPoints != Preset.RuleNotSet
            ? $", rules {lobby.VictoryPoints}/{lobby.DraftPoints}"
            : "";

        var first = $", first {lobby.First}";
        return $"  <- their setup: {f.Army?.Count ?? 0} machine(s){challenge}{board}{rules}{first}";
    }

}
