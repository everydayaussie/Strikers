using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Strikers.Netplay;

internal sealed class TunnelClient
{
    private const int ControlPort = 7835;
    private const int MaxFrame = 256;
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(3);

    internal static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);

    private const int MaxForwards = 16;

    private readonly string _server;
    private readonly int _controlPort;
    private readonly string? _secret;
    private readonly int _relayPort;
    private readonly TimeSpan _silence;
    private readonly ForwardPool _pool;

    private TunnelClient(string server, int controlPort, string? secret, int relayPort,
                         Func<IPEndPoint, bool> seated, TimeSpan silence)
    {
        _server = server;
        _controlPort = controlPort;
        _secret = secret;
        _relayPort = relayPort;
        _silence = silence;
        _pool = new ForwardPool(MaxForwards, seated);
    }

    private const int SpelledBytes = 32;

    internal static string Says(string what, object? saidByTheServer)
    {
        var bytes = Encoding.UTF8.GetBytes(saidByTheServer?.ToString() ?? "");
        var spelled = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, SpelledBytes));
        var more = bytes.Length > SpelledBytes ? "..." : "";
        return $"{what}: {spelled}{more}";
    }

    internal sealed class Fault(string message) : InvalidOperationException(message)
    {
    }

    internal static string Why(Exception ex)
    {
        if (ex is Fault)
        {
            return ex.Message;
        }

        var socket = ex as SocketException ?? ex.InnerException as SocketException;
        if (socket is not null)
        {
            return $"{ex.GetType().Name} {socket.SocketErrorCode}";
        }

        return ex.GetType().Name;
    }

    internal static string DiedLine(Exception ex)
    {
        return $"  Warning: the tunnel DIED, {FrameLimits.Safe(Why(ex), 200)}";
    }

    internal static string EndedLine(Guid id, Exception ex)
    {
        return $"  tunnel: connection {id} ended, {FrameLimits.Safe(Why(ex), 200)}";
    }

    public static Task Start(int relayPort, string server, string? secret, Func<IPEndPoint, bool> seated,
                             CancellationToken ct)
    {
        var client = new TunnelClient(server, ControlPort, secret, relayPort, seated, Silence);
        return Task.Run(() => client.RunAsync(ct), ct);
    }

    internal static Task RunAgainst(int controlPort, TimeSpan silence, CancellationToken ct)
    {
        var client = new TunnelClient("127.0.0.1", controlPort, null, 0, _ => false, silence);
        return client.RunAsync(ct);
    }

    internal static Heard Judge(string kind)
    {
        return kind switch
        {
            "Heartbeat" => Heard.Ignore,
            "Connection" => Heard.Forward,
            _ => Heard.Die,
        };
    }

    internal enum Heard
    {
        Ignore,
        Forward,
        Die,
    }

    internal static int? PublicPort(JsonNode? value)
    {
        if (value is JsonValue number && number.TryGetValue<int>(out var port) && port is >= 1 and <= 65535)
        {
            return port;
        }

        return null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var control = await DialAsync(ct);
            await control.SendAsync(Msg("Hello", 0), ct);

            var (kind, value) = Decode(await ReceiveWithin(control, _silence, ct));
            var publicPort = kind switch
            {
                "Hello" => PublicPort(value)
                           ?? throw new Fault(Says("the server gave no usable port", value)),
                "Error" => throw new Fault(Says("server refused the tunnel", value)),
                "Challenge" => throw new Fault("the server wants a secret, pass --tunnel-secret"),
                _ => throw new Fault(Says("unexpected first message from the server", kind)),
            };

            Console.WriteLine($"\n  tunnel open: --server {_server}:{publicPort}\n");

            while (!ct.IsCancellationRequested)
            {
                var node = await ReceiveWithin(control, _silence, ct)
                           ?? throw new Fault("the server closed the tunnel");
                var (k, v) = Decode(node);
                switch (Judge(k))
                {
                    case Heard.Ignore:
                        break;
                    case Heard.Forward:
                        Forward(Guid.Parse(v!.GetValue<string>()), ct);
                        break;
                    default:
                        throw new Fault(k == "Error"
                            ? Says("server error", v)
                            : Says("unexpected message from the server", k));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n{DiedLine(ex)}");
            Console.Error.WriteLine("  the public address is gone, and restarting mints a NEW one " +
                                    "that has to be re-shared.\n");
        }
    }

    private static readonly TimeSpan RefusalPrintEvery = TimeSpan.FromSeconds(10);
    private DateTime _lastRefusalPrint = DateTime.MinValue;
    private int _refusedSincePrint;

    private void Forward(Guid id, CancellationToken ct)
    {
        var slot = _pool.Admit(ct);
        if (slot is null)
        {
            _refusedSincePrint++;
            var now = DateTime.UtcNow;
            if (now - _lastRefusalPrint >= RefusalPrintEvery)
            {
                Console.Error.WriteLine($"  tunnel: refusing connections, {MaxForwards} in use " +
                                        $"({_refusedSincePrint} refused)");
                _lastRefusalPrint = now;
                _refusedSincePrint = 0;
            }

            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var remote = await DialAsync(slot.Token);
                await remote.SendAsync(Msg("Accept", id.ToString()), slot.Token);

                var (relayClient, relayEnd) = await ConnectToRelay(_relayPort, slot.Token);
                using var local = relayClient;
                slot.Local = relayEnd;
                var localStream = local.GetStream();

                var spill = remote.Leftover();
                if (spill.Length > 0)
                {
                    await localStream.WriteAsync(spill, slot.Token);
                }

                var a = remote.Stream.CopyToAsync(localStream, slot.Token);
                var b = localStream.CopyToAsync(remote.Stream, slot.Token);
                await Task.WhenAny(a, b);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(EndedLine(id, ex));
            }
            finally
            {
                _pool.Release(slot);
            }
        });
    }

    internal static async Task<(TcpClient Client, IPEndPoint Local)> ConnectToRelay(int relayPort,
                                                                                    CancellationToken ct)
    {
        var local = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await local.ConnectAsync(IPAddress.Loopback, relayPort, ct);
            return (local, (IPEndPoint)local.Client.LocalEndPoint!);
        }
        catch
        {
            local.Dispose();
            throw;
        }
    }

    private static async Task<JsonNode?> ReceiveWithin(Framed framed, TimeSpan silence, CancellationToken ct)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(silence);
        try
        {
            return await framed.RecvAsync(quiet.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Fault($"the server said nothing for {silence.TotalSeconds:0} s");
        }
    }

    private async Task<Framed> DialAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(NetworkTimeout);
            try
            {
                await tcp.ConnectAsync(_server, _controlPort, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();

                throw new Fault(
                    $"could not reach {_server}:{_controlPort} in {NetworkTimeout.TotalSeconds:0}s, " +
                    "the server may be down, or DNS may be filtered (try --tunnel-server with an " +
                    "address from a public resolver)");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        var framed = new Framed(tcp);
        if (_secret is not null)
        {
            await Prove(framed, _secret, _silence, ct);
        }

        return framed;
    }

    internal static async Task Prove(Framed framed, string secret, TimeSpan silence, CancellationToken ct)
    {
        try
        {
            var (kind, value) = Decode(await ReceiveWithin(framed, silence, ct));
            if (kind != "Challenge")
            {
                throw new Fault(Says("expected an authentication challenge, got", kind));
            }

            await framed.SendAsync(Msg("Authenticate", Answer(secret, value!.GetValue<string>())), ct);
        }
        catch
        {
            framed.Dispose();
            throw;
        }
    }

    public static string Answer(string secret, string challenge)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, ChallengeBytes(challenge)));
    }

    public static byte[] ChallengeBytes(string uuid)
    {
        return Convert.FromHexString(uuid.Replace("-", ""));
    }

    public static JsonNode Msg(string variant, int payload)
    {
        return new JsonObject { [variant] = JsonValue.Create(payload) };
    }

    public static JsonNode Msg(string variant, string payload)
    {
        return new JsonObject { [variant] = JsonValue.Create(payload) };
    }

    public static (string Kind, JsonNode? Value) Decode(JsonNode? node)
    {
        return node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => (s, null),
            JsonObject o when o.Count == 1 => (o.First().Key, o.First().Value),
            _ => ("", null),
        };
    }

    internal sealed class ForwardPool(int max, Func<IPEndPoint, bool> seated, TimeSpan? grace = null)
    {
        private readonly TimeSpan _grace = grace ?? EvictionGrace;

        private readonly List<Slot> _open = [];
        private readonly Lock _gate = new();

        internal sealed class Slot(CancellationToken ct)
        {
            public readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            public readonly DateTime Admitted = DateTime.UtcNow;

            private IPEndPoint? _local;

            public CancellationToken Token
            {
                get
                {
                    return Cts.Token;
                }
            }

            public IPEndPoint? Local
            {
                get
                {
                    return Volatile.Read(ref _local);
                }
                set
                {
                    Volatile.Write(ref _local, value);
                }
            }
        }

        internal static readonly TimeSpan EvictionGrace = TimeSpan.FromSeconds(5);

        internal static int Evict(IReadOnlyList<bool> seatedOldestFirst, IReadOnlyList<TimeSpan> ageOldestFirst,
                                  TimeSpan grace)
        {
            for (var i = 0; i < seatedOldestFirst.Count; i++)
            {
                if (!seatedOldestFirst[i] && ageOldestFirst[i] >= grace)
                {
                    return i;
                }
            }

            return -1;
        }

        public Slot? Admit(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_open.Count >= max)
                {
                    var flags = new List<bool>(_open.Count);
                    var ages = new List<TimeSpan>(_open.Count);
                    var now = DateTime.UtcNow;
                    foreach (var open in _open)
                    {
                        flags.Add(open.Local is { } local && seated(local));
                        ages.Add(now - open.Admitted);
                    }

                    var index = Evict(flags, ages, _grace);
                    if (index < 0)
                    {
                        return null;
                    }

                    _open[index].Cts.Cancel();
                    _open.RemoveAt(index);
                }

                var admitted = new Slot(ct);
                _open.Add(admitted);
                return admitted;
            }
        }

        public void Release(Slot slot)
        {
            lock (_gate)
            {
                _open.Remove(slot);
            }

            slot.Cts.Dispose();
        }
    }

    internal sealed class Framed : IDisposable
    {
        private readonly TcpClient _tcp;
        private byte[] _buf = new byte[MaxFrame * 2];
        private int _end;
        private int _start;

        public Framed(TcpClient tcp) => _tcp = tcp;

        public NetworkStream Stream
        {
            get
            {
                return _tcp.GetStream();
            }
        }

        public async Task<JsonNode?> RecvAsync(CancellationToken ct)
        {
            while (true)
            {
                var nul = Array.IndexOf(_buf, (byte)0, _start, _end - _start);
                if (nul >= 0)
                {
                    var json = Encoding.UTF8.GetString(_buf, _start, nul - _start);
                    _start = nul + 1;
                    return JsonNode.Parse(json);
                }

                if (_end - _start > MaxFrame)
                {
                    throw new Fault(
                        $"the server sent more than {MaxFrame} bytes with no frame delimiter");
                }

                if (_start > 0)
                {
                    Array.Copy(_buf, _start, _buf, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                if (_end == _buf.Length)
                {
                    Array.Resize(ref _buf, _buf.Length * 2);
                }

                var n = await Stream.ReadAsync(_buf.AsMemory(_end), ct);
                if (n == 0)
                {
                    return null;
                }

                _end += n;
            }
        }

        public byte[] Leftover()
        {
            return _buf[_start.._end];
        }

        public async Task SendAsync(JsonNode msg, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await Stream.WriteAsync(bytes, ct);
            await Stream.WriteAsync(new byte[] { 0 }, ct);
        }

        public void Dispose()
        {
            _tcp.Dispose();
        }
    }
}
