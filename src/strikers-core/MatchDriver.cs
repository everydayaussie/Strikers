using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Strikers.Core;

public sealed class MatchDriver
{
    private readonly Action<Action> _post;
    private readonly List<Process> _running = [];
    private Process? _lobby;

    private Process? _relay;

    private readonly MatchLog? _log;

    public const string MatchClaimObject = @"Local\Strikers-match-running";

    private readonly string _claimName;

    private EventWaitHandle? _matchClaim;

    public MatchDriver(Action<Action> post, MatchLog? log = null, string? claimName = null)
    {
        _post = post;
        _log = log;
        _claimName = claimName ?? MatchClaimObject;
    }

    public string RoomCode { get; set; } = "";

    public bool AnythingRunning
    {
        get
        {
            return _running.Count > 0;
        }
    }

    private bool _starting;

    public int Generation { get; private set; }

    public bool BeginStart()
    {
        if (_starting || AnythingRunning)
        {
            return false;
        }

        if (!ClaimMatch())
        {
            Trouble?.Invoke("Another Strikers is running a match",
                            "One match at a time on this PC. Use the window that has the match, or stop it there first.");
            return false;
        }

        _starting = true;
        Generation++;
        return true;
    }

    private bool ClaimMatch()
    {
        if (_matchClaim is not null)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        EventWaitHandle claim;
        bool createdNew;
        try
        {
            claim = new EventWaitHandle(false, EventResetMode.ManualReset, _claimName, out createdNew);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException
                                      or IOException)
        {
            return false;
        }

        if (!createdNew)
        {
            claim.Dispose();
            return false;
        }

        _matchClaim = claim;
        return true;
    }

    private void ReleaseMatchClaim()
    {
        _matchClaim?.Dispose();
        _matchClaim = null;
    }

    public bool HoldsMatchClaim
    {
        get
        {
            return _matchClaim is not null;
        }
    }

    public bool StillWanted(int generation)
    {
        return generation == Generation && (_starting || AnythingRunning);
    }

    public void CancelStart()
    {
        _starting = false;
        ReleaseMatchClaim();
    }

    public bool HaveLobby
    {
        get
        {
            return _lobby is not null;
        }
    }

    public bool LobbyRunning
    {
        get
        {
            return _lobby is not null && _lobby.HasExited is false;
        }
    }

    public event Action<string>? Output;

    public event Action<string>? Echo;

    public event Action<string, string>? Trouble;

    public event Action? Started;

    public event Action? LobbyEnded;

    public event Action<int?>? PlayEnded;

    public static string ExitLine(bool play, int? code)
    {
        var which = play ? "netplay --play" : "netplay";
        var how = code is { } c ? $"code {c}" : "code unknown";
        return $"  {which} exited, {how}";
    }

    private void Ended(Process p, bool play, int? code)
    {
        if (!_running.Contains(p))
        {
            return;
        }

        Say(ExitLine(play, code));
        if (play)
        {
            PlayEnded?.Invoke(code);
        }
    }

    public static int? FreePort()
    {
        try
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    public void Spawn(IEnumerable<string> args, bool keepInput = false)
    {
        var exe = NetplayTool.Netplay();
        if (!File.Exists(exe))
        {
            Trouble?.Invoke("Cannot find netplay", "netplay.exe is missing. It should sit beside Strikers.exe.");
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = keepInput,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        Say($"> netplay {string.Join(' ', args)}");

        var spawnedUnder = Generation;
        var play = psi.ArgumentList.Contains("--play");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data, spawnedUnder);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data, spawnedUnder);
        p.Exited += (_, _) =>
        {
            int? code = null;
            try
            {
                p.WaitForExit(2000);
                code = p.ExitCode;
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
            {
            }

            _post(() => Ended(p, play, code));
        };

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _running.Add(p);
            Started?.Invoke();
            if (keepInput)
            {
                _lobby = p;
            }

            if (psi.ArgumentList.Contains("--relay"))
            {
                _relay = p;
            }
        }
        catch (Exception e)
        {
            Trouble?.Invoke("Could not start", $"netplay.exe did not start. {StartFailedReason(e)}".TrimEnd());
        }
    }

    public bool TypeAtLobby(IEnumerable<string> lines, string echo, string headline)
    {
        if (_lobby is null || _lobby.HasExited)
        {
            Trouble?.Invoke(headline, "Press Stop and start again.");
            return false;
        }

        try
        {
            foreach (var line in lines)
            {
                _lobby.StandardInput.WriteLine(line);
            }

            _lobby.StandardInput.Flush();
            Say(echo);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Say($"  the lobby did not answer ({e.GetType().Name})");
            Trouble?.Invoke(headline, "Press Stop and start again.");
            return false;
        }
    }

