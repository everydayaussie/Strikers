namespace Strikers.LiveProbe;

internal static partial class Program
{
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

    private const int DashPattern = 2;

    private const int RamPattern = 3;

    private const int DivePattern = 4;

    private static (int X, int Y) FacingStep(byte facing)
    {
        return (facing & 3) switch
        {
            0 => (0, -1),
            1 => (1, 0),
            2 => (0, 1),
            _ => (-1, 0),
        };
    }

    private static bool InSpreadReach(int fromX, int fromY, byte facing, int range, int toX, int toY)
    {
        if (range < 1)
        {
            return false;
        }

        var (dx, dy) = FacingStep(facing);

        var along = dx == 0 ? (toY - fromY) * dy : (toX - fromX) * dx;
        var aside = dx == 0 ? Math.Abs(toX - fromX) : Math.Abs(toY - fromY);
        return along >= 1 && along <= range && aside <= 1;
    }

    private static (int X, int Y)? ChargeLanding(int pattern, int range, int walkX, int walkY, byte facing)
    {
        if (pattern != DashPattern || range < 1)
        {
            return null;
        }

        var (stepX, stepY) = FacingStep(facing);
        return (walkX + stepX * range, walkY + stepY * range);
    }

    private static string? LandingProblem((int X, int Y) land, int width, int height, bool occupied)
    {
        if (land.X < 0 || land.X >= width || land.Y < 0 || land.Y >= height)
        {
            return $"a Dash charging to ({land.X},{land.Y}) lands off a {width}x{height} board";
        }

        if (occupied)
        {
            return $"a Dash charging to ({land.X},{land.Y}) needs that square empty, and a machine stands on it";
        }

        return null;
    }

    private static string? ReachProblem(int pattern, int skill, int range,
                                        int walkX, int walkY, byte facing, int dstX, int dstY)
    {
        var offLine = $"standing on ({walkX},{walkY}) facing {facing} does not put the victim " +
                      $"({dstX},{dstY}) on the strike line, the attack would hit something else";

        if (pattern != DashPattern)
        {
            var aligned = (facing & 3) switch
            {
                0 => dstX == walkX && dstY < walkY,
                1 => dstY == walkY && dstX > walkX,
                2 => dstX == walkX && dstY > walkY,
                _ => dstY == walkY && dstX < walkX,
            };

            var swept = skill == SpreadSkill && InSpreadReach(walkX, walkY, facing, range, dstX, dstY);

            if (!aligned && !swept)
            {
                return offLine;
            }

            return null;
        }

        var (stepX, stepY) = FacingStep(facing);
        var along = stepX == 0 ? (dstY - walkY) * stepY : (dstX - walkX) * stepX;
        var offPath = stepX == 0 ? Math.Abs(dstX - walkX) : Math.Abs(dstY - walkY);
        var room = skill == SpreadSkill ? 1 : 0;
        var tooFar = range >= 1 && along > range - 1;

        if (along < 1 || tooFar || offPath > room)
        {
            return offLine;
        }

        return null;
    }

    private static int PatternOf(ulong unit)
    {
        return PatternOf(unit, ReadPtr, ReadPatternByte);
    }

    private static int ReadPatternByte(ulong address)
    {
        return TryRead(address, 1, out var patternByte) ? patternByte[0] : -1;
    }

