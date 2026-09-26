using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Strikers.LiveProbe;

internal static partial class Program
{
    internal sealed class Detail
    {
        public const int Keep = 60;
        private readonly Queue<string> _lines = new();

        public int Count
        {
            get
            {
                lock (_lines)
                {
                    return _lines.Count;
                }
            }
        }

        public void Add(string line)
        {
            var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
            lock (_lines)
            {
                if (_lines.Count >= Keep)
                {
                    _lines.Dequeue();
                }

                _lines.Enqueue(stamped);
            }
        }

        public IReadOnlyList<string> Flush()
        {
            lock (_lines)
            {
                var all = _lines.ToList();
                _lines.Clear();
                return all;
            }
        }
    }

    internal static bool SquareOffPlacingBoard(int x, int y)
    {
        return x is < 0 or >= PlacingBoardSide || y is < 0 or >= PlacingBoardSide;
    }

    internal static byte Facing(int dir)
    {
        return (byte)(dir & 3);
    }

    internal static bool OurPageAttached(ulong data, ulong imageStart, ulong imageEnd)
    {
        return data != 0 && (data & 0xFFF) == 0 && (data < imageStart || data >= imageEnd);
    }

    private static string? AttachedPageRefusal()
    {
        var chain = Walk(report: false);
        if (!Sane(chain.Inst))
        {
            return null;
        }

        for (var seat = 0; seat < 2; seat++)
        {
            var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
            var data = Sane(player) ? ReadPtr(player + MoveData) : 0;
            if (OurPageAttached(data, _base, _moduleEnd))
            {
                return $"  REFUSING: seat {seat}'s required-move array still points at our page 0x{data:X}, and " +
                       "restoring the branch now re-enables the free of it. Detach it first:\n" +
                       $"    --poke 0x{player + MoveCount:X} 00000000 --yes\n" +
                       $"    --poke 0x{player + MoveCapacity:X} 00000000 --yes\n" +
                       $"    --poke 0x{player + MoveData:X} 0000000000000000 --yes";
            }
        }

        return null;
    }

    private static void Detach(ulong player, ulong instance)
    {
        ZeroMoveArray(player);

        if (!DetachHeld(ReadU32(player + MoveCount), ReadPtr(player + MoveData)))
        {
            ZeroMoveArray(player);
            if (!DetachHeld(ReadU32(player + MoveCount), ReadPtr(player + MoveData)))
            {
                Console.Error.WriteLine("  the game wrote the required-move array back over the detach twice; the " +
                                        "rejection branch stays patched so our page is never freed.");
                return;
            }

            Console.WriteLine("  the game wrote the required-move array back over the detach; zeroed it again.");
        }

        Console.WriteLine("  required-move array detached; AI restored to normal.");

        if (ReadByte(instance + MatchOverFlag) != 0)
        {
            Console.WriteLine("  the match is over (instance+0x28 is set). The byte is the game's, left as it stands.");
        }

        SetRejectBranch(OpJz, confirmed: true);
    }

    private static void ZeroMoveArray(ulong player)
    {
        Write(player + MoveCount, BitConverter.GetBytes(0u));
        Write(player + MoveCapacity, BitConverter.GetBytes(0u));
        Write(player + MoveData, BitConverter.GetBytes(0UL));
    }

    internal static bool DetachHeld(uint count, ulong data)
    {
        return count == 0 && data == 0;
    }

    internal static bool ArmWaitGoesOn(double gameSeconds, double limitSeconds, bool stopAsked)
    {
        _ = stopAsked;
        return gameSeconds < limitSeconds;
    }

    private const ulong PhaseSlot = 0x68;
    private const ulong PhaseClock = 0x20;
    private const double MaxFrameSeconds = 0.25;

    internal static double ArmTick(byte pause29, byte pause2A, double wallSeconds, ulong phaseBefore, float? before,
                                   ulong phaseNow, float? now)
    {
        if (pause29 != 0 || pause2A != 0 || wallSeconds < 0)
        {
            return 0;
        }

        if (now is not { } after)
        {
            return wallSeconds;
        }

        if (before is not { } was || phaseBefore != phaseNow || after < was)
        {
            return 0;
        }

        return Math.Min(after - was, wallSeconds + MaxFrameSeconds);
    }

    private static (ulong Phase, float? Clock) ReadPhaseClock(ulong inst)
    {
        if (!TryReadU64(inst + PhaseSlot, out var phase) || !Sane(phase))
        {
            return (0, null);
        }

        if (!TryRead(phase + PhaseClock, 4, out var bytes))
        {
            return (phase, null);
        }

        var clock = BitConverter.ToSingle(bytes, 0);
        if (!float.IsFinite(clock) || clock < 0)
        {
            return (phase, null);
        }

        return (phase, clock);
    }

    internal enum ArmUndo
    {
        Nothing,
        Detach,
        BranchOnly,
    }

    internal static ArmUndo UndoAfterArm(bool processGone, bool sameMatch)
    {
        if (processGone)
        {
            return ArmUndo.Nothing;
        }

        return sameMatch ? ArmUndo.Detach : ArmUndo.BranchOnly;
    }

    private static bool ArmStillOurs(ulong armedInst, int seat, ulong armedPlayer)
    {
        return AskedThrice(() =>
        {
            var live = Walk(report: false);
            var player = live.Inst == 0 ? 0 : ReadPtr(live.Inst + 0x40 + (ulong)(seat * 8));
            return RestoreAllowed(armedInst, armedPlayer, live.Inst, live.Logic, player);
        }, () => Thread.Sleep(20));
    }

    internal static bool AskedThrice(Func<bool> ask, Action between)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (ask())
            {
                return true;
            }

