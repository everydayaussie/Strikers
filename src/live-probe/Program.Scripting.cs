namespace Strikers.LiveProbe;

internal static partial class Program
{
    private static int Hijack(byte action, byte src, byte dst, int seconds, string[] args)
    {
        var res = FindOne(_base + 0x190E7A8);
        if (res == 0)
        {
            Console.Error.WriteLine("AIBoardGamePlayer not found.");
            return 3;
        }

        Console.WriteLine($"\n  AIBoardGamePlayer 0x{res:X}");
        Console.WriteLine($"  will inject: action 0x{action:X2}  src 0x{src:X2} ({src & 0xF},{src >> 4})  " +
                          $"dst 0x{dst:X2} ({dst & 0xF},{dst >> 4})");

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("\n  dry run, nothing written. Add --yes to apply.");
            return 0;
        }

        var waits = new[] { res + 0x5C, res + 0x64, res + 0x6C };
        var saved = waits.Select(a => Read(a, 8)).ToArray();
        var slow = new byte[8];
        BitConverter.GetBytes(20.0f).CopyTo(slow, 0);
        BitConverter.GetBytes(20.0f).CopyTo(slow, 4);
        foreach (var a in waits)
        {
            Write(a, slow);
        }

        Console.WriteLine("  AI slowed to 20s per action");

