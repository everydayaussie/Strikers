namespace Strikers.Core;

public enum ToastKind
{
    Info,
    Good,
    Bad,
}

public static class Toasts
{
    public const int MaxShown = 4;

    public static int HoldMs(ToastKind kind, int characters)
    {
        var overRead = Math.Max(0, characters - 40);
        var held = 3500 + (30 * overRead);

        if (kind == ToastKind.Bad)
        {
            var floored = Math.Max(6000, held);
            return Math.Min(12000, floored);
        }

        return Math.Min(8000, held);
    }
}
