namespace Strikers.Netplay;

internal sealed class Lobby(string localBuild, bool isHost, string localNetplay = "", string localProbe = "")
{
    public string LocalBuild { get; } = localBuild;

    public string? PeerBuild { get; private set; }
    public bool IsHost { get; } = isHost;
    public int Seat { get; set; } = -1;

    public string LocalNetplay { get; } = localNetplay;
    public string LocalProbe { get; } = localProbe;
    public string? PeerNetplay { get; private set; }
    public string? PeerProbe { get; private set; }

    public string? Challenge { get; private set; }

    public static string? NormaliseUuid(string? s)
    {
        if (s is null)
        {
            return null;
        }

        var hex = new string(s.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return hex.Length == 32 ? hex : null;
    }

    public SeatSetup Local { get; } = new();
    public SeatSetup? Remote { get; private set; }

    public const int BoardSide = 8;

    public const int MostSquares = BoardSide * 2;

    public static string? PlacementProblem(Placement p, int width, int depth)
    {
        if (p.X < 0 || p.X >= width || p.Y < 0 || p.Y >= depth)
        {
            return $"the other player sent a starting square ({p.X},{p.Y}) that is off the board or " +
                   $"outside their placing rows ({width} wide, {depth} deep)";
        }
        return null;
    }

    public static List<Placement> DefaultSquares(int count, int width, int height, int placementRows)
    {
        var depth = placementRows != Preset.RuleNotSet ? placementRows : 2;
        if (count <= 0 || width < 1 || height < 1 || depth < 1 || count > width * depth)
        {
            return [];
        }

        var squares = new List<Placement>();
        var start = count >= width ? 0 : (width - count) / 2;

        for (var i = 0; i < count; i++)
        {
            var slot = start + i;
            squares.Add(new Placement
            {
                X = slot % width,
                Y = height - depth + (slot / width),
                Dir = 0,
            });
        }

        return squares;
    }

    public List<Placement> AutoSquares(int count)
    {
        return DefaultSquares(count, BoardWidth, BoardHeight, AutoDepth);
    }

    public const int StockDepth = 2;

    public int AutoDepth
    {
        get
        {
            var depth = PlacementRows != Preset.RuleNotSet ? PlacementRows : StockDepth;
            return Math.Min(depth, BoardHeight);
        }
    }

    public static string? DepthOffBoard(int placementRows, int height)
    {
        var depth = placementRows != Preset.RuleNotSet ? placementRows : StockDepth;
        if (depth > height)
        {
            return $"a placing depth of {depth}{(placementRows == Preset.RuleNotSet ? " (the challenge's own)" : "")} " +
                   $"on a board {height} row(s) deep; the zone would run off the end of the board";
        }

        return null;
    }

    public bool LocalReady { get; private set; }
    public bool RemoteReady { get; private set; }

    public string? Refusal { get; private set; }

    public bool BothReady
    {
        get
        {
            return LocalReady && RemoteReady && Refusal is null;
        }
    }

    public sealed class SeatSetup
    {
        public List<string> Army { get; set; } = [];
        public List<Placement> Placements { get; set; } = [];

        public bool Complete
        {
            get
            {
                return Army.Count > 0 && Placements.Count == Army.Count;
            }
        }
    }

    public Frame Ident()
    {
        return new()
        {
            Kind = MsgKind.Ident,
            Build = LocalBuild,
            NetplayVersion = LocalNetplay.Length > 0 ? LocalNetplay : null,
            ProbeVersion = LocalProbe.Length > 0 ? LocalProbe : null,
        };
    }

    private bool _answered;

    public Frame? TakeAnswer()
    {
        if (_answered || PeerBuild is null)
        {
            return null;
        }

        _answered = true;
        return Ident();
    }

    public string? OnIdent(Frame f)
    {
        var build = FrameLimits.Safe(f.Build, 64);
        PeerBuild = build;
        PeerNetplay = FrameLimits.Safe(f.NetplayVersion, 64);
        PeerProbe = FrameLimits.Safe(f.ProbeVersion, 64);
        if (Refusal is not null)
        {
            return Refusal;
        }

        if (!Missing(LocalBuild) && !Missing(build) && build != LocalBuild)
        {
            Refusal = $"game build mismatch: this PC is {LocalBuild}, the other is {build}. " +
                      "Both players must run the same version of Horizon Forbidden West.";
            return Refusal;
        }

        Refusal = Mismatch("netplay", LocalNetplay, PeerNetplay)
               ?? Mismatch("live-probe", LocalProbe, PeerProbe);
        if (Refusal is not null)
        {
            return Refusal;
        }

        return Refusal;
    }

    private static bool Missing(string? value)
    {
        return string.IsNullOrEmpty(value) || value == "unknown";
    }

    private static string? Mismatch(string tool, string ours, string? theirs)
    {
        if (Missing(ours) || Missing(theirs) || theirs == ours)
        {
            return null;
        }

        return $"Strikers version mismatch: this PC runs {tool} {ours}, the other {tool} {theirs}. " +
               "Update both PCs to the same Strikers version, then try again.";
    }

    public void Choose(string challenge)
    {
        if (!IsHost)
        {
            throw new InvalidOperationException("only the host picks the challenge");
        }

        if (Challenges.Problem(challenge) is { } bad)
        {
            throw new InvalidOperationException(
                $"{bad} A name will not do either: nothing downstream can resolve one.");
        }

        Challenge = NormaliseUuid(challenge);
    }

    private void Told(string? challenge)
    {
        if (IsHost)
        {
            return;
        }

        if (string.IsNullOrEmpty(challenge))
        {
            Challenge = null;
            return;
        }

        if (Challenges.Problem(challenge) is { } bad)
        {
            Refusal ??= $"the host sent a challenge this cannot play: {bad}";
        }
        else
        {
            Challenge = NormaliseUuid(challenge);
        }
    }

    public string? SetArmy(IEnumerable<string> machines)
    {
        var ids = new List<string>();
        foreach (var m in machines)
        {
            var uuid = NormaliseUuid(m);
            if (uuid is null)
            {
                return $"'{m}' is not a machine UUID. live-probe --survey lists them; an address will " +
                       "not do, because the other PC cannot resolve one.";
            }

            ids.Add(uuid);
        }
        Local.Army = ids;
        return null;
    }

    public string? ApplyPreset(Preset preset)
    {
        if (preset.Problem() is { } bad)
        {
            return bad;
        }

        var uuid = NormaliseUuid(preset.Challenge)!;

        if (!IsHost && Challenge is not null && Challenge != uuid)
        {
            return $"preset '{preset.Name}' is for {preset.ChallengeName} ({uuid}), but the host chose " +
                   $"{Challenge}. Both PCs must use a preset for the same challenge.";
        }

        var army = new List<string>();
        foreach (var m in preset.ArmyFor(IsHost))
        {
            army.Add(NormaliseUuid(m.Uuid)!);
        }

        if (IsHost)
        {
            Challenge = uuid;

            ChooseBoard(preset.Board, preset.VictoryPoints ?? -1, preset.DraftPoints ?? -1,
                        preset.WidthOrStock, preset.HeightOrStock,
                        preset.PlacementRows ?? Preset.RuleNotSet);
        }

        Local.Army = army;
        Local.Placements = [.. preset.Placements.Select(p => new Placement { X = p.X, Y = p.Y, Dir = p.Dir })];
        return null;
    }

    public List<int>? Board { get; private set; }

    public int BoardWidth { get; private set; } = Preset.BoardSide;
    public int BoardHeight { get; private set; } = Preset.BoardSide;
    public int PlacementRows { get; private set; } = Preset.RuleNotSet;

    public int VictoryPoints { get; private set; } = -1;
    public int DraftPoints { get; private set; } = -1;

    public const string FirstHost = "host";
    public const string FirstJoiner = "joiner";
    public string First { get; private set; } = FirstHost;

    public static bool IsFirstChoice(string? value)
    {
        return value is FirstHost or FirstJoiner;
    }

    public void ChooseFirst(string first)
    {
        if (!IsHost)
        {
            throw new InvalidOperationException("only the host picks who goes first");
        }

        if (!IsFirstChoice(first))
        {
            throw new InvalidOperationException($"who goes first is {FirstHost} or {FirstJoiner}");
        }

        First = first;
    }

    private void ToldFirst(Frame f)
    {
        if (IsHost)
        {
            return;
        }

        if (f.First is null)
        {
            First = FirstHost;
            return;
        }

        if (!IsFirstChoice(f.First))
        {
            Refusal ??= "the host sent a first player this cannot use";
            return;
        }

        First = f.First;
    }

    public string? LocalName { get; private set; }
    public string? PeerName { get; private set; }

    public string? SetName(string? name)
    {
        if (Names.Problem(name) is { } bad)
        {
            return bad;
        }

        LocalName = Names.Clean(name);
        return null;
    }

    public void ChooseBoard(IReadOnlyList<int>? board, int victoryPoints, int draftPoints,
                            int width, int height, int placementRows)
    {
        if (!IsHost)
        {
            throw new InvalidOperationException("only the host picks the board");
        }

        Board = board is null || board.Count == 0 ? null : [.. board];
        BoardWidth = width;
        BoardHeight = height;
        PlacementRows = placementRows;
        VictoryPoints = victoryPoints;
        DraftPoints = draftPoints;
    }

    public Frame Setup()
    {
        return new()
        {
            Kind = MsgKind.Setup,
            Challenge = IsHost ? Challenge : null,
            Army = [.. Local.Army],
            Placements = [.. Local.Placements],
            Board = IsHost && Board is not null ? [.. Board] : null,

            BoardWidth = IsHost && Board is not null ? BoardWidth : Preset.RuleNotSet,
            BoardHeight = IsHost && Board is not null ? BoardHeight : Preset.RuleNotSet,
            PlacementRows = IsHost ? PlacementRows : Preset.RuleNotSet,

            VictoryPoints = IsHost ? VictoryPoints : -1,
            DraftPoints = IsHost ? DraftPoints : -1,
            First = IsHost ? First : null,

            Name = LocalName,
        };
    }

    private void ToldBoard(Frame f)
    {
        if (IsHost)
        {
            return;
        }

        if (f.Board is { Count: > 0 } terrain)
        {
            var width = f.BoardWidth != Preset.RuleNotSet ? f.BoardWidth : Preset.BoardSide;
            var height = f.BoardHeight != Preset.RuleNotSet ? f.BoardHeight : Preset.BoardSide;

            if (Preset.BoardProblem(terrain, "the board the other player sent", width, height,
                                    f.PlacementRows) is { } bad)
            {
                Refusal ??= bad;
                return;
            }

            Board = [.. terrain];
            BoardWidth = width;
            BoardHeight = height;
            PlacementRows = f.PlacementRows;
        }
        else
        {
            Board = null;
            BoardWidth = Preset.BoardSide;
            BoardHeight = Preset.BoardSide;

            if (f.PlacementRows != Preset.RuleNotSet
                && Preset.DepthProblem(f.PlacementRows, BoardHeight,
                                       "the placing depth the other player sent") is { } depthBad)
            {
                Refusal ??= depthBad;
                return;
            }

            PlacementRows = f.PlacementRows;
        }

        if (DepthOffBoard(PlacementRows, BoardHeight) is { } offBoard)
        {
            Refusal ??= $"the other player's setup asks for {offBoard}";
            return;
        }

        if (Preset.RuleProblem(f.VictoryPoints, f.DraftPoints, "the other player") is { } ruleBad)
        {
            Refusal ??= ruleBad;
            return;
        }

        VictoryPoints = f.VictoryPoints;
        DraftPoints = f.DraftPoints;
    }

    private string? _acceptedSetup;

    private static string SetupContent(Frame f)
    {
        var content = new SetupContent
        {
            Challenge = f.Challenge,
            Army = f.Army,
            Placements = f.Placements?.Select(p => new[] { p.X, p.Y, p.Dir }).ToList(),
            Board = f.Board,
            BoardWidth = f.BoardWidth,
            BoardHeight = f.BoardHeight,
            PlacementRows = f.PlacementRows,
            VictoryPoints = f.VictoryPoints,
            DraftPoints = f.DraftPoints,
            First = f.First,
            Name = f.Name,
        };

        return System.Text.Json.JsonSerializer.Serialize(content, WireJson.Default.SetupContent);
    }

    public static string? LateSetupProblem(bool bothReady, string? accepted, string arriving)
    {
        if (bothReady && accepted is not null && accepted != arriving)
        {
            return "the other player changed their setup after both players were ready";
        }

        return null;
    }

    public void OnSetup(Frame f)
    {
        var content = SetupContent(f);
        if (LateSetupProblem(LocalReady && RemoteReady, _acceptedSetup, content) is { } late)
        {
            Refusal ??= late;
            return;
        }

        Told(f.Challenge);
        ToldBoard(f);
        ToldFirst(f);

        PeerName = Names.FromPeer(f.Name);

        var army = new List<string>();
        foreach (var m in f.Army ?? [])
        {
            var uuid = NormaliseUuid(m);
            if (uuid is null)
            {
                Refusal ??= $"the other player sent a machine id that is not a UUID: "
                            + $"'{FrameLimits.Safe(m, 64)}'";
            }
            else
            {
                army.Add(uuid);
            }
        }

        if (Challenges.Find(Challenge) is { } entry && army.Count > 0)
        {
            var budget = DraftPoints != Preset.RuleNotSet ? DraftPoints : Machines.StockDraftPoints;

            if (Machines.ArmyProblem(army, entry.Slots, budget, allowUnknown: true) is { } armyBad)
            {
                Refusal ??= $"the other player sent an army this cannot field: {armyBad}";
            }
        }

        var depth = AutoDepth;
        var zone = BoardWidth * depth;
        var mostMachines = Math.Max(army.Count, Local.Army.Count);
        if (mostMachines > zone)
        {
            Refusal ??= $"an army of {mostMachines} cannot be placed on this board: each side has " +
                        $"{zone} square(s) to place on ({BoardWidth} wide by {depth} deep)";
        }

        var placements = f.Placements is null
            ? []
            : f.Placements.Select(p => p.Rotated(BoardWidth, BoardHeight)).ToList();

        foreach (var p in placements)
        {
            if (PlacementProblem(p, BoardWidth, depth) is { } squareBad)
            {
                Refusal ??= squareBad;
                break;
            }
        }

        if (placements.Select(p => (p.X, p.Y)).Distinct().Count() != placements.Count)
        {
            Refusal ??= "the other player sent two machines for one starting square";
        }

        if (placements.Count > 0 && placements.Count != army.Count)
        {
            Refusal ??= $"the other player sent {placements.Count} placements for an army of " +
                        $"{army.Count} machines";
        }

        Remote = new SeatSetup
        {
            Army = army,
            Placements = placements,
        };
        _acceptedSetup = content;
    }

    public Frame Ready()
    {
        LocalReady = true;
        return new Frame { Kind = MsgKind.Ready };
    }

    public void ForgetPeer()
    {
        PeerBuild = null;
        PeerNetplay = null;
        PeerProbe = null;
        _answered = false;
        Remote = null;
        RemoteReady = false;
        _acceptedSetup = null;
        lock (_confirmGate)
        {
            _peerConfirmedOn = 0;
            _peerSetup = null;
        }
    }

    public sealed record Written(List<int>? Board, int? VictoryPoints, int? DraftPoints, int? PlacementRows);

    public Written ToWrite(Preset? preset)
    {
        var fallback = IsHost ? preset : null;
        var board = Board ?? (fallback is { Board.Count: > 0 } chosen ? chosen.Board : null);
        var victory = VictoryPoints != Preset.RuleNotSet ? VictoryPoints : fallback?.VictoryPoints;
        var draft = DraftPoints != Preset.RuleNotSet ? DraftPoints : fallback?.DraftPoints;
        var depth = PlacementRows != Preset.RuleNotSet ? PlacementRows : (int?)null;
        return new Written(board, victory, draft, depth);
    }

    public const string SetupsDiffer =
        "the two PCs hold different setups (the armies, the board or the rules), so the match cannot start";

    private const string SetupDigestLabel = "Strikers-setup-v1\0";

    public string SetupDigest(Preset? preset = null)
    {
        var written = ToWrite(preset);
        var remote = Remote;
        var width = BoardWidth;
        var height = BoardHeight;
        var ours = Local.Placements.Select(p => new[] { p.X, p.Y, p.Dir & 3 }).ToList();
        var theirs = (remote?.Placements ?? [])
            .Select(p => p.Rotated(width, height))
            .Select(p => new[] { p.X, p.Y, p.Dir & 3 })
            .ToList();
        var theirArmy = remote?.Army ?? [];

        var content = new WrittenSetup
        {
            Challenge = Challenge,
            Board = written.Board,
            BoardWidth = width,
            BoardHeight = height,
            PlacementRows = written.PlacementRows,
            VictoryPoints = written.VictoryPoints,
            DraftPoints = written.DraftPoints,
            First = First,
            HostArmy = IsHost ? Local.Army : theirArmy,
            HostPlacements = IsHost ? ours : theirs,
            JoinerArmy = IsHost ? theirArmy : Local.Army,
            JoinerPlacements = IsHost ? theirs : ours,
            HostName = IsHost ? LocalName : PeerName,
            JoinerName = IsHost ? PeerName : LocalName,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(content, WireJson.Default.WrittenSetup);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SetupDigestLabel + json));
        return Convert.ToHexStringLower(hash);
    }

    public Lobby Frozen()
    {
        var copy = new Lobby(LocalBuild, IsHost, LocalNetplay, LocalProbe)
        {
            Seat = Seat,
        };

        var remote = Remote;
        copy.Challenge = Challenge;
        copy.Board = Board is { } board ? [.. board] : null;
        copy.BoardWidth = BoardWidth;
        copy.BoardHeight = BoardHeight;
        copy.PlacementRows = PlacementRows;
        copy.VictoryPoints = VictoryPoints;
        copy.DraftPoints = DraftPoints;
        copy.First = First;
        copy.LocalName = LocalName;
        copy.PeerName = PeerName;
        copy.Local.Army = [.. Local.Army];
        copy.Local.Placements = [.. Local.Placements];
        copy.Remote = remote is null
            ? null
            : new SeatSetup { Army = [.. remote.Army], Placements = [.. remote.Placements] };
        return copy;
    }

    public void OnReady(Frame _)
    {
        RemoteReady = true;
    }

    private readonly object _confirmGate = new();
    private int _localConfirmedOn;
    private string? _localCode;
    private int _peerConfirmedOn;
    private string? _peerSetup;
    private int _boundOn;

    public static string? ConfirmProblem(string? typed, string? fingerprint)
    {
        var said = (typed ?? "").Trim();
        if (said.Length == 0)
        {
            return "type `confirm <the code on your screen>`, so the confirmation names what was compared";
        }

        if (fingerprint is null)
        {
            return "there is no safety code on this link yet";
        }

        return string.Equals(said, fingerprint, StringComparison.OrdinalIgnoreCase)
            ? null
            : CodeMoved;
    }

    public const string CodeMoved = "the safety code changed since it was compared";

    public const string WriteUnconfirmed = "nothing was written: both players must confirm the same safety code first";

    public void ConfirmedLocally(int channel, string code)
    {
        lock (_confirmGate)
        {
            _localConfirmedOn = channel;
            _localCode = code;
        }
    }

    public static bool SetupAgrees(string? confirmed, string current)
    {
        return confirmed is not null && string.Equals(confirmed, current, StringComparison.Ordinal);
    }

    public bool ConfirmedByPeer(int channel, string? theirSetup, string ourSetup)
    {
        if (!SetupAgrees(theirSetup, ourSetup))
        {
            return false;
        }

        lock (_confirmGate)
        {
            _peerConfirmedOn = channel;
            _peerSetup = theirSetup;
        }

        return true;
    }

    public string? ConfirmedSetup
    {
        get
        {
            lock (_confirmGate)
            {
                return _peerSetup;
            }
        }
    }

    public int? BindingChannel(string? code, int current)
    {
        lock (_confirmGate)
        {
            var named = string.Equals(code, _localCode, StringComparison.Ordinal);
            return BindingDue(_localConfirmedOn, _peerConfirmedOn, current) && named ? current : null;
        }
    }

    public void BoundOn(int channel)
    {
        lock (_confirmGate)
        {
            _boundOn = channel;
        }
    }

    public string? BoundSetup
    {
        get
        {
            lock (_confirmGate)
            {
                return BindingDue(_localConfirmedOn, _peerConfirmedOn, _boundOn) ? _peerSetup : null;
            }
        }
    }

    public static bool BindingDue(int localOn, int peerOn, int current)
    {
        return localOn > 0 && localOn == peerOn && peerOn == current;
    }

    public string Status()
    {
        var lines = new List<string>();

        lines.Add(Seat < 0 ? "  connection : waiting for the other player"
                           : $"  connection : paired, seat {Seat} ({(IsHost ? "host" : "guest")})");

        lines.Add($"  build      : {LocalBuild}{(PeerBuild is null ? ", peer not identified yet" : PeerBuild == LocalBuild ? ", peer matches" : $", peer {PeerBuild}")}");
        if (LocalNetplay.Length > 0)
        {
            lines.Add($"  netplay    : {LocalNetplay}{(string.IsNullOrEmpty(PeerNetplay) ? ", peer not identified yet" : PeerNetplay == LocalNetplay ? ", peer matches" : $", peer {PeerNetplay}")}");
        }

        if (LocalProbe.Length > 0)
        {
            lines.Add($"  live-probe : {LocalProbe}{(string.IsNullOrEmpty(PeerProbe) ? ", peer not identified yet" : PeerProbe == LocalProbe ? ", peer matches" : $", peer {PeerProbe}")}");
        }

        lines.Add($"  challenge  : {Challenge ?? (IsHost ? "not chosen, run: challenges, then challenge <uuid>" : "waiting for the host to choose")}");

        lines.Add($"  your army  : {Describe(Local)}");
        lines.Add($"  their army : {(Remote is null ? "not received yet" : Describe(Remote))}");
        lines.Add($"  ready      : you {(LocalReady ? "yes" : "no")}, them {(RemoteReady ? "yes" : "no")}");

        if (Refusal is not null)
        {
            lines.Add($"  REFUSED    : {Refusal}");
        }
        else
        {
            lines.Add($"  next       : {Next()}");
        }

        return string.Join('\n', lines);
    }

    private static string Describe(SeatSetup s)
    {
        return s.Army.Count == 0 ? "not chosen"
        : $"{s.Army.Count} machine(s), {s.Placements.Count} placement(s)" + (s.Complete ? "" : "  Warning: incomplete");
    }

    private string Next()
    {
        if (Seat < 0)
        {
            return "give the other player the room code";
        }

        if (PeerBuild is null)
        {
            return "waiting for the other player to identify their game build";
        }

        if (Challenge is null)
        {
            return IsHost ? "pick the challenge: challenges, then challenge <uuid>" : "waiting for the host to pick the challenge";
        }

        if (!Local.Complete)
        {
            return "choose your machines and starting squares: army <uuid>... then place <x> <y> <dir>...";
        }

        if (!LocalReady)
        {
            return "send your setup: ready";
        }

        if (Remote is null || !RemoteReady)
        {
            return "waiting for the other player to be ready";
        }

        if (BoundSetup is null)
        {
            return "both ready, compare the safety code (the fingerprint line) with the other player, " +
                   "run: confirm <code>, then stand in the Machine Strike menu and run: write";
        }

        return "both confirmed, stand in the Machine Strike menu and run: write";
    }
}
