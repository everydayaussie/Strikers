namespace Strikers.Netplay;

internal static class Challenges
{
    internal sealed record Entry(string Uuid, string Name, int Slots, int AiSlots);

    private static readonly Entry[] All =
    [
        new("8BBC182B83FC495AA2150021217530D5", "Beginner's Practice: Easy", 2, 2),
        new("1E46038FF9804646813C959A31EAFB91", "Beginner's Practice: Medium", 4, 4),
        new("74771C9B66D9441AA4CBD5E47D4851AD", "Beginner's Practice: Hard", 6, 6),
        new("0ECEA5D9B9F841908D9716A1421F0AEC", "Regular Challenge", 0, 7),
    ];

    public static IReadOnlyList<Entry> Known
    {
        get { return All; }
    }

    public static Entry? Find(string? uuid)
    {
        var normalised = Lobby.NormaliseUuid(uuid);
        if (normalised is null)
        {
            return null;
        }

        return All.FirstOrDefault(e => e.Uuid == normalised);
    }

    public static string? Problem(string? uuid)
    {
        if (Lobby.NormaliseUuid(uuid) is not { } normalised)
        {
            return "the challenge is not a 32-digit UUID";
        }

        if (Find(normalised) is not null)
        {
            return null;
        }

        var names = string.Join(", ", All.Select(e => e.Name));
        return $"challenge {normalised} is not one this can play. Open one of: {names}. " +
               "Salma's Tutorials entries cannot work: their opponent follows a script, " +
               "so there is no AI turn to play as.";
    }
}