    private static int PatternOf(ulong unit, Func<ulong, ulong> readPtr, Func<ulong, int> readByte)
    {
        var resource = readPtr(unit + 0x08);
        if (!Sane(resource))
        {
            return -1;
        }

        return readByte(resource + 0x70);
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

        Check("a Dash's victim on the square its charge passes through is reached from the square it charges from",
              ReachProblem(DashPattern, 0, 2, 2, 0, 2, 2, 1) is null);

        Check("a victim beside the charge's path is reached by a Spread Dash and by no other",
              ReachProblem(DashPattern, SpreadSkill, 2, 2, 0, 2, 3, 1) is null
              && ReachProblem(DashPattern, 0, 2, 2, 0, 2, 3, 1) is not null);

        Check("a Dash reaches nothing on its landing, beside its landing or behind its start",
              ReachProblem(DashPattern, 0, 2, 2, 0, 2, 2, 2) is not null
              && ReachProblem(DashPattern, SpreadSkill, 2, 2, 0, 2, 3, 2) is not null
              && ReachProblem(DashPattern, 0, 2, 2, 1, 2, 2, 0) is not null);

        Check("a charge armed from beyond its victim is refused, the shape that exited a game on 2026-09-22",
              ReachProblem(DashPattern, 0, 2, 2, 3, 2, 2, 2)
              == "standing on (2,3) facing 2 does not put the victim (2,2) on the strike line, the attack would hit something else");

        Check("the charge of 2026-09-22 armed from the Charger's own square passes",
              ReachProblem(DashPattern, 0, 2, 2, 1, 2, 2, 2) is null);

        Check("a Strike machine given the same squares keeps the old verdicts",
              ReachProblem(0, 0, 2, 2, 0, 2, 2, 1) is null
              && ReachProblem(0, 0, 2, 2, 2, 2, 2, 1) is not null);

        Check("a pattern that could not be read keeps the old verdicts as well",
              ReachProblem(-1, 0, 2, 2, 0, 2, 2, 1) is null
              && ReachProblem(-1, 0, 2, 2, 2, 2, 2, 1) is not null);

        Check("a charge lands its range along its facing from the square it charges from, and only a Dash's",
              ChargeLanding(DashPattern, 2, 2, 0, 2) == (2, 2)
              && ChargeLanding(DashPattern, 3, 1, 1, 1) == (4, 1)
              && ChargeLanding(0, 2, 2, 0, 2) is null
              && ChargeLanding(DashPattern, -1, 2, 0, 2) is null);

        Check("a charge landing off the board is refused, the shape that exited a player's game on 2026-09-19",
              ChargeLanding(DashPattern, 2, 2, 0, 1) is { } crashLanding
              && LandingProblem(crashLanding, 4, 5, false) == "a Dash charging to (4,0) lands off a 4x5 board");

        Check("a charge landing on a machine is refused, and an empty landing on the board passes",
              LandingProblem((2, 2), 4, 5, true) is not null
              && LandingProblem((2, 2), 4, 5, false) is null);

        var pointers = new Dictionary<ulong, ulong>
        {
            [0x20008] = 0x30000,
        };
        var bytes = new Dictionary<ulong, int>
        {
            [0x20070] = 0,
            [0x30070] = DashPattern,
        };

        Check("a piece's pattern is read from its machine's resource, not from the staging bytes on the piece",
              PatternOf(0x20000, a => pointers.GetValueOrDefault(a), a => bytes.GetValueOrDefault(a, -1)) == DashPattern);

        Check("a piece whose resource pointer cannot be read has no pattern",
              PatternOf(0x20000, a => 0UL, a => bytes.GetValueOrDefault(a, -1)) == -1);

        Check("a Dash is judged by its own rule once its pattern is read, which refuses a victim on the landing",
              ReachProblem(DashPattern, 0, 2, 2, 1, 2, 2, 3) is not null
              && ReachProblem(0, 0, 2, 2, 1, 2, 2, 3) is null);

        var dasher = new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>
        {
            [(2, 0)] = (DashPattern, 0, 2),
        };

        List<Act> chargeThenLandingVictim =
        [
            new Act(true, 2, 0, 2, 1, 2, false, 2, 0),
            new Act(true, 2, 2, 2, 4, 2, true, 2, 2),
        ];

        List<Act> chargeThenPathVictim =
        [
            new Act(true, 2, 0, 2, 1, 2, false, 2, 0),
            new Act(true, 2, 2, 2, 3, 2, true, 2, 2),
        ];

        Check("a Dash's numbers follow it to the end of its charge, so its next charge is judged as a Dash's",
              TurnReachProblems(chargeThenLandingVictim, dasher).Count == 1
              && TurnReachProblems(chargeThenPathVictim, dasher).Count == 0);

        var strikers = new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>
        {
            [(1, 1)] = (0, 0, 1),
            [(4, 4)] = (0, 0, 1),
        };

        List<Act> secondMisses =
        [
            new Act(true, 1, 1, 1, 0, 0, false, 1, 1),
            new Act(true, 4, 4, 4, 3, 0, false, 6, 6),
        ];

        List<Act> secondReaches =
        [
            new Act(true, 1, 1, 1, 0, 0, false, 1, 1),
            new Act(true, 4, 4, 4, 3, 0, false, 4, 4),
        ];

        Check("a second action whose stand square does not reach its victim is refused",
              TurnReachProblems(secondMisses, strikers).Count == 1);

        Check("the same turn with a second action that does reach passes",
              TurnReachProblems(secondReaches, strikers).Count == 0);

        var sweeper = new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>
        {
            [(1, 1)] = (0, SpreadSkill, 2),
        };

        List<Act> moveThenSweep =
        [
            new Act(false, 1, 1, 1, 3, 2, false),
            new Act(true, 1, 3, 2, 4, 2, false, 1, 3),
        ];

        Check("a machine's own numbers follow it to the square it moved to",
              TurnReachProblems(moveThenSweep, sweeper).Count == 0
              && TurnReachProblems(moveThenSweep,
                                   new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>()).Count == 1);

        var placedElsewhere = new HashSet<(int X, int Y)> { (3, 0), (4, 7) };
        Check("a late pin writes the record's square into its machine only when that square is on the board, " +
              "in the AI seat's rows, not a Chasm under a machine that cannot fly, and holds no other machine",
              LatePinProblem((2, 0), 8, 8, (0, 1), 0, 0, placedElsewhere) is null
              && LatePinProblem((3, 0), 8, 8, (0, 1), 0, 0, placedElsewhere) is { } stacked
              && stacked.Contains("another machine")
              && LatePinProblem((2, 3), 8, 8, (0, 1), 0, 0, placedElsewhere) is not null
              && LatePinProblem((8, 0), 8, 8, (0, 1), 0, 0, placedElsewhere) is not null
              && LatePinProblem((2, 0), 8, 8, (0, 1), ChasmTile, 0, placedElsewhere) is not null
              && LatePinProblem((2, 0), 8, 8, (0, 1), ChasmTile, DivePattern, placedElsewhere) is null
              && LatePinProblem((2, 0), 8, 8, null, 0, 0, placedElsewhere) is not null
              && LatePinProblem((2, 0), 8, 8, (0, 1), null, 0, placedElsewhere) is not null);

        static GateUnit Unit(ulong id, int x, int y, int health, bool ai, int pattern, int range, int move,
                             int skill = -1)
        {
            return new GateUnit(id, x, y, health, ai, pattern, skill, range, move);
        }

        static GateBoard Board(int width, int height, ulong active, params GateUnit[] units)
        {
            return new GateBoard(width, height, units, true, active);
        }

        static string? Verdict(GateBoard board, Act act, int k)
        {
            return JudgeBeforeStrike(board, act, k).Problem;
        }

        const ulong ours = 16;
        const ulong ours2 = 24;
        const ulong theirs = 32;
        const ulong theirs2 = 40;
        var burrower = Unit(ours, 1, 6, 4, true, 0, 1, 2);
        var besideIt = Unit(ours2, 2, 6, 4, true, 0, 1, 2);
        var enemyAhead = Unit(theirs, 1, 4, 4, false, 0, 1, 2);
        var enemyAside = Unit(theirs2, 3, 4, 4, false, 0, 1, 2);
        var field = Board(8, 8, ours, burrower, besideIt, enemyAhead, enemyAside);

        Check("the board check refuses an action whose activate found nothing, or found the opponent's " +
              "machine, which the game activates by its square alone, and passes the AI's own machine there",
              Verdict(field with { Activated = false }, new Act(false, 5, 5, 5, 4, 0, false), 1) is { } nothing
              && nothing.Contains("activated nothing") && nothing.Contains("action 2")
              && Verdict(field with { Active = theirs2 }, new Act(false, 3, 4, 3, 5, 2, false), 0) is { } notOurs
              && notOurs.Contains("would hand the computer the opponent's machine on (3,4)")
              && Verdict(field, new Act(false, 2, 6, 2, 5, 0, false), 0) is { } dragged
              && dragged.Contains("activated the machine on (1,6)")
              && Verdict(field, new Act(false, 1, 6, 0, 6, 3, false), 0) is null);

        var ramVictim = Unit(theirs, 4, 2, 4, false, 0, 1, 2);
        var chargeVictim = Unit(theirs, 6, 2, 4, false, 0, 1, 2);
        Check("the board check bounds the walk from the machine's real square by its move, one more for a " +
              "move that does not strike, its range for a charge and one for a Ram",
              Verdict(field, new Act(false, 1, 6, 1, 3, 0, false), 0) is null
              && Verdict(field, new Act(false, 1, 6, 0, 3, 0, false), 0) is { } tooFar
              && tooFar.Contains("walks the machine on (1,6) 4 squares")
              && Verdict(field, new Act(true, 1, 6, 1, 4, 0, false, 1, 5), 0) is null
              && Verdict(field with { Units = [burrower with { X = 0, Y = 7 }, besideIt, enemyAhead, enemyAside] },
                         new Act(true, 0, 7, 1, 4, 0, false, 1, 5), 0) is { } strikeTooFar
              && strikeTooFar.Contains("its move is 2")
              && Verdict(Board(8, 8, ours2, burrower, Unit(ours2, 4, 6, 4, true, RamPattern, 1, 2), ramVictim),
                         new Act(true, 4, 6, 4, 2, 0, false, 4, 3), 0) is null
              && Verdict(Board(8, 8, ours2, burrower, Unit(ours2, 4, 7, 4, true, RamPattern, 1, 2), ramVictim),
                         new Act(true, 4, 7, 4, 2, 0, false, 4, 3), 0) is { } ramTooFar
              && ramTooFar.Contains("1 more is the most")
              && Verdict(Board(8, 10, ours2, burrower, Unit(ours2, 6, 8, 4, true, DashPattern, 2, 3), chargeVictim),
                         new Act(true, 6, 8, 6, 2, 0, false, 6, 3), 0) is null
              && Verdict(Board(8, 10, ours2, burrower, Unit(ours2, 6, 9, 4, true, DashPattern, 2, 3), chargeVictim),
                         new Act(true, 6, 9, 6, 2, 0, false, 6, 3), 0) is { } chargeTooFar
              && chargeTooFar.Contains("2 more is the most"));

        Check("the board check refuses a move or a strike square where another machine stands, the AI's or " +
              "the opponent's, and passes the machine's own square and an empty one",
              Verdict(field, new Act(false, 1, 6, 1, 4, 0, false), 0) is { } ontoTheirs
              && ontoTheirs.Contains("moves the machine to (1,4), where the opponent's machine stands")
              && Verdict(field, new Act(false, 1, 6, 2, 6, 1, false), 0) is { } ontoOurs
              && ontoOurs.Contains("the AI's own machine")
              && Verdict(field, new Act(true, 1, 6, 1, 4, 0, false, 1, 6), 0) is null
              && Verdict(field, new Act(false, 1, 6, 0, 6, 3, false), 0) is null
              && Verdict(field with { Units = [burrower, besideIt, enemyAhead, enemyAside, Unit(48, 1, 5, 4, false, 0, 1, 2)] },
                         new Act(true, 1, 6, 1, 4, 0, false, 1, 5), 0) is { } strikeUnder
              && strikeUnder.Contains("stands the machine on (1,5), where the opponent's machine stands"));

        var dash = Unit(ours2, 6, 6, 4, true, DashPattern, 2, 3);
        var dashVictim = Unit(theirs, 6, 5, 4, false, 0, 1, 2);
        var dashSide = Unit(theirs2, 7, 6, 4, false, 0, 1, 2);
        Check("a charge whose landing is off the board or held by a machine is refused, and an empty landing on the " +
              "board passes",
              Verdict(Board(8, 8, ours2, dash, dashVictim, dashSide), new Act(true, 6, 6, 6, 5, 0, false, 6, 6), 0)
                  is null
              && Verdict(Board(8, 8, ours2, dash, dashVictim, dashSide, Unit(48, 6, 4, 4, false, 0, 1, 2)),
                         new Act(true, 6, 6, 6, 5, 0, false, 6, 6), 0) is { } heldLanding
              && heldLanding.Contains("needs that square empty")
              && Verdict(Board(8, 8, ours2, dash, dashVictim, dashSide), new Act(true, 6, 6, 7, 6, 1, false, 6, 6), 0)
                  is { } offLanding
              && offLanding.Contains("off a 8x8 board"));

        Check("the board check refuses a strike on a square that holds no opponent's machine or lies off the line " +
              "from where the machine stands, and passes a victim on the line",
              Verdict(field, new Act(true, 1, 6, 1, 4, 0, false, 1, 6), 0) is null
              && Verdict(field, new Act(true, 1, 6, 1, 5, 0, false, 1, 6), 0) is { } empty
              && empty.Contains("strikes (1,5), which holds nothing")
              && Verdict(field with { Active = ours2 }, new Act(true, 2, 6, 1, 6, 3, false, 2, 6), 0) is { } own
              && own.Contains("the AI's own machine")
              && Verdict(field, new Act(true, 1, 6, 3, 4, 0, false, 1, 6), 0) is { } offLine
              && offLine.Contains("strike line"));

        Check("D-243: the board check refuses an activate while another machine is still activated, the walk that " +
              "puts it on that square, and passes the same machine's owed move and a closed activation",
              JudgeBeforeActivate(field, new Act(false, 2, 6, 2, 5, 0, false), 1) is { Problem: { } drag, Dying: false }
              && drag.Contains("walk that machine onto (2,6)") && drag.Contains("the AI's own machine")
              && JudgeBeforeActivate(field, new Act(false, 1, 6, 1, 5, 0, false), 1).Problem is null
              && JudgeBeforeActivate(field with { Activated = false }, new Act(false, 2, 6, 2, 5, 0, false), 1)
                  .Problem is null
              && JudgeBeforeActivate(field with { Active = 153 }, new Act(false, 2, 6, 2, 5, 0, false), 1)
                  .Problem is null
              && JudgeBeforeActivate(field with { Units = [burrower with { Health = 0 }, besideIt] },
                                     new Act(false, 2, 6, 2, 5, 0, false), 1) is { Problem: not null, Dying: true });

        var dyingOnStand = field with { Units = [burrower, besideIt, enemyAside, Unit(48, 1, 5, 0, false, 0, 1, 2)] };
        var liveOnStand = field with { Units = [burrower, besideIt, enemyAside, Unit(48, 1, 5, 4, false, 0, 1, 2)] };
        var stepAhead = new Act(false, 1, 6, 1, 5, 0, false);
        GateJudgement StepAhead(GateBoard b)
        {
            return JudgeBeforeStrike(b, stepAhead, 1);
        }

        GateJudgement NextActivates(GateBoard b)
        {
            return JudgeBeforeActivate(b, new Act(false, 2, 6, 2, 5, 0, false), 1);
        }

        var nothingActive = field with { Activated = false };
        Check("the board check waits for a torn read, a machine at 0 health on the square, nothing activated yet, or " +
              "another machine still activated, inside its 150 ms and then refuses, and a settled board passes at once",
              GateSettle(nothingActive, nothingActive, 10, GateSettleBudgetMs, StepAhead).Step == GateStep.Wait
              && GateSettle(nothingActive, nothingActive, 150, GateSettleBudgetMs, StepAhead) is { Step: GateStep.Refuse } neverActivated
              && neverActivated.Problem!.Contains("activated nothing") && neverActivated.Problem.Contains("still so after")
              && GateSettle(field, field, 10, GateSettleBudgetMs, NextActivates).Step == GateStep.Wait
              && GateSettle(field, field, 150, GateSettleBudgetMs, NextActivates) is { Step: GateStep.Refuse } stillOpen
              && stillOpen.Problem!.Contains("is still activated") && stillOpen.Problem.Contains("still so after")
              && GateSettle(null, field, 10, GateSettleBudgetMs, StepAhead).Step == GateStep.Wait
              && GateSettle(field, field with { Active = ours2 }, 10, GateSettleBudgetMs, StepAhead).Step == GateStep.Wait
              && GateSettle(null, field, 150, GateSettleBudgetMs, StepAhead) is { Step: GateStep.Refuse } torn
              && torn.Problem!.Contains("did not read the same twice")
              && GateSettle(dyingOnStand, dyingOnStand, 60, GateSettleBudgetMs, StepAhead).Step == GateStep.Wait
              && GateSettle(dyingOnStand, dyingOnStand, 150, GateSettleBudgetMs, StepAhead) is { Step: GateStep.Refuse } stayed
              && stayed.Problem!.Contains("still listed at 0 health")
              && GateSettle(liveOnStand, liveOnStand, 0, GateSettleBudgetMs, StepAhead).Step == GateStep.Refuse
              && GateSettle(field, field, 0, GateSettleBudgetMs, StepAhead).Step == GateStep.Pass);

        Check("a verdict after the action's shortest delay, or after its record was consumed, is late, and one " +
              "inside the delay is not",
              !GateLate(25, 0.30, false) && GateLate(300, 0.30, false) && GateLate(25, 0.30, true)
              && DelayFloor(float.NaN) == 0.30 && DelayFloor(-1f) == 0.30 && Math.Abs(DelayFloor(0.8f) - 0.8) < 1e-6);

        var order = new List<string>();
        var freezeTries = 0;
        var holdsOnTry = 1;
        bool Refuses()
        {
            order.Add("judge");
            return true;
        }

        bool JudgeThrows()
        {
            order.Add("judge");
            throw new InvalidOperationException("a read of the board failed");
        }

        var quietTries = new List<bool>();
        var oursAnswers = new Queue<bool>();
        bool Freezes(bool quiet)
        {
            order.Add("freeze");
            quietTries.Add(quiet);
            freezeTries++;
            return freezeTries >= holdsOnTry;
        }

        bool StillOurs()
        {
            return oursAnswers.Count == 0 || oursAnswers.Dequeue();
        }

        void WaitsStill()
        {
            order.Add("wait");
        }

        void Detaches()
        {
            order.Add("detach");
        }

        void Tells(bool first, bool frozen)
        {
            order.Add(!first ? "tell-again" : frozen ? "tell" : "tell-open");
        }

        (bool? Stopped, string Calls) PollOnce(GateHalt halt, Func<bool> judge)
        {
            order.Clear();
            bool? stopped;
            try
            {
                stopped = halt.Poll(judge, Freezes, StillOurs, WaitsStill, Detaches, Tells);
            }
            catch (Exception)
            {
                stopped = null;
            }

            return (stopped, string.Join(",", order));
        }

        var inTime = new GateHalt();
        var held = PollOnce(inTime, Refuses);
        holdsOnTry = 99;
        var open = new GateHalt();
        var notHeld = PollOnce(open, Refuses);
        Check("a refusal writes the freeze before any line is printed and before it detaches, always waits for the " +
              "count to hold still between the two, and never detaches when the freeze could not be written",
              held.Stopped == true && held.Calls == "judge,freeze,tell,wait,detach" && inTime.Detached
              && notHeld.Calls == "judge,freeze,tell-open" && !open.Detached);

        freezeTries = 0;
        holdsOnTry = 3;
        var retried = new GateHalt();
        var retry1 = PollOnce(retried, Refuses);
        var openAfterFirst = retried.Refused && !retried.Detached;
        var retry2 = PollOnce(retried, Refuses);
        var retry3 = PollOnce(retried, Refuses);
        freezeTries = 0;
        holdsOnTry = 99;
        var never = new GateHalt();
        var neverPolls = new List<(bool? Stopped, string Calls)>();
        for (var poll = 0; poll < 5; poll++)
        {
            neverPolls.Add(PollOnce(never, Refuses));
        }

        Check("a refused turn whose freeze could not be written keeps polling without asking the check again, tries " +
              "the freeze at every poll and detaches once it holds, and a freeze that never holds leaves the undo to " +
              "the turn's end",
              retry1.Stopped == false && openAfterFirst
              && retry2.Stopped == false && retry2.Calls.Contains("freeze") && !retry2.Calls.Contains("judge")
              && !retry2.Calls.Contains("detach")
              && retry3.Stopped == true && retried.Detached && retry3.Calls.Contains("detach")
              && !retry3.Calls.Contains("judge")
              && neverPolls.All(p => p.Stopped == false && !p.Calls.Contains("detach"))
              && neverPolls.Skip(1).All(p => p.Calls.EndsWith("freeze") && !p.Calls.Contains("judge"))
              && never.Refused && !never.Detached);

        freezeTries = 0;
        holdsOnTry = 1;
        var threwHeld = new GateHalt();
        var afterThrowHeld = PollOnce(threwHeld, JudgeThrows);
        freezeTries = 0;
        holdsOnTry = 99;
        var threwOpen = new GateHalt();
        var afterThrowOpen = PollOnce(threwOpen, JudgeThrows);
        Check("an exception inside the board check is a refusal: the freeze is tried, the records are detached only " +
              "under it, and the exception never reaches the undo past the freeze",
              afterThrowHeld.Stopped == true && threwHeld.Refused && threwHeld.Detached
              && threwHeld.Failure == nameof(InvalidOperationException)
              && afterThrowHeld.Calls.IndexOf("freeze") < afterThrowHeld.Calls.IndexOf("detach")
              && afterThrowOpen.Stopped is not null && threwOpen.Refused && afterThrowOpen.Calls.Contains("freeze")
              && threwOpen.Failure == nameof(InvalidOperationException));

        freezeTries = 0;
        holdsOnTry = 2;
        var armedGone = new GateHalt();
        var goneFirst = PollOnce(armedGone, Refuses);
        oursAnswers.Enqueue(false);
        var goneRetry = PollOnce(armedGone, Refuses);
        var frozenWhileGone = armedGone.Frozen;
        oursAnswers.Enqueue(true);
        oursAnswers.Enqueue(false);
        var goneBeforeDetach = PollOnce(armedGone, Refuses);
        freezeTries = 0;
        holdsOnTry = 1;
        oursAnswers.Clear();
        oursAnswers.Enqueue(false);
        var goneAfterFirst = new GateHalt();
        var goneUnderFirst = PollOnce(goneAfterFirst, Refuses);
        oursAnswers.Clear();
        Check("a retried freeze is written only while the match the turn was armed on is still ours, and the " +
              "records are detached after any freeze only when it still is, else left to the undo at the turn's end",
              goneFirst.Stopped == false && goneFirst.Calls == "judge,freeze,tell-open"
              && goneRetry.Stopped == false && goneRetry.Calls == "" && !frozenWhileGone
              && goneBeforeDetach.Stopped == true && goneBeforeDetach.Calls == "freeze,tell-again,wait"
              && armedGone.Frozen && !armedGone.Detached
              && goneUnderFirst.Stopped == true && goneUnderFirst.Calls == "judge,freeze,tell,wait"
              && goneAfterFirst.Frozen && !goneAfterFirst.Detached);

        freezeTries = 0;
        holdsOnTry = 99;
        quietTries.Clear();
        var failing = new GateHalt();
        for (var poll = 0; poll < 4; poll++)
        {
            PollOnce(failing, Refuses);
        }

        Check("a freeze whose write keeps failing reports the failure on its first try only",
              quietTries.SequenceEqual([false, true, true, true]) && failing.FreezeFailures == 4);

        var leftTheList = field with { Active = 153 };
        Check("at B, a machine the game activated that has left the list is waited for inside the 150 ms and " +
              "refused only at the budget",
              GateSettle(leftTheList, leftTheList, 10, GateSettleBudgetMs, StepAhead).Step == GateStep.Wait
              && GateSettle(leftTheList, leftTheList, 150, GateSettleBudgetMs, StepAhead) is { Step: GateStep.Refuse } leftRefused
              && leftRefused.Problem!.Contains("is not on the board")
              && leftRefused.Problem.Contains("still so after"));

        var points = GatePoints([new Act(false, 1, 6, 1, 5, 0, false), new Act(true, 2, 6, 2, 4, 0, true, 2, 5),
                                 new Act(false, 3, 6, 3, 5, 0, false)]);
        Check("the board check runs after each action's activate or burst, and after each move or attack that " +
              "another action follows",
              points.Count == 5
              && points[1] == new GatePoint(false, 0) && points[2] == new GatePoint(true, 1)
              && points[4] == new GatePoint(false, 1) && points[5] == new GatePoint(true, 2)
              && points[6] == new GatePoint(false, 2) && !points.ContainsKey(3) && !points.ContainsKey(7));

        const int grazer = 257;
        const int burrow = 258;
        Check("a Ram's push and advance, its Overcharge move from its victim's square, and a strike then an " +
              "Overcharge move onto the square a knockback emptied pass the board check (gate-v12, turn 1)",
              Verdict(Board(8, 8, grazer, Unit(grazer, 4, 6, 4, true, RamPattern, 1, 2), Unit(burrow, 3, 6, 4, true, 0, 1, 2),
                            Unit(theirs, 3, 4, 4, false, RamPattern, 1, 2), Unit(theirs2, 4, 4, 4, false, 0, 1, 2)),
                      new Act(true, 4, 6, 4, 4, 0, false, 4, 5), 0) is null
              && Verdict(Board(8, 8, grazer, Unit(grazer, 4, 4, 4, true, RamPattern, 1, 2), Unit(burrow, 3, 6, 4, true, 0, 1, 2),
                               Unit(theirs, 3, 4, 4, false, RamPattern, 1, 2), Unit(theirs2, 4, 3, 3, false, 0, 1, 2)),
                         new Act(false, 4, 4, 4, 5, 0, true), 1) is null
              && Verdict(Board(8, 8, burrow, Unit(grazer, 4, 5, 2, true, RamPattern, 1, 2), Unit(burrow, 3, 6, 4, true, 0, 1, 2),
                               Unit(theirs, 3, 4, 4, false, RamPattern, 1, 2), Unit(theirs2, 4, 3, 3, false, 0, 1, 2)),
                         new Act(true, 3, 6, 3, 4, 0, false, 3, 5), 2) is null
              && Verdict(Board(8, 8, burrow, Unit(grazer, 4, 5, 2, true, RamPattern, 1, 2), Unit(burrow, 3, 5, 3, true, 0, 1, 2),
                               Unit(theirs, 3, 3, 3, false, RamPattern, 1, 2), Unit(theirs2, 4, 3, 3, false, 0, 1, 2)),
                         new Act(false, 3, 5, 3, 4, 0, true), 3) is null);

        const int tremor = 273;
        const int plow = 274;
        var hunterOurs = new[] { Unit(275, 5, 2, 8, true, 4, 3, 3, SpreadSkill), Unit(276, 5, 4, 4, true, RamPattern, 1, 3),
                                 Unit(277, 3, 3, 7, true, 5, 3, 2) };
        var hunterTheirs = new[] { Unit(289, 4, 0, 10, false, 1, 2, 2), Unit(290, 5, 0, 4, false, 0, 1, 2),
                                   Unit(291, 3, 0, 7, false, 0, 1, 2) };
        Check("a charge in place, the charger's owed move from its landing, and moves onto the square it charged from " +
              "and onto its victim's square pass the board check (halt-hunter, turn 2)",
              Verdict(Board(6, 5, tremor, [Unit(tremor, 4, 3, 10, true, DashPattern, 2, 2, SpreadSkill),
                                           Unit(plow, 4, 4, 2, true, RamPattern, 1, 2), Unit(292, 4, 2, 3, false, 0, 1, 4),
                                           .. hunterOurs, .. hunterTheirs]),
                      new Act(true, 4, 3, 4, 2, 0, false, 4, 3), 0) is null
              && JudgeBeforeActivate(Board(6, 5, tremor, [Unit(tremor, 4, 1, 10, true, DashPattern, 2, 2, SpreadSkill),
                                                          Unit(plow, 4, 4, 2, true, RamPattern, 1, 2), .. hunterOurs, .. hunterTheirs]),
                                     new Act(false, 4, 1, 5, 1, 3, false), 1).Problem is null
              && Verdict(Board(6, 5, tremor, [Unit(tremor, 4, 1, 10, true, DashPattern, 2, 2, SpreadSkill),
                                              Unit(plow, 4, 4, 2, true, RamPattern, 1, 2), .. hunterOurs, .. hunterTheirs]),
                         new Act(false, 4, 1, 5, 1, 3, false), 1) is null
              && Verdict(Board(6, 5, plow, [Unit(tremor, 5, 1, 10, true, DashPattern, 2, 2, SpreadSkill),
                                            Unit(plow, 4, 4, 2, true, RamPattern, 1, 2), .. hunterOurs, .. hunterTheirs]),
                         new Act(false, 4, 4, 4, 3, 1, false), 2) is null
              && Verdict(Board(6, 5, plow, [Unit(tremor, 5, 1, 10, true, DashPattern, 2, 2, SpreadSkill),
                                            Unit(plow, 4, 3, 2, true, RamPattern, 1, 2), .. hunterOurs, .. hunterTheirs]),
                         new Act(false, 4, 3, 4, 2, 1, true), 3) is null);

        const int chargerId = 305;
        const int burrowerId = 306;
        Check("a charge in place with the charger's move from its landing, and a walk then a charge, pass the board " +
              "check (walk-then-charge-ending, PC2's turn 1 and PC1's last turn)",
              Verdict(Board(4, 4, chargerId, Unit(chargerId, 1, 2, 4, true, DashPattern, 2, 3),
                            Unit(burrowerId, 2, 2, 4, true, 0, 1, 2), Unit(theirs, 1, 1, 4, false, 0, 1, 2),
                            Unit(theirs2, 3, 1, 4, false, DashPattern, 2, 3)),
                      new Act(true, 1, 2, 1, 1, 0, false, 1, 2), 0) is null
              && Verdict(Board(4, 4, chargerId, Unit(chargerId, 1, 0, 4, true, DashPattern, 2, 3),
                               Unit(burrowerId, 2, 2, 4, true, 0, 1, 2), Unit(theirs, 1, 1, 2, false, 0, 1, 2),
                               Unit(theirs2, 3, 1, 4, false, DashPattern, 2, 3)),
                         new Act(false, 1, 0, 0, 0, 0, false), 1) is null
              && Verdict(Board(4, 4, burrowerId, Unit(chargerId, 0, 0, 4, true, DashPattern, 2, 3),
                               Unit(burrowerId, 2, 2, 4, true, 0, 1, 2), Unit(theirs, 1, 1, 2, false, 0, 1, 2),
                               Unit(theirs2, 3, 1, 4, false, DashPattern, 2, 3)),
                         new Act(false, 2, 2, 3, 2, 1, false), 2) is null
              && Verdict(Board(4, 4, chargerId, Unit(chargerId, 1, 0, 4, true, DashPattern, 2, 3),
                               Unit(burrowerId, 1, 2, 2, true, 0, 1, 2), Unit(theirs, 0, 2, 1, false, 0, 1, 2),
                               Unit(theirs2, 3, 1, 2, false, DashPattern, 2, 3)),
                         new Act(true, 1, 0, 3, 1, 2, false, 3, 0), 0) is null);

        const int glint = 321;
        const int fireclaw = 322;
        var diveTheirs = new[] { Unit(337, 1, 1, 6, false, 4, 3, 2), Unit(338, 4, 3, 8, false, 0, 2, 2) };
        Check("a Dive from its firing square, its Overcharge move onto its victim's square after the knockback, and " +
              "another machine's walk, strike and Overcharge strike pass the board check (dying-attacker, turn 2)",
              Verdict(Board(8, 8, glint, [Unit(glint, 5, 6, 5, true, 4, 3, 3), Unit(fireclaw, 5, 7, 10, true, 0, 2, 3),
                                          Unit(theirs, 3, 5, 8, false, 0, 1, 2), Unit(theirs2, 7, 4, 5, false, 4, 2, 3),
                                          .. diveTheirs]),
                      new Act(true, 5, 6, 7, 4, 0, false, 7, 6), 0) is null
              && Verdict(Board(8, 8, glint, [Unit(glint, 7, 5, 4, true, 4, 3, 3), Unit(fireclaw, 5, 7, 10, true, 0, 2, 3),
                                             Unit(theirs, 3, 5, 8, false, 0, 1, 2), Unit(theirs2, 7, 3, 4, false, 4, 2, 3),
                                             .. diveTheirs]),
                         new Act(false, 7, 5, 7, 4, 0, true), 1) is null
              && Verdict(Board(8, 8, fireclaw, [Unit(glint, 7, 4, 2, true, 4, 3, 3), Unit(fireclaw, 5, 7, 10, true, 0, 2, 3),
                                                Unit(theirs, 3, 5, 8, false, 0, 1, 2), Unit(theirs2, 7, 3, 4, false, 4, 2, 3),
                                                .. diveTheirs]),
                         new Act(true, 5, 7, 3, 5, 0, false, 3, 6), 2) is null
              && Verdict(Board(8, 8, fireclaw, [Unit(glint, 7, 4, 2, true, 4, 3, 3), Unit(fireclaw, 3, 6, 10, true, 0, 2, 3),
                                                Unit(theirs, 3, 5, 6, false, 0, 1, 2), Unit(theirs2, 7, 3, 4, false, 4, 2, 3),
                                                .. diveTheirs]),
                         new Act(true, 3, 6, 3, 5, 0, true, 3, 6), 3) is null);

        const int glinthawk = 353;
        const int stormbird = 354;
        var shotOurs = new[] { Unit(355, 0, 6, 9, true, 4, 3, 2), Unit(356, 2, 4, 9, true, 4, 3, 2),
                               Unit(357, 3, 4, 7, true, 4, 2, 3), Unit(358, 4, 6, 7, true, 4, 2, 3) };
        var shotTheirs = new[] { Unit(369, 5, 3, 6, false, 0, 2, 3), Unit(370, 4, 5, 11, false, DashPattern, 3, 2),
                                 Unit(371, 2, 3, 5, false, 1, 2, 3) };
        Check("a Dive that struck from where it walked and landed beside its victim, its Overcharge move that killed " +
              "it, and another machine's move after pass the board check (inplace-shot-facing, PC2's turn 4)",
              Verdict(Board(8, 8, glinthawk, [Unit(glinthawk, 4, 4, 2, true, 4, 3, 3),
                                              Unit(stormbird, 2, 5, 1, true, 4, 3, 3, SpreadSkill), .. shotOurs, .. shotTheirs]),
                      new Act(true, 4, 4, 5, 3, 0, false, 5, 5), 0) is null
              && Verdict(Board(8, 8, glinthawk, [Unit(glinthawk, 5, 4, 2, true, 4, 3, 3),
                                                 Unit(stormbird, 2, 5, 1, true, 4, 3, 3, SpreadSkill), .. shotOurs, .. shotTheirs]),
                         new Act(false, 5, 4, 5, 5, 0, true), 1) is null
              && JudgeBeforeActivate(Board(8, 8, glinthawk, [Unit(stormbird, 2, 5, 1, true, 4, 3, 3, SpreadSkill),
                                                             .. shotOurs, .. shotTheirs]),
                                     new Act(false, 2, 5, 1, 5, 2, false), 2).Problem is null
              && Verdict(Board(8, 8, stormbird, [Unit(stormbird, 2, 5, 1, true, 4, 3, 3, SpreadSkill), .. shotOurs,
                                                 .. shotTheirs]),
                         new Act(false, 2, 5, 1, 5, 2, false), 2) is null);

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
              stub8[..7].SequenceEqual(MoveBoundsSite8Original[..7]) &&
              stub8[27..31].SequenceEqual(MoveBoundsSite8Original[7..]) && stub8[^5] == 0xE9 &&
              MoveBoundsSite8Original.Length == 11 &&
              MoveBoundsSite8Original.Length == (int)(MoveBoundsSite8Back - MoveBoundsSite8Rva));
        Check("stub 8 on a 6-wide board zeroes two padding bytes at the row end and skips the counter by two",
              Contains(stub8, [0x41, 0x83, 0xFA, 0x05]) &&
              Contains(stub8, [0xC6, 0x44, 0x11, 0x71, 0x00]) && Contains(stub8, [0xC6, 0x44, 0x11, 0x72, 0x00]) &&
              !Contains(stub8, [0xC6, 0x44, 0x11, 0x73, 0x00]) &&
              Contains(stub8, [0x83, 0x82, 0xB0, 0x00, 0x00, 0x00, 0x02]));
        Check("stub 8's row-end skip lands exactly on the jump back",
              stub8[42] == 0x75 && stub8[43] == 17 && stub8.Length == 66);
        Check("stub 8 on an 8-wide board is the displaced pair, the cap and the jump back",
              MoveBoundsStub8(stubProbe + 0x1C0, 8).Length == 36);

