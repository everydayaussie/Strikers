using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Strikers.Netplay;

internal sealed class Peer(string host, int port, string room, int preferredSeat = -1, string? routingId = null)
{
    private TcpClient? _client;
    private StreamWriter? _writer;
    private int _seat = -1;

    private SecureChannel? _channel;
    private ECDiffieHellman? _ephemeral;

    private readonly List<Frame> _outbox = [];

    private DateTime _holdingSince = DateTime.UtcNow;

    internal enum Exchange
    {
        Idle,
        Initiating,
        Responding,
    }

    private Exchange _exchange;
    private bool _exchangedOnThisConnection;

    private byte[]? _myCommitment;
    private bool _myBound;

    private byte[]? _peerCommitment;

    public byte[]? Binding { get; set; }

    public bool RequireBinding { get; set; }

    private byte[]? _channelBinding;
    public int ChannelGeneration { get; private set; }

    private byte[]? _derivedFrom;

    private readonly List<byte[]> _retiredKeys = [];

    private int _exchangesDone;

    private readonly object _sendGate = new();

    private bool _answeringNewKey;

    private readonly string _code = room;

    private readonly string _routingId = routingId ?? RoomId.For(room);

    public string RoutingId
    {
        get { return _routingId; }
    }

    public string? Fingerprint { get; private set; }

    public SessionRole LocalRole { get; set; } = SessionRole.Play;
    public SessionRole? RemoteRole { get; private set; }

    public string? LocalName { get; set; }
    public string? RemoteName { get; private set; }

    public int Seat
    {
        get
        {
            return _seat;
        }
    }

    public event Action<Frame>? Received;

    public event Action? ChannelBuilt;

    public bool Halted { get; private set; }
    public string? HaltReason { get; private set; }

