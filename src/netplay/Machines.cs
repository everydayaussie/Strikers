namespace Strikers.Netplay;

internal static class Machines
{
    internal sealed record Machine(
        string Name, string Uuid, int Cost, int Health, int Move, int Range, int Power,
        string Pattern, string Ability);

    private static readonly Machine[] All =
    [
        new("Burrower", "1C96A39FFE37791F8AE07BD49A2230FF", 1, 4, 2, 1, 2, "Strike", "-"),
        new("Grazer", "2B34B3566FC1ED50071517AE89C5252F", 1, 4, 2, 1, 1, "Ram", "Roam"),
        new("Leaplasher", "76D3C3711B67863E82D8C703AAB1A815", 1, 3, 4, 1, 1, "Strike", "Enpower"),
        new("Scrounger", "B78EF94227B7D36454715138E9A17144", 1, 5, 3, 1, 2, "Strike", "-"),
        new("Spikesnout", "44333B622E88297526F3BAEFE049E1E6", 1, 5, 2, 1, 2, "Strike", "-"),
        new("Bristleback", "C5A08FEFF4757D3A3BD4A614A0B5E948", 2, 4, 3, 1, 2, "Ram", "Spill"),
        new("Charger", "FDC39FDFF4CE9C5B8190CE46AAFCF7B5", 2, 4, 3, 2, 2, "Dash", "Roam"),
        new("Fanghorn", "EC1B65E8878813E8A601E253EAA2913E", 2, 5, 2, 2, 2, "Ram", "Climb"),
        new("Glinthawk", "4726C13DD5722AF80595854DA7E2FCBF", 2, 5, 3, 3, 2, "Dive", "-"),
        new("Lancehorn", "D648F85DD50802FA13A3AF73EC249DF1", 2, 5, 2, 2, 2, "Ram", "Scurry"),
        new("Longleg", "FBD88CC00C71FC3A1C9606BF56DF79B7", 2, 6, 4, 2, 1, "Shot", "Enpower"),
        new("Plowhorn", "A70D3B57757E0A4692DF0D4511231EDE", 2, 5, 2, 1, 2, "Ram", "Seed"),
        new("Scrapper", "52739299954A231229FD4BE0818ADBB0", 2, 5, 2, 2, 3, "Shot", "-"),
        new("Skydrifter", "36791A8338E8498ACD11B127EB75CF82", 2, 6, 2, 3, 2, "Dive", "-"),
        new("Tracker Burrower", "166DBB122F7176A0028C7DB5BE42BDEC", 2, 4, 2, 1, 2, "Strike", "Unearth"),
        new("Bellowback", "823A38C44964F6F8CF8E022B64A29FA6", 3, 7, 2, 2, 3, "Shot", "Spill"),
        new("Clawstrider", "ED68990FF5589A2DA853F0D163682C40", 3, 8, 2, 2, 3, "Strike", "-"),
        new("Redeye Watcher", "0E44B98882BA9AFD876C0DB6144D35F5", 3, 5, 2, 2, 2, "Shot", "Blind"),
        new("Shell-Walker", "05662ED22BB56D93305986970E81E6C4", 3, 7, 2, 1, 2, "Strike", "Shield"),
        new("Snapmaw", "4B962F6770CF0C4B905C3E2782E46B24", 3, 7, 2, 3, 3, "Tow", "-"),
        new("Sunwing", "D7570C9AAEC2D94C677452D1861A99DA", 3, 7, 3, 2, 3, "Dive", "-"),
        new("Widemaw", "7BDE91066BA0DB7EFFB78AC3CFE1B5BF", 3, 7, 2, 2, 3, "Tow", "-"),
        new("Clamberjaw", "8960A49889D0783EF8EDCE2CC08C0BCE", 4, 8, 3, 1, 3, "Strike", "Stalk"),
        new("Elemental Clawstrider", "AE1497611CC146BCBEC792BA79E94C0C", 4, 8, 2, 2, 3, "Shot", "Burn"),
        new("Ravager", "8218A7A4485CED3F9DBFF00447CA94A0", 4, 9, 2, 2, 2, "Shot", "Spread"),
        new("Rollerback", "EC24DE233B69D5702EC68D058C0DF9ED", 4, 5, 3, 2, 3, "Strike", "Retaliate"),
        new("Stalker", "0D28E4466864E92033A848F03ED49A69", 4, 5, 3, 2, 4, "Strike", "Stalk"),
        new("Waterwing", "BA426B8061A7441283CB2D74EFE8F26F", 4, 8, 3, 2, 2, "Tow", "Confuse"),
        new("Apex Clawstrider", "05EDB2988A7B9D03733EC774E2491915", 5, 8, 2, 1, 3, "Strike", "Retaliate"),
        new("Behemoth", "370A5C1EE3F50A95FE7A04BE068355FC", 5, 10, 2, 2, 3, "Shot", "Shield"),
        new("Bilegut", "455F58E56AA6405EA67613ADBE69E737", 5, 9, 2, 3, 3, "Tow", "Unearth"),
        new("Dreadwing", "435534A445562BA16633AF4B908D83B2", 5, 9, 2, 3, 3, "Dive", "Confuse"),
        new("Tremortusk", "23C2AA3CCB2680F418CA1D7E9F179E9D", 5, 10, 2, 2, 3, "Dash", "Spread"),
        new("Rockbreaker", "ADEA18E33DA2CAF15010D29CA12FE1F3", 6, 9, 3, 2, 3, "Shot", "Unearth"),
        new("Shellsnapper", "C730992552952A2BA35C975435C03F37", 6, 10, 2, 3, 3, "Tow", "-"),
        new("Stormbird", "7875B4B22E79B8BF0996D4B74BCC0477", 6, 9, 3, 3, 3, "Dive", "Spread"),
        new("Thunderjaw", "F9433C1448F8F052BD457978CD0BFEC5", 6, 10, 3, 2, 3, "Dash", "Spread"),
        new("Tideripper", "C6E4562880DBB8F7080473258C8D1F6C", 6, 10, 2, 3, 4, "Tow", "-"),
        new("Fireclaw", "D132E56AAEF7AC7F24DBC79886876512", 7, 10, 3, 2, 4, "Strike", "Burn"),
        new("Frostclaw", "373D8F477EAEAF6BE80EFF08610BBA8F", 7, 10, 2, 2, 4, "Strike", "Freeze"),
        new("Scorcher", "E1301D6553C24EDD4CFBE9E21EC99D1C", 8, 12, 2, 2, 4, "Dash", "Burn"),
        new("Slitherfang", "F5172B344148EE7E5DB47BD7A23AF9F9", 9, 12, 2, 3, 4, "Dash", "Unearth"),
        new("Slaughterspine", "82F13F6222FCF687A5684CC1F9B96F4F", 10, 15, 2, 1, 4, "Strike", "Spill"),
    ];

