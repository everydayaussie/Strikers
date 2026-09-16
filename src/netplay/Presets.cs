using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strikers.Netplay;

internal sealed class Preset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("challengeName")] public string ChallengeName { get; set; } = "";

    [JsonPropertyName("challenge")] public string Challenge { get; set; } = "";

    [JsonPropertyName("army")] public List<PresetMachine> Army { get; set; } = [];

    [JsonPropertyName("armyGuest")] public List<PresetMachine> ArmyGuest { get; set; } = [];

    public List<PresetMachine> ArmyFor(bool isHost)
    {
        if (isHost || ArmyGuest.Count == 0)
        {
            return Army;
        }

        return ArmyGuest;
    }

    [JsonPropertyName("placements")] public List<Placement> Placements { get; set; } = [];

    [JsonPropertyName("note")] public string Note { get; set; } = "";

    public int WidthOrStock
    {
        get
        {
            return BoardWidth ?? BoardSide;
        }
    }

    public int HeightOrStock
    {
        get
        {
            return BoardHeight ?? BoardSide;
        }
    }

    [JsonPropertyName("board")] public List<int> Board { get; set; } = [];

    [JsonPropertyName("boardWidth")] public int? BoardWidth { get; set; }
    [JsonPropertyName("boardHeight")] public int? BoardHeight { get; set; }

    [JsonPropertyName("placementRows")] public int? PlacementRows { get; set; }

    [JsonPropertyName("victoryPoints")] public int? VictoryPoints { get; set; }
    [JsonPropertyName("draftPoints")] public int? DraftPoints { get; set; }

    public string Describe()
    {
        var machines = string.Join(", ", Army.Select(m => m.Name));
        var squares = string.Join(" ", Placements.Select(p => $"({p.X},{p.Y}) dir {p.Dir}"));
        return $"{Name}: {ChallengeName}, {machines}, at {squares}";
    }

    public static string? BoardProblem(IReadOnlyList<int> board, string what, int width, int height,
                                       int placementRows)
    {
        if (width is < 1 or > BoardMaxSide || height is < 1 or > BoardMaxSide)
        {
            return $"{what} is {width}x{height}; a board runs 1 to {BoardMaxSide} each way, because " +
                   "the game allocates no more than that and a bigger number reads memory it does not own";
        }

        if (board.Count != width * height)
        {
            return $"{what} has {board.Count} terrain value(s); a {width}x{height} board is " +
                   $"{width * height}";
        }

        foreach (var t in board)
        {
            if (t is < -2 or > 3)
            {
                return $"{what} has terrain value {t}; the range is -2 Chasm to 3 Mountains";
            }
        }

        return DepthProblem(placementRows, height, what);
    }

    public static string? DepthProblem(int placementRows, int height, string what)
    {
        if (placementRows == RuleNotSet)
        {
            return null;
        }

        if (placementRows < 1 || placementRows > BoardMaxSide)
        {
            return $"{what} asks for a placing depth of {placementRows}; the range is 1 to {BoardMaxSide}";
        }

        if (placementRows > height)
        {
            return $"{what} asks for a placing depth of {placementRows} on a board {height} rows " +
                   "deep; the zone would run off the end of the board";
        }

        return null;
    }

    public static List<int> Rotate180(IReadOnlyList<int> board, int width, int height)
    {
        var turned = new List<int>(board.Count);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                turned.Add(board[((height - 1 - y) * width) + (width - 1 - x)]);
            }
        }

        return turned;
    }

    public const int RuleNotSet = -1;

    public const int BoardSide = 8;

    public const int BoardMaxSide = 8;

    public static string? RuleProblem(int victoryPoints, int draftPoints, string who)
    {
        if (victoryPoints != RuleNotSet && victoryPoints is < 1 or > 200)
        {
            return $"{who} set a victory-point target of {victoryPoints}; the range is 1 to 200";
        }

        if (draftPoints != RuleNotSet && draftPoints is < 1 or > 200)
        {
            return $"{who} set a draft-point budget of {draftPoints}; the range is 1 to 200";
        }

        return null;
    }

    public string? Problem()
    {
        if (Lobby.NormaliseUuid(Challenge) is null)
        {
            return $"preset '{Name}' has no valid challenge UUID";
        }

        if (Army.Count == 0)
        {
            return $"preset '{Name}' has no machines";
        }

        if (Army.Count != Placements.Count)
        {
            return $"preset '{Name}' has {Army.Count} machine(s) and {Placements.Count} placement(s); " +
                   "the game places one per record, in order";
        }

        if (ArmyGuest.Count > 0 && ArmyGuest.Count != Army.Count)
        {
            return $"preset '{Name}' gives the host {Army.Count} machine(s) and the guest " +
                   $"{ArmyGuest.Count}; a preset's two seats must field the same number";
        }

        foreach (var m in Army.Concat(ArmyGuest))
        {
            if (Lobby.NormaliseUuid(m.Uuid) is null)
            {
                return $"preset '{Name}' machine '{m.Name}' has no valid UUID";
            }
        }

        if (Board.Count > 0 &&
            BoardProblem(Board, $"preset '{Name}'", WidthOrStock, HeightOrStock,
                         PlacementRows ?? RuleNotSet) is { } boardBad)
        {
            return boardBad;
        }

        if (RuleProblem(VictoryPoints ?? RuleNotSet, DraftPoints ?? RuleNotSet, $"preset '{Name}'") is { } ruleBad)
        {
            return ruleBad;
        }

        return null;
    }
}