        const int moveTableAt = 0x70;
        const int moveCountAt = 0xB0;
        static string? Stub8Run(byte[] code, ulong stubAt, ulong backTo, byte[] frame, byte terrain)
        {
            long rcx = 0;
            long r10 = 0;
            var below = false;
            var equal = false;
            var ip = 0;
            for (var steps = 0; steps < 64; steps++)
            {
                var left = code.Length - ip;
                if (ip < 0 || left <= 0)
                {
                    return $"ran off the stub at {ip}";
                }

                var op = code[ip];
                var second = left > 1 ? code[ip + 1] : (byte)0;
                var third = left > 2 ? code[ip + 2] : (byte)0;
                if (op == 0x48 && second == 0x63 && third == 0x8A && left >= 7)
                {
                    var at = BitConverter.ToInt32(code, ip + 3);
                    if (at != moveCountAt)
                    {
                        return $"the index is read from +0x{at:X}";
                    }

                    rcx = BitConverter.ToInt32(frame, at);
                    ip += 7;
                }
                else if (op == 0x83 && second == 0xF9 && left >= 3)
                {
                    var against = (uint)(sbyte)third;
                    below = (uint)rcx < against;
                    equal = (uint)rcx == against;
                    ip += 3;
                }
                else if (op == 0x72 && left >= 2)
                {
                    ip += below ? 2 + (sbyte)second : 2;
                }
                else if (op == 0x75 && left >= 2)
                {
                    ip += equal ? 2 : 2 + (sbyte)second;
                }
                else if (op == 0xC7 && second == 0x82 && left >= 10)
                {
                    var at = BitConverter.ToInt32(code, ip + 2);
                    if (at != moveCountAt)
                    {
                        return $"a dword is set at +0x{at:X}";
                    }

                    Array.Copy(code, ip + 6, frame, at, 4);
                    ip += 10;
                }
                else if (op == 0x83 && second == 0x82 && left >= 7)
                {
                    var at = BitConverter.ToInt32(code, ip + 2);
                    if (at != moveCountAt)
                    {
                        return $"a dword is added to at +0x{at:X}";
                    }

                    BitConverter.GetBytes(BitConverter.ToInt32(frame, at) + (sbyte)code[ip + 6]).CopyTo(frame, at);
                    ip += 7;
                }
                else if (op == 0x88 && second == 0x44 && third == 0x11 && left >= 4)
                {
                    var at = rcx + (sbyte)code[ip + 3];
                    if (at < moveTableAt || at >= moveCountAt)
                    {
                        return $"a square's terrain is stored at +0x{at:X}, outside the table";
                    }

                    frame[(int)at] = terrain;
                    ip += 4;
                }
                else if (op == 0xC6 && second == 0x44 && third == 0x11 && left >= 5)
                {
                    var at = rcx + (sbyte)code[ip + 3];
                    if (at < moveTableAt || at >= moveCountAt)
                    {
                        return $"a padding byte is stored at +0x{at:X}, outside the table";
                    }

                    frame[(int)at] = code[ip + 4];
                    ip += 5;
                }
                else if (op == 0x41 && second == 0x89 && third == 0xCA && left >= 3)
                {
                    r10 = (uint)rcx;
                    ip += 3;
                }
                else if (op == 0x41 && second == 0x83 && third == 0xE2 && left >= 4)
                {
                    r10 = (uint)r10 & (uint)(sbyte)code[ip + 3];
                    below = false;
                    equal = r10 == 0;
                    ip += 4;
                }
                else if (op == 0x41 && second == 0x83 && third == 0xFA && left >= 4)
                {
                    var against = (uint)(sbyte)code[ip + 3];
                    below = (uint)r10 < against;
                    equal = (uint)r10 == against;
                    ip += 4;
                }
                else if (op == 0xE9 && left >= 5)
                {
                    var to = (ulong)((long)(stubAt + (ulong)ip + 5) + BitConverter.ToInt32(code, ip + 1));
                    return to == backTo ? null : $"the jump at {ip} goes to 0x{to:X}, not back";
                }
                else
                {
                    return $"byte 0x{op:X2} at {ip} is not an instruction the stub emits";
                }
            }

            return "no jump back within 64 instructions";
        }

