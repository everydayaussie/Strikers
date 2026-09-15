namespace Strikers.App;

internal static class UpdateCheck
{
    public const string ShowDoor = "STRIKERS_SHOW_UPDATE";

    private static string? newest;

    public static string? Newest
    {
        get
        {
            return Volatile.Read(ref newest);
        }
    }

    public static string? Ours()
    {
        return Release.Version(typeof(UpdateCheck).Assembly);
    }

    public static int ModId()
    {
        return Release.ModId(typeof(UpdateCheck).Assembly);
    }

    public static async Task<(string? Newest, string Note)> RunAsync()
    {
        var modId = ModId();
        var door = Environment.GetEnvironmentVariable(ShowDoor);
        if (door is not null)
        {
            var forced = Release.Clean(door);
            Volatile.Write(ref newest, forced);
            return (forced, $"update check: {ShowDoor} says the newest is {forced ?? "unreadable"}");
        }

        if (modId <= 0)
        {
            return (null, "");
        }

        var (found, note) = await Release.FetchNewestAsync(Release.NexusApi, modId, $"Strikers/{Ours() ?? "0"}",
                                                           Release.AnswerWithin, Release.QuietWithin);
        Volatile.Write(ref newest, found);
        return (found, note);
    }
}