    public async Task Run(CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!token.IsCancellationRequested && !Halted)
        {
            try
            {
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(host, port, token);
                _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

                _channel = null;
                _answeringNewKey = false;
                _channelBinding = null;
                _ephemeral?.Dispose();
                _ephemeral = null;
                Fingerprint = null;
                _exchange = Exchange.Idle;
                _exchangedOnThisConnection = false;
                _myCommitment = null;
                _peerCommitment = null;

                _derivedFrom = null;
                _retiredKeys.Clear();
                _exchangesDone = 0;
                RemoteRole = null;

                RemoteName = null;
                lock (_outbox)
                {
                    _outbox.Clear();
                    _holdingSince = DateTime.UtcNow;
                }

                Send(new Frame
                {
                    Kind = MsgKind.Hello,
                    Room = _routingId,
                    Seat = _seat >= 0 ? _seat : preferredSeat,
                    ResumeFrom = -1,
                });

                backoff = TimeSpan.FromSeconds(1);
                using var reader = new StreamReader(_client.GetStream(), Encoding.UTF8);

                var rate = new RateLimiter(FrameLimits.MaxFramesPerWindow, FrameLimits.RateWindow);

                while (!token.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await ReadLineBoundedAsync(reader, FrameLimits.MaxLine, token);
                    }
                    catch (InvalidDataException e)
                    {
                        HaltAndTell(e.Message);
                        break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (!rate.Allow(DateTime.UtcNow))
                    {
                        HaltAndTell($"more than {FrameLimits.MaxFramesPerWindow} frames in " +
                                    $"{FrameLimits.RateWindow.TotalSeconds:0} seconds");
                        break;
                    }

                    var frame = Protocol.Decode(line);
                    if (frame is null)
                    {
                        continue;
                    }

                    var oversize = FrameLimits.Refuse(frame);
                    if (oversize is not null)
                    {
                        HaltAndTell($"the other side sent {oversize}");
                        break;
                    }

                    if (Halted)
                    {
                        break;
                    }

                    if (frame.Kind == MsgKind.Paired)
                    {
                        if (_seat < 0)
                        {
                            if (frame.Seat is not (0 or 1))
                            {
                                HaltAndTell($"the relay assigned an impossible seat ({frame.Seat})");
                                break;
                            }

                            _seat = frame.Seat;
                        }

                        Console.WriteLine($"  paired, seat {_seat}");

                        if (_exchange == Exchange.Initiating)
                        {
                            SendCommitment();
                        }
                        else if (!_exchangedOnThisConnection)
                        {
                            BeginInitiating();
                        }

                        continue;
                    }

                    if (frame.Seat >= 0 && frame.Seat == _seat)
                    {
                        continue;
                    }

                    if (frame.Kind == MsgKind.KeyCommit)
                    {
                        var commitment = Convert.FromBase64String(frame.Commit!);
                        if (JudgeCommitment(_exchange, _seat, _peerCommitment, commitment) == CommitVerdict.Ignore)
                        {
                            continue;
                        }

                        _ephemeral?.Dispose();
                        _ephemeral = KeyExchange.Begin();
                        _peerCommitment = commitment;
                        _myBound = Binding is not null;
                        _exchange = Exchange.Responding;
                        _exchangedOnThisConnection = true;
                        lock (_sendGate)
                        {
                            _answeringNewKey = _channel is not null;
                        }

                        Send(new Frame
                        {
                            Kind = MsgKind.KeyEx,
                            PublicKey = Convert.ToBase64String(KeyExchange.PublicBlob(_ephemeral)),
                            Bound = _myBound,
                        });
                        continue;
                    }

                    if (frame.Kind == MsgKind.KeyEx)
                    {
                        if (_exchange == Exchange.Initiating)
                        {
                            Send(new Frame
                            {
                                Kind = MsgKind.KeyEx,
                                PublicKey = Convert.ToBase64String(KeyExchange.PublicBlob(_ephemeral!)),
                                Bound = _myBound,
                            });
                        }
                        else if (_exchange == Exchange.Responding)
                        {
                            if (RevealProblem(frame.PublicKey, frame.Bound, _peerCommitment) is { } broken)
                            {
                                HaltAndTell(broken);
                                break;
                            }
                        }
                        else
                        {
                            continue;
                        }

                        _exchange = Exchange.Idle;
                        if (!TryCompleteKeyExchange(frame))
                        {
                            if (!Halted)
                            {
                                HaltAndTell("the other side's key exchange could not be completed");
                            }

                            break;
                        }

                        continue;
                    }

                    if (frame.Kind == MsgKind.Sealed)
                    {
                        var opened = _channel?.Open(frame.Box ?? "");
                        var inner = opened is null ? null : Protocol.Decode(opened);
                        if (inner is null)
                        {
                            if (UnopenedIsStraggler(_channel is not null, _exchange))
                            {
                                Console.WriteLine("  dropped a frame sealed under the other side's previous key");
                                continue;
                            }

                            HaltAndTell("a sealed frame that could not be opened: altered, replayed, " +
                                        "reordered, or sent before the key exchange");
                            break;
                        }

                        inner.Seat = frame.Seat;
                        inner.Seq = frame.Seq;
                        frame = inner;

                        var innerOversize = FrameLimits.Refuse(frame);
                        if (innerOversize is not null)
                        {
                            HaltAndTell($"the other side sent {innerOversize}");
                            break;
                        }
                    }
                    else if (frame.Kind is not (MsgKind.Paired or MsgKind.Halt or MsgKind.Left))
                    {
                        HaltAndTell(_channel is null
                            ? $"an unencrypted {frame.Kind} frame before the key exchange"
                            : $"an unencrypted {frame.Kind} frame after the key exchange");
                        break;
                    }

                    if (frame.Kind == MsgKind.Left)
                    {
                        RemoteRole = null;
                        RemoteName = null;
                        Console.WriteLine("  <- the other player left");
                    }

                    if (frame.Kind == MsgKind.Halt)
                    {
                        var why = FrameLimits.Safe(frame.Reason);
                        Halt(RelayedHaltReason(why.Length > 0 ? why : "peer halted"));
                        return;
                    }

                    if (frame.Kind == MsgKind.Role)
                    {
                        RemoteRole = frame.Role;

                        if (Names.FromPeer(frame.Name) is { } named)
                        {
                            RemoteName = named;
                        }
                    }

                    if (HandlerFault(() => Received?.Invoke(frame)) is { } fault)
                    {
                        HaltAndTell(fault);
                        break;
                    }

                    if (Halted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                Console.WriteLine($"  link down ({ex.GetType().Name}); retrying in {backoff.TotalSeconds:0}s");
            }
            finally
            {
                _client?.Dispose();
                _writer = null;
            }

            if (Halted || token.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(backoff, token);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
        }
    }

    internal static string? HandlerFault(Action handle)
    {
        try
        {
            handle();
            return null;
        }
        catch (Exception e) when (e is not (OperationCanceledException or SocketException or IOException))
        {
            return $"a frame handler stopped on an error ({e.GetType().Name}), so nothing more can be received safely";
        }
    }

    private void BeginInitiating()
    {
        _ephemeral?.Dispose();
        _ephemeral = KeyExchange.Begin();
        _myBound = Binding is not null;
        _myCommitment = KeyExchange.Commitment(KeyExchange.PublicBlob(_ephemeral), _myBound);
        _exchange = Exchange.Initiating;
        _exchangedOnThisConnection = true;
        SendCommitment();
    }

    private void SendCommitment()
    {
        Send(new Frame
        {
            Kind = MsgKind.KeyCommit,
            Commit = Convert.ToBase64String(_myCommitment!),
            Bound = _myBound,
        });
    }

    internal enum CommitVerdict
    {
        Ignore,
        Respond,
    }

    internal static CommitVerdict JudgeCommitment(Exchange state, int seat, byte[]? answering, byte[] arriving)
    {
        if (state == Exchange.Initiating && seat == 0)
        {
            return CommitVerdict.Ignore;
        }

        if (state == Exchange.Responding && answering is not null && answering.SequenceEqual(arriving))
        {
            return CommitVerdict.Ignore;
        }

        return CommitVerdict.Respond;
    }

    internal static string? RevealProblem(string? publicKey, bool bound, byte[]? committed)
    {
        if (committed is null)
        {
            return "a key exchange revealed with no commitment before it";
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(publicKey ?? "");
        }
        catch (FormatException)
        {
            return "the other side's key does not match the commitment it sent";
        }

        if (!KeyExchange.Commitment(key, bound).SequenceEqual(committed))
        {
            return "the other side's key does not match the commitment it sent";
        }

        return null;
    }

    internal enum BindVerdict
    {
        Open,
        Bound,
        OpenDroppingBinding,
        Refuse,
    }

    internal static BindVerdict JudgeBinding(bool ourBound, bool peerBound, bool required)
    {
        if (ourBound && peerBound)
        {
            return BindVerdict.Bound;
        }

        if (required)
        {
            return BindVerdict.Refuse;
        }

        return ourBound ? BindVerdict.OpenDroppingBinding : BindVerdict.Open;
    }

    public byte[]? BindToChannel(int generation)
    {
        if (generation != ChannelGeneration || _channelBinding is null)
        {
            return null;
        }

        Binding = _channelBinding;
        return Binding;
    }

    private bool TryCompleteKeyExchange(Frame f)
    {
        if (_ephemeral is null || f.PublicKey is null)
        {
            return false;
        }

        var binding = JudgeBinding(_myBound, f.Bound, RequireBinding);
        if (binding == BindVerdict.Refuse)
        {
            HaltAndTell("the other side's key exchange is not bound to the session the players compared");
            return false;
        }

        if (TooManyExchanges(_exchangesDone))
        {
            HaltAndTell($"the other side ran more than {MaxExchangesPerConnection} key exchanges on one connection");
            return false;
        }

        var offer = JudgeKey(f.PublicKey, _channel is null ? null : _derivedFrom, _retiredKeys,
                             out var theirs);
        switch (offer)
        {
            case KeyOffer.Malformed:
                return false;

            case KeyOffer.SameAsCurrent:
                if (CountsAndReleases(offer))
                {
                    _exchangesDone++;
                    ReleaseHeldFrames();
                }

                return true;

            case KeyOffer.Retired:
                HaltAndTell("the other PC went back to a key exchange it had already replaced");
                return false;
        }

        if (binding == BindVerdict.OpenDroppingBinding)
        {
            Binding = null;
            Console.WriteLine("  the other side started over, so this session is compared again");
        }

        var keys = KeyExchange.Complete(_code, _ephemeral, theirs, isHost: _seat == 0,
                                        binding == BindVerdict.Bound ? Binding : null);
        if (keys is null)
        {
            return false;
        }

        _exchangesDone++;
        lock (_sendGate)
        {
            _channel = new SecureChannel(keys);
            _answeringNewKey = false;
            _channelBinding = keys.Binding;
            ChannelGeneration++;
            _retiredKeys.Add(theirs);
            _derivedFrom = theirs;
            Fingerprint = keys.Fingerprint;
            Console.WriteLine($"  encrypted, fingerprint {Fingerprint}");
            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine("  (read that to the other player if you want to be certain nobody is in the middle)");
            }

            Send(new Frame { Kind = MsgKind.Role, Role = LocalRole, Name = LocalName });

            Frame[] held;
            lock (_outbox)
            {
                held = [.. KeepOnNewKey(_outbox)];
                _outbox.Clear();
                _holdingSince = DateTime.UtcNow;
            }

            foreach (var queued in held)
            {
                Send(queued);
            }
        }

        ChannelBuilt?.Invoke();
        return true;
    }

    internal enum KeyOffer
    {
        Malformed,
        SameAsCurrent,
        Retired,
        Fresh,
    }

    internal static KeyOffer JudgeKey(string? publicKey, byte[]? current,
                                      IEnumerable<byte[]> retired, out byte[] decoded)
    {
        decoded = [];
        if (publicKey is null)
        {
            return KeyOffer.Malformed;
        }

        try { decoded = Convert.FromBase64String(publicKey); }
        catch (FormatException) { return KeyOffer.Malformed; }

        if (current is not null && current.SequenceEqual(decoded))
        {
            return KeyOffer.SameAsCurrent;
        }

        foreach (var key in retired)
        {
            if (key.SequenceEqual(decoded))
            {
                return KeyOffer.Retired;
            }
        }

        return KeyOffer.Fresh;
    }

    internal static List<Frame> KeepOnNewKey(IEnumerable<Frame> queued)
    {
        var kept = new List<Frame>();
        foreach (var f in queued)
        {
            if (f.Kind == MsgKind.Confirmed)
            {
                continue;
            }

            kept.Add(f);
        }

        return kept;
    }

    public void Send(Frame f)
    {
        string? holdProblem = null;
        lock (_sendGate)
        {
            Frame? outgoing = f;
            if (f.Kind is not (MsgKind.Hello or MsgKind.KeyEx or MsgKind.KeyCommit))
            {
                var channel = _channel;

                if (SendHolds(channel is not null, _answeringNewKey) || channel is null)
                {
                    int held;
                    lock (_outbox)
                    {
                        if (_outbox.Count == 0)
                        {
                            _holdingSince = DateTime.UtcNow;
                        }

                        _outbox.Add(f);
                        held = _outbox.Count;
                    }

                    holdProblem = HoldProblem(held, DateTime.UtcNow - _holdingSince);
                    outgoing = null;
                }
                else
                {
                    outgoing = new Frame { Kind = MsgKind.Sealed, Box = channel.Seal(Protocol.Encode(f)) };
                }
            }

            if (outgoing is not null)
            {
                try { _writer?.WriteLine(Protocol.Encode(outgoing)); }
                catch (Exception e) when (IsLinkError(e)) { }
            }
        }

        if (holdProblem is not null)
        {
            HaltAndTell(holdProblem);
        }
    }

    internal const int MaxHeldFrames = 64;
    internal static readonly TimeSpan HoldsFramesFor = TimeSpan.FromSeconds(30);

    internal static string? HoldProblem(int held, TimeSpan holding)
    {
        if (held >= MaxHeldFrames)
        {
            return $"the other side's key exchange did not finish, and {MaxHeldFrames} frames are waiting on it";
        }

        if (holding >= HoldsFramesFor)
        {
            return "the other side's key exchange did not finish within " +
                   $"{HoldsFramesFor.TotalSeconds:0} seconds, so nothing of ours could be sent";
        }

        return null;
    }

    internal static bool CountsAndReleases(KeyOffer offer)
    {
        return offer is KeyOffer.SameAsCurrent;
    }

    private void ReleaseHeldFrames()
    {
        lock (_sendGate)
        {
            _answeringNewKey = false;
            Frame[] held;
            lock (_outbox)
            {
                held = [.. _outbox];
                _outbox.Clear();
                _holdingSince = DateTime.UtcNow;
            }

            foreach (var queued in held)
            {
                Send(queued);
            }
        }
    }

    internal const int MaxExchangesPerConnection = 16;

    internal static bool TooManyExchanges(int retired)
    {
        return retired >= MaxExchangesPerConnection;
    }

    internal static bool SendHolds(bool haveChannel, bool answeringNewKey)
    {
        return !haveChannel || answeringNewKey;
    }

    internal static bool UnopenedIsStraggler(bool haveChannel, Exchange state)
    {
        return !haveChannel && state != Exchange.Idle;
    }

    internal static bool IsLinkError(Exception e)
    {
        return e is IOException or ObjectDisposedException;
    }

    internal void DropLink()
    {
        _client?.Dispose();
    }

    public void SendMoves(IEnumerable<Move> moves, bool final = false)
    {
        Send(new Frame { Kind = MsgKind.Move, Moves = moves.ToList(), Final = final });
    }

    public void SendMove(Move move)
    {
        SendMoves([move]);
    }

    public void SendPlace(int index, Placement square)
    {
        Send(new Frame { Kind = MsgKind.Place, PlaceIdx = index, Place = square });
    }

    public void SendRematch()
    {
        Send(new Frame { Kind = MsgKind.Rematch });
    }

    public void SendHash(int turn, BoardSnapshot snapshot)
    {
        Send(new Frame
        {
            Kind = MsgKind.Hash,
            Turn = turn,
            PieceHash = BoardHash.Pieces(snapshot, _seat),
            TerrainHash = BoardHash.Terrain(snapshot, _seat),
        });
    }

    internal static string? HashCompareProblem(int seat, int candidateCount, int turn)
    {
        if (seat < 0)
        {
            return "hash compared before pairing, no seat, so no canonical frame to compare in";
        }

        if (candidateCount == 0)
        {
            return $"the peer's hash for turn {turn} arrived with no board of ours to compare it against";
        }

        return null;
    }

    public bool CheckHash(Frame theirs, IReadOnlyList<BoardSnapshot> mine, int turn)
    {
        if (HashCompareProblem(_seat, mine.Count, turn) is { } cannot)
        {
            Halt(cannot);
            return false;
        }

        if (HashMatches(theirs, mine))
        {
            return true;
        }

        return CheckHash(theirs, mine.Count > 1 ? mine[1] : mine[^1], turn);
    }

    public bool HashMatches(Frame theirs, IReadOnlyList<BoardSnapshot> mine)
    {
        if (_seat < 0)
        {
            return false;
        }

        foreach (var board in mine)
        {
            if (theirs.PieceHash == BoardHash.Pieces(board, _seat) &&
                theirs.TerrainHash == BoardHash.Terrain(board, _seat))
            {
                return true;
            }
        }

        return false;
    }

    public bool CheckHash(Frame theirs, BoardSnapshot mine, int turn)
    {
        if (_seat < 0)
        {
            Halt("hash compared before pairing, no seat, so no canonical frame to compare in");
            return false;
        }

        var ourPieces = BoardHash.Pieces(mine, _seat);
        var ourTerrain = BoardHash.Terrain(mine, _seat);

        var pieceOk = theirs.PieceHash == ourPieces;
        var terrainOk = theirs.TerrainHash == ourTerrain;
        if (pieceOk && terrainOk)
        {
            return true;
        }

        var what = !pieceOk && !terrainOk ? "pieces and terrain" : !pieceOk ? "pieces" : "terrain";

        var reason = $"desync after turn {turn}: {what} differ (raised on seat {_seat})\n" +
                     $"    seat {_seat}  pieces {ourPieces}  terrain {ourTerrain}\n" +
                     $"    seat {1 - _seat}  pieces {FrameLimits.Safe(theirs.PieceHash, 32)}  " +
                     $"terrain {FrameLimits.Safe(theirs.TerrainHash, 32)}";

        Halt(reason);
        Send(new Frame { Kind = MsgKind.Halt, Reason = reason });
        return false;
    }

    internal static async Task<string?> ReadLineBoundedAsync(StreamReader reader, int max, CancellationToken token)
    {
        var line = new StringBuilder();
        var one = new char[1];

        while (true)
        {
            var n = await reader.ReadAsync(one, token);
            if (n == 0)
            {
                return line.Length > 0 ? line.ToString() : null;
            }

            if (one[0] == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append(one[0]);
            if (line.Length > max)
            {
                throw new InvalidDataException(
                    $"the other side sent more than {max} characters with no end of line");
            }
        }
    }

    internal static string RelayedHaltReason(string why)
    {
        return "the other PC stopped the match: " + why;
    }

    public void HaltAndTell(string reason)
    {
        if (Halted)
        {
            return;
        }

        Halt(reason);
        Send(new Frame { Kind = MsgKind.Halt, Reason = reason });
    }

    public static Action<string>? OnHalt;

    private void Halt(string reason)
    {
        Halted = true;
        HaltReason = reason;
        Console.Error.WriteLine($"HALT: {reason}");
        try { OnHalt?.Invoke(reason); } catch { }
    }
}