        static string? Stub8Board(byte[] code, ulong stubAt, ulong backTo, int builtFor, int width, int height,
                                  byte[] terrain)
        {
            var frame = new byte[0x200];
            for (var t = 0; t < width * height; t++)
            {
                if (Stub8Run(code, stubAt, backTo, frame, terrain[t]) is { } problem)
                {
                    return $"square {t}: {problem}";
                }

                BitConverter.GetBytes(BitConverter.ToInt32(frame, moveCountAt) + 1).CopyTo(frame, moveCountAt);
            }

            var count = BitConverter.ToInt32(frame, moveCountAt);
            if (count is < 0 or > 64)
            {
                return $"the count ends at {count}";
            }

            if (builtFor != width)
            {
                return null;
            }

            if (count != 8 * height)
            {
                return $"the count ends at {count}, not {8 * height}";
            }

            for (var slot = 0; slot < 64; slot++)
            {
                var (x, y) = (slot % 8, slot / 8);
                var expected = x < width && y < height ? terrain[x + width * y] : (byte)0;
                if (frame[moveTableAt + slot] != expected)
                {
                    return $"place {slot} holds {frame[moveTableAt + slot]}, not {expected}";
                }
            }

            return null;
        }

        var stub8Problems = new List<string>();
        for (var builtFor = 1; builtFor <= 8; builtFor++)
        {
            var built = MoveBoundsStub8(stubProbe + 0x1C0, builtFor);
            for (var width = 1; width <= 8; width++)
            {
                for (var height = 1; height <= 8; height++)
                {
                    var squares = width * height;
                    var terrains = new List<byte[]>
                    {
                        new byte[squares],
                        Enumerable.Repeat((byte)0xFF, squares).ToArray(),
                        Enumerable.Repeat((byte)0xFD, squares).ToArray(),
                    };
                    for (var s = 0; s < squares; s++)
                    {
                        var one = new byte[squares];
                        one[s] = 0xFE;
                        terrains.Add(one);
                    }

                    foreach (var terrain in terrains)
                    {
                        if (Stub8Board(built, stubProbe + 0x1C0, _base + MoveBoundsSite8Back, builtFor, width, height,
                                       terrain) is { } problem)
                        {
                            stub8Problems.Add($"built {builtFor} wide, board {width}x{height}: {problem}");
                        }
                    }
                }
            }
        }