    public void EndLobby()
    {
        if (_lobby is null)
        {
            return;
        }

        var lobby = _lobby;

        lobby.Exited += (_, _) => _post(() =>
        {
            if (ReferenceEquals(_lobby, lobby))
            {
                LobbyEnded?.Invoke();
            }
        });

        if (lobby.HasExited)
        {
            LobbyEnded?.Invoke();
            return;
        }

        try
        {
            lobby.StandardInput.WriteLine("quit");
            lobby.StandardInput.Flush();
            Say("> quit");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Say($"  lobby had already exited ({e.GetType().Name})");
            LobbyEnded?.Invoke();
        }
    }

    public bool Restart(Func<bool> ready)
    {
        if (!ready())
        {
            return false;
        }

        Stop(releaseClaim: false);
        return BeginStart();
    }

    public void StopAll()
    {
        Stop(releaseClaim: true);
    }

    public void StopAllAndDropStragglers()
    {
        Stop(releaseClaim: true);
        Generation++;
    }

    private void Stop(bool releaseClaim)
    {
        var relay = _relay;
        var others = 0;
        foreach (var p in _running.ToArray())
        {
            if (ReferenceEquals(p, relay))
            {
                continue;
            }

            others++;
            KillOne(p);
        }

        if (relay is not null)
        {
            if (others > 0)
            {
                Thread.Sleep(250);
            }

            KillOne(relay);
        }

        _running.Clear();
        _lobby = null;
        _relay = null;
        _starting = false;
        if (releaseClaim)
        {
            ReleaseMatchClaim();
        }
    }

    private static void KillOne(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            p.Dispose();
        }
        catch (Exception)
        {
        }
    }

    internal void OnLine(string? line, int spawnedUnder)
    {
        if (line is null)
        {
            return;
        }

        var readUnder = spawnedUnder;

        _post(() =>
        {
            if (readUnder != Generation)
            {
                return;
            }

            _log?.Write(ForTheRecord(Masked(line)));
            Output?.Invoke(line);
        });
    }

    private string Masked(string line)
    {
        return MaskCode(line, RoomCode);
    }

    internal static string MaskCode(string line, string code)
    {
        if (code.Length == 0)
        {
            return line;
        }

        return Regex.Replace(line, @"(?<![A-Za-z0-9])" + Regex.Escape(code) + @"(?![A-Za-z0-9])", "******");
    }

    internal static string ForTheRecord(string line, string? folder = null)
    {
        var noFingerprint = Regex.Replace(line, @"(fingerprint )[0-9A-Fa-f]{8,64}", "$1********");
        var noBinding = Regex.Replace(noFingerprint, @"(session bound |--binding )[0-9A-Fa-f]{64}", "$1********");
        var noRoom = Regex.Replace(noBinding, @"(--room-id |room id: |room )[A-Za-z0-9]{16,}", "$1******");
        var noCode = Regex.Replace(noRoom, @"(--room |--join |room code: )[A-Z0-9]{4,12}(?![A-Za-z0-9])", "$1******");
        var noOurName = Regex.Replace(noCode, @"(--name )[A-Za-z0-9]{1,8}(?![A-Za-z0-9])", "$1******");
        var noTheirName = Regex.Replace(noOurName, @"(<- their name: )(?:[0-9A-Fa-f]{2}){1,8}(?![0-9A-Fa-f])", "$1******");
        var noJoinedName = Regex.Replace(noTheirName, @"(screen: )[A-Za-z0-9]{1,8}(?= has joined)", "$1******");
        var noArmyName = Regex.Replace(noJoinedName, @"(--army-name-hex )[0-9A-Fa-f]+", "$1******");
        return WithoutFolder(noArmyName, folder ?? AppContext.BaseDirectory);
    }

    private static string WithoutFolder(string line, string folder)
    {
        var bare = folder.TrimEnd('\\', '/');
        if (bare.Length <= 3)
        {
            return line;
        }

        var inside = line.Replace(bare + System.IO.Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
        return inside.Replace(bare, ".", StringComparison.OrdinalIgnoreCase);
    }

    public static string StartFailedReason(Exception e)
    {
        if (e is System.ComponentModel.Win32Exception failed)
        {
            return new System.ComponentModel.Win32Exception(failed.NativeErrorCode).Message;
        }

        return "";
    }

    public void Say(string line)
    {
        var shown = Masked(line);
        _log?.Write(ForTheRecord(shown));
        Echo?.Invoke(shown);
    }
}
