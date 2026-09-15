using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessVmWrite = 0x0020;
    private const int ProcessVmOperation = 0x0008;
    private const int ProcessQueryInformation = 0x0400;

    private const int ProcessCreateThread = 0x0002;

    private const ulong GlobalRva = 0x8983150;

    private static volatile bool _parentGone;
    private static volatile bool _ctrlPressed;

    private static long _firstCtrlTicks;

    internal static readonly TimeSpan CtrlGrace = TimeSpan.FromSeconds(2);

    internal static bool CtrlPressLetThrough(DateTime? firstPress, DateTime now)
    {
        return firstPress is { } first && now - first >= CtrlGrace;
    }

    internal static bool ReadFailedCrash(bool processExited)
    {
        return processExited;
    }

    private const uint StillActive = 259;

    private static bool GameExited()
    {
        return GetExitCodeProcess(_handle, out var code) && code != StillActive;
    }

    private static void WatchParent(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _parentGone = true;
            return;
        }

        var t = new Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch
            {
            }
            finally
            {
                parent.Dispose();
            }

            _parentGone = true;
        })
        {
            IsBackground = true,
            Name = "parent-watch",
        };
        t.Start();
    }

    private const ulong MoveCount = 0x68;
    private const ulong MoveCapacity = 0x6C;
    private const ulong MoveData = 0x70;
    private const int MoveStride = 0x18;

    private const ulong DraftSetupsCount = 0x20;
    private const ulong DraftSetupsData = 0x28;
    private const ulong AllowCustomDraft = 0x32;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint handle, nint address, byte[] buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(nint handle, nint address, byte[] buffer, nint size, out nint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint _alignment;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint _alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualQueryEx(nint handle, nint address, out MemoryBasicInformation info, nint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint handle, nint address, nint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(nint handle, nint address, nint size, uint protect, out uint old);

    private const uint PageExecuteReadWrite = 0x40;

    private const ulong RejectJzRva = 0xE39A45;
    private const byte OpJz = 0x74;
    private const byte OpJmp = 0xEB;

    private const ulong RejectFlag = 0x28;

    private const ulong PauseFlags = 0x29;

    private const ulong MatchOverFlag = 0x28;
    private const ulong MatchWinner = 0x30;

    private const ulong PlayerVictoryPoints = 0x50;

    private const ulong SettingsFromBoardGame = 0x40;
    private const ulong MaxVictoryPointsOffset = 0x20;

    private const ulong MaxDraftPointsOffset = 0x2C;

    private static nint _handle;
    private static ulong _base;

    private static ulong _moduleEnd;

    private static int Main(string[] args)
    {
        if (LauncherOnly.Refuses(args))
        {
            return LauncherOnly.ExitCode;
        }

        if (args.Contains("--version"))
        {
            var mvid = typeof(Program).Assembly.ManifestModule.ModuleVersionId;
            var (vFor, vExpires) = TestBuild.Read(typeof(Program).Assembly);
            var mark = TestBuild.Mark(vFor, vExpires);
            Console.WriteLine($"live-probe {mvid:N}"[..26] + (mark is null ? "" : $"  ({mark})"));
            return 0;
        }

        if (args.Contains("--selftest"))
        {
            return SelfTest();
        }

        var (_, testExpires) = TestBuild.Read(typeof(Program).Assembly);
        if (!TestBuild.RestoreVerb(args) && TestBuild.Expired(testExpires, DateOnly.FromDateTime(DateTime.Now)))
        {
            Console.Error.WriteLine($"  {TestBuild.ExpiredMessage()}");
            return 3;
        }

        if (args.Contains("--stop-draft-guard"))
        {
            return StopDraftGuard();
        }

        var proc = Process.GetProcessesByName("HorizonForbiddenWest").FirstOrDefault();
        if (proc is null)
        {
            Console.Error.WriteLine("HorizonForbiddenWest.exe is not running.");
            return 1;
        }

        try
        {
            _base = (ulong)proc.MainModule!.BaseAddress;
            _moduleEnd = _base + (ulong)proc.MainModule.ModuleMemorySize;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"HorizonForbiddenWest.exe is exiting; its modules cannot be read ({e.Message}).");
            return 1;
        }

        if (args.Contains("--build"))
        {
            var size = (uint)proc.MainModule.ModuleMemorySize;
            var stamp = PeTimeDateStamp(proc.MainModule.FileName);
            Console.WriteLine(stamp == 0 ? $"size-{size:X}" : $"{stamp:X8}-{size:X}");
            return 0;
        }

        var wants = args.Contains("--poke") || args.Contains("--inject-move") ||
                    args.Contains("--alloc-bytes") ||
                    args.Contains("--game-alloc") ||
                    args.Contains("--fake-fact") || args.Contains("--hijack") ||
                    args.Contains("--script-move") || args.Contains("--patch-reject") ||
                    args.Contains("--restore-reject") || args.Contains("--set-placement") ||
                    args.Contains("--place-one") || args.Contains("--hold-placement") ||
                    args.Contains("--set-rules") ||
                    args.Contains("--set-units") || args.Contains("--set-acted") ||
                    args.Contains("--set-names") ||
                    args.Contains("--unlock-challenges") || args.Contains("--set-board") ||
                    args.Contains("--set-board-size") ||
                    args.Contains("--patch-move-bounds") || args.Contains("--commit-log") ||
                    args.Contains("--commit-ring") ||
                    args.Contains("--park") || args.Contains("--hold") || args.Contains("--highlight") ||
                    args.Contains("--freeze") || args.Contains("--force-first") ||
                    (IndexOfArg(args, "--first") is var fa && fa >= 0 &&
                     !(fa + 1 < args.Length && args[fa + 1].Equals("read", StringComparison.OrdinalIgnoreCase))) ||
                    args.Contains("--script-turn") || args.Contains("--script-pass");
        var runsGameCode = args.Contains("--game-alloc") ||
                           (args.Contains("--set-units") && args.Contains("--allocate"));
        var access = ProcessVmRead | ProcessQueryInformation | (wants ? ProcessVmWrite | ProcessVmOperation : 0)
                     | (runsGameCode ? ProcessCreateThread : 0);
        _handle = OpenProcess(access, false, proc.Id);
        if (_handle == 0)
        {
            Console.Error.WriteLine($"OpenProcess failed ({Marshal.GetLastWin32Error()}). Try running as administrator.");
            return 1;
        }

        var parentPidAt = IndexOfArg(args, "--parent-pid");
        if (parentPidAt >= 0 && parentPidAt + 1 < args.Length && int.TryParse(args[parentPidAt + 1], out var parentPid))
        {
            WatchParent(parentPid);
        }

        Console.CancelKeyPress += (_, e) =>
        {
            var now = DateTime.UtcNow;
            var first = Interlocked.CompareExchange(ref _firstCtrlTicks, now.Ticks, 0);
            e.Cancel = !CtrlPressLetThrough(first == 0 ? null : new DateTime(first, DateTimeKind.Utc), now);
            _ctrlPressed = true;
            _parentGone = true;
        };

        if (!Console.IsOutputRedirected)
        {
            if (args.Contains("--snapshot") || args.Contains("--watch-snapshot"))
            {
                Console.Error.WriteLine($"pid {proc.Id}  base 0x{_base:X}");
            }
            else
            {
                Console.WriteLine($"pid {proc.Id}  base 0x{_base:X}");
            }
        }

        if (args.Contains("--terrain-altered"))
        {
            _terrainAltered = true;
        }

        if (args.Contains("--asymmetric-board"))
        {
            _asymmetricBoard = true;
        }

        var threadsAt = IndexOfArg(args, "--scan-threads");
        if (threadsAt >= 0 && threadsAt + 1 < args.Length &&
            int.TryParse(args[threadsAt + 1], out var askedThreads) && askedThreads > 0)
        {
            _scanThreads = Math.Min(askedThreads, 64);
        }

        try
        {
            var hexAt = IndexOfArg(args, "--hex");
            if (hexAt >= 0)
            {
                var addr = ParseAddr(args, hexAt + 1);
                var len = hexAt + 2 < args.Length ? (int)ParseAddr(args, hexAt + 2) : 0x80;
                DumpHex(addr, len == 0 ? 0x80 : len);
                return 0;
            }

            var findAt = IndexOfArg(args, "--find-ptr");
            if (findAt >= 0)
            {
                return FindPtr(ParseAddr(args, findAt + 1), _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            var rangeAt = IndexOfArg(args, "--find-range");
            if (rangeAt >= 0)
            {
                return FindRange(ParseAddr(args, rangeAt + 1), ParseAddr(args, rangeAt + 2));
            }

            if (args.Contains("--freeze"))
            {
                return Freeze(on: !args.Contains("--clear"));
            }

            if (args.Contains("--highlight"))
            {
                return Highlight(on: !args.Contains("--clear"));
            }

            var bytesAt = IndexOfArg(args, "--find-bytes");
            if (bytesAt >= 0 && bytesAt + 1 < args.Length)
            {
                return FindBytes(Convert.FromHexString(args[bytesAt + 1].Replace("0x", "")));
            }

            if (args.Contains("--find-ai"))
            {
                return FindAiPlayer(_base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            var hijackAt = IndexOfArg(args, "--hijack");
            if (hijackAt >= 0)
            {
                return Hijack((byte)ParseAddr(args, hijackAt + 1), (byte)ParseAddr(args, hijackAt + 2),
                              (byte)ParseAddr(args, hijackAt + 3),
                              hijackAt + 4 < args.Length && int.TryParse(args[hijackAt + 4], out var hj) ? hj : 300,
                              args);
            }

            var terrainAt = IndexOfArg(args, "--watch-terrain");
            if (terrainAt >= 0)
            {
                return WatchTerrain(terrainAt + 1 < args.Length && int.TryParse(args[terrainAt + 1], out var ts) ? ts : 300);
            }

            var boardAt = IndexOfArg(args, "--watch-board");
            if (boardAt >= 0)
            {
                return WatchBoard(boardAt + 1 < args.Length && int.TryParse(args[boardAt + 1], out var bs) ? bs : 300);
            }

            var addrAt = IndexOfArg(args, "--watch-addr");
            if (addrAt >= 0)
            {
                return WatchAddr(ParseAddr(args, addrAt + 1),
                                 addrAt + 2 < args.Length ? (int)ParseAddr(args, addrAt + 2) : 0x20,
                                 addrAt + 3 < args.Length && int.TryParse(args[addrAt + 3], out var ws) ? ws : 60);
            }

            var huntAt = IndexOfArg(args, "--hunt-ai");
            if (huntAt >= 0)
            {
                return HuntAi(huntAt + 1 < args.Length && int.TryParse(args[huntAt + 1], out var hs) ? hs : 120);
            }

            var watchAiAt = IndexOfArg(args, "--watch-ai");
            if (watchAiAt >= 0)
            {
                return WatchAi(watchAiAt + 1 < args.Length && int.TryParse(args[watchAiAt + 1], out var secs) ? secs : 30);
            }

            var factAt = IndexOfArg(args, "--fake-fact");
            if (factAt >= 0)
            {
                return FakeFact(ParseAddr(args, factAt + 1), args);
            }

            var pokeAt = IndexOfArg(args, "--poke");
            if (pokeAt >= 0)
            {
                return Poke(ParseAddr(args, pokeAt + 1), args, pokeAt + 2);
            }

            var allocAt = IndexOfArg(args, "--alloc-bytes");
            if (allocAt >= 0)
            {
                return AllocBytes(args, allocAt + 1);
            }

            var gameAllocAt = IndexOfArg(args, "--game-alloc");
            if (gameAllocAt >= 0)
            {
                return GameAlloc(args, gameAllocAt + 1);
            }

            var injectAt = IndexOfArg(args, "--inject-move");
            if (injectAt >= 0)
            {
                return InjectMove(args, injectAt + 1);
            }

            var scriptAt = IndexOfArg(args, "--script-move");
            if (scriptAt >= 0)
            {
                return ScriptMove(args, scriptAt + 1);
            }

            var turnAt = IndexOfArg(args, "--script-turn");
            if (turnAt >= 0)
            {
                return ScriptTurn(args, turnAt + 1);
            }

            if (args.Contains("--script-pass"))
            {
                return ScriptActions([], args);
            }

            if (args.Contains("--hold"))
            {
                return Hold(args);
            }

            if (args.Contains("--park"))
            {
                return Park(args);
            }

            var firstAt = IndexOfArg(args, "--first");
            if (firstAt >= 0)
            {
                return First(args, firstAt);
            }

            var forceAt = IndexOfArg(args, "--force-first");
            if (forceAt >= 0)
            {
                return ForceFirst(args, forceAt);
            }

            var placeOneAt = IndexOfArg(args, "--place-one");
            if (placeOneAt >= 0)
            {
                return PlaceOne(args, placeOneAt + 1);
            }

            if (args.Contains("--set-rules"))
            {
                return SetRules(args, _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            if (args.Contains("--hold-placement"))
            {
                return HoldPlacement(ArgIntOrNull(args, "--wait") ?? 600);
            }

            var placeAt = IndexOfArg(args, "--set-placement");
            if (placeAt >= 0)
            {
                return SetPlacement(args, placeAt + 1);
            }

            if (args.Contains("--list-units"))
            {
                return ListUnits(_base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            if (args.Contains("--find-draft"))
            {
                return FindDraft();
            }

            if (args.Contains("--list-board-games"))
            {
                return ListBoardGames(_base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            if (args.Contains("--challenges"))
            {
                return Challenges();
            }

            if (args.Contains("--unlock-challenges"))
            {
                return UnlockChallenges(args);
            }

            var boardSetAt = IndexOfArg(args, "--set-board");
            if (boardSetAt >= 0)
            {
                return SetBoard(args, boardSetAt + 1, _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            if (args.Contains("--board-shape"))
            {
                return BoardShape(args, _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            if (args.Contains("--set-board-size"))
            {
                return SetBoardSize(args, _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            if (args.Contains("--patch-move-bounds"))
            {
                return PatchMoveBounds(args.Contains("--yes"), args.Contains("--clear"),
                                       keep: IndexOfArg(args, "--parent-pid") >= 0);
            }

            if (args.Contains("--commit-log"))
            {
                return CommitLog(args);
            }

            if (args.Contains("--commit-ring"))
            {
                return CommitRing(args.Contains("--yes"), args.Contains("--clear"),
                                  keep: IndexOfArg(args, "--parent-pid") >= 0);
            }

            if (args.Contains("--snapshot"))
            {
                return Snapshot();
            }

            if (args.Contains("--match-live"))
            {
                var live = Walk(report: false).Logic != 0;
                Console.WriteLine(live ? "  a match is live" : "  no match is live");
                return live ? 0 : 2;
            }

            var watchSnapAt = IndexOfArg(args, "--watch-snapshot");
            if (watchSnapAt >= 0)
            {
                return WatchSnapshot(watchSnapAt + 1 < args.Length && int.TryParse(args[watchSnapAt + 1], out var ws)
                                         ? ws : 600);
            }

            if (args.Contains("--survey"))
            {
                return Survey(_base + (ulong)proc.MainModule.ModuleMemorySize, args.Contains("--grids"));
            }

            var actedAt = IndexOfArg(args, "--set-acted");
            if (actedAt >= 0)
            {
                return SetActed(args, actedAt + 1);
            }

            if (args.Contains("--set-names"))
            {
                return SetNames(args);
            }

            var unitsAt = IndexOfArg(args, "--set-units");
            if (unitsAt >= 0)
            {
                return SetUnits(args, unitsAt + 1, _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            var statsAt = IndexOfArg(args, "--set-stats");
            if (statsAt >= 0)
            {
                return SetStats(args, statsAt + 1, _base + (ulong)proc.MainModule.ModuleMemorySize);
            }

            var depthAt = IndexOfArg(args, "--depth");
            var maxDepth = depthAt >= 0 && depthAt + 1 < args.Length && int.TryParse(args[depthAt + 1], out var md) ? md : 5;
            var nodesAt = IndexOfArg(args, "--nodes");
            var maxNodes = nodesAt >= 0 && nodesAt + 1 < args.Length && int.TryParse(args[nodesAt + 1], out var mn) ? mn : 400000;

            if (args.Contains("--find-controller"))
            {
                return FindController(maxDepth, maxNodes);
            }

            var wcAt = IndexOfArg(args, "--watch-controller");
            if (wcAt >= 0)
            {
                return WatchController(
                    wcAt + 1 < args.Length && int.TryParse(args[wcAt + 1], out var wcs) ? wcs : 120,
                    maxDepth, maxNodes);
            }

            if (args.Contains("--patch-reject"))
            {
                return SetRejectBranch(OpJmp, args.Contains("--yes")) ? 0 : 1;
            }

            if (args.Contains("--restore-reject"))
            {
                if (AttachedPageRefusal() is { } attached)
                {
                    Console.Error.WriteLine(attached);
                    return 1;
                }

                return SetRejectBranch(OpJz, args.Contains("--yes")) ? 0 : 1;
            }

            var watchAt = IndexOfArg(args, "--watch");
            var samples = watchAt >= 0 && watchAt + 1 < args.Length && int.TryParse(args[watchAt + 1], out var s) ? s : 1;

            return Probe(samples, args.Contains("--tiles")) ? 0 : 2;
        }
        finally
        {
            CloseHandle(_handle);
        }
    }

}