        foreach (var problem in stub8Problems.Take(3))
        {
            Console.WriteLine($"  stub 8: {problem}");
        }

        Check("stub 8 at any width never writes past the 64-square table or lets the count pass 64, on any board " +
              "up to 8x8 and any terrain, and still lays its own width out eight wide",
              stub8Problems.Count == 0);
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

        var cursorStub = AiCursorStub(0x140E3F3B0, 0x140E41790);
        Check("AI cursor: the stub saves rbx and a shadow space, runs the stock activate on the controller, then the light with the controller and 0, and returns",
              cursorStub.Length == 43
              && cursorStub.Take(10).SequenceEqual(new byte[] { 0x53, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xD9, 0x48, 0xB8 })
              && BitConverter.ToUInt64(cursorStub, 10) == 0x140E3F3B0
              && cursorStub.Skip(18).Take(9).SequenceEqual(new byte[] { 0xFF, 0xD0, 0x48, 0x8B, 0xCB, 0x33, 0xD2, 0x48, 0xB8 })
              && BitConverter.ToUInt64(cursorStub, 27) == 0x140E41790
              && cursorStub.Skip(35).SequenceEqual(new byte[] { 0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3 }));
        Check("AI cursor: the stub sits after the four commit stubs and ends inside the page, and the slot is the AI vtable's third virtual",
              CursorStubAt() == CommitStubsAt + CommitSites.Length * CommitStubStride
              && CursorStubAt() >= CommitStubsAt + (CommitSites.Length - 1) * CommitStubStride + commitStubs.Max(c => c.Length)
              && CursorStubAt() + cursorStub.Length <= 0x1000
              && ActivateSlot == 0x10
              && AiControllerVtableRva == ControllerVtables.Single(v => v.Name.StartsWith("AI")).Rva);
        Check("AI cursor: the slot is put back only when it points outside the game; the stock is left, and a game pointer that is not the stock is another build's",
              AiActivateVerdict(BitConverter.GetBytes(0x140E3F3B0UL), 0x140E3F3B0, 0x140000000, 0x149400000) == AiActivateState.Stock
              && AiActivateVerdict(BitConverter.GetBytes(0x1A0000410UL), 0x140E3F3B0, 0x140000000, 0x149400000) == AiActivateState.Ours
              && AiActivateVerdict(BitConverter.GetBytes(0x140E48F70UL), 0x140E3F3B0, 0x140000000, 0x149400000) == AiActivateState.Foreign
              && AiActivateVerdict(null, 0x140E3F3B0, 0x140000000, 0x149400000) == AiActivateState.Unreadable
              && AiActivateVerdict([0x01], 0x140E3F3B0, 0x140000000, 0x149400000) == AiActivateState.Unreadable);

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

