namespace Strikers.Core;

public static class Glossary
{
    public sealed record Meaning(string Display, string Rule);

    private static readonly Dictionary<string, Meaning> Attacks = new()
    {
        ["Strike"] = new("Melee", "attacks the first machine within its attack range."),
        ["Ram"] = new("Ram", "attacks the first machine in range, knocks it back a square, and advances onto the square it freed."),
        ["Dive"] = new("Swoop", "attacks the first machine in range and lands beside it; +1 combat power on every terrain, and terrain penalties do not apply to it."),
        ["Shot"] = new("Gunner", "fires at exactly its maximum range, over anything in between; it cannot hit anything closer."),
        ["Dash"] = new("Dash", "charges to the far end of its attack range, damaging and spinning every machine along the path, its own side's included; needs an empty square to land on."),
        ["Tow"] = new("Pull", "attacks the first machine in range and pulls it one square closer; +1 combat power on Marsh, and crosses Marsh freely."),
    };

    private static readonly Dictionary<string, Meaning> Abilities = new()
    {
        ["Roam"] = new("Gallop", "+1 combat power when attacking from Grassland."),
        ["Stalk"] = new("Stalk", "+1 combat power when attacking from Forest."),
        ["Scurry"] = new("Climb", "+1 combat power when attacking from Hills."),
        ["Climb"] = new("High Ground", "+1 combat power when attacking from Mountains."),
        ["Spread"] = new("Sweep", "an attack also hits the machines standing to either side of the target, your own included."),
        ["Shield"] = new("Shield", "+1 combat power when defending."),
        ["Retaliate"] = new("Retaliate", "turns towards its attacker and deals 1 damage back, if the attacker is within its own attack range."),
        ["Burn"] = new("Burn", "attacking a machine on Forest turns that tile to Grassland."),
        ["Freeze"] = new("Freeze", "attacking a machine on Marsh turns that tile to Grassland."),
        ["Seed"] = new("Growth", "attacking a machine on Grassland raises that tile to Forest."),
        ["Unearth"] = new("Alter Terrain", "after it deals damage, the tile under it lowers and the tile under its victim raises."),
        ["Blind"] = new("Blind", "enemy machines within its attack range lose 1 attack power for the turn, stacking."),
        ["Enpower"] = new("Empower", "at the start of each turn, friendly machines within its attack range gain +1 attack power, stacking."),
        ["Spill"] = new("Spray", "at the start of each turn, every piece within its attack range loses 1 health, both sides'."),
        ["Confuse"] = new("Whiplash", "at the start of every turn, every piece within its attack range turns 180 degrees, both sides'; the carrier itself is exempt."),
        ["Stun"] = new("Drain", "at the start of a turn, machines on the four squares next to it cannot be used that turn, your own included."),
    };

    public static Meaning Attack(string internalName)
    {
        var name = (internalName ?? "").Trim();
        return Attacks.TryGetValue(name, out var meaning) ? meaning : new Meaning(name, "");
    }

    public static Meaning Ability(string internalName)
    {
        var name = (internalName ?? "").Trim();
        return Abilities.TryGetValue(name, out var meaning) ? meaning : new Meaning(name, "");
    }
}

