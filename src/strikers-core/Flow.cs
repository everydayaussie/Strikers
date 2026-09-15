namespace Strikers.Core;

public enum Stage
{
    Start,
    HostChoose,
    HostInvite,
    JoinPaste,
    JoinWait,

    ChooseArmy,

    Safety,
    SetUp,
    Playing,
}

public sealed class Flow
{
    private static readonly Stage[] HostPath =
    [
        Stage.HostChoose, Stage.HostInvite, Stage.ChooseArmy, Stage.Safety, Stage.SetUp, Stage.Playing,
    ];

    private static readonly Stage[] JoinPath =
    [
        Stage.JoinPaste, Stage.JoinWait, Stage.ChooseArmy, Stage.Safety, Stage.SetUp, Stage.Playing,
    ];

    public Stage Current { get; private set; } = Stage.Start;

    public bool Hosting { get; private set; }

    public Stage[] Path
    {
        get
        {
            return PathFor(Hosting);
        }
    }

    public static Stage[] PathFor(bool hosting)
    {
        return hosting ? HostPath : JoinPath;
    }

    public int Number
    {
        get
        {
            return Array.IndexOf(Path, Current) + 1;
        }
    }

    public int Total
    {
        get
        {
            return Path.Length;
        }
    }

    public void Begin(bool hosting)
    {
        Hosting = hosting;
        Current = hosting ? Stage.HostChoose : Stage.JoinPaste;
    }

    public void Reset()
    {
        Current = Stage.Start;
        Hosting = false;
    }

    public bool GoTo(Stage next)
    {
        var here = Array.IndexOf(Path, Current);
        var there = Array.IndexOf(Path, next);

        if (here < 0 || there < 0 || there <= here)
        {
            return false;
        }

        Current = next;
        return true;
    }

    public bool Rewind(Stage earlier)
    {
        var here = Array.IndexOf(Path, Current);
        var there = Array.IndexOf(Path, earlier);

        if (there < 0 || there >= here)
        {
            return false;
        }

        Current = earlier;
        return true;
    }
}