        List<(int X, int Y, int Facing, int Hp)> pulledSpun = [(0, 0, 0, 3), (1, 2, 0, 4)];
        List<(int X, int Y, int Facing, int Hp)> walkedTwo = [(0, 0, 0, 3), (1, 3, 2, 4)];
        List<(int X, int Y, int Facing, int Hp)> bothMoved = [(0, 1, 0, 3), (1, 2, 2, 4)];
        Check("one machine displaced by exactly one square is a shove or a pull, whatever its facing; two squares or two machines are not",
              ShovedOneSquare(walked, stood) && ShovedOneSquare(pulledSpun, stood)
              && !ShovedOneSquare(walkedTwo, stood) && !ShovedOneSquare(bothMoved, stood)
              && !ShovedOneSquare(spun, stood) && !ShovedOneSquare([], stood)
              && RecentDamage >= TimeSpan.FromSeconds(1));

        List<(string What, ulong Addr, byte[] Bytes)> namesPlan = [("your corner", 0x1000, [0x41, 0x42, 0x00]), ("their corner", 0x2000, [0x43, 0x00])];
        var namesMemory = new Dictionary<ulong, byte[]> { [0x1000] = [0x41, 0x42, 0x00], [0x2000] = [0x43, 0x00] };
        var namesRebuilt = new Dictionary<ulong, byte[]> { [0x1000] = [0x41, 0x4C, 0x4F], [0x2000] = [0x43, 0x00] };
        Check("the names hold rewrites when a written label no longer reads back, or cannot be read, and stays quiet while both hold",
              !NamesNeedRewrite(namesPlan, a => namesMemory.GetValueOrDefault(a.Addr))
              && NamesNeedRewrite(namesPlan, a => namesRebuilt.GetValueOrDefault(a.Addr))
              && NamesNeedRewrite(namesPlan, a => null)
              && NamesHeldLine.Contains("holding"));

        Check("the names hold runs as the names hold, never as a second AI hold, and the AI hold still dispatches on its own flag",
              !IsAiHold(["--set-names", "--me", "EXAMPLE", "--yes", NamesHoldFlag, "--parent-pid", "4242"])
              && !IsAiHold(["--set-names", "--me", "EXAMPLE", "--yes", "--hold"])
              && IsAiHold(["--hold", "--wait", "600", "--secs", "0", "--yes", "--parent-pid", "4242"])
              && NamesHoldFlag != "--hold");

        Check("the label search finds every match, overlapping ones and one at the very end, and nothing where there is none",
              MatchesIn("AAAA"u8, "AA"u8.ToArray()).SequenceEqual([0, 1, 2])
              && MatchesIn("xALOY\0ALOY\0"u8, "ALOY\0"u8.ToArray()).SequenceEqual([1, 6])
              && MatchesIn("ALOx"u8, "ALOY\0"u8.ToArray()).Count == 0
              && MatchesIn(""u8, "A"u8.ToArray()).Count == 0);

        Check("the names are looked for in private read-write memory only, never in write-combined, image or mapped memory",
              HeapRegion(0x20000, 0x04)
              && !HeapRegion(0x20000, 0x404) && !HeapRegion(0x1000000, 0x04) && !HeapRegion(0x40000, 0x04)
              && !HeapRegion(0x20000, 0x02));

        List<(string What, ulong Addr, byte[] Bytes)> theirsOnly = [("their corner", 0x2000, [0x43, 0x00])];
        Check("a corner the hold was asked to name and has not written counts as missing, so the hold keeps looking for it",
              CornerMissing(theirsOnly, "EXAMPLE", "OTHER")
              && !CornerMissing(namesPlan, "EXAMPLE", "OTHER")
              && !CornerMissing(theirsOnly, null, "OTHER")
              && CornerMissing([], null, "OTHER")
              && !CornerMissing([], null, null));