internal sealed class PresetMachine
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
}

internal static class Presets
{
    public static Preset Builtin()
    {
        return new Preset
        {
            Name = "gate",
            ChallengeName = "Beginner's Practice: Easy",
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army =
            [
                new PresetMachine { Name = "Burrower", Uuid = "1C96A39FFE37791F8AE07BD49A2230FF" },
                new PresetMachine { Name = "Scrounger", Uuid = "B78EF94227B7D36454715138E9A17144" },
            ],
            Placements =
            [
                new Placement { X = 3, Y = 6, Dir = 0 },
                new Placement { X = 4, Y = 6, Dir = 0 },
            ],
            Note = "both machines at range 1",
        };
    }

    public static Preset Ranged()
    {
        return new Preset
        {
            Name = "ranged",
            ChallengeName = "Beginner's Practice: Easy",
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army =
            [
                new PresetMachine { Name = "Scrapper", Uuid = "52739299954A231229FD4BE0818ADBB0" },
                new PresetMachine { Name = "Scrounger", Uuid = "B78EF94227B7D36454715138E9A17144" },
            ],
            Placements =
            [
                new Placement { X = 3, Y = 6, Dir = 0 },
                new Placement { X = 4, Y = 6, Dir = 0 },
            ],
            Note = "Scrapper with Shot (Gunner) at range 2",
        };
    }

    public static Preset Whiplash()
    {
        return new Preset
        {
            Name = "whiplash",
            ChallengeName = "Beginner's Practice: Easy",
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army =
            [
                new PresetMachine { Name = "Waterwing", Uuid = "BA426B8061A7441283CB2D74EFE8F26F" },
                new PresetMachine { Name = "Scrounger", Uuid = "B78EF94227B7D36454715138E9A17144" },
            ],
            Placements =
            [
                new Placement { X = 3, Y = 6, Dir = 0 },
                new Placement { X = 4, Y = 6, Dir = 0 },
            ],
            Note = "Waterwing carries Confuse (Whiplash), range 2: spins the Scrounger beside it every turn",
        };
    }

    public static Preset Spray()
    {
        return new Preset
        {
            Name = "spray",
            ChallengeName = "Beginner's Practice: Easy",
            Challenge = "8BBC182B83FC495AA2150021217530D5",
            Army =
            [
                new PresetMachine { Name = "Bellowback", Uuid = "823A38C44964F6F8CF8E022B64A29FA6" },
                new PresetMachine { Name = "Scrounger", Uuid = "B78EF94227B7D36454715138E9A17144" },
            ],
            Placements =
            [
                new Placement { X = 3, Y = 6, Dir = 0 },
                new Placement { X = 4, Y = 6, Dir = 0 },
            ],
            Note = "Bellowback with Spill (Spray) at range 2",
        };
    }

