using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Strikers.Netplay;

internal sealed class Relay
{
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    internal static readonly TimeSpan HelloWithin = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan SendWithin = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private readonly HashSet<IPEndPoint> _seatedPeers = [];

    private readonly string? _onlyRoom;
    private readonly TimeSpan _helloWithin;
    private readonly TimeSpan _sendWithin;
    private long _toldOtherVersionAt;

    internal static readonly TimeSpan OtherVersionQuiet = TimeSpan.FromSeconds(30);

    internal static string OtherVersionLine(int version)
    {
        return $"relay refused a player on another version of Strikers (protocol v{version})";
    }

    internal bool TellOtherVersion(DateTime now)
    {
        while (true)
        {
            var last = Interlocked.Read(ref _toldOtherVersionAt);
            if (last != 0 && now.Ticks - last < OtherVersionQuiet.Ticks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _toldOtherVersionAt, now.Ticks, last) == last)
            {
                return true;
            }
        }
    }

    public Relay(string? onlyRoom = null, TimeSpan? helloWithin = null, TimeSpan? sendWithin = null)
    {
        _onlyRoom = onlyRoom;
        _helloWithin = helloWithin ?? HelloWithin;
        _sendWithin = sendWithin ?? SendWithin;
    }

    internal static bool RoomAllowed(string? onlyRoom, string? asked)
    {
        if (onlyRoom is null)
        {
            return true;
        }

        return string.Equals(SafeRoomKey(onlyRoom), SafeRoomKey(asked), StringComparison.OrdinalIgnoreCase);
    }

    internal int RoomCount
    {
        get { lock (_gate) { return _rooms.Count; } }
    }

    internal bool Seated(IPEndPoint from)
    {
        var key = PeerKey(from);
        if (key is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _seatedPeers.Contains(key);
        }
    }

    internal static IPEndPoint? PeerKey(EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ip)
        {
            return null;
        }

        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        return new IPEndPoint(address, ip.Port);
    }

    internal static IPAddress BindFor(string? bindArg, bool tunnel)
    {
        if (bindArg is { } explicitAddress)
        {
            return IPAddress.Parse(explicitAddress);
        }

        return tunnel ? IPAddress.Loopback : IPAddress.Any;
    }

    internal static void Configure(TcpClient client, TimeSpan sendWithin)
    {
        client.NoDelay = true;
        client.SendTimeout = (int)sendWithin.TotalMilliseconds;
    }

    private sealed class Room
    {
        public string Code = "";
        public readonly Connection?[] Seats = new Connection?[2];
        public int NextSeq = 1;
    }

    internal sealed class Connection(TcpClient client)
    {
        public readonly TcpClient Client = client;
        public readonly StreamWriter Writer = new(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

        public readonly IPEndPoint? Key = PeerKey(client.Client.RemoteEndPoint);

        public void Send(Frame f)
        {
            try
            {
                Writer.WriteLine(Protocol.Encode(f));
            }
            catch (IOException)
            {
                Abandon();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Abandon()
        {
            try
            {
                Client.Client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public static string NewCode()
    {
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars);
    }

    public async Task Run(int port, CancellationToken token, IPAddress? bind = null)
    {
        var listener = new TcpListener(bind ?? IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"relay listening on {bind ?? IPAddress.Any}:{port}");
        await AcceptAll(listener, token);
    }

    internal async Task AcceptAll(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await NextClient(listener, token);
                _ = Task.Run(() => Serve(client, token), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<TcpClient> NextClient(TcpListener listener, CancellationToken token)
    {
        while (true)
        {
            try
            {
                return await listener.AcceptTcpClientAsync(token);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset
                                                or SocketError.ConnectionAborted)
            {
            }
        }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        Configure(client, _sendWithin);
        var conn = new Connection(client);
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
        var rate = new RateLimiter(FrameLimits.MaxFramesPerWindow, FrameLimits.RateWindow);

        Room? room = null;
        var seat = -1;

        var refused = false;

        void Refuse(string reason)
        {
            conn.Send(new Frame { Kind = MsgKind.Halt, Reason = reason });
            refused = true;
        }

        using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        helloDeadline.CancelAfter(_helloWithin);

        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await Peer.ReadLineBoundedAsync(reader, FrameLimits.LineCapFor(room is not null),
                                                           room is null ? helloDeadline.Token : token);
                }
                catch (InvalidDataException e)
                {
                    Refuse(e.Message);
                    break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Refuse($"no Hello within {_helloWithin.TotalSeconds:0} s");
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (!rate.Allow(DateTime.UtcNow))
                {
                    Refuse($"more than {FrameLimits.MaxFramesPerWindow} frames in " +
                           $"{FrameLimits.RateWindow.TotalSeconds:0} seconds");
                    break;
                }

                var frame = Protocol.Decode(line);
                if (frame is null)
                {
                    continue;
                }

                if (frame.Version != Protocol.Version)
                {
                    Refuse($"protocol v{frame.Version}, relay speaks v{Protocol.Version}");
                    if (TellOtherVersion(DateTime.UtcNow))
                    {
                        Console.WriteLine(OtherVersionLine(frame.Version));
                    }

                    break;
                }

                if (FrameLimits.Refuse(frame) is { } oversize)
                {
                    Refuse($"the connection sent {oversize}");
                    break;
                }

                if (frame.Kind == MsgKind.Hello)
                {
                    if (room is not null)
                    {
                        Refuse("a second Hello on one connection");
                        break;
                    }

                    if (!RoomAllowed(_onlyRoom, frame.Room))
                    {
                        Refuse("unknown room");
                        break;
                    }

                    (room, seat) = Join(conn, frame);
                    if (room is null)
                    {
                        Refuse("room full");
                        break;
                    }
                    continue;
                }

                if (room is null)
                {
                    continue;
                }

                Forward(room, seat, frame);
                if (frame.Kind == MsgKind.Bye)
                {
                    break;
                }
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (room is not null && seat >= 0)
            {
                lock (_gate)
                {
                    if (conn.Key is { } key)
                    {
                        _seatedPeers.Remove(key);
                    }

                    if (ReferenceEquals(room.Seats[seat], conn))
                    {
                        room.Seats[seat] = null;

                        room.Seats[1 - seat]?.Send(new Frame { Kind = MsgKind.Left, Room = room.Code, Seat = seat });
                    }

                    if (room.Seats[0] is null && room.Seats[1] is null)
                    {
                        _rooms.Remove(room.Code);
                    }
                }
            }

            await CloseGracefully(client, refused);
            var closed = CloseLine(seat, DateTime.UtcNow);
            if (closed is not null)
            {
                Console.WriteLine(closed);
            }
        }
    }

    internal string? CloseLine(int seat, DateTime now)
    {
        if (seat >= 0)
        {
            return $"  peer disconnected (seat {seat})";
        }

        var unseated = TellUnseated(now);
        if (unseated == 0)
        {
            return null;
        }

        return UnseatedLine(unseated);
    }

    internal static readonly TimeSpan UnseatedQuiet = TimeSpan.FromSeconds(10);

    private readonly Lock _unseatedGate = new();
    private DateTime _unseatedToldAt = DateTime.MinValue;
    private int _unseatedSinceTold;

    private int TellUnseated(DateTime now)
    {
        lock (_unseatedGate)
        {
            _unseatedSinceTold++;
            if (now - _unseatedToldAt < UnseatedQuiet)
            {
                return 0;
            }

            var count = _unseatedSinceTold;
            _unseatedToldAt = now;
            _unseatedSinceTold = 0;
            return count;
        }
    }

    private static string UnseatedLine(int count)
    {
        return count == 1
            ? "  a connection closed before it took a seat"
            : $"  {count} connections closed before they took a seat";
    }

    private static async Task CloseGracefully(TcpClient client, bool refused)
    {
        if (!refused)
        {
            client.Dispose();
            return;
        }

        try
        {
            client.Client.Shutdown(SocketShutdown.Send);

            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var scratch = new byte[1024];
            while (await client.Client.ReceiveAsync(scratch, SocketFlags.None, deadline.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException
                                      or InvalidOperationException or OperationCanceledException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    internal static string SafeRoomKey(string? room)
    {
        return FrameLimits.Safe(room, 64).Trim().ToUpperInvariant();
    }

    private (Room?, int) Join(Connection conn, Frame hello)
    {
        var code = SafeRoomKey(hello.Room);
        if (code.Length == 0)
        {
            return (null, -1);
        }

        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out var room))
            {
                room = new Room { Code = code };
                _rooms[code] = room;
                Console.WriteLine($"room {code} created");
            }

            var want = hello.Seat is 0 or 1 && room.Seats[hello.Seat] is null
                ? hello.Seat
                : Array.FindIndex(room.Seats, s => s is null);
            if (want < 0)
            {
                return (null, -1);
            }

            room.Seats[want] = conn;
            if (conn.Key is { } key)
            {
                _seatedPeers.Add(key);
            }

            Console.WriteLine($"room {code}: peer took seat {want}");

            conn.Send(new Frame { Kind = MsgKind.Paired, Room = code, Seat = want });

            if (room.Seats[0] is not null && room.Seats[1] is not null)
            {
                Console.WriteLine($"room {code}: both seats filled");
                for (var i = 0; i < 2; i++)
                {
                    room.Seats[i]!.Send(new Frame { Kind = MsgKind.Paired, Room = code, Seat = i, ResumeFrom = room.NextSeq - 1 });
                }
            }

            return (room, want);
        }
    }

    private void Forward(Room room, int seat, Frame frame)
    {
        lock (_gate)
        {
            frame.Seq = room.NextSeq++;
            frame.Seat = seat;
            room.Seats[1 - seat]?.Send(frame);
        }
    }
}