        Check("the hold looks again a second after a label is lost, then every 3 s, every 10 s after a minute, and every 30 s while all is well",
              !NamesSweepDue(true, TimeSpan.FromSeconds(0.5), TimeSpan.FromMinutes(5))
              && NamesSweepDue(true, TimeSpan.FromSeconds(1.5), TimeSpan.FromMinutes(5))
              && !NamesSweepDue(true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2))
              && NamesSweepDue(true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3))
              && !NamesSweepDue(true, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5))
              && NamesSweepDue(true, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10))
              && !NamesSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(29))
              && NamesSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(30)));

        Check("D-291: every sweep of the names hold is logged with how long it read the game's memory and what it found",
              NamesSweepLine(TimeSpan.FromMilliseconds(612), 0)
                  == "  names hold swept the game's memory in 612 ms, nothing to write"
              && NamesSweepLine(TimeSpan.FromMilliseconds(640.7), 2)
                  == "  names hold swept the game's memory in 640 ms, 2 to write");

        Check("at match entry the names wait for both corners to exist, and write what there is after 20 s",
              NamesEntryDone(namesPlan, "EXAMPLE", "OTHER", TimeSpan.Zero)
              && !NamesEntryDone(theirsOnly, "EXAMPLE", "OTHER", TimeSpan.FromSeconds(5))
              && !NamesEntryDone([], "EXAMPLE", "OTHER", TimeSpan.FromSeconds(19))
              && NamesEntryDone(theirsOnly, "EXAMPLE", "OTHER", TimeSpan.FromSeconds(20)));

        List<(string What, ulong Addr, byte[] Bytes)> sweepFound = [("your corner", 0x3000, [0x41, 0x42, 0x00]), ("their corner", 0x2000, [0x43, 0x00])];
        var (namesMerged, namesFresh) = MergeNames(theirsOnly, sweepFound);
        var (_, nothingNew) = MergeNames(namesMerged, sweepFound);
        Check("a sweep writes only the labels it has not written already, and the hold keeps every label it holds",
              namesFresh.Count == 1 && namesFresh[0].Addr == 0x3000
              && namesMerged.Count == 2 && namesMerged.Any(e => e.Addr == 0x2000) && namesMerged.Any(e => e.Addr == 0x3000)
              && nothingNew.Count == 0);

        Check("a name is written only where the game's own label still reads at the moment of writing, and its length only with it",
              StillStock("ALOY\0"u8.ToArray(), StockAt("your corner"), labelLeftAlone: false)
              && !StillStock("AL\0Y\0"u8.ToArray(), StockAt("your corner"), labelLeftAlone: false)
              && !StillStock(null, StockAt("their corner"), labelLeftAlone: false)
              && StillStock(BitConverter.GetBytes(8u), StockAt("their corner length"), labelLeftAlone: false)
              && !StillStock(BitConverter.GetBytes(8u), StockAt("their corner length"), labelLeftAlone: true)
              && !StillStock("ALOY\0"u8.ToArray(), StockAt("a label this build does not know"), labelLeftAlone: false));

        Check("the set screen's label is written only until the match is first seen live, never after its restore",
              SetLabelTicks(DateTime.MinValue) && !SetLabelTicks(DateTime.UtcNow));

        var setShort = SetLabelBytes("Halt Hunter", 47, StockSetLabel.Length);
        Check("the army name goes into the set label with its terminator and the tag, and clears what the stock text and an old tag left",
              setShort.TextLength == 11 && setShort.Tagged && !setShort.Cut
              && setShort.Bytes.Length == StockSetLabel.Length + 1 + SetLabelTag.Length
              && setShort.Bytes.AsSpan(0, 12).SequenceEqual("Halt Hunter\0"u8)
              && setShort.Bytes.AsSpan(12, 8).SequenceEqual(SetLabelTag)
              && setShort.Bytes.AsSpan(20).IndexOfAnyExcept((byte)0) < 0);

        var accented = new string('\u00E9', 24);
        var setLong = SetLabelBytes(accented, 47, StockSetLabel.Length);
        var setNarrow = SetLabelBytes("Halt Hunter", 15, StockSetLabel.Length);
        Check("a name too long for the room drops the tag and is cut at a whole character, and a narrow block takes the name without the tag",
              !setLong.Tagged && setLong.Cut && setLong.TextLength == 46 && setLong.Bytes[46] == 0
              && System.Text.Encoding.UTF8.GetString(setLong.Bytes, 0, 46) == new string('\u00E9', 23)
              && setLong.Bytes.Length <= 48
              && !setNarrow.Tagged && !setNarrow.Cut && setNarrow.TextLength == 11 && setNarrow.Bytes.Length <= 16);

        byte[] SetWindow(uint refcount, uint length, string text)
        {
            var window = new byte[TagLookBehind];
            var start = TagLookBehind - text.Length;
            BitConverter.GetBytes(refcount).CopyTo(window, start - 16);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(window, start - 12);
            BitConverter.GetBytes(length).CopyTo(window, start - 8);
            BitConverter.GetBytes(0x3Fu).CopyTo(window, start - 4);
            System.Text.Encoding.ASCII.GetBytes(text).CopyTo(window, start);
            return window;
        }

        Check("a tagged label is traced back to its text by the header whose length matches, and a freed or mismatched one is not",
              TaggedTextLength(SetWindow(1, 11, "Halt Hunter")) == 11
              && TaggedTextLength(SetWindow(1, 10, "Halt Hunter")) is null
              && TaggedTextLength(SetWindow(0, 11, "Halt Hunter")) is null);

        var liveSet = Convert.FromHexString("01000000FFFFFFFF0E0000003F000000");
        Check("the set label is written only while its block is live: a released block, an interned copy or a wrong length is left alone",
              LiveLabelHeader(liveSet, 14)
              && !LiveLabelHeader(Convert.FromHexString("00000000FFFFFFFF0E0000003F000000"), 14)
              && !LiveLabelHeader(Convert.FromHexString("0100008012345678" + "0E0000000E000000"), 14)
              && !LiveLabelHeader(liveSet, 11)
              && !LiveLabelHeader(null, 14));

        var runDrawn = new byte[TextRunFontSize + 4];
        BitConverter.GetBytes(1.0f).CopyTo(runDrawn, 12);
        BitConverter.GetBytes(21.0f).CopyTo(runDrawn, TextRunFontSize);
        var runNot = new byte[TextRunFontSize + 4];
        List<SetLabelSpot> setSpots = [new(0x5000, 14, 15), new(0x6000, 14, 47)];
        var setDrawn = MarkDrawn(setSpots, [[0x7030], [0x8030]], run => run == 0x8000 ? runDrawn : runNot);
        Check("only a copy a text run points at is the label on screen; a copy other objects point at, or nothing does, is its source",
              setDrawn.Count == 2 && !setDrawn[0].Drawn && setDrawn[1].Drawn && setDrawn[1].Text == 0x6000
              && MarkDrawn(setSpots, [[], []], _ => runDrawn).All(s => !s.Drawn)
              && LooksLikeTextRun(runDrawn) && !LooksLikeTextRun(runNot));

        Check("the source is searched the moment its references move, the label every 10 s while only the source is held, and on the names cadence otherwise",
              SetLabelSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(0.5), sourceMoved: true, labelMissing: false)
              && !SetLabelSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(9), sourceMoved: false, labelMissing: true)
              && SetLabelSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(10), sourceMoved: false, labelMissing: true)
              && !SetLabelSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(29), sourceMoved: false, labelMissing: false)
              && SetLabelSweepDue(false, TimeSpan.Zero, TimeSpan.FromSeconds(30), sourceMoved: false, labelMissing: false)
              && SetLabelSweepDue(true, TimeSpan.FromSeconds(1.5), TimeSpan.FromMinutes(5), sourceMoved: false, labelMissing: true));

        Dictionary<ulong, uint> sourceSeen = new() { [0x9000] = 1 };
        Check("the source's references moving is a change in its refcount, and an unreadable one is not",
              SourceRefsMoved(sourceSeen, _ => 3u)
              && !SourceRefsMoved(sourceSeen, _ => 1u)
              && !SourceRefsMoved(sourceSeen, _ => null)
              && !SourceRefsMoved(new Dictionary<ulong, uint>(), _ => 3u));

        Check("the source's text is searched as the UTF-8 bytes that were written, with the terminator",
              TerminatedUtf8("Tow test").SequenceEqual("Tow test\0"u8.ToArray())
              && TerminatedUtf8("Café").Length == 6 && TerminatedUtf8("Café")[5] == 0);

        Check("the set label goes back to the game's text over the whole length that was written",
              SetLabelRestoreBytes(20).Length == 20
              && SetLabelRestoreBytes(20).AsSpan(0, 15).SequenceEqual("Beginner's Set\0"u8)
              && SetLabelRestoreBytes(20).AsSpan(15).IndexOfAnyExcept((byte)0) < 0
              && SetLabelRestoreBytes(4).Length == 15);

        Check("an army name loses control characters and outer spaces, stops at 32 characters, and an empty one writes nothing",
              CleanArmyName(" Halt\tHunter\n ") == "HaltHunter"
              && CleanArmyName(new string('a', 40))!.Length == MaxArmyNameChars
              && CleanArmyName(" \t ") is null && CleanArmyName(null) is null
              && CleanArmyName("Caf\u00E9 Gr\u00F6\u00DFe") == "Caf\u00E9 Gr\u00F6\u00DFe");

        Check("the army name arrives as hex from netplay, a name shaped like a flag stays a name, and bad hex writes nothing",
              ArmyNameArg(["--set-names", ArmyNameHexFlag, "48616C742048756E746572"]) == "Halt Hunter"
              && ArmyNameArg(["--set-names", ArmyNameHexFlag, "2D2D686967686C69676874"]) == "--highlight"
              && ArmyNameArg(["--set-names", ArmyNameHexFlag, "4G"]) is null
              && ArmyNameArg(["--set-names", ArmyNameHexFlag, "486"]) is null
              && ArmyNameArg(["--set-names", "--army-name", "Plain Steel"]) == "Plain Steel"
              && ArmyNameArg(["--set-names", "--me", "EXAMPLE"]) is null
              && ArmyNameHexFlag == "--army-name-hex");

        Check("--army-name rides the names hold's own arguments, so it never dispatches as the AI hold",
              !IsAiHold(["--set-names", "--army-name", "Halt Hunter", "--yes", NamesHoldFlag, "--parent-pid", "4242"]));

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

        const string board = "0B7E1F3C5A9D42E8B16C4F7A2D8E90B3";
        string[][] writePaths =
        [
            ["--patch-move-bounds", "--clear"],
            ["--patch-move-bounds", "--yes", "--parent-pid", "4242"],
            ["--patch-reject", "--yes"],
            ["--restore-reject", "--yes"],
            ["--force-first", "clear", "--yes"],
            ["--force-first", "host", "--yes", "--wait", "600", "--parent-pid", "4242"],
            ["--first", "0"],
            ["--freeze"],
            ["--freeze", "--clear"],
            ["--commit-ring", "--clear"],
            ["--commit-ring", "--yes", "--parent-pid", "4242"],
            ["--commit-log"],
            ["--hold", "--wait", "600", "--secs", "0", "--yes", "--parent-pid", "4242"],
            ["--hold-placement", "--wait", "600", "--parent-pid", "4242"],
            ["--set-names", "--me", "EXAMPLE", "--yes", NamesHoldFlag, "--wait", "600", "--parent-pid", "4242"],
            ["--set-names", "--army-name-hex", "41", "--yes", NamesHoldFlag, "--parent-pid", "4242"],
            ["--set-units", "--allocate", "--board-game", board, "--human", board, "--ai", board, "--yes"],
            ["--set-units", "--board-game", board, "--human", board, "--ai", board, "--yes"],
            ["--set-rules", "--board-game", board, "--victory-points", "7", "--yes"],
            ["--set-board-size", "--board-game", board, "--rows", "6", "--cols", "6", "--yes"],
            ["--set-board", "0", "1", "--board-game", board, "--yes"],
            ["--set-placement", "1", "6", "0", "--yes"],
            ["--place-one", "0", "1", "6", "0", "--yes"],
            ["--game-alloc", "64"],
            ["--script-turn", "a", "1", "6", "--yes"],
            ["--script-move", "1", "6", "1", "5", "--yes"],
            ["--script-pass", "--yes"],
            ["--inject-move", "1", "6", "1", "5"],
            ["--poke", "0x10000", "00", "--yes"],
            ["--unlock-challenges", "--yes"],
            ["--highlight"],
        ];
        string[][] readPaths =
        [
            ["--snapshot"],
            ["--watch-snapshot", "600", "--parent-pid", "4242"],
            ["--match-live"],
            ["--first", "read"],
            ["--board-shape", "--board-game", board],
            ["--challenges"],
            ["--survey"],
            ["--probe"],
        ];
        const string unknownBuild = "667B1778-949F000";
        Check("every write refuses on a game build this live-probe does not know and runs on the one it knows, " +
              "and the reads run on both",
              writePaths.All(a => UnknownBuildRefusal(a, unknownBuild) is not null
                                  && UnknownBuildRefusal(a, KnownBuilds[0]) is null)
              && readPaths.All(a => UnknownBuildRefusal(a, unknownBuild) is null
                                 && (HandleAccess(a) & WriteAccess) == 0)
              && UnknownBuildRefusal(writePaths[0], BuildText(0, 0x949F000)) is not null
              && writePaths.All(a => UnknownBuildRefusal(a, BuildText(0x667B1777, 0x949F000)) is null)
              && KnownBuilds.All(KnownBuild)
              && UnknownBuildLine(unknownBuild).StartsWith("REFUSED: game build not supported: ", StringComparison.Ordinal)
              && UnknownBuildLine(unknownBuild).Contains(unknownBuild));

        var gameBase = 0x7FF740D90000UL;
        Check("the halt's freeze writes only into a match whose challenge is a board game one",
              FreezeTarget(0x20000, gameBase + BoardGameChallengeRva, gameBase) == 0x20000 + PauseFlags
              && FreezeTarget(0x20000, gameBase + 0x1911840, gameBase) == 0
              && FreezeTarget(0x20000, gameBase + 0x190FCB8, gameBase) == 0
              && FreezeTarget(0x20000, gameBase + 0x1900000, gameBase) == 0
              && FreezeTarget(0x20000, 0, gameBase) == 0
              && FreezeTarget(0, gameBase + BoardGameChallengeRva, gameBase) == 0);

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

    private static Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)> MachineNumbers(ulong logic)
    {
        var map = new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>();

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
            var pattern = PatternOf(u);
            var (skill, range) = SkillAndRange(u);
            map[(Nibble(packed), Nibble(packed >> 4))] = (pattern, skill, range);
        }
        return map;
    }

    private static List<string> TurnReachProblems(List<Act> acts,
                                                  Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)> machines)
    {
        var at = new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>(machines);
        var problems = new List<string>();

        for (var i = 0; i < acts.Count; i++)
        {
            var a = acts[i];
            if (!at.TryGetValue((a.SrcX, a.SrcY), out var machine))
            {
                machine = (-1, -1, -1);
            }

            var (walkX, walkY) = StandSquare(a);

            if (a.Attack && i > 0)
            {
                var problem = ReachProblem(machine.Pattern, machine.Skill, machine.Range,
                                           walkX, walkY, a.Facing, a.DstX, a.DstY);
                if (problem is not null)
                {
                    problems.Add($"action {i + 1}: {problem}");
                }
            }

            var land = a.Attack ? ChargeLanding(machine.Pattern, machine.Range, walkX, walkY, a.Facing) : null;
            at.Remove((a.SrcX, a.SrcY));
            at[land ?? (walkX, walkY)] = machine;
        }

        return problems;
    }

    private static bool SimulateTurn(ulong logic, ulong inst, int seat, List<Act> acts)
    {
        var at = Occupancy(logic, inst, seat);
        var numbers = MachineNumbers(logic);
        var numbersAt = new Dictionary<(int X, int Y), (int Pattern, int Skill, int Range)>(numbers);
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

                Console.WriteLine($"\n  Warning: {complaint}" + (exact ? "." : ", as far as this can tell."));
                Console.WriteLine("  The live board check reads the real board when that action comes and refuses");
                Console.WriteLine("  it there, with the actions before it already applied.");
            }

            var (dx, dy) = StandSquare(a);

            if (StandsOffBoard(a, width, height))
            {
                Console.Error.WriteLine($"\n  Warning: action {i + 1} moves to ({dx},{dy}), off a {width}x{height} board.");
                Console.Error.WriteLine("  The game clamps that instead of refusing it, landing a piece on an occupied tile");
                Console.Error.WriteLine("  and taking the match down. Refused for every action, not only the first.");
                ok = false;
            }

            if (!numbersAt.TryGetValue((a.SrcX, a.SrcY), out var machine))
            {
                machine = (-1, -1, -1);
            }

            var land = a.Attack ? ChargeLanding(machine.Pattern, machine.Range, dx, dy, a.Facing) : null;
            if (land is { } l && i > 0)
            {
                var occupied = at.ContainsKey(l) && l != (a.SrcX, a.SrcY);
                var landProblem = LandingProblem(l, width, height, occupied);
                if (landProblem is not null && !occupied)
                {
                    Console.Error.WriteLine($"\n  Warning: action {i + 1}: {landProblem}.");
                    Console.Error.WriteLine("  The game performs the charge itself and needs its landing on the board and empty.");
                    ok = false;
                }
                else if (landProblem is not null)
                {
                    Console.WriteLine($"\n  Warning: action {i + 1}: {landProblem}" +
                                      (exact ? "." : ", as far as this can tell."));
                    Console.WriteLine("  The live board check refuses it when that action comes, if it is still so.");
                }
            }

            var ends = land ?? (dx, dy);
            at.Remove((a.SrcX, a.SrcY));
            at[ends] = "AI";
            numbersAt.Remove((a.SrcX, a.SrcY));
            numbersAt[ends] = machine;
            if (a.Attack)
            {
                exact = false;
            }
        }

        var reach = TurnReachProblems(acts, numbers);
        foreach (var problem in reach)
        {
            Console.Error.WriteLine($"\n  Warning: {problem}.");
        }

        if (reach.Count > 0)
        {
            Console.Error.WriteLine("  The stand square and facing do not reach the victim, so the game would strike");
            Console.Error.WriteLine("  something else. Where a machine stands, where it faces and where its victim is");
            Console.Error.WriteLine("  hold whatever moved before them, so this is refused for every action, not only");
            Console.Error.WriteLine("  the first.");
            ok = false;
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
                        Console.Error.WriteLine("  That is the known cause: an activate that finds no machine of " +
                                                "ours is consumed\n  anyway, activates nothing, and the move or " +
                                                "attack behind it then returns without\n  consuming itself. " +
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