    public static IReadOnlyList<Machine> Known
    {
        get { return All; }
    }

    public const int StockDraftPoints = 10;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060", Justification = "D-104")]
    public static string? CannotPlay(Machine m)
    {
        return null;
    }

    public static Machine? Find(string? uuid)
    {
        var normalised = Lobby.NormaliseUuid(uuid);
        if (normalised is null)
        {
            return null;
        }

        return All.FirstOrDefault(m => m.Uuid == normalised);
    }

    public const int MaxFreeArmy = 9;

    internal static bool FoldsIntoAttack(IReadOnlyList<Move> turn, int i)
    {
        var move = turn[i];
        if (move.Attack || move.Burst || i + 1 >= turn.Count)
        {
            return false;
        }

        var strike = turn[i + 1];
        var inPlace = strike.StrikeFrom == (strike.SrcX, strike.SrcY);
        var sameMachine = strike.SrcX == move.DstX && strike.SrcY == move.DstY;
        var ownNext = i + 2 < turn.Count && turn[i + 2].SrcX == strike.SrcX && turn[i + 2].SrcY == strike.SrcY;
        return strike.Attack && !strike.Burst && inPlace && sameMachine && !ownNext;
    }

    public const int ActivationsPerTurn = 2;

    private static bool LeftByAttack(Move attack, (int X, int Y) square, int reach)
    {
        if (square == (attack.SrcX, attack.SrcY) || square == (attack.DstX, attack.DstY)
            || square == attack.StrikeFrom || square == (attack.TargetX, attack.TargetY)
            || square == (attack.LandX, attack.LandY))
        {
            return true;
        }

        var (dx, dy) = MoveDetector.Step(attack.Facing);
        for (var along = 1; along <= reach; along++)
        {
            if (square == (attack.StrikeFrom.X + dx * along, attack.StrikeFrom.Y + dy * along))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsActivation(IReadOnlyList<Move> turn, int i, int reach)
    {
        var m = turn[i];
        if (m.Burst)
        {
            return false;
        }

        if (i == 0)
        {
            return true;
        }

        var before = turn[i - 1];
        var folded = i >= 2 && FoldsIntoAttack(turn, i - 2);
        var owedMove = !m.Attack && before.Attack && !before.Burst && !folded
                       && LeftByAttack(before, (m.SrcX, m.SrcY), reach);
        return !owedMove && !FoldsIntoAttack(turn, i - 1);
    }

    internal static int Activations(IReadOnlyList<Move> turn, int reach)
    {
        var activations = 0;
        for (var i = 0; i < turn.Count; i++)
        {
            if (StartsActivation(turn, i, reach))
            {
                activations++;
            }
        }

        return activations;
    }

    private static string? StrikeSquareProblem(Move m, Machine machine, int range)
    {
        if (!m.Attack || m.AtkX < 0 || m.AtkY < 0 || (m.AtkX == m.DstX && m.AtkY == m.DstY))
        {
            return null;
        }

        if (machine.Pattern == "Dive")
        {
            var beside = Math.Max(Math.Abs(m.DstX - m.TargetX), Math.Abs(m.DstY - m.TargetY)) <= 1;
            return beside
                ? null
                : $"lands a {machine.Name} on ({m.DstX},{m.DstY}), and a Dive lands next to its victim on " +
                  $"({m.TargetX},{m.TargetY})";
        }

        if (machine.Pattern == "Dash")
        {
            var (dx, dy) = MoveDetector.Step(m.Facing);
            var lands = (X: m.AtkX + dx * range, Y: m.AtkY + dy * range);
            return m.DstX == lands.X && m.DstY == lands.Y
                ? null
                : $"lands a {machine.Name} on ({m.DstX},{m.DstY}), and its charge from ({m.AtkX},{m.AtkY}) " +
                  $"facing {m.Facing} ends on ({lands.X},{lands.Y})";
        }

        return $"strikes from ({m.AtkX},{m.AtkY}) and leaves a {machine.Name} on ({m.DstX},{m.DstY}), and only " +
               "a Dive or a Dash finishes away from the square it strikes from";
    }

    public static string? TurnProblem(IReadOnlyList<Move> turn, BoardSnapshot board, int seat)
    {
        var activations = Activations(turn, Math.Max(board.Width, board.Height));
        if (activations > ActivationsPerTurn)
        {
            return $"the turn has {activations} activations, and a turn has {ActivationsPerTurn}, " +
                   "with an Overcharge as the only extra action";
        }

        var pieces = board.Pieces.Where(p => p.Owner == seat)
                          .Select(p => (p.X, p.Y, p.Uuid, p.Range))
                          .ToList();
        for (var i = 0; i < turn.Count; i++)
        {
            var m = turn[i];
            var at = pieces.FindLastIndex(p => p.X == m.SrcX && p.Y == m.SrcY);
            if (at < 0)
            {
                continue;
            }

            var machine = Find(pieces[at].Uuid);
            if (machine is not null)
            {
                var range = pieces[at].Range >= 1 ? pieces[at].Range : machine.Range;
                if (m.Charged)
                {
                    var (dx, dy) = MoveDetector.Step(m.Facing);
                    var ends = (X: m.DstX + dx * range, Y: m.DstY + dy * range);
                    if (machine.Pattern != "Dash")
                    {
                        return $"action {i + 1} charges a {machine.Name}, whose pattern is {machine.Pattern}";
                    }

                    if (m.LandX != ends.X || m.LandY != ends.Y)
                    {
                        return $"action {i + 1} lands a {machine.Name} on ({m.LandX},{m.LandY}), and its charge " +
                               $"from ({m.DstX},{m.DstY}) facing {m.Facing} ends on ({ends.X},{ends.Y})";
                    }
                }

                if (StrikeSquareProblem(m, machine, range) is { } strikeSquare)
                {
                    return $"action {i + 1} {strikeSquare}";
                }

                (int X, int Y) stand = m.Attack ? m.StrikeFrom : (m.DstX, m.DstY);
                var steps = Math.Abs(stand.X - m.SrcX) + Math.Abs(stand.Y - m.SrcY);
                var strikes = m.Attack || FoldsIntoAttack(turn, i);
                var extra = !strikes ? 1
                            : machine.Pattern == "Dash" ? range
                            : machine.Pattern == "Ram" ? 1
                            : 0;
                if (steps > machine.Move + extra)
                {
                    var verb = m.Attack ? "strikes"
                               : strikes ? "then strikes where it stands, which is injected as one attack"
                               : "does not strike";
                    return $"action {i + 1} walks a {machine.Name} {steps} squares, ({m.SrcX},{m.SrcY}) to " +
                           $"({stand.X},{stand.Y}), and {verb}; its move is {machine.Move}" +
                           (extra > 0 ? $" and {extra} more is the most that pattern allows" : "");
                }
            }

            var stands = m.Charged ? (X: m.LandX, Y: m.LandY) : (X: m.DstX, Y: m.DstY);
            var acted = pieces[at];
            pieces.RemoveAt(at);
            pieces.Add((stands.X, stands.Y, acted.Uuid, acted.Range));
        }

        return null;
    }

    public static string? ArmyProblem(IReadOnlyList<string> army, int slots, int budget,
                                      bool allowUnknown = false)
    {
        if (army.Count == 0)
        {
            return "an army needs at least one machine";
        }

        var most = slots > 0 ? slots : MaxFreeArmy;
        if (army.Count > most)
        {
            return slots > 0
                ? $"that challenge fields {slots} machine(s) and this army has {army.Count}"
                : $"an army of {army.Count} is more than a side may field ({most})";
        }

        if (slots > 0 && army.Count != slots)
        {
            return $"that challenge fields {slots} machine(s) and this army has {army.Count}";
        }

        var cost = 0;
        foreach (var uuid in army)
        {
            if (Find(uuid) is not { } machine)
            {
                if (allowUnknown)
                {
                    continue;
                }

                return $"'{uuid}' is not a machine this save has";
            }

            if (CannotPlay(machine) is { } why)
            {
                return $"{machine.Name} cannot be used: {why}";
            }

            cost += machine.Cost;
        }

        if (budget > 0 && cost > budget)
        {
            return $"that army costs {cost} against a budget of {budget}";
        }

        return null;
    }

    public static int Cost(IEnumerable<string> army)
    {
        return army.Sum(uuid => Find(uuid)?.Cost ?? 0);
    }
}