        try
        {
            var vtable = _base + 0x1906AD0;
            var until = DateTime.UtcNow.AddSeconds(seconds);
            Console.WriteLine("  READY, end your turn now");

            while (DateTime.UtcNow < until)
            {
                var obj = FindOne(vtable, res >> 40);
                if (obj == 0)
                {
                    continue;
                }

                Console.WriteLine($"    found LathiumPlayerEasy at 0x{obj:X}");

                var mine = new byte[] { action, src, dst, 0 };
                var writes = 0;
                var seen = new HashSet<string>();

                var mineHex = Convert.ToHexString(mine);

                while (DateTime.UtcNow < until && ReadPtr(obj) == vtable)
                {
                    var asHex = Convert.ToHexString(Read(obj + 0xF0, 4));
                    if (asHex != mineHex)
                    {
                        if (seen.Add(asHex))
                        {
                            Console.WriteLine($"    {DateTime.UtcNow:HH:mm:ss.fff}  AI wrote {asHex} " +
                                              $"(done {ReadByte(obj + 0xFC):X2}), overwriting");
                        }

                        Write(obj + 0xF0, mine);
                        writes++;
                    }

                    Thread.Sleep(1);
                }

                Console.WriteLine($"    object gone after {writes} write(s) of {Convert.ToHexString(mine)}");
                return 0;
            }

            Console.WriteLine("  never caught the decision");
            return 4;
        }
        finally
        {
            for (var i = 0; i < waits.Length; i++)
            {
                Write(waits[i], saved[i]);
            }

            Console.WriteLine("  AI wait times restored");
        }
    }

    private static int ScriptMove(string[] args, int firstArg)
    {
        if (firstArg + 3 >= args.Length)
        {
            Console.Error.WriteLine("--script-move <srcX> <srcY> <dstX> <dstY> [--attack] [--from X Y] [--burst] " +
                                    "[--facing N] [--secs N] [--player N] [--yes]");
            Console.Error.WriteLine("  activates the AI's piece at src, then moves it to dst, or with");
            Console.Error.WriteLine("  --attack, strikes the piece standing on dst instead of moving.");
            Console.Error.WriteLine("  --from names the square the attacker stands on to strike (a ranged attack);");
            Console.Error.WriteLine("  without it the square one back from the victim is derived, which is a range-1 attack.");
            Console.Error.WriteLine("  --burst inserts a burst (Overcharge) before the action: 2 health for an extra one.");
            Console.Error.WriteLine("  tiles are the game's coordinates, zero-based, y=0 at the opponent's edge.");
            return 1;
        }

        var srcX = (int)ParseAddr(args, firstArg);
        var srcY = (int)ParseAddr(args, firstArg + 1);
        var dstX = (int)ParseAddr(args, firstArg + 2);
        var dstY = (int)ParseAddr(args, firstArg + 3);

        var facingAt = IndexOfArg(args, "--facing");
        var facing = (byte)(facingAt >= 0 ? ParseAddr(args, facingAt + 1) & 3 : 0);
        var attackOne = args.Contains("--attack");

        var fromAt = IndexOfArg(args, "--from");
        var fromX = fromAt >= 0 ? (int)ParseAddr(args, fromAt + 1) : -1;
        var fromY = fromAt >= 0 ? (int)ParseAddr(args, fromAt + 2) : -1;

        if (fromAt >= 0 && !attackOne)
        {
            Console.Error.WriteLine("  --from only means something on an attack: a move's destination IS its square.");
            return 1;
        }

        if (attackOne && facingAt < 0)
        {
            var aimX = fromX >= 0 ? fromX : srcX;
            var aimY = fromY >= 0 ? fromY : srcY;

            if (!DeriveFacing(aimX, aimY, dstX, dstY, out facing))
            {
                return 6;
            }

            Console.WriteLine($"\n  facing {facing} derived from ({aimX},{aimY}) -> ({dstX},{dstY})" +
                              $"  ({"N E S W".Split(' ')[facing]})");
        }

        return ScriptActions([new Act(attackOne, srcX, srcY, dstX, dstY, facing, args.Contains("--burst"),
                                      fromX, fromY)], args);
    }

    private static int ScriptTurn(string[] args, int firstArg)
    {
        if (firstArg >= args.Length)
        {
            Console.Error.WriteLine("--script-turn \"move,sx,sy,dx,dy,f;attack,sx,sy,vx,vy,f[,fromX,fromY]\" [--secs N] [--stall N] [--player N] [--force] [--yes]");
            Console.Error.WriteLine("  every action of one turn, armed together and ended once.");
            Console.Error.WriteLine("  exit 0 only if the WHOLE turn was applied; 6 if it stopped part-way.");
            Console.Error.WriteLine("  --stall N is how long with nothing consumed counts as stopped (default 20s).");
            return 1;
        }

        var acts = new List<Act>();
        foreach (var spec in args[firstArg].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = spec.Split(',');
            if (f.Length is < 6 or > 9)
            {
                Console.Error.WriteLine($"  '{spec}' has {f.Length} fields, expected 6 to 9.");
                return 1;
            }

            var kind = f[0].Trim().ToLowerInvariant();
            if (kind is not ("move" or "attack"))
            {
                Console.Error.WriteLine($"  '{kind}' is not move or attack.");
                return 1;
            }

            if (!int.TryParse(f[1], out var sx) || !int.TryParse(f[2], out var sy) ||
                !int.TryParse(f[3], out var dx) || !int.TryParse(f[4], out var dy) ||
                !byte.TryParse(f[5], out var fc))
            {
                Console.Error.WriteLine($"  '{spec}' has a field that is not a number.");
                return 1;
            }

            var hasFrom = f.Length >= 8;
            var fromX = -1;
            var fromY = -1;

            if (hasFrom)
            {
                if (kind != "attack")
                {
                    Console.Error.WriteLine($"  '{spec}': a from pair only means something on an attack.");
                    return 1;
                }

                if (!int.TryParse(f[6], out fromX) || !int.TryParse(f[7], out fromY))
                {
                    Console.Error.WriteLine($"  '{spec}' has a from field that is not a number.");
                    return 1;
                }
            }

            var burstField = hasFrom ? 8 : 6;
            acts.Add(new Act(kind == "attack", sx, sy, dx, dy, (byte)(fc & 3),
                             f.Length == burstField + 1 && f[burstField].Trim() is "burst" or "1" or "true",
                             fromX, fromY));
        }

        if (acts.Count == 0)
        {
            Console.Error.WriteLine("  no actions given.");
            return 1;
        }

        return ScriptActions(acts, args);
    }

    private static bool DeriveFacing(int srcX, int srcY, int dstX, int dstY, out byte facing)
    {
        int dx = dstX - srcX, dy = dstY - srcY;
        facing = 0;
        if (dx != 0 && dy != 0)
        {
            Console.Error.WriteLine($"\n  ({srcX},{srcY}) -> ({dstX},{dstY}) is diagonal, and facing is one of " +
                                    "four directions.");
            Console.Error.WriteLine("  Pass --facing 0|1|2|3 (N/E/S/W) explicitly if you mean to.");
            return false;
        }
        facing = (byte)(dx > 0 ? 1 : dx < 0 ? 3 : dy > 0 ? 2 : 0);
        return true;
    }

    private sealed record Act(bool Attack, int SrcX, int SrcY, int DstX, int DstY, byte Facing, bool Burst,
                              int FromX = -1, int FromY = -1);

    private static (int X, int Y) StrikeFrom(int victimX, int victimY, byte facing)
    {
        return (facing & 3) switch
        {
            0 => (victimX, victimY + 1),
            1 => (victimX - 1, victimY),
            2 => (victimX, victimY - 1),
            _ => (victimX + 1, victimY),
        };
    }

    private const int SpreadSkill = 5;

    private static bool InSpreadReach(int fromX, int fromY, byte facing, int range, int toX, int toY)
    {
        if (range < 1)
        {
            return false;
        }

        var (dx, dy) = (facing & 3) switch
        {
            0 => (0, -1),
            1 => (1, 0),
            2 => (0, 1),
            _ => (-1, 0),
        };

        var along = dx == 0 ? (toY - fromY) * dy : (toX - fromX) * dx;
        var aside = dx == 0 ? Math.Abs(toX - fromX) : Math.Abs(toY - fromY);
        return along >= 1 && along <= range && aside <= 1;
    }

    private static (int Skill, int Range) SkillAndRange(ulong unit)
    {
        var resource = ReadPtr(unit + 0x08);
        if (!Sane(resource))
        {
            return (-1, -1);
        }

        var range = TryRead(resource + 0x5C, 4, out var rangeBytes) ? BitConverter.ToInt32(rangeBytes, 0) : -1;
        var ability = ReadPtr(resource + 0x48);
        var skill = Sane(ability) && TryRead(ability + 0x38, 1, out var skillBytes) ? skillBytes[0] : -1;
        return (skill, range);
    }

    private static (int X, int Y) StandSquare(Act a)
    {
        if (!a.Attack)
        {
            return (a.DstX, a.DstY);
        }

        if (a.FromX >= 0 && a.FromY >= 0)
        {
            return (a.FromX, a.FromY);
        }

        return StrikeFrom(a.DstX, a.DstY, a.Facing);
    }

    private static bool StandsOffBoard(Act a, int width, int height)
    {
        var (dx, dy) = StandSquare(a);
        return dx < 0 || dx >= width || dy < 0 || dy >= height;
    }

    private static List<Act> MergeRotateAttacks(List<Act> acts)
    {
        var merged = new List<Act>();
        var i = 0;
        while (i < acts.Count)
        {
            var move = acts[i];
            if (i + 1 < acts.Count && !move.Attack && !move.Burst)
            {
                var strike = acts[i + 1];
                var inPlace = StandSquare(strike) == (strike.SrcX, strike.SrcY);
                var sameMachine = strike.SrcX == move.DstX && strike.SrcY == move.DstY;
                var ownMoveNext = i + 2 < acts.Count
                                  && acts[i + 2].SrcX == strike.SrcX
                                  && acts[i + 2].SrcY == strike.SrcY;

                if (strike.Attack && !strike.Burst && inPlace && sameMachine && !ownMoveNext)
                {
                    merged.Add(new Act(true, move.SrcX, move.SrcY, strike.DstX, strike.DstY, strike.Facing, false,
                                       move.DstX, move.DstY));
                    i += 2;
                    continue;
                }
            }

            merged.Add(move);
            i++;
        }

        return merged;
    }

    private static int SelfTest()
    {
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

        Check("a placement into the seat the AI holds is allowed",
              !WrongSeat(aiSeat: 1, askedSeat: 1, forced: false));
        Check("a placement into the other seat is refused, because that is the local player's",
              WrongSeat(aiSeat: 0, askedSeat: 1, forced: false));
        Check("an unresolved AI seat cannot judge the request, so it does not refuse it",
              !WrongSeat(aiSeat: -1, askedSeat: 1, forced: false));
        Check("--force is the operator's way past it, and is never on the injection path",
              !WrongSeat(aiSeat: 0, askedSeat: 1, forced: true));

        Check("a placing depth that fits the board is written",
              DepthRefusal(askedDepth: 2, rowCount: 8) is null);
        Check("a placing depth deeper than the board is refused",
              DepthRefusal(askedDepth: 8, rowCount: 5) is not null);
        Check("a depth exactly as deep as the board is allowed, because D-125 kept only the structural half",
              DepthRefusal(askedDepth: 8, rowCount: 8) is null);
        Check("a board that cannot be read refuses the write instead of skipping the bound",
              DepthRefusal(askedDepth: 2, rowCount: 0) is not null);

        Check("a page-aligned pointer reads as ours",
              OwnPage(0x2751A330000) && OwnPage(0x1000));
        Check("a pointer inside a blob does not read as ours",
              !OwnPage(0x275B399E9B0) && !OwnPage(0x27C0BB32C50));

        Check("the grant bound is a stated cap, well above any real army",
              MaxGrantArmy == 24 && MaxGrantArmy > 9);

        Check("an on-board move is not off-board",
              !StandsOffBoard(new Act(false, 3, 6, 3, 5, 0, false), 8, 8));
        Check("a move to an off-board row is caught",
              StandsOffBoard(new Act(false, 3, 6, 3, 8, 0, false), 8, 8));
        Check("a move to a negative column is caught",
              StandsOffBoard(new Act(false, 0, 0, -1, 0, 0, false), 8, 8));

        Check("an attack whose stand square is off-board is caught",
              StandsOffBoard(new Act(true, 4, 1, 4, 0, 2, false), 8, 8));
        Check("an attack whose stand square is on-board is allowed",
              !StandsOffBoard(new Act(true, 4, 4, 4, 3, 2, false), 8, 8));

        Check("a caller-named firing square off the board is caught",
              StandsOffBoard(new Act(true, 4, 4, 4, 3, 2, false, FromX: 9, FromY: 4), 8, 8));

        Check("a move's stand square is its destination",
              StandSquare(new Act(false, 3, 6, 3, 5, 0, false)) == (3, 5));

        Check("a move and the same machine's Rotate attack become one walk-then-attack before another machine acts",
              MergeRotateAttacks([
                  new Act(false, 1, 1, 1, 3, 2, false),
                  new Act(true, 1, 3, 0, 3, 3, false, 1, 3),
                  new Act(true, 4, 0, 4, 3, 2, false, 4, 1),
              ]).SequenceEqual([
                  new Act(true, 1, 1, 0, 3, 3, false, 1, 3),
                  new Act(true, 4, 0, 4, 3, 2, false, 4, 1),
              ]));

        Check("a Rotate attack that is the turn's last action merges too",
              MergeRotateAttacks([
                  new Act(false, 2, 6, 2, 4, 0, false),
                  new Act(true, 2, 4, 3, 4, 1, false, 2, 4),
              ]).SequenceEqual([
                  new Act(true, 2, 6, 3, 4, 1, false, 2, 4),
              ]));

        List<Act> ownedMove =
        [
            new Act(false, 1, 1, 1, 3, 2, false),
            new Act(true, 1, 3, 0, 3, 3, false, 1, 3),
            new Act(false, 1, 3, 2, 3, 1, false),
        ];
        Check("an in-place attack followed by that machine's own move is left alone",
              MergeRotateAttacks(ownedMove).SequenceEqual(ownedMove));

        List<Act> overcharged =
        [
            new Act(false, 1, 1, 1, 3, 2, false),
            new Act(true, 1, 3, 0, 3, 3, true, 1, 3),
            new Act(true, 4, 0, 4, 3, 2, false, 4, 1),
        ];
        Check("an Overcharge attack after a move is left alone",
              MergeRotateAttacks(overcharged).SequenceEqual(overcharged));

        List<Act> walkThenStrike =
        [
            new Act(false, 1, 1, 1, 3, 2, false),
            new Act(true, 1, 3, 0, 2, 0, false, 0, 3),
            new Act(true, 4, 0, 4, 3, 2, false, 4, 1),
        ];
        Check("an attack that walks first is left alone",
              MergeRotateAttacks(walkThenStrike).SequenceEqual(walkThenStrike));

        Check("a straight strike derives a single facing",
              DeriveFacing(4, 5, 4, 3, out var fN) && fN == 0);

        Check("a Sweep's off-axis victim at max range is inside the reach",
              InSpreadReach(3, 3, 0, 2, 4, 1) && InSpreadReach(3, 3, 0, 2, 2, 1));

        Check("the square a Sweep aims at is inside the reach",
              InSpreadReach(3, 3, 0, 2, 3, 1));

        Check("a victim nearer than max range, on the line or beside it, is inside the reach",
              InSpreadReach(3, 3, 0, 2, 3, 2) && InSpreadReach(3, 3, 0, 2, 4, 2));

        Check("the reach ends at max range and one column off the line",
              !InSpreadReach(3, 3, 0, 2, 3, 0) && !InSpreadReach(3, 3, 0, 2, 5, 1));

        Check("the reach turns with the facing and never reaches behind",
              InSpreadReach(2, 0, 1, 2, 4, 1) && !InSpreadReach(2, 0, 1, 2, 1, 0) && !InSpreadReach(2, 0, 1, 2, 2, 1));

        Check("an unreadable range reaches nothing",
              !InSpreadReach(3, 3, 0, -1, 4, 1) && !InSpreadReach(3, 3, 0, 0, 4, 1));
        Check("a name of the full length is accepted",
              NameProblem("EXAMPLE1") is null && NameProblem("A") is null);

        Check("a name one character too long is refused",
              NameProblem("EXAMPLE12") is not null);

        Check("an empty name is refused",
              NameProblem("") is not null && NameProblem(null) is not null);

        Check("a name outside letters and digits is refused",
              NameProblem("A'B") is not null && NameProblem("A B") is not null
              && NameProblem("aloy") is not null);

        var ownHeader = Convert.FromHexString("01000000FFFFFFFF040000001F000000");
        var drawnOpponentHeader = Convert.FromHexString("01000000FFFFFFFF080000002F000000");
        var internedHeader = Convert.FromHexString("0100008012B2E8280800000008000000");
        Check("a label's room is read from its block's header: 15, 31 and 8 for the three shapes seen",
              RoomFromHeader(ownHeader, 4) == 15 && RoomFromHeader(drawnOpponentHeader, 8) == 31
              && RoomFromHeader(internedHeader, 8) == 8);
        Check("a header whose length is not the label's, or whose capacity is not a block's, is no header",
              RoomFromHeader(ownHeader, 8) is null
              && RoomFromHeader(Convert.FromHexString("00000000000000000000000000000000"), 4) is null
              && RoomFromHeader(Convert.FromHexString("01000000FFFFFFFF0400000020000000"), 4) is null
              && RoomFromHeader(Convert.FromHexString("414C4F59006C697368"), 4) is null);
        Check("a name longer than its room is cut to it, a shorter one is kept",
              FitToRoom("LONGNAMEFORTHECORNER", 15) == "LONGNAMEFORTHEC" && FitToRoom("PC", 15) == "PC"
              && FitToRoom("EXAMPLENAME1", 12) == "EXAMPLENAME1" && FitToRoom(null, 4) is null);

        Check("the turn banner is always exactly the 15 bytes it replaces, a full name shown whole",
              TurnBanner("EXAMPLE")?.Length == 15 && TurnBanner("A")?.Length == 15
              && TurnBanner("EXAMPLE1") == "EXAMPLE1'S TURN");

        Check("the banner keeps the name and reads as a turn",
              TurnBanner("ROST") == "ROST'S TURN    ");

        Check("a name that cannot be a name has no banner",
              TurnBanner("EXAMPLENAME12") is null && TurnBanner("") is null);

        var planned = PlanLine("their corner", 0x1234, System.Text.Encoding.ASCII.GetBytes("ASPHALT\0"));
        var lengthField = PlanLine("their corner length", 0x122C, BitConverter.GetBytes(7u));
        var cut = CutNote("their turn banner", 0x5678, headerRead: true, 5);
        var refusals = new[] { NameProblem("ASPHALTER"), NameProblem("ASPHALT!") };
        Check("no line of the names write carries a name, so a legal ASPHALT cannot spell a halt",
              !planned.Contains("halt", StringComparison.OrdinalIgnoreCase) && planned.Contains("8 bytes")
              && lengthField.EndsWith("  7") && cut.Contains("first 5")
              && refusals.All(r => r is not null && !r.Contains("halt", StringComparison.OrdinalIgnoreCase)));

        Check("a diagonal strike has no single facing and is refused",
              !DeriveFacing(4, 5, 6, 3, out _));

        Check("a board count inside the allocation is accepted",
              ShapeProblem(1, "x") is null && ShapeProblem(8, "x") is null);

        Check("a board count past what the game allocates is refused",
              ShapeProblem(9, "x") is not null && ShapeProblem(1000, "x") is not null);

        Check("a board count of zero or less is refused",
              ShapeProblem(0, "x") is not null && ShapeProblem(-1, "x") is not null);

        Check("a board write walks at most the rows the game allocates, whatever the count claims",
              RowsToWalk(8) == 8 && RowsToWalk(1) == 1
              && RowsToWalk(9) == 8 && RowsToWalk(1000) == 8 && RowsToWalk(int.MaxValue) == 8);

        Check("a peer's facing is masked to two bits, whatever byte arrives",
              Facing(0) == 0 && Facing(3) == 3 && Facing(4) == 0 && Facing(255) == 3
              && Facing(-1) == 3 && Facing(int.MaxValue) == 3);

        Check("a placement square off the board is refused rather than substituted",
              !SquareOffPlacingBoard(0, 0) && !SquareOffPlacingBoard(7, 7)
              && SquareOffPlacingBoard(8, 0) && SquareOffPlacingBoard(0, 8)
              && SquareOffPlacingBoard(-1, 0) && SquareOffPlacingBoard(0, -1)
              && SquareOffPlacingBoard(int.MinValue, 0) && SquareOffPlacingBoard(0, int.MaxValue));

        Check("a rel32 displacement is measured from the end of the instruction",
              BitConverter.ToInt32(Rel32(0x1000, 0x1005, 5)) == 0 &&
              BitConverter.ToInt32(Rel32(0x1000, 0x2000, 5)) == 0x0FFB &&
              BitConverter.ToInt32(Rel32(0x2000, 0x1000, 5)) == -0x1005);

        Check("a rel32 over 2 GB is refused rather than truncated",
              Throws(() => Rel32(0x140000000, 0x340000000, 5)) &&
              Throws(() => Rel32(0x340000000, 0x140000000, 5)) &&
              !Throws(() => Rel32(0x140000000, 0x140001000, 5)));

        var stubProbe = _base + 0x100000;
        Check("all three stubs fit in the single page they share",
              MoveBoundsStub1(stubProbe + 0x10, stubProbe).Length +
              MoveBoundsStub2(stubProbe + 0x40, stubProbe).Length +
              MoveBoundsStub3(stubProbe + 0x80, stubProbe).Length + 0x40 < 0x1000);

        Check("no stub dereferences a game pointer",
              new[]
              {
                  MoveBoundsStub1(stubProbe + 0x10, stubProbe),
                  MoveBoundsStub2(stubProbe + 0x40, stubProbe),
                  MoveBoundsStub3(stubProbe + 0x80, stubProbe),
              }.All(s => !Contains(s, [0x4D, 0x8B, 0x52, 0x28]) &&
                         !Contains(s, [0x4D, 0x8B, 0x12]) &&
                         !Contains(s, [0x4C, 0x8B, 0x56, 0xC0]) &&
                         !Contains(s, [0x4D, 0x8B, 0x51, 0x08])));

        Check("each stub compares against the width slot at the page base",
              new[] { (0x10, 3), (0x40, 2), (0x80, 3) }.All(p =>
              {
                  var stub = p.Item1 == 0x10 ? MoveBoundsStub1(stubProbe + (ulong)p.Item1, stubProbe)
                           : p.Item1 == 0x40 ? MoveBoundsStub2(stubProbe + (ulong)p.Item1, stubProbe)
                           : MoveBoundsStub3(stubProbe + (ulong)p.Item1, stubProbe);
                  var i = IndexOf(stub, [0x3B, 0x05]);
                  if (i < 0)
                  {
                      return false;
                  }

                  var disp = BitConverter.ToInt32(stub, i + 2);
                  var from = stubProbe + (ulong)p.Item1 + (ulong)i + 6;
                  return (ulong)((long)from + disp) == stubProbe;
              }));

        Check("each patch fits the stock run it replaces, with room for the jump",
              MoveBoundsSite1Original.Length >= 5 && MoveBoundsSite2Original.Length >= 5 &&
              MoveBoundsSite3Original.Length >= 5 &&
              MoveBoundsSite1Original.Length == (int)(MoveBoundsSite1Back - MoveBoundsSite1Rva) &&
              MoveBoundsSite2Original.Length == (int)(MoveBoundsSite2Back - MoveBoundsSite2Rva) &&
              MoveBoundsSite3Original.Length == (int)(MoveBoundsSite3Back - MoveBoundsSite3Rva));

        Check("each retired site's stock bytes fill the run an older build's jump replaced",
              RetiredSite4Original.Length >= 5 && RetiredSite5Original.Length >= 5 &&
              RetiredSite6Original.Length >= 5 &&
              RetiredSite4Original.Length == (int)(RetiredSite4Back - RetiredSite4Rva) &&
              RetiredSite5Original.Length == (int)(RetiredSite5Back - RetiredSite5Rva) &&
              RetiredSite6Original.Length == (int)(RetiredSite6Back - RetiredSite6Rva) &&
              RetiredSites().Length == 3);

        Check("stub 3 hands the y-check the height slot, not the game's field",
              Contains(MoveBoundsStub3(stubProbe + 0x80, stubProbe), [0x44, 0x8B, 0x05]) &&
              !Contains(MoveBoundsStub3(stubProbe + 0x80, stubProbe), [0x44, 0x8B, 0x46, 0x10]));

        Check("all three sites are recorded as loading the single shared bound",
              MoveBoundsSite1Original[..4].SequenceEqual(new byte[] { 0x41, 0x8B, 0x49, 0x58 }) &&
              MoveBoundsSite2Original[..4].SequenceEqual(new byte[] { 0x41, 0x8B, 0x49, 0x58 }) &&
              MoveBoundsSite3Original[..4].SequenceEqual(new byte[] { 0x44, 0x8B, 0x46, 0x10 }));

        Check("stubs 1 and 2 load ecx from the height slot, not from logic+0x58",
              Contains(MoveBoundsStub1(stubProbe + 0x10, stubProbe), [0x8B, 0x0D]) &&
              !Contains(MoveBoundsStub1(stubProbe + 0x10, stubProbe), [0x41, 0x8B, 0x49, 0x58]) &&
              Contains(MoveBoundsStub2(stubProbe + 0x40, stubProbe), [0x8B, 0x0D]) &&
              !Contains(MoveBoundsStub2(stubProbe + 0x40, stubProbe), [0x41, 0x8B, 0x49, 0x58]));
        Check("the height reads land on the height slot, four bytes past the width",
              new[] { (0x10, MoveBoundsStub1(stubProbe + 0x10, stubProbe), new byte[] { 0x8B, 0x0D }, 6),
                      (0x40, MoveBoundsStub2(stubProbe + 0x40, stubProbe), new byte[] { 0x8B, 0x0D }, 6),
                      (0x80, MoveBoundsStub3(stubProbe + 0x80, stubProbe), new byte[] { 0x44, 0x8B, 0x05 }, 7) }.All(p =>
              {
                  var (at, stub, opcode, length) = p;
                  var i = IndexOf(stub, opcode);
                  if (i < 0)
                  {
                      return false;
                  }

                  var disp = BitConverter.ToInt32(stub, i + opcode.Length);
                  var from = stubProbe + (ulong)at + (ulong)i + (ulong)length;
                  return (ulong)((long)from + disp) == stubProbe + 4;
              }));

        Check("no stub touches the stack",
              new[]
              {
                  MoveBoundsStub1(stubProbe + 0x10, stubProbe),
                  MoveBoundsStub2(stubProbe + 0x40, stubProbe),
                  MoveBoundsStub3(stubProbe + 0x80, stubProbe),
              }.All(s => !Contains(s, [0x41, 0x52]) && !Contains(s, [0x41, 0x5A])));

        Check("stub 2 sets rdi on the accept path, as the reject path does",
              Contains(MoveBoundsStub2(stubProbe + 0x40, stubProbe), [0x49, 0x8D, 0x79, 0x58]));

        var stub8 = MoveBoundsStub8(stubProbe + 0x1C0, 6);
        Check("stub 8 carries the builder's two instructions verbatim and ends in a jump back",
              stub8[..11].SequenceEqual(MoveBoundsSite8Original) && stub8[^5] == 0xE9 &&
              MoveBoundsSite8Original.Length == 11 &&
              MoveBoundsSite8Original.Length == (int)(MoveBoundsSite8Back - MoveBoundsSite8Rva));
        Check("stub 8 on a 6-wide board zeroes two padding bytes at the row end and skips the counter by two",
              Contains(stub8, [0x41, 0x83, 0xFA, 0x05]) &&
              Contains(stub8, [0xC6, 0x44, 0x11, 0x71, 0x00]) && Contains(stub8, [0xC6, 0x44, 0x11, 0x72, 0x00]) &&
              !Contains(stub8, [0xC6, 0x44, 0x11, 0x73, 0x00]) &&
              Contains(stub8, [0x83, 0x82, 0xB0, 0x00, 0x00, 0x00, 0x02]));
        Check("stub 8's row-end skip lands exactly on the jump back",
              stub8[22] == 0x75 && stub8[23] == 17 && stub8.Length == 46);
        Check("stub 8 on an 8-wide board is the displaced pair and the jump back alone",
              MoveBoundsStub8(stubProbe + 0x1C0, 8).Length == 16);
        Check("stub 8 touches r10 only, no stack, no game pointer",
              !Contains(stub8, [0x41, 0x52]) && !Contains(stub8, [0x41, 0x5A]) &&
              !Contains(stub8, [0x4D, 0x8B, 0x12]) && !Contains(stub8, [0x48, 0x8B]));
        Check("all five stubs fit in the single page they share",
              MoveBoundsStub1(stubProbe + 0x10, stubProbe).Length +
              MoveBoundsStub2(stubProbe + 0x40, stubProbe).Length +
              MoveBoundsStub3(stubProbe + 0x80, stubProbe).Length +
              MoveBoundsStub7(stubProbe + 0x180, stubProbe).Length + MoveBoundsStub8(stubProbe + 0x1C0, 1).Length + 0x60 < 0x1000);

        var stub7 = MoveBoundsStub7(stubProbe + 0x180, stubProbe);
        Check("stub 7 compares x against the width slot and y against the height slot, in that order",
              Count(stub7, [0x3B, 0x05]) == 2 &&
              new[] { (IndexOf(stub7, [0x3B, 0x05]), 0UL), (IndexOf(stub7, [0x3B, 0x05]) + 2 + IndexOf(stub7[(IndexOf(stub7, [0x3B, 0x05]) + 2)..], [0x3B, 0x05]), 4UL) }.All(p =>
              {
                  var (i, slot) = p;
                  var disp = BitConverter.ToInt32(stub7, i + 2);
                  var from = stubProbe + 0x180 + (ulong)i + 6;
                  return (ulong)((long)from + disp) == stubProbe + slot;
              }));
        Check("stub 7 rejects through the game's own path twice and ends in a jump back",
              Count(stub7, [0x0F, 0x8D]) == 2 && stub7[^5] == 0xE9 &&
              !Contains(stub7, [0x8B, 0x53, 0x10]) && !Contains(stub7, [0x41, 0x52]) && !Contains(stub7, [0x41, 0x5A]));
        Check("site 7's stock run is the bound load through both compares, 22 bytes to the accept path",
              MoveBoundsSite7Original.Length == 22 &&
              MoveBoundsSite7Original[..3].SequenceEqual(new byte[] { 0x8B, 0x53, 0x10 }) &&
              MoveBoundsSite7Original.Length == (int)(MoveBoundsSite7Back - MoveBoundsSite7Rva) &&
              MoveBoundsSite7Reject == MoveBoundsSite7Back + 8);

        var ringProbe = stubProbe + 0x2000;
        var commitStubAt = new Func<int, ulong>(i => stubProbe + (ulong)(CommitStubsAt + (i * CommitStubStride)));
        var commitStubs = CommitSites.Select((site, i) =>
                                             CommitStub(commitStubAt(i), ringProbe, site.Kind, site.Stock,
                                                        stubProbe + 0x9000 + site.Rva + (ulong)site.Stock.Length)).ToList();
        Check("commit log: every stub fits its 0x100 slot and ends in a jump back",
              commitStubs.All(c => c.Length < CommitStubStride && c[^5] == 0xE9));
        Check("commit log: the stubs clear the page header and the last one still ends inside the page",
              CommitStubsAt >= CommitRingSlot + 8 &&
              (ulong)CommitStubsAt + (ulong)((CommitSites.Length - 1) * CommitStubStride) +
              (ulong)commitStubs[^1].Length <= 0x1000);
        Check("commit log: every stub reads the ring page RIP-relative, from its first instruction",
              commitStubs.Select((c, i) =>
              {
                  if (c[0] != 0x4C || c[1] != 0x8D || c[2] != 0x15)
                  {
                      return false;
                  }

                  var disp = BitConverter.ToInt32(c, 3);
                  var from = commitStubAt(i) + 7;
                  return (ulong)((long)from + disp) == ringProbe;
              }).All(ok => ok));
        Check("commit log: the displaced instruction sits before the jump back, and no stub dereferences a unit or the argument",
              commitStubs.Select((c, i) =>
              {
                  var displaced = CommitSites[i].Stock;
                  var beforeJump = c.Length - 5 - displaced.Length;
                  return c.Skip(beforeJump).Take(displaced.Length).SequenceEqual(displaced) &&
                         !Contains(c, [0x8B, 0x02]) && !Contains(c, [0x8B, 0x42]) && !Contains(c, [0x0F, 0xB6]) &&
                         Count(c, [0x48, 0x8B, 0x81, 0x88, 0x00, 0x00, 0x00]) == 1;
              }).All(ok => ok));
        Check("commit log: every stub stores the controller's vtable and no stub reads +0xB0",
              commitStubs.All(c => Count(c, [0x48, 0x8B, 0x01]) == 1 && Contains(c, [0x49, 0x89, 0x43, 0x30]) &&
                                   !Contains(c, [0x8B, 0x81, 0xB0, 0x00, 0x00, 0x00])));
        Check("commit log: the four stock entries are five-byte register saves with no relative operand",
              CommitSites.All(site => site.Stock.Length == 5 && site.Stock[0] == 0x48 && site.Stock[1] == 0x89 &&
                                      site.Stock[3] == 0x24) &&
              CommitSites.Select(site => site.Rva).Distinct().Count() == 4);

        Check("commit ring: the page is decoded back out of the jump the patch writes",
              CommitSites.Select((site, i) =>
              {
                  var entry = _base + site.Rva;
                  var jump = new List<byte> { 0xE9 };
                  jump.AddRange(Rel32(entry, commitStubAt(i), 5));
                  return CommitPageFromJump(entry, [.. jump]) == commitStubAt(i) - CommitStubsAt;
              }).All(ok => ok));
        Check("commit ring: a stock entry and a short read decode to no page",
              CommitPageFromJump(0x1000, CommitSites[0].Stock) == 0 &&
              CommitPageFromJump(0x1000, [0xE9, 0x00]) == 0);

        var probeRecord = new byte[CommitRecordSize];
        BitConverter.GetBytes(2u).CopyTo(probeRecord, 0x00);
        BitConverter.GetBytes(37u).CopyTo(probeRecord, 0x04);
        BitConverter.GetBytes(0xC0FFEEUL).CopyTo(probeRecord, 0x08);
        BitConverter.GetBytes(0xBEEF00UL).CopyTo(probeRecord, 0x10);
        BitConverter.GetBytes(4).CopyTo(probeRecord, 0x18);
        BitConverter.GetBytes(1).CopyTo(probeRecord, 0x1C);
        BitConverter.GetBytes(3).CopyTo(probeRecord, 0x20);
        BitConverter.GetBytes(1).CopyTo(probeRecord, 0x24);
        BitConverter.GetBytes(0xDEAD00UL).CopyTo(probeRecord, 0x28);
        BitConverter.GetBytes(_base + 0x190D7C0).CopyTo(probeRecord, 0x30);
        BitConverter.GetBytes(4).CopyTo(probeRecord, 0x38);
        BitConverter.GetBytes(1).CopyTo(probeRecord, 0x3C);
        Check("commit ring: a move record reads as the human's, from the controller's unit and squares",
              CommitRecordJson(probeRecord, 7) ==
              "{\"seq\":37,\"kind\":\"move\",\"whose\":\"human\",\"unit\":7,\"ptr\":\"BEEF00\"," +
              "\"fx\":4,\"fy\":1,\"tx\":3,\"ty\":1,\"sx\":4,\"sy\":1}");
        BitConverter.GetBytes(1u).CopyTo(probeRecord, 0x00);
        Check("commit ring: an activate record names the machine the argument carries, not the controller's",
              CommitRecordMachine(probeRecord) == 0xDEAD00 &&
              CommitRecordJson(probeRecord, -1).Contains("\"kind\":\"activate\"") &&
              CommitRecordJson(probeRecord, -1).Contains("\"ptr\":\"DEAD00\""));
        BitConverter.GetBytes(2u).CopyTo(probeRecord, 0x00);
        BitConverter.GetBytes(_base + 0x190D708).CopyTo(probeRecord, 0x30);
        Check("commit ring: the AI's controller reads as the AI, and an unknown vtable as neither",
              CommitRecordJson(probeRecord, 7).Contains("\"whose\":\"ai\"") &&
              ControllerVtableName(0) == "?" && ControllerVtableName(_base + 0x1) == "?");
        Check("commit ring: a machine that has died since the record was made reads as slot -1",
              CommitRecordJson(probeRecord, -1).Contains("\"unit\":-1"));
        Check("commit ring: a reader starts at the ring's count, so history is not reported as lost",
              CommitsLostSince(seen: 0, started: false, written: 500) == 0 &&
              CommitsLostSince(seen: 40, started: true, written: 500) == 500 - 40 - CommitRecordCount &&
              CommitsLostSince(seen: 40, started: true, written: 50) == 0);

        Check("hold index: an empty board and a fresh controller read placement 0", AiPlacementIndex(0, 0) == 0);
        Check("hold index: first machine picked is placement 0", AiPlacementIndex(1, 1) == 0);
        Check("hold index: a fresh controller with one machine placed is placement 1",
              AiPlacementIndex(1, 0) == 1);
        Check("hold index: a committed controller stays on its own placement", AiPlacementIndex(4, 2) == 3);

        Check("restore: the same live match and resource is allowed",
              RestoreAllowed(0x1000, 0x2000, 0x1000, 0x3000, 0x2000));
        Check("restore: no live match is refused", RestoreAllowed(0x1000, 0x2000, 0, 0, 0) is false);
        Check("restore: a different match is refused", RestoreAllowed(0x1000, 0x2000, 0x1100, 0x3000, 0x2000) is false);
        Check("restore: the same match with a rebuilt resource is refused",
              RestoreAllowed(0x1000, 0x2000, 0x1000, 0x3000, 0x2100) is false);
        Check("arm undo: the same live match is detached, and a dead process is left alone",
              UndoAfterArm(processGone: false, sameMatch: true) == ArmUndo.Detach
              && UndoAfterArm(processGone: true, sameMatch: true) == ArmUndo.Nothing);
        Check("arm undo: a match left while armed gets its branch back and nothing written into it",
              UndoAfterArm(processGone: false, sameMatch: false) == ArmUndo.BranchOnly
              && UndoAfterArm(processGone: true, sameMatch: false) == ArmUndo.Nothing);
        Check("hold pin: the count-only index would not have held placement 1 before its pick",
              !PlacementNeedsPin(1 - 1, 1) && PlacementNeedsPin(AiPlacementIndex(1, 0), 1));

        Check("torn walk: one unit pointer twice is a torn read",
              TornByDuplicateUnit([0x2943A3D0880UL, 0x29459240380UL, 0x2943A3D0880UL]));
        Check("torn walk: distinct unit pointers are a board",
              !TornByDuplicateUnit([0x2943A3D0880UL, 0x29459240380UL, 0x2945924A100UL]));
        Check("hold index: fourth machine picked is placement 3, no transition needed",
              AiPlacementIndex(4, 1) == 3);
        Check("hold pin: placement 3 with three squares written holds",
              PlacementNeedsPin(AiPlacementIndex(4, 1), 3));
        Check("hold pin: placement 3 with four squares written releases",
              !PlacementNeedsPin(AiPlacementIndex(4, 1), 4));
        Check("hold pin: the frozen transition index would not have held placement 3",
              !PlacementNeedsPin(2, 3));

        Check("place-one pick: the previous placement finishing does not commit this record",
              !PickCommitsRecord(5, 1, 5));
        Check("place-one pick: a late pin on this placement commits this record",
              PickCommitsRecord(6, 1, 5));
        Check("place-one pick: before the pick nothing is committed", !PickCommitsRecord(5, 0, 5));
        Check("place-one pick: the first machine picked commits record 0", PickCommitsRecord(1, 1, 0));

        var slots = new Dictionary<int, int> { [0] = 5, [1] = 3, [2] = 0 };
        Check("record slot: each record hands its own machine",
              SlotForRecord(slots, 0) == 5 && SlotForRecord(slots, 1) == 3 && SlotForRecord(slots, 2) == 0);
        Check("record slot: a record no allow line has named hands nothing",
              SlotForRecord(slots, 3) == -1 && SlotForRecord(slots, -1) == -1);
        Check("record slot: an early square releases at once with its own machine",
              !PlacementNeedsPin(2, 3) && SlotForRecord(slots, 2) == 0);

        Check("hold count: a full roster is readable", UnitCountReadable(64));
        Check("hold count: a count past the roster bound is not an observation", !UnitCountReadable(65));
        Check("hold index: a lower reading does not start a placement", !PlacementAdvanced(2, 3));
        Check("hold index: the next placement advances", PlacementAdvanced(4, 3));

        Check("the draft guard's stop event carries the launcher's name",
              DraftStopEvent == @"Local\Strikers-draft-guard-stop");

        Check("allow parses its count and the machine's slot, or nothing",
              ParseAllow("allow 3 2") == (3, 2) && ParseAllow("allow 3") == (3, -1)
              && ParseAllow("allow x") == (-1, -1) && ParseAllow("allow 3 -1") == (-1, -1)
              && ParseAllow("release") == (-1, -1));

        List<string> unplaced = ["AAAA", "BBBB", "AAAA", "CCCC"];
        Check("the first unplaced unit of the wanted machine is handed over, case-blind",
              PickUnplaced(unplaced, "bbbb") == 1 && PickUnplaced(unplaced, "AAAA") == 0);

        Check("a machine not waiting to be placed hands over nothing",
              PickUnplaced(unplaced, "DDDD") == -1 && PickUnplaced(unplaced, "") == -1
              && PickUnplaced([], "AAAA") == -1);

        var ring = new Detail();
        for (var i = 0; i < Detail.Keep + 10; i++)
        {
            ring.Add($"line {i}");
        }

        var flushed = ring.Flush();
        Check("the hold's detail keeps its last lines only and a flush empties it",
              flushed.Count == Detail.Keep && flushed[0].EndsWith("line 10")
              && flushed[^1].EndsWith($"line {Detail.Keep + 9}") && ring.Count == 0 && ring.Flush().Count == 0);

        List<(int X, int Y, int Facing, int Hp)> stood = [(0, 0, 0, 3), (1, 1, 2, 4)];
        List<(int X, int Y, int Facing, int Hp)> spun = [(0, 0, 2, 3), (1, 1, 0, 4)];
        List<(int X, int Y, int Facing, int Hp)> walked = [(0, 0, 0, 3), (1, 2, 2, 4)];
        Check("machines that only turned in place are a spin, one that changed square is a move",
              TurnedInPlace(spun, stood) && TurnedInPlace(stood, stood)
              && !TurnedInPlace(walked, stood) && !TurnedInPlace([], stood));

        var sept30 = new DateOnly(2026, 9, 30);
        Check("a test build's mark names the tester and never the date, and its expiry reads the same here as in the launcher",
              TestBuild.Mark("Jane", sept30) == "private test build for Jane"
              && TestBuild.Mark(null, null) is null
              && !TestBuild.Expired(sept30, sept30) && TestBuild.Expired(sept30, sept30.AddDays(1))
              && !TestBuild.Expired(null, sept30.AddYears(10))
              && !TestBuild.ExpiredMessage().Contains(';') && !TestBuild.ExpiredMessage().Contains("2026"));

        var home = @"C:\Games\Strikers\";
        var ownExe = home + "live-probe.exe";
        var noon = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var before = noon.AddSeconds(-5);
        Check("a launcher-only live-probe runs under the Strikers or netplay beside it and nothing else, with only --version and --selftest open (D-241)",
              LauncherOnly.Allows(["--snapshot"], ownExe, noon, home + "Strikers.exe", before, LauncherOnly.Parents)
              && LauncherOnly.Allows(["--snapshot"], ownExe, noon, home + "netplay.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--snapshot"], ownExe, noon, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--snapshot"], ownExe, noon, @"C:\Other\netplay.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--snapshot"], ownExe, noon, home + "other-tool.exe", before, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--snapshot"], ownExe, noon, home + "netplay.exe", noon.AddSeconds(5), LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--snapshot"], ownExe, noon, null, null, LauncherOnly.Parents)
              && LauncherOnly.Allows(["--version"], ownExe, noon, null, null, LauncherOnly.Parents)
              && LauncherOnly.Allows(["--selftest"], ownExe, noon, null, null, LauncherOnly.Parents)
              && !LauncherOnly.Allows(["--version", "--launcher-check"], ownExe, noon, null, null, LauncherOnly.Parents)
              && !LauncherOnly.Message().Contains(';'));

        Check("a ring that did not read this sample keeps its anchor, and only a ring gone or replaced restarts it",
              StepCommitRing(0x5000, 0, unreadable: true) == CommitRingStep.Wait
              && StepCommitRing(0x5000, 0x5000, unreadable: false) == CommitRingStep.Emit
              && StepCommitRing(0x5000, 0, unreadable: false) == CommitRingStep.Reset
              && StepCommitRing(0x5000, 0x9000, unreadable: false) == CommitRingStep.Reset);

        Check("the allocator is called only when its vtable and its target both lie in the game's image",
              AllocatorCallable(0x140000000, 0x150000000, 0x142000000, 0x141000000)
              && !AllocatorCallable(0x140000000, 0x150000000, 0x142000000, 0x7FF600001000)
              && !AllocatorCallable(0x140000000, 0x150000000, 0x2A0000000, 0x141000000)
              && !AllocatorCallable(0, 0x150000000, 0x142000000, 0x141000000));

        Check("the move-bounds sites read as stock, patched, or mixed, and mixed is refused",
              MoveBoundsState([false, false]) == MoveBoundsStock && MoveBoundsState([true, true]) == MoveBoundsPatched
              && MoveBoundsState([true, false]) == MoveBoundsMixed);
        Check("the held line carries the word netplay waits for",
              MoveBoundsHeldLine.Contains("holding"));
        Check("a restore verb runs on an expired build and a write does not",
              TestBuild.RestoreVerb(["--patch-move-bounds", "--clear"])
              && TestBuild.RestoreVerb(["--force-first", "clear", "--yes"])
              && TestBuild.RestoreVerb(["--freeze"])
              && !TestBuild.RestoreVerb(["--patch-move-bounds", "--yes"])
              && !TestBuild.RestoreVerb(["--set-units", "--yes"])
              && !TestBuild.RestoreVerb(["--set-units", "--yes", "--patch-move-bounds", "--clear"])
              && !TestBuild.RestoreVerb(["--force-first", "clear", "--patch-move-bounds", "--yes"]));
        Check("the placing rows must fit the board",
              PlacingRowsProblem(2, 8) is null && PlacingRowsProblem(8, 8) is null
              && PlacingRowsProblem(9, 8) is not null && PlacingRowsProblem(0, 8) is not null);
        Check("--player is a seat, absent, or a refusal",
              SeatArg(["--x"]) is null && SeatArg(["--player", "1"]) == 1 && SeatArg(["--player", "0"]) == 0
              && SeatArg(["--player", "4096"]) == -1 && SeatArg(["--player"]) == -1);

        var hookedEntries = CommitSites.Select((site, i) =>
        {
            var jump = new List<byte> { 0xE9 };
            jump.AddRange(Rel32(_base + site.Rva, commitStubAt(i), 5));
            return jump.ToArray();
        }).ToList();
        var partEntries = hookedEntries.Take(2).Concat(CommitSites.Skip(2).Select(site => site.Stock)).ToList();
        List<byte[]> crossedEntries = [hookedEntries[1], hookedEntries[0], hookedEntries[2], hookedEntries[3]];
        Check("the ring is read only when all four entries jump to their own stubs in one page",
              CommitRingPageFromEntries(_base, hookedEntries) == stubProbe
              && CommitRingPageFromEntries(_base, partEntries) == 0
              && CommitRingPageFromEntries(_base, crossedEntries) == 0);

        byte[] stockRun = [0x48, 0x89, 0x5C, 0x24, 0x08];
        byte[] jumpRun = [0xE9, 0x01, 0x02, 0x03, 0x04];
        List<(ulong At, byte[] Patch, byte[] Stock)> patchSet =
            [(0x100, jumpRun, stockRun), (0x200, jumpRun, stockRun), (0x300, jumpRun, stockRun), (0x400, jumpRun, stockRun)];
        var memory = new Dictionary<ulong, byte[]>();
        var writesSeen = 0;
        bool ThirdWriteFails(ulong at, byte[] bytes)
        {
            writesSeen++;
            if (writesSeen == 3)
            {
                return false;
            }

            memory[at] = bytes;
            return true;
        }

        var whole = PatchAllOrNone(patchSet, ThirdWriteFails);
        Check("a patch set that fails part way puts the stock bytes back over every site it wrote",
              whole == PatchSet.RolledBack && memory.Count > 0 && memory.Values.All(v => v.SequenceEqual(stockRun))
              && PatchAllOrNone(patchSet, (_, _) => true) == PatchSet.Whole);

        var patchWrites = 0;
        var rollbackFails = PatchAllOrNone(patchSet, (_, _) =>
        {
            patchWrites++;
            return patchWrites != 3 && patchWrites != 5;
        });
        Check("a rollback that fails to put a site back reports a part set, not a clean one",
              rollbackFails == PatchSet.Partial);

        Check("every stub fills the record, then its sequence number, and publishes the count last",
              commitStubs.All(c =>
              {
                  var lastField = IndexOf(c, [0x41, 0x89, 0x43, 0x3C]);
                  var kindAt = IndexOf(c, [0x41, 0xC7, 0x03]);
                  var seqAt = IndexOf(c, [0x41, 0x89, 0x43, 0x04]);
                  var publish = IndexOf(c, [0x41, 0x89, 0x82, 0x00, 0x00, 0x00, 0x00]);
                  return lastField > 0 && kindAt > lastField && seqAt > kindAt && publish > seqAt
                         && Count(c, [0x41, 0x89, 0x82, 0x00, 0x00, 0x00, 0x00]) == 1;
              }));

        var seqRecord = new byte[CommitRecordSize];
        BitConverter.GetBytes(41u).CopyTo(seqRecord, 0x04);
        Check("a record is taken only when its sequence number follows the records already taken",
              CommitRecordCurrent(seqRecord, 40) && !CommitRecordCurrent(seqRecord, 41)
              && !CommitRecordCurrent(seqRecord, 8) && !CommitRecordCurrent(new byte[CommitRecordSize], 0));

        const ulong imageStart = 0x140000000;
        const ulong imageEnd = 0x145000000;
        const ulong siteAt = 0x140E404F0;
        byte[] JumpFromSite(ulong to)
        {
            var jump = new List<byte> { 0xE9 };
            jump.AddRange(Rel32(siteAt, to, 5));
            return [.. jump];
        }

        Check("a clear writes only over a jump that leaves the game's image, and an unreadable site is not stock",
              SiteClearVerdict(true, stockRun, stockRun, siteAt, imageStart, imageEnd) == SiteFound.Stock
              && SiteClearVerdict(true, JumpFromSite(imageEnd + 0x10000), stockRun, siteAt, imageStart, imageEnd) == SiteFound.Ours
              && SiteClearVerdict(true, JumpFromSite(imageStart - 0x10000), stockRun, siteAt, imageStart, imageEnd) == SiteFound.Ours
              && SiteClearVerdict(true, JumpFromSite(imageStart + 0x1000), stockRun, siteAt, imageStart, imageEnd) == SiteFound.Foreign
              && SiteClearVerdict(true, [0x90, 0x90, 0x90, 0x90, 0x90], stockRun, siteAt, imageStart, imageEnd) == SiteFound.Foreign
              && SiteClearVerdict(false, new byte[5], stockRun, siteAt, imageStart, imageEnd) == SiteFound.Unreadable);

        Check("the expiry gate's restore list is an allowlist, and the ring's clear is on it",
              TestBuild.RestoreVerb(["--commit-ring", "--clear"])
              && !TestBuild.RestoreVerb(["--commit-ring", "--yes"])
              && !TestBuild.RestoreVerb(["--restore-reject", "--commit-ring", "--yes"])
              && !TestBuild.RestoreVerb(["--restore-reject", "--unlock-challenges"])
              && !TestBuild.RestoreVerb(["--restore-reject", "--first", "human"])
              && TestBuild.RestoreVerb(["--freeze", "--parent-pid", "42"])
              && !TestBuild.RestoreVerb(["--freeze", "--parent-pid", "--commit-ring"]));

        bool Survives(Func<bool> test)
        {
            try
            {
                return test();
            }
            catch (Exception)
            {
                return false;
            }
        }

        Check("a trailing --stall, --secs or --wait takes its default instead of throwing after the patch",
              Survives(() => ScriptOptions(["--script-pass", "--yes", "--stall"]) == (180, TimeSpan.FromSeconds(20), false))
              && Survives(() => ScriptOptions(["--script-pass", "--final", "--secs"]).Final)
              && Survives(() => ScriptOptions(["--stall", "7", "--secs", "9"]) == (9, TimeSpan.FromSeconds(7), false))
              && ArgIntOrNull(["--force-first", "ai", "--yes", "--wait"], "--wait") is null);

        Check("a turn refused at its end counts as applied only on a count that was read",
              !AppliedAfterReject(false, 0, 17, 10) && AppliedAfterReject(true, 7, 17, 10)
              && !AppliedAfterReject(true, 8, 17, 10) && !AppliedAfterReject(true, 99, 17, 10));
        Check("a pass needs its EndTurn even when told the turn ended the match",
              RecordsNeeded(0, final: true) == 1 && RecordsNeeded(10, final: true) == 10
              && RecordsNeeded(10, final: false) == 11);

        Check("an unread placing row count refuses instead of falling back to two",
              SeatRowSpan(1, rowsRead: false, 0, 5) is null
              && SeatRowSpan(1, rowsRead: true, 1, 5) == (0, 0)
              && SeatRowSpan(0, rowsRead: true, 2, 8) == (6, 7)
              && SeatRowSpan(1, rowsRead: true, 0, 5) is null);

        Check("the unpin is written only into the controller the live placing phase is still on",
              UnpinOwed(0xA000, 0xA000, 1e9f) && !UnpinOwed(0xA000, 0, 1e9f) && !UnpinOwed(0xA000, 0xB000, 1e9f)
              && !UnpinOwed(0xA000, 0xA000, 0f) && !UnpinOwed(0, 0, 1e9f));

        var shared = new Detail();
        var threw = false;
        var tooMany = false;
        var adder = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 200000; i++)
                {
                    shared.Add($"line {i}");
                }
            }
            catch (Exception)
            {
                threw = true;
            }
        });
        adder.Start();
        try
        {
            while (adder.IsAlive)
            {
                tooMany |= shared.Flush().Count > Detail.Keep;
            }
        }
        catch (Exception)
        {
            threw = true;
        }

        adder.Join();
        Check("the hold's detail takes lines on one thread while another flushes it",
              !threw && !tooMany && shared.Count <= Detail.Keep);

        Check("the move-bounds patch is built only for a board its stubs are safe on, 8 a side at most",
              MoveBoundsShapeProblem(6, 5) is null && MoveBoundsShapeProblem(8, 8) is null
              && MoveBoundsShapeProblem(9, 5) is not null && MoveBoundsShapeProblem(5, 9) is not null
              && MoveBoundsShapeProblem(16, 16) is not null && MoveBoundsShapeProblem(0, 5) is not null);

        Check("a match with no winner yet names no seat, even when a seat's pointer reads as zero",
              WinnerSeat(0, 0, 0x2000) == -1 && WinnerSeat(0x1000, 0x1000, 0x2000) == 0
              && WinnerSeat(0x2000, 0x1000, 0x2000) == 1 && WinnerSeat(0x3000, 0x1000, 0x2000) == -1);

        Check("at the sink: a board is not resized shallower than the placing depth the challenge holds",
              ResizeDepthProblem(3, depthRead: true, 8) is not null && ResizeDepthProblem(3, depthRead: true, 2) is null
              && ResizeDepthProblem(3, depthRead: true, 3) is null && ResizeDepthProblem(3, depthRead: false, 0) is not null
              && ResizeDepthProblem(-1, depthRead: false, 0) is null);

        var firstPress = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        Check("the first Ctrl+C asks the holds to let go and restore; only a second ends the process at once",
              !CtrlPressLetThrough(null, firstPress) && CtrlPressLetThrough(firstPress, firstPress.AddSeconds(3)));

        Check("a failed read in an armed turn is a crash only when the game process has exited",
              !ReadFailedCrash(processExited: false) && ReadFailedCrash(processExited: true));

        (ulong Logic, int Width, int Height)[] boundsPolls =
            [(0xA000, 6, 5), (0, 0, 0), (0xC000, 6, 5), (0, 0, 0), (0xB000, 8, 8), (0, 0, 0)];
        var boundsMisses = 0;
        var boundsLetGo = -1;
        for (var i = 0; i < boundsPolls.Length; i++)
        {
            var poll = boundsPolls[i];
            boundsMisses = MoveBoundsMisses(boundsMisses, poll.Logic, poll.Width, poll.Height, 6, 5);
            if (boundsMisses >= MoveBoundsGoneReads && boundsLetGo < 0)
            {
                boundsLetGo = i;
            }
        }

        Check("the move-bounds holder lets go once three polls in a row find no live match of its shape",
              boundsLetGo == 5);

        Check("a coin flip already forced to the seat asked for is held when a wait was asked for",
              ForceFirstAdopts(clear: false, wait: 600, confirmed: true)
              && !ForceFirstAdopts(clear: true, wait: 600, confirmed: true)
              && !ForceFirstAdopts(clear: false, wait: 0, confirmed: true)
              && !ForceFirstAdopts(clear: false, wait: 600, confirmed: false));

        var foreignFlip = new byte[FlipBitStock.Length];
        Check("the coin flip's timed restore writes only over one of our two patches",
              FlipRestoreVerdict(true, FlipBitForced(1)) == SiteFound.Ours
              && FlipRestoreVerdict(true, FlipBitForced(0)) == SiteFound.Ours
              && FlipRestoreVerdict(true, FlipBitStock) == SiteFound.Stock
              && FlipRestoreVerdict(true, foreignFlip) == SiteFound.Foreign
              && FlipRestoreVerdict(false, foreignFlip) == SiteFound.Unreadable);

        var guardGone = GuardSequence([(true, true), (false, false), (true, true), (false, false), (false, false)]);
        var guardPlaced = GuardSequence([(true, true), (true, false), (true, true), (true, false), (true, false)]);
        var guardBefore = GuardSequence([(false, false), (false, false), (true, false), (true, false)]);
        Check("the draft guard hands the seats back only on two polls in a row, and never before placing",
              guardGone.SequenceEqual([GuardVerdict.Waiting, GuardVerdict.Waiting, GuardVerdict.Waiting,
                                       GuardVerdict.Waiting, GuardVerdict.Gone])
              && guardPlaced.SequenceEqual([GuardVerdict.Waiting, GuardVerdict.Waiting, GuardVerdict.Waiting,
                                            GuardVerdict.Waiting, GuardVerdict.Placed])
              && guardBefore.All(v => v == GuardVerdict.Waiting));

        var askedTimes = 0;
        var pauses = 0;
        var neverYes = AskedThrice(() =>
        {
            askedTimes++;
            return false;
        }, () => pauses++);
        var thirdYes = 0;
        var yesOnThird = AskedThrice(() => ++thirdYes == 3, () => { });
        Check("a test of whether a match is still ours asks three times before it says no",
              !neverYes && askedTimes == 3 && pauses == 2 && yesOnThird && thirdYes == 3);

        Check("--restore-reject tells a page of ours from the game's own arrays",
              OurPageAttached(0x2A0000, 0x140000000, 0x14A000000)
              && !OurPageAttached(0, 0x140000000, 0x14A000000)
              && !OurPageAttached(0x2A0010, 0x140000000, 0x14A000000)
              && !OurPageAttached(0x140001000, 0x140000000, 0x14A000000));

        var allocStub = GameAllocStub(0x7FF7A76DA388, 0x2A0000);
        var allocDone = 24 + GameAllocStubJumpOver();
        Check("D-232: the allocator stub is a leaf call with shadow space and a 16-byte aligned stack",
              allocStub.Length == 81
              && allocStub[0] == 0x48 && allocStub[1] == 0x83 && allocStub[2] == 0xEC && allocStub[3] == 0x28
              && allocStub[^5] == 0x48 && allocStub[^4] == 0x83 && allocStub[^3] == 0xC4
              && allocStub[^2] == 0x28 && allocStub[^1] == 0xC3);

        Check("D-232: a null allocator skips the call and lands exactly on the result store",
              allocStub[22] == 0x74 && allocStub[23] == GameAllocStubJumpOver()
              && allocDone == 54
              && allocStub[allocDone] == 0x48 && allocStub[allocDone + 1] == 0xB9);

        Check("D-232: the stub carries the allocator slot, the size slot and the result page it was given",
              BitConverter.ToUInt64(allocStub, 6) == 0x7FF7A76DA388
              && BitConverter.ToUInt64(allocStub, 29) == 0x2A0000 + GameAllocSizeSlot
              && allocStub[37] == 0x8B && allocStub[38] == 0x12
              && BitConverter.ToUInt64(allocStub, allocDone + 2) == 0x2A0000);

        Check("D-232: the stub calls the allocate slot the game's own code calls",
              allocStub[48] == 0xFF && allocStub[49] == 0x90
              && BitConverter.ToUInt32(allocStub, 50) == AllocateVtableSlot);

        const ulong arenaLow = 0x26DC0000000;
        const ulong arenaHigh = 0x271C0000000;
        Check("D-232: the arena holds the game's objects, and the global allocator's memory sits above it",
              InGameArena(0x26E5CFE6258, arenaLow, arenaHigh)
              && InGameArena(arenaLow, arenaLow, arenaHigh)
              && !InGameArena(arenaHigh, arenaLow, arenaHigh)
              && !InGameArena(0x272C80424C0, arenaLow, arenaHigh)
              && !InGameArena(0x27402873F00, arenaLow, arenaHigh));

        Check("D-232: a page of ours and the game's allocator's memory are alike to the range check",
              !InGameArena(0x2A0000, arenaLow, arenaHigh)
              && !InGameArena(0x272EA402840, arenaLow, arenaHigh)
              && !InGameArena(0x272A8431A40, arenaLow, arenaHigh));

        Check("D-232: a heap range that did not read makes every pointer fail rather than pass",
              !InGameArena(0x26E5CFE6258, 0, 0)
              && !InGameArena(0x26E5CFE6258, arenaHigh, arenaLow)
              && !InGameArena(0, arenaLow, arenaHigh));

        Check("a second Ctrl+C inside two seconds of the first is the same press",
              !CtrlPressLetThrough(firstPress, firstPress.AddMilliseconds(150)));

        var armNow = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        Check("a Ctrl+C does not end an armed turn's wait, only its deadline does",
              ArmWaitGoesOn(0.5, 1.0, stopAsked: true)
              && !ArmWaitGoesOn(2.0, 1.0, stopAsked: false));

        const ulong phaseA = 0x1DCA1F50180;
        const ulong phaseB = 0x1DCA1F60180;
        Check("an armed turn's stall and deadline stand still while either pause byte is set",
              ArmTick(1, 0, 5.0, phaseA, 2.0f, phaseA, 7.0f) == 0
              && ArmTick(0, 1, 5.0, phaseA, 2.0f, phaseA, 7.0f) == 0
              && ArmTick(1, 1, 5.0, phaseA, null, phaseA, null) == 0);

        Check("D-245: a phase clock that did not move, as under the pause menu, adds no time to an armed turn",
              ArmTick(0, 0, 5.0, phaseA, 32.8997f, phaseA, 32.8997f) == 0);

        Check("D-245: a running phase clock adds its own advance, frame steps included",
              Math.Abs(ArmTick(0, 0, 1.8, phaseA, 2.0354f, phaseA, 3.9039f) - 1.8685) < 0.001
              && Math.Abs(ArmTick(0, 0, 0.005, phaseA, 10.0f, phaseA, 10.0167f) - 0.0167) < 0.0001);

        Check("D-245: a phase clock that cannot be read falls back to wall time",
              ArmTick(0, 0, 0.5, phaseA, 2.0f, 0, null) == 0.5
              && ArmTick(0, 0, 0.5, phaseA, 2.0f, phaseA, null) == 0.5);

        Check("D-245: a new phase object, a clock that went back or a jump past one frame adds nothing extra",
              ArmTick(0, 0, 0.005, phaseA, 50.0f, phaseB, 1.7f) == 0
              && ArmTick(0, 0, 0.005, phaseA, 50.0f, phaseA, 1.7f) == 0
              && ArmTick(0, 0, 0.005, 0, null, phaseA, 1.7f) == 0
              && ArmTick(0, 0, 0.005, phaseA, 1.0f, phaseA, 40.0f) <= 0.255 + 1e-9);

        Check("D-245: the hold's wait for a match ends when the process that started it is gone",
              HoldWaitGoesOn(armNow, armNow.AddSeconds(600), 0, parentGone: false)
              && !HoldWaitGoesOn(armNow, armNow.AddSeconds(600), 0, parentGone: true)
              && !HoldWaitGoesOn(armNow, armNow.AddSeconds(600), 0x1D58CC206E0, parentGone: false));

        Check("D-245: a hold or watcher given 0 seconds has no wall-clock end",
              HoldDeadline(armNow, 0) == DateTime.MaxValue && HoldDeadline(armNow, 300) == armNow.AddSeconds(300));

        Check("a negative number of seconds ends at once instead of never",
              HoldDeadline(armNow, -1) == armNow && HoldDeadline(armNow, int.MinValue) == armNow);

        Check("a system clock stepped backwards adds no time, and a poll inside one clock tick keeps the game's advance",
              ArmTick(0, 0, -5.0, phaseA, 2.0f, phaseA, 2.1f) == 0
              && ArmTick(0, 0, -5.0, phaseA, 2.0f, 0, null) == 0
              && Math.Abs(ArmTick(0, 0, 0.0, phaseA, 10.0f, phaseA, 10.0167f) - 0.0167) < 0.0001);

        Check("the match-gone check runs only after the game clock has stood still a second, not between frames",
              !StoodStillCheckDue(TimeSpan.FromMilliseconds(12), TimeSpan.FromSeconds(30))
              && !StoodStillCheckDue(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400))
              && StoodStillCheckDue(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)));

        Check("D-245: a match that ends with no board change, a forfeit, still makes the watcher emit a sample",
              SampleKey("{\"pieces\":[]}", "\"placing\":null", "\"match\":{\"over\":false}", "\"turn\":0")
              != SampleKey("{\"pieces\":[]}", "\"placing\":null", "\"match\":{\"over\":true}", "\"turn\":0"));

        Check("D-248: the turn passing with no board change, every machine that acted dead, still makes the watcher emit a sample",
              SampleKey("{\"pieces\":[]}", "\"placing\":null", "\"match\":{\"over\":false}", TurnJson("instance+0x68+0x18 -> HumanBoardGamePlayingControllerInstance", 1))
              != SampleKey("{\"pieces\":[]}", "\"placing\":null", "\"match\":{\"over\":false}", TurnJson("instance+0x68+0x18 -> AIBoardGamePlayingControllerInstance", 1)));

        Check("D-248: the turn is the human's seat, the AI's seat, or -1 for any other controller or an unknown AI seat",
              TurnJson("instance+0x68+0x18 -> HumanBoardGamePlayingControllerInstance", 1) == "\"turn\":0"
              && TurnJson("instance+0x68+0x18 -> AIBoardGamePlayingControllerInstance", 1) == "\"turn\":1"
              && TurnJson("instance+0x68+0x18 -> HumanBoardGamePlayingControllerInstance", 0) == "\"turn\":1"
              && TurnJson("instance+0x68+0x18 -> BoardGamePlayingControllerInstance", 1) == "\"turn\":-1"
              && TurnJson("", 1) == "\"turn\":-1"
              && TurnJson("instance+0x68+0x18 -> AIBoardGamePlayingControllerInstance", -1) == "\"turn\":-1");

        Check("D-245: the watcher says a match is gone once, a second after a live match disappears",
              NoMatchDue(sawMatch: true, armNow, armNow.AddSeconds(1), alreadySaid: false)
              && !NoMatchDue(sawMatch: true, armNow, armNow.AddMilliseconds(400), alreadySaid: false)
              && !NoMatchDue(sawMatch: false, armNow, armNow.AddSeconds(5), alreadySaid: false)
              && !NoMatchDue(sawMatch: true, armNow, armNow.AddSeconds(5), alreadySaid: true)
              && !NoMatchDue(sawMatch: true, null, armNow.AddSeconds(5), alreadySaid: false));

        Check("a detach reads back as done only when the count and the data are both zero",
              DetachHeld(0, 0) && !DetachHeld(3, 0) && !DetachHeld(0, 0x2A0000) && !DetachHeld(2, 0x2A0000));

        Check("a placement release is written again when the timer reads pinned after it",
              ReleaseLost(released: true, 1e9f) && !ReleaseLost(released: true, 0.5f)
              && !ReleaseLost(released: false, 1e9f));

        static List<GuardVerdict> GuardSequence((bool InstSane, bool PlacingLive)[] polls)
        {
            var verdicts = new List<GuardVerdict>();
            var seen = false;
            var gone = 0;
            var placed = 0;
            foreach (var poll in polls)
            {
                seen |= poll.PlacingLive;
                var step = GuardStep(poll.InstSane, poll.PlacingLive, seen, gone, placed);
                gone = step.GoneReads;
                placed = step.PlacedReads;
                verdicts.Add(step.Verdict);
            }

            return verdicts;
        }

        Console.WriteLine($"\n  {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static Dictionary<(int X, int Y), string> Occupancy(ulong logic, ulong inst, int seat)
    {
        var ours = ReadPtr(inst + 0x40 + (ulong)(seat * 8));
        var theirs = ReadPtr(inst + 0x40 + (ulong)((1 - seat) * 8));
        var map = new Dictionary<(int, int), string>();

        var count = (int)ReadU32(logic + 0x38);
        var array = ReadPtr(logic + 0x40);
        for (var i = 0; i < count && i < 64; i++)
        {
            var u = ReadPtr(array + (ulong)(i * 8));
            if (!Sane(u))
            {
                continue;
            }

            var packed = ReadByte(u + 0x38);
            var owner = ReadPtr(u);
            map[(Nibble(packed), Nibble(packed >> 4))] =
                owner == ours ? "AI" : owner == theirs ? "opponent" : "?";
        }
        return map;
    }

    private static bool SimulateTurn(ulong logic, ulong inst, int seat, List<Act> acts)
    {
        var at = Occupancy(logic, inst, seat);
        var exact = true;
        var ok = true;

        var rows = ReadPtr(logic + 0x08);
        var height = (int)ReadU32(rows + 0x20);
        var width = (int)ReadU32(ReadPtr(ReadPtr(rows + 0x28)) + 0x20);

        for (var i = 0; i < acts.Count; i++)
        {
            var a = acts[i];
            at.TryGetValue((a.SrcX, a.SrcY), out var who);

            if (who != "AI")
            {
                var complaint = who is null
                    ? $"action {i + 1} activates ({a.SrcX},{a.SrcY}), where nothing will be standing"
                    : $"action {i + 1} activates ({a.SrcX},{a.SrcY}), held by the {who}";

                if (exact)
                {
                    Console.Error.WriteLine($"\n  Warning: {complaint}.");
                    Console.Error.WriteLine("  The game does not refuse that. It consumes the activate, activates nothing,");
                    Console.Error.WriteLine("  and then the next record stops the turn dead, move and attack only run");
                    Console.Error.WriteLine("  while controller+0x320 is set, and they pop from inside that test. The rest");
                    Console.Error.WriteLine("  of the turn never happens and nothing reports it. --force arms it anyway.");
                    ok = false;
                }
                else
                {
                    Console.WriteLine($"\n  Warning: {complaint}, as far as this can tell.");
                    Console.WriteLine("  An earlier action was an attack, which moves pieces this cannot predict, so");
                    Console.WriteLine("  this is a warning rather than a refusal.");
                }
            }

            var (dx, dy) = StandSquare(a);

            if (StandsOffBoard(a, width, height))
            {
                Console.Error.WriteLine($"\n  Warning: action {i + 1} moves to ({dx},{dy}), off a {width}x{height} board.");
                Console.Error.WriteLine("  The game clamps that instead of refusing it, landing a piece on an occupied tile");
                Console.Error.WriteLine("  and taking the match down. Refused for every action, not only the first.");
                ok = false;
            }

            at.Remove((a.SrcX, a.SrcY));
            at[(dx, dy)] = "AI";
            if (a.Attack)
            {
                exact = false;
            }
        }

        return ok;
    }

    private static void ReportPartial(ulong logic, ulong inst, int seat, List<string> what, int done,
                                      TimeSpan stall)
    {
        Console.Error.WriteLine($"\n  Warning: PARTIAL TURN, {done} of {what.Count} action record(s) were applied, " +
                                $"then nothing moved for {stall.TotalSeconds:0}s.");
        Console.Error.WriteLine("  The game raised no rejection. Both boards will look healthy and they are NOT " +
                                "the same board.\n");

        for (var i = 0; i < what.Count; i++)
        {
            Console.Error.WriteLine($"    {(i < done ? "applied " : "NOT RUN ")} {i + 1}. {what[i]}");
        }

        if (done < what.Count && done > 0)
        {
            var stalledOn = what[done];
            var previous = what[done - 1];
            Console.Error.WriteLine($"\n  It stopped on record {done + 1}: {stalledOn}");

            if (!stalledOn.StartsWith("activate") && previous.StartsWith("activate ("))
            {
                var sq = previous["activate (".Length..].TrimEnd(')').Split(',');
                if (sq.Length == 2 && int.TryParse(sq[0], out var ax) && int.TryParse(sq[1], out var ay))
                {
                    var at = Occupancy(logic, inst, seat);
                    var who = at.TryGetValue((ax, ay), out var w) ? w : null;
                    Console.Error.WriteLine($"  The activate before it named ({ax},{ay}), which now holds " +
                                            (who is null ? "NOTHING." : $"a piece of the {who}."));
                    if (who != "AI")
                    {
                        Console.Error.WriteLine("  Key: That is the known cause: an activate that finds no machine of " +
                                                "ours is consumed\n     anyway, activates nothing, and the move or " +
                                                "attack behind it then returns without\n     consuming itself. " +
                                                "controller+0x348 stays set and nothing dispatches again.");
                    }
                }
            }
        }

        Console.Error.WriteLine("\n  Warning: The AI seat is now mid-turn with our records detached. It may take the rest " +
                                "of the turn\n     itself unless --hold is applied. Do not send a hash for this " +
                                "turn and do not play on.");
    }

}
