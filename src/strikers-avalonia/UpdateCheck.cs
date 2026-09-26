namespace Strikers.App;

internal static class UpdateCheck
{
    public const string ShowDoor = "STRIKERS_SHOW_UPDATE";

    private static string? newest;

    private static string? page;

    public static string? Newest
    {
        get
        {
            return Volatile.Read(ref newest);
        }
    }

    public static string Page()
    {
        return Volatile.Read(ref page) ?? PageFor(onNexus: ModId() > 0);
    }

    private static string PageFor(bool onNexus)
    {
        return onNexus ? Release.PageUrl(ModId()) : Release.ReleasesPageUrl(Release.GitHubRepo);
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

        var agent = $"Strikers/{Ours() ?? "0"}";

        string? onNexus = null;
        var note = "";
        if (modId > 0)
        {
            (onNexus, note) = await Release.FetchNewestAsync(Release.NexusApi, modId, agent,
                                                             Release.AnswerWithin, Release.QuietWithin);
        }

        var (onGitHub, tagNote) = await Release.FetchNewestTagAsync(Release.GitHubApi, Release.GitHubRepo, agent,
                                                                   Release.AnswerWithin, Release.QuietWithin);

        var takeGitHub = Release.Newer(onGitHub, onNexus) || onNexus is null;
        var found = takeGitHub ? onGitHub : onNexus;
        Volatile.Write(ref newest, found);
        Volatile.Write(ref page, PageFor(onNexus: !takeGitHub));

        var both = note.Length > 0 ? $"{note}, {tagNote}" : tagNote;
        return (found, both);
    }
}