    public static Preset Preview()
    {
        return new Preset
        {
            Name = "preview",
            ChallengeName = "Beginner's Practice: Medium",
            Challenge = "1E46038FF9804646813C959A31EAFB91",
            Army =
            [
                new PresetMachine { Name = "Slaughterspine", Uuid = "82F13F6222FCF687A5684CC1F9B96F4F" },
                new PresetMachine { Name = "Frostclaw", Uuid = "373D8F477EAEAF6BE80EFF08610BBA8F" },
                new PresetMachine { Name = "Sunwing", Uuid = "D7570C9AAEC2D94C677452D1861A99DA" },
                new PresetMachine { Name = "Skydrifter", Uuid = "36791A8338E8498ACD11B127EB75CF82" },
            ],

            ArmyGuest =
            [
                new PresetMachine { Name = "Fireclaw", Uuid = "D132E56AAEF7AC7F24DBC79886876512" },
                new PresetMachine { Name = "Tideripper", Uuid = "C6E4562880DBB8F7080473258C8D1F6C" },
                new PresetMachine { Name = "Dreadwing", Uuid = "435534A445562BA16633AF4B908D83B2" },
                new PresetMachine { Name = "Glinthawk", Uuid = "4726C13DD5722AF80595854DA7E2FCBF" },
            ],

            Placements =
            [
                new Placement { X = 2, Y = 6, Dir = 0 },
                new Placement { X = 3, Y = 6, Dir = 0 },
                new Placement { X = 4, Y = 6, Dir = 0 },
                new Placement { X = 5, Y = 6, Dir = 0 },
            ],

            Board =
            [
                 3,  0,  2,  0,  0,  2,  0,  3,
                 0,  3,  1,  2,  2,  1,  3,  0,
                -2, -2, -2, -2,  3, -2, -2, -2,
                 3, -2, -2,  3,  3, -2, -2, -2,
                -2, -2, -2,  3,  3, -2, -2,  3,
                -2, -2, -2,  3, -2, -2, -2, -2,
                 0,  3,  1,  2,  2,  1,  3,  0,
                 3,  0,  2,  0,  0,  2,  0,  3,
            ],

            VictoryPoints = 20,
            DraftPoints = 40,

            Note = "4v4 apex machines on the custom board 'The Abyss'; run it with " +
                   "--interactive-placement on BOTH PCs",
        };
    }

    private static List<Preset> Normalise(List<Preset>? read)
    {
        var presets = new List<Preset>();
        foreach (var p in read ?? [])
        {
            if (p is null)
            {
                continue;
            }

            if (p.Name is null)
            {
                p.Name = "";
            }

            if (p.ChallengeName is null)
            {
                p.ChallengeName = "";
            }

            if (p.Challenge is null)
            {
                p.Challenge = "";
            }

            if (p.Note is null)
            {
                p.Note = "";
            }

            p.Army = Kept(p.Army);
            p.ArmyGuest = Kept(p.ArmyGuest);
            p.Placements = Kept(p.Placements);
            presets.Add(p);
        }

        return presets;
    }

    private static List<T> Kept<T>(List<T>? read) where T : class
    {
        var kept = new List<T>();
        foreach (var item in read ?? [])
        {
            if (item is not null)
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    private static List<Preset> Builtins()
    {
        return [Builtin(), Ranged(), Whiplash(), Spray(), Preview()];
    }

    public const string FileName = "presets.json";

    public static List<Preset> Load(string? directory, out string? complaint)
    {
        complaint = null;
        var list = Builtins();

        var dir = directory ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
        {
            return list;
        }

        try
        {
            var text = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(text, WireJson.Default.ListPreset);
            if (loaded is null)
            {
                complaint = $"{FileName} parsed to nothing, using the built-in preset only";
                return list;
            }

            foreach (var p in Normalise(loaded))
            {
                list.RemoveAll(existing => string.Equals(existing.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(p);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            complaint = $"could not read {FileName} ({ex.Message}), using the built-in presets only";
            return Builtins();
        }

        return list;
    }

    public static Preset? Find(IEnumerable<Preset> presets, string name)
    {
        return presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static string NameFor(IEnumerable<Preset> presets, string uuid)
    {
        var wanted = Lobby.NormaliseUuid(uuid);
        foreach (var p in presets)
        {
            foreach (var m in p.Army)
            {
                if (Lobby.NormaliseUuid(m.Uuid) == wanted && m.Name.Length > 0)
                {
                    return m.Name;
                }
            }
        }

        return wanted is null ? uuid : wanted[..8] + "...";
    }

    public static string PlacementAdvice(IReadOnlyList<string> army, IReadOnlyList<Placement> squares,
                                         IEnumerable<Preset> presets)
    {
        var known = presets.ToList();
        var parts = new List<string>();
        for (var i = 0; i < squares.Count; i++)
        {
            var name = i < army.Count ? NameFor(known, army[i]) : "(no machine for this square)";
            parts.Add($"{name} on ({squares[i].X},{squares[i].Y}) dir {squares[i].Dir}");
        }

        return string.Join(", ", parts);
    }
}