            if (attempt < 2)
            {
                between();
            }
        }

        return false;
    }

    private static void UndoArm(bool crashed, int seat, ulong player, ulong instance)
    {
        switch (UndoAfterArm(crashed, !crashed && ArmStillOurs(instance, seat, player)))
        {
            case ArmUndo.Nothing:
                Console.WriteLine("\n  game process is gone, nothing to detach or restore.");
                Console.WriteLine("  The code patch lived in the process, not the exe, so it is already undone.");
                break;

            case ArmUndo.Detach:
                Detach(player, instance);
                break;

            case ArmUndo.BranchOnly:
                Console.WriteLine("\n  the match this was armed on is gone; its memory is left alone and only " +
                                  "the rejection branch is put back.");
                SetRejectBranch(OpJz, confirmed: true);
                break;
        }
    }

    internal static (int Seconds, TimeSpan Stall, bool Final) ScriptOptions(string[] args)
    {
        var seconds = ArgIntOrNull(args, "--secs") ?? 180;
        var stall = TimeSpan.FromSeconds(ArgIntOrNull(args, "--stall") ?? 20);
        return (seconds, stall, args.Contains("--final"));
    }

    internal static int RecordsNeeded(int actionRecords, bool final)
    {
        return final && actionRecords > 0 ? actionRecords : actionRecords + 1;
    }

    internal static bool AppliedAfterReject(bool countRead, uint left, int slots, int need)
    {
        return countRead && left <= (uint)slots && slots - (int)left >= need;
    }

    private static int ScriptActions(List<Act> given, string[] args)
    {
        var (seconds, stall, final) = ScriptOptions(args);
        var acts = MergeRotateAttacks(given);
        if (acts.Count != given.Count)
        {
            Console.WriteLine($"  {given.Count - acts.Count} Rotate attack(s) folded into the move before them, " +
                              "so no activation is left open for the next activate to walk.");
        }

        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live.");
            return 2;
        }

        var seat = SeatArg(args) ?? FindAiSeat(chain.Inst);
        if (seat < 0)
        {
            Console.Error.WriteLine(SeatArg(args) is -1
                ? "REFUSED: --player must be 0 or 1."
                : "  could not tell which seat is the AI. Pass --player 0|1.");
            return 3;
        }

        var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
        var resource = ReadPtr(player + 0x20);

        Console.WriteLine($"\n  AI seat    player[{seat}] @0x{player:X}");
        Console.WriteLine($"  resource   0x{resource:X}  difficulty low {ReadF(resource + 0x48):F2} " +
                          $"med {ReadF(resource + 0x4C):F2} high {ReadF(resource + 0x50):F2}");
        Console.WriteLine($"  currently  required moves: {ReadU32(player + MoveCount)} of " +
                          $"{ReadU32(player + MoveCapacity)} @0x{ReadPtr(player + MoveData):X}");
        Console.WriteLine(acts.Count == 0
            ? "\n  will script  a PASS, the AI takes its turn and does nothing:"
            : $"\n  will script  a turn of {acts.Count} action(s):");
        foreach (var a in acts)
        {
            var stand = StandSquare(a);
            Console.WriteLine($"    activate ({a.SrcX},{a.SrcY})  " +
                              (a.Burst ? $"burst in place  " : "") +
                              (a.Attack
                                  ? $"walk to ({stand.X},{stand.Y})" +
                                    $" and strike facing {a.Facing}, hitting ({a.DstX},{a.DstY})"
                                  : $"move to ({a.DstX},{a.DstY}) facing {a.Facing}"));
        }

        Console.WriteLine($"  then hold a trailing EndTurn so the count never drains to zero, for {seconds}s");

        if (acts.Count > 0
            && !Validate(chain.Logic, chain.Inst, seat, acts[0])
            && !args.Contains("--force"))
        {
            return 4;
        }

        if (acts.Count > 1 && !SimulateTurn(chain.Logic, chain.Inst, seat, acts) && !args.Contains("--force"))
        {
            return 4;
        }

        if (ReadByte(chain.Inst + MatchOverFlag) != 0)
        {
            Console.Error.WriteLine("\n  instance+0x28 is set, so this match has already ended and the game refuses");
            Console.Error.WriteLine("  required-move records. Start a match, or retry this one, and arm again.");
            Console.Error.WriteLine("  To arm the ended match anyway, which tells the game it is live:");
            Console.Error.WriteLine($"    live-probe --poke 0x{chain.Inst + MatchOverFlag:X} 00 --yes");
            return 5;
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        if (!SetRejectBranch(OpJmp, confirmed: true))
        {
            Console.Error.WriteLine("\n  could not disable the rejection free, refusing to arm.");
            return 1;
        }

        var page = (ulong)VirtualAllocEx(_handle, 0, 0x1000, 0x1000 | 0x2000, 0x04);
        if (page == 0)
        {
            Console.Error.WriteLine($"  VirtualAllocEx failed ({Marshal.GetLastWin32Error()}).");
            SetRejectBranch(OpJz, confirmed: true);
            return 1;
        }

        var slots = acts.Count * 3 + 5;
        var buf = new byte[MoveStride * slots];
        var n = 0;

        var what = new List<string>();

        foreach (var a in acts)
        {
            Record(buf, n++, ActivateAction, a.SrcX, a.SrcY, 0);
            what.Add($"activate ({a.SrcX},{a.SrcY})");

            if (a.Burst)
            {
                Record(buf, n++, BurstAction, a.SrcX, a.SrcY, 0);
                what.Add($"burst ({a.SrcX},{a.SrcY})");
            }

            var (tileX, tileY) = StandSquare(a);
            Record(buf, n++, a.Attack ? AttackAction : MoveAction, tileX, tileY, a.Facing);
            what.Add(a.Attack
                         ? $"attack from ({tileX},{tileY}) facing {a.Facing}, hits ({a.DstX},{a.DstY})"
                         : $"move to ({tileX},{tileY}) facing {a.Facing}");
        }

        for (var i = n; i < slots; i++)
        {
            Record(buf, i, EndTurnAction, 0, 0, 0);
        }

        if (!Write(page, buf))
        {
            Console.Error.WriteLine("\n  could not write the record array into our own page.");
            Detach(player, chain.Inst);
            return 1;
        }

        if (!Write(player + MoveCapacity, BitConverter.GetBytes(slots)))
        {
            Console.Error.WriteLine("\n  could not raise the record capacity.");
            Detach(player, chain.Inst);
            return 1;
        }

        if (!Write(player + MoveData, BitConverter.GetBytes(page)))
        {
            Console.Error.WriteLine("\n  could not point the game at our record array.");
            Detach(player, chain.Inst);
            return 1;
        }

        if (!Write(player + MoveCount, BitConverter.GetBytes(slots)))
        {
            Console.Error.WriteLine("\n  could not set the record count.");
            Detach(player, chain.Inst);
            return 1;
        }

        Console.WriteLine($"\n  allocated  0x{page:X}");
        Console.WriteLine($"  armed      count {slots} -> {n} action record(s), " +
                          $"then {slots - n} EndTurn records as drain padding");
        var gate = new GateRun(chain, seat, player, acts, slots, GateFloors(resource));
        Console.WriteLine("  READY, end your turn.\n");

        var need = RecordsNeeded(n, final);
        var applied = false;

        var armClock = 0.0;
        var changeClock = 0.0;
        var last = (uint)slots;
        var lastPoll = DateTime.UtcNow;
        var (lastPhase, lastPhaseClock) = ReadPhaseClock(chain.Inst);
        var lastOursCheck = DateTime.UtcNow;
        var stillSince = DateTime.UtcNow;
        var stopNoted = false;
        var crashed = false;
        var matchGone = false;
        var rejectedSeen = false;
        var reported = false;
        try
        {
            while (ArmWaitGoesOn(armClock, seconds, _parentGone))
            {
                if (!TryReadByte(chain.Inst + RejectFlag, out var rejected))
                {
                    Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  READ FAILED at instance+0x28 " +
                                      $"({Marshal.GetLastWin32Error()}).");
                    crashed = ReadFailedCrash(GameExited());
                    Console.WriteLine(crashed
                        ? "  THE GAME PROCESS HAS EXITED, this is a crash, not an array wipe; skipping the detach writes."
                        : "  the game is still running, so the match memory is gone; undoing what is still ours.");
                    break;
                }

                if (_parentGone && !stopNoted)
                {
                    stopNoted = true;
                    Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  Ctrl+C: this turn runs to its end before it is " +
                                      "undone, because an undo under a record the game still has pending closes the " +
                                      "game. Press again two seconds or more later to end at once, arm left in place.");
                }

                var polled = DateTime.UtcNow;
                var pause29 = ReadByte(chain.Inst + PauseFlags);
                var pause2A = ReadByte(chain.Inst + PauseFlags + 1);
                var (phaseNow, phaseClock) = ReadPhaseClock(chain.Inst);
                var tick = ArmTick(pause29, pause2A, (polled - lastPoll).TotalSeconds, lastPhase, lastPhaseClock,
                                   phaseNow, phaseClock);
                armClock += tick;
                lastPoll = polled;
                lastPhase = phaseNow;
                lastPhaseClock = phaseClock;
                if (tick > 0)
                {
                    stillSince = polled;
                }

                if (StoodStillCheckDue(polled - stillSince, polled - lastOursCheck))
                {
                    lastOursCheck = polled;
                    if (!ArmStillOurs(chain.Inst, seat, player))
                    {
                        Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  the match this turn was armed on is gone.");
                        matchGone = true;
                        break;
                    }
                }

                if (rejected != 0)
                {
                    var countRead = TryReadU32(player + MoveCount, out var afterReject);
                    if (AppliedAfterReject(countRead, afterReject, slots, need))
                    {
                        Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  turn APPLIED IN FULL, {n} action record(s) " +
                                          "consumed. The game then refused the trailing EndTurn, which is what the " +
                                          "end of the match looks like from this side.");
                        applied = true;
                        break;
                    }

                    rejectedSeen = true;
                    Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  instance+0x28 SET, so the match has ended and the game takes no more records.");
                    Console.WriteLine($"  count was {(countRead ? $"{afterReject}" : "unreadable")}, " +
                                      $"data still 0x{ReadPtr(player + MoveData):X}" +
                                      ", not freed, because the branch is patched.");
                    Console.WriteLine("  detaching and clearing the flag.");
                    break;
                }

                if (!TryReadU32(player + MoveCount, out var count))
                {
                    crashed = ReadFailedCrash(GameExited());
                    Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  READ FAILED at player+0x68, " +
                                      (crashed ? "the game has exited." : "the game is still running."));
                    break;
                }

                string? countLine = null;
                if (count != last)
                {
                    countLine = $"  {DateTime.Now:HH:mm:ss}  count {last} -> {count}" +
                                (count < last ? "   record consumed" : "");
                    last = count;
                    changeClock = armClock;
                }

                var done = (int)(slots - count);

                if (gate.Poll(done, () => TryReadU32(player + MoveCount, out var again) ? again : null, countLine))
                {
                    break;
                }

                if (done >= need)
                {
                    if (gate.Refused)
                    {
                        Console.Error.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  the turn ran to its end, the refused " +
                                                "action included, because the freeze never held. The arm is undone " +
                                                "as after a full turn.");
                        break;
                    }

                    Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  turn APPLIED IN FULL, {n} action record(s)" +
                                      (need == n
                                          ? " consumed. It ended the match, so no EndTurn was expected."
                                          : " and the EndTurn consumed."));
                    applied = true;
                    break;
                }

                if (done > 0 && armClock - changeClock > stall.TotalSeconds)
                {
                    ReportPartial(chain.Logic, chain.Inst, seat, what, done, stall);
                    reported = true;
                    break;
                }

                if (count is > 0 and < 2)
                {
                    if (!ArmStillOurs(chain.Inst, seat, player))
                    {
                        Console.WriteLine($"\n  {DateTime.Now:HH:mm:ss}  the match this turn was armed on is gone.");
                        matchGone = true;
                        break;
                    }

                    var tail = new byte[MoveStride];
                    Record(tail, 0, EndTurnAction, 0, 0, 0);
                    if (!Write(page + (ulong)MoveStride, tail))
                    {
                        break;
                    }

                    if (!Write(player + MoveCount, BitConverter.GetBytes(2u)))
                    {
                        break;
                    }

                    last = 2;
                }

                if (count == 0)
                {
                    Console.WriteLine("  count reached 0 on a SUCCESSFUL read, the game emptied the array itself.");
                    break;
                }

                Thread.Sleep(5);
            }

            if (gate.Refused)
            {
                return GateRefusedExit;
            }

            if (crashed)
            {
                return 1;
            }

            if (applied)
            {
                if (gate.Summary() is { } gateSummary)
                {
                    Console.WriteLine($"  {gateSummary}");
                }

                Console.WriteLine($"  game time {armClock:0.0} s");
                return 0;
            }

            if (matchGone)
            {
                Console.Error.WriteLine("\n  Warning: the match was left while this turn was armed, so nothing of it " +
                                        "can be accounted for.");
                return 6;
            }

            if (rejectedSeen)
            {
                Console.Error.WriteLine("\n  Warning: the turn stopped at a rejection, so it was applied only in part.");
            }

            if (reported)
            {
                return 6;
            }

            if (TryReadU32(player + MoveCount, out var left) && left < slots && slots - left < need)
            {
                ReportPartial(chain.Logic, chain.Inst, seat, what, (int)(slots - left), stall);
            }
            else if (!rejectedSeen)
            {
                Console.Error.WriteLine($"\n  Warning: {seconds}s of game time elapsed and no record was consumed. The turn was " +
                                        "never dispatched, nothing was applied, and nothing is owed to the peer.");
            }

            return 6;
        }
        finally
        {
            if (!gate.Detached)
            {
                UndoArm(crashed, seat, player, chain.Inst);
            }
        }
    }

    private const ulong CoinFlipVtableRva = 0x190E560;
    private const ulong FirstPlayer = 0x58;
    private const ulong CoinFlipObject = 0x60;

    private static int First(string[] args, int modeAt)
    {
        var mode = modeAt + 1 < args.Length ? args[modeAt + 1].ToLowerInvariant() : "read";
        if (mode is not ("human" or "ai" or "read"))
        {
            Console.Error.WriteLine("  --first takes human, ai, or read.");
            return 2;
        }

        var seconds = ArgIntOrNull(args, "--secs") ?? 300;
        var wait = ArgIntOrNull(args, "--wait") ?? 300;

        Console.WriteLine($"\n  --first {mode}, waiting for a match (up to {wait}s)");

        var until = DateTime.UtcNow.AddSeconds(wait);
        var chain = Walk(report: false);
        while (DateTime.UtcNow < until && !Sane(chain.Inst))
        {
            Thread.Sleep(200);
            chain = Walk(report: false);
        }

        if (!Sane(chain.Inst))
        {
            Console.Error.WriteLine($"  no match instance appeared. Waited {wait}s.");
            return 2;
        }

        if (mode != "read" && !args.Contains("--yes"))
        {
            Console.WriteLine("  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var lastSeen = "";
        var wrote = false;
        var sawFlip = false;
        ulong lastInst = 0;

        while (DateTime.UtcNow < deadline)
        {
            var live = Walk(report: false);
            if (!Sane(live.Inst))
            {
                Thread.Sleep(100);
                continue;
            }

            var inst = live.Inst;
            var ai = FindAiSeat(inst);
            if (ai < 0)
            {
                Thread.Sleep(100);
                continue;
            }
            var human = 1 - ai;

            var p0 = ReadPtr(inst + 0x40);
            var p1 = ReadPtr(inst + 0x48);
            var cur = ReadPtr(inst + FirstPlayer);
            if (cur == 0)
            {
                Thread.Sleep(40);
                continue;
            }

            var seat = cur == p0 ? 0 : cur == p1 ? 1 : -1;
            var who = seat < 0 ? $"neither player (0x{cur:X})" : seat == ai ? $"the AI seat (player[{seat}])"
                                                                            : $"OUR seat (player[{seat}])";

            var flipObj = ReadPtr(inst + CoinFlipObject);
            var inFlip = Sane(flipObj) && ReadPtr(flipObj) == _base + CoinFlipVtableRva;
            if (inFlip)
            {
                sawFlip = true;
            }

            var line = $"{who}   inst 0x{inst:X}{(inFlip ? "   [coin-flip state]" : "")}";
            if (line != lastSeen)
            {
                Console.WriteLine($"  first player is {line}");
                lastSeen = line;
            }

            if (mode == "read")
            {
                Thread.Sleep(40);
                continue;
            }

            var target = mode == "ai" ? ai : human;
            var want = target == 0 ? p0 : p1;
            if (!Sane(want))
            {
                Thread.Sleep(40);
                continue;
            }

            if (inst != lastInst)
            {
                if (lastInst != 0)
                {
                    Console.WriteLine($"  a new instance replaced the last one " +
                                      $"(0x{lastInst:X} -> 0x{inst:X}), re-applying");
                }

                lastInst = inst;
                wrote = false;
            }

            if (cur != want)
            {
                if (!Write(inst + FirstPlayer, BitConverter.GetBytes(want)))
                {
                    Console.Error.WriteLine("  could not write instance+0x58.");
                    return 1;
                }

                var back = ReadPtr(inst + FirstPlayer);
                Console.WriteLine($"  forced first player to the {mode} seat " +
                                  $"(player[{target}] 0x{want:X}) on inst 0x{inst:X}; reads back 0x{back:X} " +
                                  $"{(back == want ? "OK" : "-- MISMATCH")}");
                wrote = back == want;
            }
            else if (!wrote)
            {
                Console.WriteLine($"  inst 0x{inst:X} already had the {mode} seat " +
                                  "first, the roll agreed, nothing forced");
                wrote = true;
            }

            if (sawFlip && !inFlip)
            {
                Console.WriteLine($"  the coin-flip state has passed on inst 0x{inst:X}, done.");
                return 0;
            }

            Thread.Sleep(40);
        }

        Console.WriteLine(mode == "read" ? "  watch window elapsed." :
                          wrote ? "  window elapsed with the write applied." :
                                  "  window elapsed WITHOUT writing, the flip already matched, or +0x58 never appeared.");
        return 0;
    }

    internal static bool RestoreAllowed(ulong armedInst, ulong armedRes, ulong liveInst, ulong liveLogic, ulong liveRes)
    {
        return liveLogic != 0 && liveInst == armedInst && liveRes == armedRes;
    }

    private static bool ThinkRangeStillOurs(ulong armedInst, int seat, ulong armedRes)
    {
        return AskedThrice(() =>
        {
            var live = Walk(report: false);
            var player = live.Inst == 0 ? 0 : ReadPtr(live.Inst + 0x40 + (ulong)(seat * 8));
            var res = player == 0 ? 0 : ReadPtr(player + 0x20);
            return RestoreAllowed(armedInst, armedRes, live.Inst, live.Logic, res);
        }, () => Thread.Sleep(20));
    }

    private const float HeldThink = 1e9f;

    internal static bool HoldWaitGoesOn(DateTime now, DateTime until, ulong logic, bool parentGone)
    {
        return now < until && logic == 0 && !parentGone;
    }

    internal static DateTime HoldDeadline(DateTime now, int seconds)
    {
        return seconds == 0 ? DateTime.MaxValue : now.AddSeconds(Math.Max(seconds, 0));
    }

    private static readonly TimeSpan StillBeforeCheck = TimeSpan.FromSeconds(1);

    internal static bool StoodStillCheckDue(TimeSpan stoodStill, TimeSpan sinceLastCheck)
    {
        return stoodStill > StillBeforeCheck && sinceLastCheck > StillBeforeCheck;
    }

    private static int Hold(string[] args)
    {
        var detail = new Detail();
        var seconds = ArgIntOrNull(args, "--secs") ?? 300;
        var thinkAt = IndexOfArg(args, "--think");
        var think = thinkAt >= 0 && thinkAt + 1 < args.Length && float.TryParse(args[thinkAt + 1], out var tv) ? tv : HeldThink;

        var wait = ArgIntOrNull(args, "--wait") ?? 0;

        var chain = Walk(report: false);
        if (chain.Logic == 0 && wait > 0)
        {
            Console.WriteLine($"\n  waiting for a match (up to {wait}s)");
            var until = DateTime.UtcNow.AddSeconds(wait);
            while (HoldWaitGoesOn(DateTime.UtcNow, until, chain.Logic, _parentGone))
            {
                Thread.Sleep(200);
                chain = Walk(report: false);
            }
        }

        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("No match is live." + (wait > 0 ? $" Waited {wait}s." : ""));
            return 2;
        }

        var seat = SeatArg(args) ?? FindAiSeat(chain.Inst);
        if (seat < 0)
        {
            Console.Error.WriteLine(SeatArg(args) is -1
                ? "REFUSED: --player must be 0 or 1."
                : "  could not tell which seat is the AI. Pass --player 0|1.");
            return 3;
        }

        var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
        var res = ReadPtr(player + 0x20);

        Console.WriteLine($"\n  AI seat    player[{seat}] @0x{player:X}  resource 0x{res:X}");
        Console.WriteLine($"  think      {ReadF(res + 0x5C):F2}..{ReadF(res + 0x60):F2}s  -> {think:F0}..{think:F0}s");
        if (!Console.IsOutputRedirected)
        {
            Console.WriteLine($"  untouched  activate {ReadF(res + 0x54):F2}..{ReadF(res + 0x58):F2}  " +
                              $"attack {ReadF(res + 0x64):F2}..{ReadF(res + 0x68):F2}  " +
                              $"move {ReadF(res + 0x6C):F2}..{ReadF(res + 0x70):F2}");
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        var slow = new byte[8];
        BitConverter.GetBytes(think).CopyTo(slow, 0);
        BitConverter.GetBytes(think).CopyTo(slow, 4);

        var saved = Read(res + 0x5C, 8);
        if (!Write(res + 0x5C, slow))
        {
            Console.Error.WriteLine("  could not write the think range.");
            return 1;
        }

        var baseline = AiPieces(chain.Logic, player);
        Console.WriteLine("  the AI seat is held");
        if (!Console.IsOutputRedirected)
        {
            Console.WriteLine($"  AI pieces  {Describe(baseline)}");
            Console.WriteLine("  (type 'release' on stdin, or wait out --secs, to restore)\n");
        }

        _holdReleased = false;
        if (Console.IsInputRedirected)
        {
            _ = Task.Run(() =>
            {
                string? line;
                while ((line = Console.ReadLine()) is not null)
                {
                    if (line.Trim() is "release" or "quit")
                    {
                        _holdReleased = true;
                        return;
                    }
                }
            });
        }

        var deadline = HoldDeadline(DateTime.UtcNow, seconds);
        var moved = false;
        var crashed = false;
        var damagedAt = DateTime.MinValue;
        var overSeen = false;
        try
        {
            while (!_holdReleased && !_parentGone && DateTime.UtcNow < deadline)
            {
                var live = Walk(report: false);
                if (live.Logic != 0)
                {
                    if (live.Inst == chain.Inst && ReadByte(live.Inst + MatchOverFlag) != 0)
                    {
                        overSeen = true;
                        break;
                    }

                    var seatNow = SeatArg(args) is not null ? seat : FindAiSeat(live.Inst);
                    if (seatNow >= 0)
                    {
                        var playerNow = ReadPtr(live.Inst + 0x40 + (ulong)(seatNow * 8));
                        var resNow = playerNow == 0 ? 0 : ReadPtr(playerNow + 0x20);

                        if (resNow != 0 && resNow != res)
                        {
                            Console.WriteLine($"\n  a new match rebuilt the AI player " +
                                              $"-- re-arming on resource 0x{resNow:X} (was 0x{res:X})");

                            res = resNow;
                            player = playerNow;
                            seat = seatNow;
                            chain = live;

                            saved = Read(res + 0x5C, 8);
                            if (!Write(res + 0x5C, slow))
                            {
                                Console.Error.WriteLine("  could not write the think range on the new match.");
                                return 1;
                            }

                            baseline = AiPieces(chain.Logic, player);
                            Console.WriteLine($"  think      -> {think:F0}..{think:F0}s   AI pieces  {Describe(baseline)}");
                        }
                        else if (live.Logic != chain.Logic)
                        {
                            chain = live;
                            baseline = AiPieces(chain.Logic, player);
                        }
                    }
                }
                else
                {
                    Thread.Sleep(200);
                    continue;
                }

                if (!TryReadU32(chain.Logic + 0x38, out _))
                {
                    Console.WriteLine($"\n  READ FAILED, the game is gone or dying.");
                    crashed = true;
                    break;
                }

                var now = AiPieces(chain.Logic, player);

                if (now.Count != baseline.Count)
                {
                    detail.Add($"AI piece count {baseline.Count} -> {now.Count} (placement or a kill), not movement");
                    baseline = now;
                }
                else if (!now.Select(Where).SequenceEqual(baseline.Select(Where)))
                {
                    var armed = TryReadU32(player + MoveCount, out var queued) && queued > 0;

                    var damagedAndMoved = now.Zip(baseline).Any(p => p.First.Hp < p.Second.Hp)
                                 || DateTime.UtcNow - damagedAt < RecentDamage;

                    var turnedOnly = TurnedInPlace(now, baseline);

                    var movedOneSquare = ShovedOneSquare(now, baseline);

                    if (armed)
                    {
                        detail.Add($"AI machine moved with {queued} record(s) armed, the injected move, not the AI");
                        detail.Add($"   was  {Describe(baseline)}");
                        detail.Add($"   now  {Describe(now)}");
                    }
                    else if (damagedAndMoved)
                    {
                        Console.WriteLine("  an AI machine took damage and moved: a Ram or Charge shove by the " +
                                          "opponent, not the AI acting.");
                        detail.Add($"   was  {Describe(baseline)}");
                        detail.Add($"   now  {Describe(now)}");
                    }
                    else if (turnedOnly)
                    {
                        detail.Add("AI machine(s) turned in place with no record armed: a turn-start spin, not a move");
                        detail.Add($"   was  {Describe(baseline)}");
                        detail.Add($"   now  {Describe(now)}");
                    }
                    else if (movedOneSquare)
                    {
                        detail.Add("an AI machine was pushed or pulled one square with no record armed: " +
                                   "the opponent's Ram, Tow or Strike shove, not the AI acting");
                        detail.Add($"   was  {Describe(baseline)}");
                        detail.Add($"   now  {Describe(now)}");
                    }
                    else
                    {
                        foreach (var line in detail.Flush())
                        {
                            Console.WriteLine($"  {line}");
                        }

                        Console.WriteLine("\n  HOLD DID NOT HOLD, an AI machine moved or turned with NO record " +
                                          "armed and no damage taken.");
                        Console.WriteLine($"     was  {Describe(baseline)}");
                        Console.WriteLine($"     now  {Describe(now)}");
                        moved = true;
                    }

                    baseline = now;
                }
                else if (!now.SequenceEqual(baseline))
                {
                    detail.Add("AI health changed, no square or facing moved: the opponent acting, not the AI");
                    detail.Add($"   {Describe(baseline)}  ->  {Describe(now)}");
                    damagedAt = DateTime.UtcNow;
                    baseline = now;
                }

                Thread.Sleep(50);
            }

            if (_parentGone && !_holdReleased)
            {
                Console.WriteLine($"  the process that started the hold is gone; " +
                                  "restoring and exiting.");
            }
            else if (_holdReleased)
            {
                Console.WriteLine($"  released.");
            }
            else if (overSeen)
            {
                Console.WriteLine("  the match is over; restoring the AI hold");
            }
            else if (moved)
            {
                Console.WriteLine($"\n  the hold failed at least once " +
                                              "-- see above.");
            }
            else if (!crashed)
            {
                Console.WriteLine($"\n  hold held for the whole window, " +
                                  "the AI never moved on its own.");
            }

            return moved ? 7 : 0;
        }
        finally
        {
            if (crashed)
            {
                Console.WriteLine("\n  game process is gone, the think range died with it.");
            }
            else if (ThinkRangeStillOurs(chain.Inst, seat, res))
            {
                Write(res + 0x5C, saved);
                Console.WriteLine($"  think range restored to {ReadF(res + 0x5C):F2}..{ReadF(res + 0x60):F2}s");
            }
            else
            {
                Console.WriteLine("  think range NOT restored: the match it was armed on is gone, so that memory " +
                                  "is not ours to write. The stretch died with the match.");
            }
        }
    }

    private static List<(int X, int Y, int Facing, int Hp)> AiPieces(ulong logic, ulong player)
    {
        var list = new List<(int X, int Y, int Facing, int Hp)>();
        var count = (int)ReadU32(logic + 0x38);
        var array = ReadPtr(logic + 0x40);
        for (var i = 0; i < count && i < 64; i++)
        {
            var u = ReadPtr(array + (ulong)(i * 8));
            if (!Sane(u) || ReadPtr(u) != player)
            {
                continue;
            }

            var packed = ReadByte(u + 0x38);
            list.Add((Nibble(packed), Nibble(packed >> 4), ReadByte(u + 0x3B) & 3, ReadByte(u + 0x3A)));
        }
        list.Sort();
        return list;
    }

    internal static readonly TimeSpan RecentDamage = TimeSpan.FromSeconds(2);

    internal static bool ShovedOneSquare(IReadOnlyList<(int X, int Y, int Facing, int Hp)> now,
                                         IReadOnlyList<(int X, int Y, int Facing, int Hp)> baseline)
    {
        if (now.Count != baseline.Count || now.Count == 0)
        {
            return false;
        }

        var before = baseline.Select(p => (p.X, p.Y)).ToList();
        var after = now.Select(p => (p.X, p.Y)).ToList();
        var left = before.Except(after).ToList();
        var arrived = after.Except(before).ToList();
        if (left.Count != 1 || arrived.Count != 1)
        {
            return false;
        }

        return Math.Abs(left[0].X - arrived[0].X) + Math.Abs(left[0].Y - arrived[0].Y) == 1;
    }

    internal static bool TurnedInPlace(IReadOnlyList<(int X, int Y, int Facing, int Hp)> now,
                                       IReadOnlyList<(int X, int Y, int Facing, int Hp)> baseline)
    {
        if (now.Count != baseline.Count)
        {
            return false;
        }

        return now.Zip(baseline).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y);
    }

    private static (int, int, int) Where((int X, int Y, int Facing, int Hp) p)
    {
        return (p.X, p.Y, p.Facing);
    }

    private static string Describe(List<(int X, int Y, int Facing, int Hp)> pieces)
    {
        return pieces.Count == 0 ? "none" : string.Join(", ", pieces.Select(p => $"({p.X},{p.Y}) f{p.Facing} hp{p.Hp}"));
    }

    private const byte ActivateAction = 0;
    private const byte BurstAction = 1;
    private const byte MoveAction = 2;
    private const byte AttackAction = 3;
    private const byte EndTurnAction = 4;

    private static void Record(byte[] buf, int index, byte action, int x, int y, byte facing)
    {
        var at = index * MoveStride;
        buf[at + 0x00] = action;
        BitConverter.GetBytes(x).CopyTo(buf, at + 0x08);
        BitConverter.GetBytes(y).CopyTo(buf, at + 0x0C);
        buf[at + 0x10] = facing;
    }

    internal static bool WrongSeat(int aiSeat, int askedSeat, bool forced)
    {
        return aiSeat >= 0 && aiSeat != askedSeat && !forced;
    }

    private static int FindAiSeat(ulong inst)
    {
        for (var seat = 0; seat < 2; seat++)
        {
            var player = ReadPtr(inst + 0x40 + (ulong)(seat * 8));
            if (!Sane(player))
            {
                continue;
            }

            var resource = ReadPtr(player + 0x20);
            if (!Sane(resource))
            {
                continue;
            }

            float low = ReadF(resource + 0x48), med = ReadF(resource + 0x4C), high = ReadF(resource + 0x50);
            if (low is < 0f or > 1f || med is < 0f or > 1f || high is < 0f or > 1f)
            {
                continue;
            }

            if (Math.Abs(low + med + high - 1f) > 0.01f)
            {
                continue;
            }

            return seat;
        }
        return -1;
    }

    private const int PlacementStride = 0x10;

    internal static (int Lo, int Hi)? SeatRowSpan(int seat, bool rowsRead, uint rows, int height)
    {
        if (!rowsRead || rows > int.MaxValue || PlacingRowsProblem((int)rows, height) is not null)
        {
            return null;
        }

        var depth = (int)rows;
        return seat == 1 ? (0, depth - 1) : (height - depth, height - 1);
    }

    private const ulong UnitPlacementRowCount = 0x120;

    private const int ChasmTile = -2;

    internal static string? LatePinProblem((int X, int Y) square, int width, int height, (int Lo, int Hi)? rows,
                                           int? terrain, int pattern, IReadOnlySet<(int X, int Y)> others)
    {
        if (square.X < 0 || square.X >= width || square.Y < 0 || square.Y >= height)
        {
            return $"its square ({square.X},{square.Y}) is off the {width}x{height} board";
        }

        if (rows is not { } span || square.Y < span.Lo || square.Y > span.Hi)
        {
            return $"its square ({square.X},{square.Y}) is outside the AI seat's placing rows";
        }

        if (terrain is not { } tile)
        {
            return $"the terrain under its square ({square.X},{square.Y}) could not be read";
        }

        if (tile == ChasmTile && pattern != DivePattern)
        {
            return $"its square ({square.X},{square.Y}) is a Chasm, where only a Dive machine is placed";
        }

        if (others.Contains(square))
        {
            return $"another machine already stands on its square ({square.X},{square.Y})";
        }

        return null;
    }

    private static string? LatePinProblemAt((ulong Challenge, ulong Inst, ulong Logic) chain, int seat, ulong unit,
                                            (int X, int Y) square)
    {
        var rowArr = ReadPtr(chain.Logic + 0x08);
        var height = (int)ReadU32(rowArr + 0x20);
        var width = (int)ReadU32(ReadPtr(ReadPtr(rowArr + 0x28)) + 0x20);

        var boardGame = ReadPtr(chain.Inst);
        var settings = Sane(boardGame) ? ReadPtr(boardGame + SettingsFromBoardGame) : 0;
        uint rows = 0;
        var rowsRead = Sane(settings) && TryReadU32(settings + UnitPlacementRowCount, out rows);

        int? terrain = null;
        var inside = square.X >= 0 && square.X < width && square.Y >= 0 && square.Y < height;
        var tiles = ReadPtr(chain.Logic + 0x20);
        if (inside && Sane(tiles) && TryReadByte(tiles + (ulong)((square.X + width * square.Y) * 0x48) + 0x40, out var t))
        {
            terrain = (sbyte)t;
        }

        var others = new HashSet<(int X, int Y)>();
        var count = (int)ReadU32(chain.Logic + 0x38);
        var array = ReadPtr(chain.Logic + 0x40);
        for (var i = 0; i < count && i < 64; i++)
        {
            var u = ReadPtr(array + (ulong)(i * 8));
            if (u == unit || !Sane(u) || ReadByte(u + 0x3A) == 0)
            {
                continue;
            }

            var packed = ReadByte(u + 0x38);
            others.Add((Nibble(packed), Nibble(packed >> 4)));
        }

        return LatePinProblem(square, width, height, SeatRowSpan(seat, rowsRead, rows, height), terrain,
                              PatternOf(unit), others);
    }

    private static int PlaceOne(string[] args, int at)
    {
        if (at + 3 >= args.Length ||
            !int.TryParse(args[at], out var index) || !int.TryParse(args[at + 1], out var x) ||
            !int.TryParse(args[at + 2], out var y) || !byte.TryParse(args[at + 3], out var dir))
        {
            Console.Error.WriteLine("--place-one <index> <x> <y> <dir> [--player N] [--yes]");
            return 1;
        }

        dir = Facing(dir);

        var seat = SeatArg(args) ?? 1;
        if (seat < 0)
        {
            Console.Error.WriteLine("REFUSED: --player must be 0 or 1.");
            return 3;
        }

        var chain = Walk(report: false);
        if (chain.Logic == 0)
        {
            Console.Error.WriteLine("no match is live, nothing to place into.");
            return 2;
        }

        var aiSeat = FindAiSeat(chain.Inst);
        if (WrongSeat(aiSeat, seat, args.Contains("--force")))
        {
            Console.Error.WriteLine($"REFUSED: asked to place into seat {seat}, but the AI holds seat " +
                                    $"{aiSeat} in this match. Writing there would move the local " +
                                    "player's own machines.");
            return 3;
        }

        var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
        var draft = Sane(player) ? ReadPtr(player + 0x30) : 0;
        if (!Sane(draft) || !TryReadU32(draft + 0x10, out var count))
        {
            Console.Error.WriteLine($"seat {seat} has no draft to place into.");
            return 2;
        }

        if (index < 0 || index >= (int)count)
        {
            Console.Error.WriteLine($"index {index} is outside the {count} placement record(s).");
            return 1;
        }

        var slotAt = IndexOfArg(args, "--for-slot");
        if (slotAt >= 0)
        {
            if (slotAt + 1 >= args.Length || !int.TryParse(args[slotAt + 1], out var forSlot) || forSlot < 0)
            {
                Console.Error.WriteLine("--for-slot needs the machine's army slot, 0 or more.");
                return 1;
            }

            var wanted = DraftUnitUuid(player, forSlot);
            if (wanted.Length == 0)
            {
                Console.Error.WriteLine($"REFUSED: slot {forSlot} is not a machine in seat {seat}'s draft.");
                return 3;
            }

            var unplaced = UnplacedUnits(player);
            if (unplaced is null || PickUnplaced(unplaced.Select(u => u.Uuid).ToList(), wanted) < 0)
            {
                Console.Error.WriteLine($"REFUSED: machine {wanted} (slot {forSlot}) is not waiting to be " +
                                        "placed here: already placed, or the list could not be read.");
                return 3;
            }

            var ctrl = AiPlacingController(chain.Inst, player);
            if (Sane(ctrl) && TryReadU32(ctrl + 0x20, out var ctrlState) && ctrlState == 1)
            {
                var aiPieces = CountAiPieces(chain.Logic, player);
                if (aiPieces < 0)
                {
                    Console.Error.WriteLine("REFUSED: the AI seat's piece count could not be read, so " +
                                            "whether the game has picked for this record is unknown.");
                    return 3;
                }

                if (PickCommitsRecord(aiPieces, ctrlState, index) && !PickedUnitIs(ctrl, player, forSlot, out var why))
                {
                    Console.Error.WriteLine($"REFUSED: {why}. Both players must restart the match.");
                    return 3;
                }
            }

            Console.WriteLine($"  slot {forSlot} is machine {wanted}, waiting to be placed");
        }

        var boardGame = ReadPtr(chain.Inst);
        var settings = Sane(boardGame) ? ReadPtr(boardGame + SettingsFromBoardGame) : 0;
        uint rows = 0;
        var rowsRead = Sane(settings) && TryReadU32(settings + UnitPlacementRowCount, out rows);

        var height = (int)ReadU32(ReadPtr(chain.Logic + 0x08) + 0x20);
        if (SeatRowSpan(seat, rowsRead, rows, height) is not { } span)
        {
            var rowsWhy = rowsRead
                ? PlacingRowsProblem(rows > int.MaxValue ? -1 : (int)rows, height)
                : "the placing row count cannot be read, so no square can be judged";
            Console.Error.WriteLine($"REFUSED: {rowsWhy}.");
            return 3;
        }

        var (lo, hi) = span;
        if (y < lo || y > hi)
        {
            Console.Error.WriteLine($"REFUSED: y {y} is outside seat {seat}'s placing rows {lo}..{hi}. " +
                                    "The game would CLAMP this rather than refuse it, so the two boards " +
                                    "would silently disagree.");
            return 3;
        }

        var rowArr = ReadPtr(chain.Logic + 0x08);
        var width = (int)ReadU32(ReadPtr(ReadPtr(rowArr + 0x28)) + 0x20);
        if (x < 0 || x >= width)
        {
            Console.Error.WriteLine($"REFUSED: x {x} is outside the board's {width} columns. " +
                                    "The game would CLAMP this rather than refuse it, so the two boards " +
                                    "would silently disagree.");
            return 3;
        }

        var data = ReadPtr(draft + 0x18);
        if (!Sane(data))
        {
            Console.Error.WriteLine("the placement array has no data pointer yet.");
            return 2;
        }

        var addr = data + (ulong)(index * PlacementStride);
        var rec = new byte[9];
        BitConverter.GetBytes(x).CopyTo(rec, 0);
        BitConverter.GetBytes(y).CopyTo(rec, 4);
        rec[8] = dir;

        if (!args.Contains("--yes"))
        {
            Console.WriteLine($"  would write record {index} @0x{addr:X} = ({x},{y}) dir {dir}. " +
                              "Add --yes to apply.");
            return 0;
        }

        if (!Write(addr, rec))
        {
            Console.Error.WriteLine($"write to 0x{addr:X} failed.");
            return 2;
        }

        Console.WriteLine($"  record {index} @0x{addr:X} = ({x},{y}) dir {dir}");
        return 0;
    }

    private static volatile int _phAllowed;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _phSlots = new();
    private static volatile bool _phReleased;

    private static volatile bool _holdReleased;

    internal static int AiPlacementIndex(int aiOwnedPieces, uint controllerState)
    {
        if (aiOwnedPieces < 1)
        {
            return 0;
        }
        if (controllerState == 0)
        {
            return aiOwnedPieces;
        }
        return aiOwnedPieces - 1;
    }

    internal static bool PickCommitsRecord(int aiOwnedPieces, uint controllerState, int index)
    {
        if (controllerState != 1)
        {
            return false;
        }

        return AiPlacementIndex(aiOwnedPieces, controllerState) == index;
    }

    internal static int SlotForRecord(System.Collections.Generic.IReadOnlyDictionary<int, int> slots, int index)
    {
        if (index < 0 || !slots.TryGetValue(index, out var slot))
        {
            return -1;
        }

        return slot;
    }

    internal static bool PlacementNeedsPin(int index, int allowed)
    {
        return index >= allowed;
    }

    internal const int MaxUnitsRead = 64;

    internal static bool UnitCountReadable(uint count)
    {
        return count <= MaxUnitsRead;
    }

    internal static bool PlacementAdvanced(int index, int current)
    {
        return index > current;
    }

    internal static (int N, int Slot) ParseAllow(string line)
    {
        var w = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length is < 2 or > 3 || w[0] != "allow" || !int.TryParse(w[1], out var n) || n < 0)
        {
            return (-1, -1);
        }

        if (w.Length == 2)
        {
            return (n, -1);
        }

        if (!int.TryParse(w[2], out var slot) || slot < 0)
        {
            return (-1, -1);
        }

        return (n, slot);
    }

    internal static int PickUnplaced(IReadOnlyList<string> unplacedUuids, string wanted)
    {
        if (wanted.Length == 0)
        {
            return -1;
        }

        for (var i = 0; i < unplacedUuids.Count; i++)
        {
            if (string.Equals(unplacedUuids[i], wanted, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string DraftUnitUuid(ulong player, int slot)
    {
        var draft = ReadPtr(player + 0x30);
        if (!Sane(draft) || !TryReadU32(draft + 0x00, out var count) || !UnitCountReadable(count)
            || slot < 0 || slot >= (int)count)
        {
            return "";
        }

        var data = ReadPtr(draft + 0x08);
        if (!Sane(data))
        {
            return "";
        }

        return ResourceUuid(ReadPtr(data + (ulong)(slot * 8)));
    }

    private static List<(ulong Unit, string Uuid)>? UnplacedUnits(ulong player)
    {
        if (!TryReadU32(player + 0x40, out var left) || !UnitCountReadable(left))
        {
            return null;
        }

        var list = ReadPtr(player + 0x48);
        if (left > 0 && !Sane(list))
        {
            return null;
        }

        var units = new List<(ulong Unit, string Uuid)>();
        for (var i = 0; i < (int)left; i++)
        {
            var unit = ReadPtr(list + (ulong)(i * 8));
            if (!Sane(unit))
            {
                return null;
            }

            units.Add((unit, ResourceUuid(ReadPtr(unit + 0x08))));
        }

        return units;
    }

    private static ulong AiPlacingController(ulong inst, ulong player)
    {
        var phase = FindPlacingPhase(inst);
        if (phase == 0)
        {
            return 0;
        }

        var ctrl = ReadPtr(phase + 0x18);
        var vt = Sane(ctrl) ? ReadPtr(ctrl) : 0;
        var isAi = vt == _base + AutoPlacingCtrlVtableRva || vt == _base + AiPlacingCtrlVtableRva;
        if (!isAi || ReadPtr(ctrl + 0x10) != player)
        {
            return 0;
        }

        return ctrl;
    }

    private static string? HandControllerTheUnit(ulong ctrl, ulong player, int slot)
    {
        var wanted = DraftUnitUuid(player, slot);
        if (wanted.Length == 0)
        {
            return $"slot {slot} is not a machine in this seat's draft";
        }

        var unplaced = UnplacedUnits(player);
        if (unplaced is null)
        {
            return "the unplaced list could not be read";
        }

        var j = PickUnplaced(unplaced.Select(u => u.Uuid).ToList(), wanted);
        if (j < 0)
        {
            return $"no unplaced machine {wanted} for slot {slot}: already placed, or not in this army";
        }

        var chosen = unplaced[j].Unit;
        var current = ReadPtr(ctrl + 0x28);
        if (chosen == current)
        {
            return null;
        }

        if (Sane(current))
        {
            Write(current + 0x3D, [0]);
        }

        if (!Write(chosen + 0x3D, [1]) || !Write(ctrl + 0x28, BitConverter.GetBytes(chosen)))
        {
            return "the controller could not be written";
        }

        Console.WriteLine($"  the controller is handed machine {wanted} " +
                          $"(slot {slot}, unplaced entry {j}) in place of the game's own pick");
        return null;
    }

    private static bool PickedUnitIs(ulong ctrl, ulong player, int slot, out string why)
    {
        var wanted = DraftUnitUuid(player, slot);
        var unit = ReadPtr(ctrl + 0x28);
        var picked = Sane(unit) ? ResourceUuid(ReadPtr(unit + 0x08)) : "";
        if (wanted.Length > 0 && string.Equals(picked, wanted, StringComparison.OrdinalIgnoreCase))
        {
            why = "";
            return true;
        }

        why = $"the game picked machine {picked} before the square for slot {slot} ({wanted}) " +
              "arrived; a release would place the wrong machine on the peer's square";
        return false;
    }

    private static int CountAiPieces(ulong logic, ulong aiPlayer)
    {
        if (!Sane(logic) || !Sane(aiPlayer))
        {
            return -1;
        }
        if (!TryReadU32(logic + 0x38, out var count) || !UnitCountReadable(count))
        {
            return -1;
        }
        if (count == 0)
        {
            return 0;
        }
        var array = ReadPtr(logic + 0x40);
        if (!Sane(array))
        {
            return -1;
        }
        var owned = 0;
        for (var i = 0; i < (int)count; i++)
        {
            var unit = ReadPtr(array + (ulong)(i * 8));
            if (!Sane(unit) || !TryRead(unit, 8, out var ownerBytes))
            {
                return -1;
            }
            if (BitConverter.ToUInt64(ownerBytes) == aiPlayer)
            {
                owned++;
            }
        }
        return owned;
    }

    private static int HoldPlacement(int waitSecs)
    {
        var pd = new Detail();
        _phAllowed = 0;
        _phSlots.Clear();
        _phReleased = false;

        if (Console.IsInputRedirected)
        {
            _ = Task.Run(() =>
            {
                string? line;
                while ((line = Console.ReadLine()) is not null)
                {
                    var t = line.Trim();
                    if (t is "release" or "quit")
                    {
                        pd.Add("placement hold: release received");
                        _phReleased = true;
                        return;
                    }
                    var (n, slot) = ParseAllow(t);
                    if (n >= 0)
                    {
                        if (n >= 1 && slot >= 0)
                        {
                            _phSlots[n - 1] = slot;
                        }
                        _phAllowed = n;
                        var which = slot >= 0 ? $", machine slot {slot} next" : "";
                        pd.Add($"placement hold: allowed {n}{which}");
                    }
                }
            });
        }

        Console.WriteLine($"  placement hold armed (up to {waitSecs} s)");

        var deadline = DateTime.UtcNow.AddSeconds(waitSecs);
        var sawPlacing = false;
        ulong pinnedCtrl = 0;
        var pinsReported = 0;

        var aiIndex = -1;

        var releasedThis = false;
        var heldThis = false;

        var refusedThis = false;

        var gonePolls = 0;
        var ended = false;

        while (!_phReleased && !_parentGone)
        {
            Thread.Sleep(15);

            var chain = Walk(report: false);
            var phase = Sane(chain.Inst) ? FindPlacingPhase(chain.Inst) : 0;
            if (phase == 0)
            {
                if (sawPlacing)
                {
                    gonePolls++;
                    if (gonePolls < 2)
                    {
                        continue;
                    }

                    Console.WriteLine(Sane(chain.Inst)
                                          ? $"  the placement phase is over ({pinsReported} AI placement(s) were held). Exiting."
                                          : "  the match is gone, placement hold done.");
                    ended = true;
                    break;
                }

                if (DateTime.UtcNow > deadline)
                {
                    Console.Error.WriteLine(Sane(chain.Inst)
                                                ? "  timed out waiting for the placing phase, nothing was held."
                                                : "  timed out waiting for a match, nothing was held.");
                    return 2;
                }

                continue;
            }

            gonePolls = 0;
            sawPlacing = true;

            var ctrl = ReadPtr(phase + 0x18);
            var vt = Sane(ctrl) ? ReadPtr(ctrl) : 0;
            var isAi = vt == _base + AutoPlacingCtrlVtableRva || vt == _base + AiPlacingCtrlVtableRva;

            if (!isAi)
            {
                continue;
            }

            var player = ReadPtr(ctrl + 0x10);
            var aiSeat = FindAiSeat(chain.Inst);
            if (aiSeat is not (0 or 1) || player != ReadPtr(chain.Inst + 0x40 + (ulong)(aiSeat * 8)))
            {
                continue;
            }

            var aiPieces = CountAiPieces(chain.Logic, player);
            if (aiPieces < 0)
            {
                continue;
            }
            if (!TryReadU32(ctrl + 0x20, out var state))
            {
                continue;
            }
            var index = AiPlacementIndex(aiPieces, state);
            if (PlacementAdvanced(index, aiIndex))
            {
                aiIndex = index;
                releasedThis = false;
                heldThis = false;
                refusedThis = false;
                pd.Add($"AI placement {aiIndex} started");
            }

            var timer = ReadF(ctrl + 0x30);

            if (!releasedThis && timer < 1e8f)
            {
                Write(ctrl + 0x30, BitConverter.GetBytes(1e9f));
                pinnedCtrl = ctrl;
                if (!heldThis)
                {
                    heldThis = true;
                    pinsReported++;
                    var when = state == 0 ? "before its pick" : "AFTER its pick, the model will lag";
                    var waiting = PlacementNeedsPin(aiIndex, _phAllowed) ? ", waiting for the peer's square" : ", its square is here";
                    pd.Add($"AI placement {aiIndex} held {when} (allowed {_phAllowed}){waiting}");
                }
            }
            else if (!releasedThis && timer > 1e8f && !PlacementNeedsPin(aiIndex, _phAllowed))
            {
                var slotNow = SlotForRecord(_phSlots, aiIndex);

                if (slotNow >= 0 && ReadU32(ctrl + 0x20) == 0)
                {
                    var problem = HandControllerTheUnit(ctrl, player, slotNow);
                    if (problem is not null)
                    {
                        if (!refusedThis)
                        {
                            refusedThis = true;
                            foreach (var line in pd.Flush())
                            {
                                Console.WriteLine($"  {line}");
                            }

                            Console.WriteLine($"  REFUSED to release AI placement " +
                                              $"{aiIndex}: {problem}");
                        }

                        continue;
                    }
                }

                if (ReadU32(ctrl + 0x20) == 1)
                {
                    if (slotNow >= 0 && !PickedUnitIs(ctrl, player, slotNow, out var why))
                    {
                        if (!refusedThis)
                        {
                            refusedThis = true;
                            foreach (var line in pd.Flush())
                            {
                                Console.WriteLine($"  {line}");
                            }

                            Console.WriteLine($"  REFUSED to release AI placement " +
                                              $"{aiIndex}: {why}");
                        }

                        continue;
                    }

                    var unit = ReadPtr(ctrl + 0x28);
                    var draft = ReadPtr(player + 0x30);
                    var data = Sane(draft) ? ReadPtr(draft + 0x18) : 0;
                    if (Sane(unit) && Sane(data))
                    {
                        var rec = data + (ulong)(aiIndex * PlacementStride);
                        var rx = (int)ReadU32(rec);
                        var ry = (int)ReadU32(rec + 4);
                        var rd = ReadByte(rec + 8);
                        if (LatePinProblemAt(chain, aiSeat, unit, (rx, ry)) is { } pinProblem)
                        {
                            if (!refusedThis)
                            {
                                refusedThis = true;
                                foreach (var line in pd.Flush())
                                {
                                    Console.WriteLine($"  {line}");
                                }

                                Console.WriteLine($"  REFUSED to release AI placement {aiIndex}: its pin was late, " +
                                                  "so the record's square would be written straight into the " +
                                                  $"machine, and {pinProblem}");
                            }

                            continue;
                        }

                        Write(unit + 0x38, [(byte)((rx & 0xF) | (ry << 4))]);
                        Write(unit + 0x3B, [(byte)((ReadByte(unit + 0x3B) & 0xFC) | (rd & 3))]);
                        foreach (var line in pd.Flush())
                        {
                            Console.WriteLine($"  {line}");
                        }

                        Console.WriteLine($"  pin was late, cursor rewritten " +
                                          $"to ({rx},{ry}) dir {rd} from record {aiIndex}; the model " +
                                          "stands where the game walked it until this machine moves.");
                    }
                }

                Write(ctrl + 0x30, BitConverter.GetBytes(0f));
                releasedThis = true;
                pd.Add($"released, AI placing machine {aiIndex}");
            }
            else if (ReleaseLost(releasedThis, timer))
            {
                Write(ctrl + 0x30, BitConverter.GetBytes(0f));
                pd.Add($"AI placement {aiIndex}'s release was overwritten by the game's timer tick; written again");
            }
        }

        if (Sane(pinnedCtrl))
        {
            var live = Walk(report: false);
            var livePhase = Sane(live.Inst) ? FindPlacingPhase(live.Inst) : 0;
            var liveCtrl = livePhase == 0 ? 0 : ReadPtr(livePhase + 0x18);
            if (UnpinOwed(pinnedCtrl, liveCtrl, liveCtrl == pinnedCtrl ? ReadF(pinnedCtrl + 0x30) : 0f))
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    Write(pinnedCtrl + 0x30, BitConverter.GetBytes(0f));
                    Thread.Sleep(40);
                    if (!ReleaseLost(released: true, ReadF(pinnedCtrl + 0x30)))
                    {
                        break;
                    }
                }
            }
            else if (liveCtrl != pinnedCtrl && ReadF(pinnedCtrl + 0x30) > 1e8f)
            {
                Console.WriteLine("  the pinned controller is not unpinned: the placing phase is no longer on " +
                                  "it, so it went with its phase or its match and that memory is not ours to write.");
            }
        }

        if (!ended)
        {
            Console.WriteLine($"  placement hold released.");
        }

        return 0;
    }

    internal static bool UnpinOwed(ulong pinned, ulong livePhaseCtrl, float timer)
    {
        return pinned != 0 && pinned == livePhaseCtrl && timer > 1e8f;
    }

    internal static bool ReleaseLost(bool released, float timer)
    {
        return released && timer > 1e8f;
    }

    private const int PlacingBoardSide = 8;

    private static int SetPlacement(string[] args, int at)
    {
        var want = new List<(int X, int Y, byte Dir)>();
        for (var i = at; i + 2 < args.Length; i += 3)
        {
            if (!int.TryParse(args[i], out var x) || !int.TryParse(args[i + 1], out var y) ||
                !byte.TryParse(args[i + 2], out var dir))
            {
                break;
            }

            if (SquareOffPlacingBoard(x, y))
            {
                Console.Error.WriteLine($"REFUSED: ({x},{y}) is off every board, which is at most " +
                                        $"{PlacingBoardSide}x{PlacingBoardSide}. The game would CLAMP " +
                                        "this rather than refuse it, so the two boards would silently disagree.");
                return 3;
            }

            want.Add((x, y, Facing(dir)));
        }

        if (want.Count == 0)
        {
            Console.Error.WriteLine("--set-placement <x> <y> <dir> [<x> <y> <dir> ...] [--player N] " +
                                    "[--secs N] [--force] [--yes]");
            Console.Error.WriteLine("  dir is LathiumDir: 0 N, 1 E, 2 S, 3 W. Coordinates are the game's " +
                                    "(zero-based, y=0 at the opponent's far edge).");
            return 1;
        }

        var secsAt = IndexOfArg(args, "--secs");
        var secs = secsAt >= 0 && secsAt + 1 < args.Length && int.TryParse(args[secsAt + 1], out var sv) ? sv : 300;
        var playerArg = SeatArg(args);
        if (playerArg is -1)
        {
            Console.Error.WriteLine("REFUSED: --player must be 0 or 1.");
            return 3;
        }

        var forcedSeat = playerArg ?? -1;
        var force = args.Contains("--force");
        var yes = args.Contains("--yes");

        Console.WriteLine($"\n  waiting for a match (up to {secs}s). Records to write:");
        foreach (var (x, y, dir) in want)
        {
            Console.WriteLine($"    ({x},{y}) dir {dir}");
        }

        var deadline = DateTime.UtcNow.AddSeconds(secs);
        ulong armedData = 0;
        var armedCount = -1;
        var sawPending = false;

        while (DateTime.UtcNow < deadline && !_parentGone)
        {
            Thread.Sleep(50);

            var chain = Walk(report: false);
            if (chain.Logic == 0)
            {
                continue;
            }

            var seat = forcedSeat >= 0 ? forcedSeat : FindAiSeat(chain.Inst);
            if (seat < 0)
            {
                continue;
            }

            var player = ReadPtr(chain.Inst + 0x40 + (ulong)(seat * 8));
            if (!Sane(player))
            {
                continue;
            }

            var draft = ReadPtr(player + 0x30);
            if (!Sane(draft))
            {
                continue;
            }

            if (!TryReadU32(draft + 0x10, out var count) || count == 0 || !UnitCountReadable(count))
            {
                continue;
            }

            var data = ReadPtr(draft + 0x18);
            if (!Sane(data))
            {
                continue;
            }

            if (data == armedData && (int)count == armedCount && RecordsMatch(data, want))
            {
                if (TryReadU32(player + 0x40, out var left))
                {
                    Console.Write($"\r  armed, units left to place: {left}    ");
                    if (left > 0)
                    {
                        sawPending = true;
                    }
                    else if (sawPending)
                    {
                        Console.WriteLine("\n  placement finished.");
                        return 0;
                    }
                }
                continue;
            }

            Console.WriteLine($"\n  match live. seat {seat}  player 0x{player:X}  draft 0x{draft:X}");
            var haveRem = TryReadU32(player + 0x40, out var rem);
            Console.WriteLine($"  placements: {count} @0x{data:X}  (units still to place: " +
                              $"{(haveRem ? rem.ToString() : "?")})");

            if (haveRem && rem > 0 && rem < count)
            {
                Console.WriteLine($"  Warning: placement already started, record(s) 0..{count - rem - 1} " +
                                  "are already consumed and will not change.");
            }

            for (var i = 0; i < (int)count; i++)
            {
                if (!TryRead(data + (ulong)(i * PlacementStride), PlacementStride, out var rec))
                {
                    continue;
                }

                Console.WriteLine($"    existing[{i}]  x {BitConverter.ToInt32(rec, 0)}  " +
                                  $"y {BitConverter.ToInt32(rec, 4)}  dir {rec[8]}");
            }

            if (want.Count != (int)count && !force)
            {
                Console.Error.WriteLine($"  REJECT: {want.Count} record(s) given but the array holds {count}. " +
                                        "Growing it needs the game's allocator. Pass --force to write only " +
                                        "the ones given.");
                return 1;
            }

            if (!yes)
            {
                Console.WriteLine("  dry run, pass --yes to write.");
                return 0;
            }

            var wrote = true;
            for (var i = 0; i < want.Count && i < (int)count; i++)
            {
                var rec = new byte[PlacementStride];
                BitConverter.GetBytes(want[i].X).CopyTo(rec, 0);
                BitConverter.GetBytes(want[i].Y).CopyTo(rec, 4);
                rec[8] = want[i].Dir;
                if (!Write(data + (ulong)(i * PlacementStride), rec))
                {
                    wrote = false;
                    break;
                }
            }
            if (!wrote)
            {
                return 1;
            }

            for (var i = 0; i < want.Count && i < (int)count; i++)
            {
                if (!TryRead(data + (ulong)(i * PlacementStride), PlacementStride, out var rec))
                {
                    continue;
                }

                Console.WriteLine($"    wrote[{i}]     x {BitConverter.ToInt32(rec, 0)}  " +
                                  $"y {BitConverter.ToInt32(rec, 4)}  dir {rec[8]}");
            }

            armedData = data;
            armedCount = (int)count;
            Console.WriteLine("  READY, start or continue the match; the placer reads these as it places.");
        }

        Console.WriteLine("\n  timed out.");
        return armedData == 0 ? 2 : 0;
    }

    private static bool RecordsMatch(ulong data, List<(int X, int Y, byte Dir)> want)
    {
        for (var i = 0; i < want.Count; i++)
        {
            if (!TryRead(data + (ulong)(i * PlacementStride), PlacementStride, out var rec))
            {
                return false;
            }

            if (BitConverter.ToInt32(rec, 0) != want[i].X)
            {
                return false;
            }

            if (BitConverter.ToInt32(rec, 4) != want[i].Y)
            {
                return false;
            }

            if (rec[8] != want[i].Dir)
            {
                return false;
            }
        }
        return true;
    }


    internal const int GateRefusedExit = 8;

    internal const double GateSettleBudgetMs = 150;

    private const double StockDelayFloorSeconds = 0.30;

    private const ulong UnitMoveOffset = 0x58;

    private const ulong ActivateDelayOffset = 0x54;

    private const ulong AttackDelayOffset = 0x64;

    private const ulong MoveDelayOffset = 0x6C;

    private const int MostSaneMove = 8;

    internal sealed record GateUnit(ulong Unit, int X, int Y, int Health, bool Ai, int Pattern, int Skill, int Range,
                                    int Move);

    internal sealed record GateBoard(int Width, int Height, IReadOnlyList<GateUnit> Units, bool Activated,
                                     ulong Active);

    internal sealed record GateJudgement(string? Problem, bool Dying, bool Unsettled = false);

    internal enum GateStep
    {
        Pass,
        Wait,
        Refuse,
    }

    internal sealed record GateVerdict(GateStep Step, string? Problem);

    internal sealed record GatePoint(bool BeforeActivate, int Action);

    private static Dictionary<int, GatePoint> GatePoints(List<Act> acts)
    {
        var points = new Dictionary<int, GatePoint>();
        var records = 0;
        for (var k = 0; k < acts.Count; k++)
        {
            if (k > 0)
            {
                points[records] = new GatePoint(true, k);
            }

            records++;
            if (acts[k].Burst)
            {
                records++;
            }

            points[records] = new GatePoint(false, k);
            records++;
        }

        return points;
    }

    private static List<GateUnit> UnitsOn(GateBoard board, (int X, int Y) square, ulong except)
    {
        return [.. board.Units.Where(u => (u.X, u.Y) == square && u.Unit != except)];
    }

    private static string Holder(IReadOnlyList<GateUnit> there)
    {
        if (there.Count == 0)
        {
            return "nothing";
        }

        return there.Any(u => !u.Ai) ? "the opponent's machine" : "the AI's own machine";
    }

    private static bool AllDying(IReadOnlyList<GateUnit> there)
    {
        return there.Count > 0 && there.All(u => u.Health == 0);
    }

    private static GateJudgement JudgeBeforeActivate(GateBoard board, Act next, int k)
    {
        if (!board.Activated)
        {
            return new GateJudgement(null, false);
        }

        var open = board.Units.FirstOrDefault(u => u.Unit == board.Active);
        if (open is null || (open.X, open.Y) == (next.SrcX, next.SrcY))
        {
            return new GateJudgement(null, false);
        }

        var there = UnitsOn(board, (next.SrcX, next.SrcY), open.Unit);
        return new GateJudgement(
            $"action {k + 1} activates ({next.SrcX},{next.SrcY}), which holds {Holder(there)}, while the machine " +
            $"on ({open.X},{open.Y}) is still activated, so the game would walk that machine onto " +
            $"({next.SrcX},{next.SrcY})",
            open.Health == 0, true);
    }

    private static GateJudgement JudgeBeforeStrike(GateBoard board, Act a, int k)
    {
        var src = (a.SrcX, a.SrcY);
        if (!board.Activated)
        {
            return new GateJudgement(
                $"action {k + 1} activated nothing on ({a.SrcX},{a.SrcY}), which holds " +
                $"{Holder(UnitsOn(board, src, 0))}, so the rest of the turn would stop there", false, true);
        }

        var mover = board.Units.FirstOrDefault(u => u.Unit == board.Active);
        if (mover is null)
        {
            return new GateJudgement(
                $"action {k + 1}: the machine the game activated for ({a.SrcX},{a.SrcY}) is not on the board", false,
                true);
        }

        if ((mover.X, mover.Y) != src)
        {
            return new GateJudgement(
                $"action {k + 1} was to activate ({a.SrcX},{a.SrcY}), and the game activated the machine on " +
                $"({mover.X},{mover.Y})", false);
        }

        if (!mover.Ai)
        {
            return new GateJudgement(
                $"action {k + 1} would hand the computer the opponent's machine on ({a.SrcX},{a.SrcY})", false);
        }

        var found = new List<GateJudgement>();
        if (mover.Health == 0)
        {
            found.Add(new GateJudgement($"action {k + 1}: the machine on ({a.SrcX},{a.SrcY}) is at 0 health", true));
        }

        var stand = StandSquare(a);
        var verb = a.Attack ? "stands the machine on" : "moves the machine to";
        if (stand.X < 0 || stand.X >= board.Width || stand.Y < 0 || stand.Y >= board.Height)
        {
            found.Add(new GateJudgement(
                $"action {k + 1} {verb} ({stand.X},{stand.Y}), off the {board.Width}x{board.Height} board", false));
        }
        else if (UnitsOn(board, stand, mover.Unit) is { Count: > 0 } onStand)
        {
            found.Add(new GateJudgement(
                $"action {k + 1} {verb} ({stand.X},{stand.Y}), where {Holder(onStand)} stands", AllDying(onStand)));
        }

        if (a.Attack && ChargeLanding(mover.Pattern, mover.Range, stand.X, stand.Y, a.Facing) is { } land)
        {
            var onLanding = UnitsOn(board, land, mover.Unit);
            if (LandingProblem(land, board.Width, board.Height, onLanding.Count > 0) is { } landing)
            {
                found.Add(new GateJudgement(
                    $"action {k + 1}: {landing}" + (onLanding.Count > 0 ? $" ({Holder(onLanding)})" : ""),
                    AllDying(onLanding)));
            }
        }

        if (a.Attack)
        {
            var victims = UnitsOn(board, (a.DstX, a.DstY), mover.Unit);
            if (!victims.Any(u => !u.Ai))
            {
                found.Add(new GateJudgement(
                    $"action {k + 1} strikes ({a.DstX},{a.DstY}), which holds {Holder(victims)}", false));
            }
            else if (victims.Where(u => !u.Ai).All(u => u.Health == 0))
            {
                found.Add(new GateJudgement(
                    $"action {k + 1} strikes ({a.DstX},{a.DstY}), where the opponent's machine is at 0 health", true));
            }

            if (ReachProblem(mover.Pattern, mover.Skill, mover.Range, stand.X, stand.Y, a.Facing, a.DstX, a.DstY)
                is { } reach)
            {
                found.Add(new GateJudgement($"action {k + 1}: {reach}", false));
            }
        }

        if (WalkProblem(mover, a, stand, k) is { } walk)
        {
            found.Add(new GateJudgement(walk, false));
        }

        if (found.FirstOrDefault(f => !f.Dying) is { } refused)
        {
            return refused;
        }

        return found.FirstOrDefault() ?? new GateJudgement(null, false);
    }

    private static string? WalkProblem(GateUnit mover, Act a, (int X, int Y) stand, int k)
    {
        if (mover.Move < 1 || mover.Move > MostSaneMove)
        {
            return null;
        }

        var extra = !a.Attack ? 1
                    : mover.Pattern == DashPattern ? mover.Range
                    : mover.Pattern == RamPattern ? 1
                    : 0;
        if (extra < 0)
        {
            return null;
        }

        var steps = Math.Abs(stand.X - mover.X) + Math.Abs(stand.Y - mover.Y);
        if (steps <= mover.Move + extra)
        {
            return null;
        }

        return $"action {k + 1} walks the machine on ({mover.X},{mover.Y}) {steps} squares to " +
               $"({stand.X},{stand.Y}), and its move is {mover.Move}" +
               (extra > 0 ? $" and {extra} more is the most its pattern allows" : "");
    }

    internal static bool SameGateBoard(GateBoard first, GateBoard second)
    {
        return first.Width == second.Width && first.Height == second.Height
               && first.Activated == second.Activated && first.Active == second.Active
               && first.Units.SequenceEqual(second.Units);
    }

    internal static GateVerdict GateSettle(GateBoard? first, GateBoard? second, double spentMs, double budgetMs,
                                           Func<GateBoard, GateJudgement> judge)
    {
        if (first is null || second is null || !SameGateBoard(first, second))
        {
            return spentMs < budgetMs
                ? new GateVerdict(GateStep.Wait, null)
                : new GateVerdict(GateStep.Refuse,
                                  $"the board did not read the same twice within {budgetMs:0} ms, so the action " +
                                  "could not be checked");
        }

        var judged = judge(second);
        if (judged.Problem is null)
        {
            return new GateVerdict(GateStep.Pass, null);
        }

        if ((judged.Dying || judged.Unsettled) && spentMs < budgetMs)
        {
            return new GateVerdict(GateStep.Wait, null);
        }

        return new GateVerdict(GateStep.Refuse,
                               judged.Dying ? $"{judged.Problem}, still listed at 0 health after {budgetMs:0} ms"
                               : judged.Unsettled ? $"{judged.Problem}, still so after {budgetMs:0} ms"
                               : judged.Problem);
    }

    internal static bool GateLate(double sincePopMs, double floorSeconds, bool recordConsumed)
    {
        return recordConsumed || sincePopMs >= floorSeconds * 1000;
    }

    internal static double DelayFloor(float read)
    {
        return float.IsFinite(read) && read >= 0 && read <= 60 ? read : StockDelayFloorSeconds;
    }

    internal sealed class GateHalt
    {
        public bool Refused { get; private set; }

        public bool Frozen { get; private set; }

        public bool Detached { get; private set; }

        public string? Failure { get; private set; }

        public int FreezeFailures { get; private set; }

        public bool Poll(Func<bool> judge, Func<bool, bool> freeze, Func<bool> stillOurs, Action waitStill,
                         Action detach, Action<bool, bool> tell)
        {
            var first = !Refused;
            if (first)
            {
                try
                {
                    if (!judge())
                    {
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Failure = e.GetType().Name;
                }

                Refused = true;
            }
            else if (!Attempt(stillOurs))
            {
                return false;
            }

            var quiet = FreezeFailures > 0;
            Frozen = Attempt(() => freeze(quiet));
            if (!Frozen)
            {
                FreezeFailures++;
            }

            if (first || Frozen)
            {
                Attempt(() =>
                {
                    tell(first, Frozen);
                    return true;
                });
            }

            if (!Frozen)
            {
                return false;
            }

            waitStill();
            if (!Attempt(stillOurs))
            {
                return true;
            }

            detach();
            Detached = true;
            return true;
        }

        private static bool Attempt(Func<bool> step)
        {
            try
            {
                return step();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static bool FreezeMatch((ulong Challenge, ulong Inst, ulong Logic) chain, bool quiet)
    {
        var target = FreezeTarget(chain.Inst, ReadPtr(chain.Challenge), _base);
        return target != 0 && Write(target, [1, 1], quiet);
    }

    private static void WaitForCountStill(ulong player)
    {
        var started = Stopwatch.StartNew();
        var stillSince = Stopwatch.StartNew();
        TryReadU32(player + MoveCount, out var seen);
        while (started.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(20);
            if (!TryReadU32(player + MoveCount, out var now))
            {
                return;
            }

            if (now != seen)
            {
                seen = now;
                stillSince.Restart();
            }
            else if (stillSince.Elapsed >= TimeSpan.FromSeconds(1))
            {
                return;
            }
        }
    }

    private static GateBoard? ReadGateBoard((ulong Challenge, ulong Inst, ulong Logic) chain, ulong player)
    {
        var (ctrl, name) = KnownChainController(chain);
        if (ctrl == 0 || !name.EndsWith("AIBoardGamePlayingControllerInstance", StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryReadByte(ctrl + 0x320, out var activated) || !TryReadU64(ctrl + 0x88, out var active))
        {
            return null;
        }

        var rows = ReadPtr(chain.Logic + 0x08);
        if (!Sane(rows) || !TryReadU32(rows + 0x20, out var height)
            || !TryReadU32(ReadPtr(ReadPtr(rows + 0x28)) + 0x20, out var width))
        {
            return null;
        }

        if (!TryReadU32(chain.Logic + 0x38, out var count) || count > 64
            || !TryReadU64(chain.Logic + 0x40, out var array))
        {
            return null;
        }

        var units = new List<GateUnit>();
        for (var i = 0; i < (int)count; i++)
        {
            if (!TryReadU64(array + (ulong)(i * 8), out var u) || !Sane(u)
                || !TryReadByte(u + 0x38, out var packed) || !TryReadByte(u + 0x3A, out var health)
                || !TryReadU64(u, out var owner))
            {
                return null;
            }

            var resource = ReadPtr(u + 0x08);
            var move = Sane(resource) && TryRead(resource + UnitMoveOffset, 4, out var moveBytes)
                ? BitConverter.ToInt32(moveBytes, 0)
                : -1;
            var (skill, range) = SkillAndRange(u);
            units.Add(new GateUnit(u, Nibble(packed), Nibble(packed >> 4), health, owner == player, PatternOf(u),
                                   skill, range, move));
        }

        if (TornByDuplicateUnit([.. units.Select(g => g.Unit)]))
        {
            return null;
        }

        return new GateBoard((int)width, (int)height, units, activated != 0, active);
    }

    private static (double Activate, double Attack, double Move) GateFloors(ulong resource)
    {
        Console.WriteLine($"  gate delays  activate {ReadF(resource + ActivateDelayOffset):0.00}.." +
                          $"{ReadF(resource + ActivateDelayOffset + 4):0.00} s, think " +
                          $"{ReadF(resource + 0x5C):0.00}..{ReadF(resource + 0x60):0.00} s, attack " +
                          $"{ReadF(resource + AttackDelayOffset):0.00}..{ReadF(resource + AttackDelayOffset + 4):0.00} s, " +
                          $"move {ReadF(resource + MoveDelayOffset):0.00}..{ReadF(resource + MoveDelayOffset + 4):0.00} s");
        return (DelayFloor(ReadF(resource + ActivateDelayOffset)), DelayFloor(ReadF(resource + AttackDelayOffset)),
                DelayFloor(ReadF(resource + MoveDelayOffset)));
    }

    private sealed class GateRun((ulong Challenge, ulong Inst, ulong Logic) chain, int seat, ulong player,
                                 List<Act> acts, int slots, (double Activate, double Attack, double Move) floors)
    {
        private readonly Dictionary<int, GatePoint> _points = GatePoints(acts);
        private readonly GateHalt _halt = new();
        private readonly List<string> _pending = [];
        private int _seen;
        private int _checked;
        private int _waits;
        private int _late;
        private double _leastMargin = double.MaxValue;
        private (int Key, string Label, DateTime Verdict)? _awaitingNext;
        private string? _refusal;
        private bool _refusedLate;

        public bool Refused
        {
            get { return _halt.Refused; }
        }

        public bool Detached
        {
            get { return _halt.Detached; }
        }

        public bool Poll(int done, Func<uint?> readCount, string? countLine)
        {
            if (countLine is not null)
            {
                _pending.Add(countLine);
            }

            var stop = _halt.Poll(() => Stop(done, readCount), quiet => FreezeMatch(chain, quiet),
                                  () => ArmStillOurs(chain.Inst, seat, player), () => WaitForCountStill(player),
                                  () => Detach(player, chain.Inst), Tell);
            Flush();
            return stop;
        }

        private void Flush()
        {
            foreach (var line in _pending)
            {
                Console.WriteLine(line);
            }

            _pending.Clear();
        }

        private void Tell(bool first, bool frozen)
        {
            Flush();
            if (!first)
            {
                Console.Error.WriteLine($"  {DateTime.Now:HH:mm:ss}  the freeze held on a later try, so the refused " +
                                        "action may already have been performed on this board. The turn's records " +
                                        "are detached once the count holds still. The freeze is never released: a " +
                                        "release now closes the game.");
                return;
            }

            Console.Error.WriteLine(_refusal is { } refusal
                ? $"\n  REFUSED by the live board check: {refusal}."
                : $"\n  REFUSED: the live board check failed ({_halt.Failure}), so the action could not be checked.");
            if (_refusedLate)
            {
                Console.Error.WriteLine("  The verdict came after the game could already have performed that action, " +
                                        "so the stack may have happened on this board.");
            }

            Console.Error.WriteLine(frozen
                ? "  The match is frozen, and the turn's records are detached once the count holds still. The freeze " +
                  "is never released: a release now closes the game."
                : "  The freeze could not be written. The check stops here, the freeze is tried again at every poll " +
                  "while the match is still the one this turn was armed on, with no further line when it fails, and " +
                  "the turn's records stay attached until it holds or the turn ends, because a detach under a " +
                  "record the game still has waiting closes the game.");
        }

        private bool Stop(int done, Func<uint?> readCount)
        {
            if (_awaitingNext is { } waiting && done > waiting.Key)
            {
                var now = DateTime.Now;
                _pending.Add($"  gate {waiting.Label}  next record consumed {now:HH:mm:ss.fff}, " +
                             $"{(now - waiting.Verdict).TotalMilliseconds:0} ms after the verdict");
                _awaitingNext = null;
            }

            if (done <= _seen)
            {
                return false;
            }

            var from = _seen + 1;
            _seen = done;
            for (var key = from; key < done; key++)
            {
                if (_points.TryGetValue(key, out var missed))
                {
                    _late++;
                    _pending.Add($"  gate {Label(missed)}  MISSED: its record was consumed before this poll saw the " +
                                 "one before it, so it was not checked");
                }
            }

            return _points.TryGetValue(done, out var point) && Check(done, point, readCount);
        }

        private static string Label(GatePoint point)
        {
            return point.BeforeActivate ? $"A action {point.Action + 1}" : $"B action {point.Action + 1}";
        }

        private bool Check(int key, GatePoint point, Func<uint?> readCount)
        {
            var popped = DateTime.Now;
            var sincePop = Stopwatch.StartNew();
            var act = acts[point.Action];
            var label = Label(point);
            var floor = point.BeforeActivate ? floors.Activate : act.Attack ? floors.Attack : floors.Move;
            var dying = new Dictionary<(int X, int Y), (double First, double Last)>();

            GateVerdict verdict;
            GateBoard? last = null;
            while (true)
            {
                var first = ReadGateBoard(chain, player);
                var second = ReadGateBoard(chain, player);
                last = second ?? last;
                var spent = sincePop.Elapsed.TotalMilliseconds;
                foreach (var u in second?.Units.Where(u => u.Health == 0) ?? [])
                {
                    dying[(u.X, u.Y)] = dying.TryGetValue((u.X, u.Y), out var seen) ? (seen.First, spent) : (spent, spent);
                }

                verdict = GateSettle(first, second, spent, GateSettleBudgetMs,
                                     b => point.BeforeActivate
                                         ? JudgeBeforeActivate(b, act, point.Action)
                                         : JudgeBeforeStrike(b, act, point.Action));
                if (verdict.Step != GateStep.Wait)
                {
                    break;
                }

                _waits++;
                Thread.Sleep(2);
            }

            var decided = DateTime.Now;
            var decidedMs = sincePop.Elapsed.TotalMilliseconds;
            var consumed = readCount() is { } count && slots - (int)count > key;
            var late = GateLate(decidedMs, floor, consumed);
            var margin = floor * 1000 - decidedMs;
            _checked++;
            _leastMargin = Math.Min(_leastMargin, margin);
            if (late)
            {
                _late++;
            }

            _pending.Add($"  gate {label}  popped {popped:HH:mm:ss.fff}  verdict {decided:HH:mm:ss.fff} " +
                         $"(+{decidedMs:0} ms)  margin {margin:0} ms of the {floor:0.00} s floor  " +
                         (verdict.Step == GateStep.Pass ? "pass" : "REFUSED") + (late ? "  LATE" : ""));
            foreach (var (square, seen) in dying)
            {
                var still = last?.Units.Any(u => (u.X, u.Y) == square && u.Health == 0) == true;
                _pending.Add($"  gate {label}  a machine at 0 health on ({square.X},{square.Y}) listed from " +
                             $"+{seen.First:0} ms to +{seen.Last:0} ms" + (still ? ", still listed" : ", then gone"));
            }

            if (verdict.Step == GateStep.Pass)
            {
                _awaitingNext = (key, label, decided);
                return false;
            }

            _refusal = verdict.Problem;
            _refusedLate = late;
            return true;
        }

        public string? Summary()
        {
            if (_checked == 0)
            {
                return null;
            }

            return $"gate: {_checked} checkpoint(s), least margin {_leastMargin:0} ms, {_waits} settle wait(s), " +
                   $"{_late} late";
        }
    }
}
