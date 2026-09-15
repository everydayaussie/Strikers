namespace Strikers.Core;

public static partial class Library
{
    public sealed record BoardDesign(string Name, string Tier, int Depth, string[] Rows);

    public sealed record ArmyDesign(string Name, string Tier, string[] Machines);

    public const string Normal = "normal";
    public const string Fun = "fun";
    public const string Mad = "mad";
    public const string Outlandish = "outlandish";
    public const string Insane = "insane";

    public static readonly IReadOnlyList<BoardDesign> Boards = [.. AllBoards()];

    public static readonly IReadOnlyList<ArmyDesign> Armies = [.. AllArmies()];

    public static readonly string[] StarterBoards =
    [
        "Lost terraces", "Golden aisles", "Morass perimeter", "Downland quarters", "Silent greenwood",
        "Swale split", "Sump spread", "Rough gauntlet", "Hidden course", "Birchwood posts",
        "Grove flanks", "Green hummock",
        "Green morass", "Dark stalls", "Rough escarpment", "Soft crosscut", "Greenwood slant",
        "Cold hillock", "Mire spread", "Pale strait", "Morass wander", "Mire studs",
        "Escarpment tiers", "High pinewood", "Near hummock", "Cairn quarters", "Soft oblique",
        "Broad rift", "Summit ranks", "Pale ring",
        "Single square", "One stone",
    ];

    public static readonly string[] StarterArmies =
    [
        "Bold edge", "Steady watch", "Tame volley", "New fist", "Still volley", "Cold volley",
        "Swift front", "Odd rush", "High chain", "Wary edge", "Grim charge", "Wild shadow",
        "Lean squad", "Still chain", "Deep crew", "Stout haul", "Wide crew", "Heavy band",
        "Stout volley", "Stout delve", "Cold muddle", "Long crew",
        "Thin anvil", "Steady muddle", "Keen guard", "Far rush", "Bold chain",
        "Blunt anvil", "Odd bleed", "Lean flame",
        "Nine spines", "Nine fangs",
    ];

    public static IEnumerable<BoardDesign> Starter(IEnumerable<BoardDesign> all)
    {
        return StarterBoards.Select(name => all.First(b => b.Name == name));
    }

    public static IEnumerable<ArmyDesign> Starter(IEnumerable<ArmyDesign> all)
    {
        return StarterArmies.Select(name => all.First(a => a.Name == name));
    }

    public static StrikeBoard Build(BoardDesign design)
    {
        var problem = Check(design, out var board);
        if (problem is not null || board is null)
        {
            throw new InvalidOperationException($"{design.Name}: {problem}");
        }

        return board;
    }

    public static string? Check(BoardDesign design, out StrikeBoard? board)
    {
        board = null;
        if (design.Rows.Length == 0 || design.Rows[0].Length == 0)
        {
            return "no rows";
        }

        var built = new StrikeBoard
        {
            Name = design.Name,
            Width = design.Rows[0].Length,
            Height = design.Rows.Length,
            PlacementRows = design.Depth,
        };

        for (var y = 0; y < built.Height; y++)
        {
            if (design.Rows[y].Length != built.Width)
            {
                return $"row {y} is {design.Rows[y].Length} wide, not {built.Width}";
            }

            for (var x = 0; x < built.Width; x++)
            {
                var terrain = design.Rows[y][x] switch
                {
                    '.' => Terrain.Grassland,
                    'f' => Terrain.Forest,
                    'h' => Terrain.Hills,
                    'M' => Terrain.Mountains,
                    'm' => Terrain.Marsh,
                    'C' => Terrain.Chasm,
                    _ => int.MinValue,
                };

                if (terrain == int.MinValue)
                {
                    return $"unknown tile '{design.Rows[y][x]}' at row {y}, column {x}";
                }

                built.Cells[(y * built.Width) + x] = terrain;
            }
        }

        var standing = built.Problem();
        if (standing is not null)
        {
            return standing;
        }

        board = built;
        return null;
    }

    public static BoardDesign? PickBoard(IEnumerable<string> takenNames, Random rng)
    {
        return Pick(Boards, b => b.Name, takenNames, rng);
    }

    public static ArmyDesign? PickArmy(IEnumerable<string> takenNames, Random rng)
    {
        return Pick(Armies, a => a.Name, takenNames, rng);
    }

    private static T? Pick<T>(IReadOnlyList<T> all, Func<T, string> name, IEnumerable<string> takenNames,
                              Random rng) where T : class
    {
        if (all.Count == 0)
        {
            return null;
        }

        var taken = new HashSet<string>(takenNames, StringComparer.OrdinalIgnoreCase);
        var fresh = all.Where(d => !taken.Contains(name(d))).ToList();
        var pool = fresh.Count > 0 ? fresh : all.ToList();
        return pool[rng.Next(pool.Count)];
    }
}
