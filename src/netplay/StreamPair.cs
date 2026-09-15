namespace Strikers.Netplay;

public static class StreamPair
{
    public sealed record Event(string Uuid, int Sample, string Kind, int Delta, int X, int Y, string? Stamp);

    public sealed record PairResult(List<(Event A, Event B)> Paired, List<Event> OnlyA, List<Event> OnlyB);

    public static List<Event> Events(IReadOnlyList<BoardSnapshot> samples, bool rotate)
    {
        var ledger = new List<Event>();
        var prev = new Dictionary<string, (int X, int Y, int Health)>();

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var now = new Dictionary<string, (int X, int Y, int Health)>();
            foreach (var p in s.Pieces)
            {
                if (string.IsNullOrEmpty(p.Uuid))
                {
                    continue;
                }
                var x = rotate ? s.Width - 1 - p.X : p.X;
                var y = rotate ? s.Height - 1 - p.Y : p.Y;
                now[p.Uuid] = (x, y, p.Health);
            }

            foreach (var (uuid, was) in prev)
            {
                if (now.TryGetValue(uuid, out var isNow))
                {
                    if (isNow.Health != was.Health)
                    {
                        ledger.Add(new Event(uuid, i, "health", isNow.Health - was.Health,
                                             isNow.X, isNow.Y, s.Stamp));
                    }
                }
                else
                {
                    ledger.Add(new Event(uuid, i, "death", 0, was.X, was.Y, s.Stamp));
                }
            }
            prev = now;
        }
        return ledger;
    }

    public static PairResult Pair(List<Event> a, List<Event> b)
    {
        var paired = new List<(Event, Event)>();
        var claimed = new bool[b.Count];
        var onlyA = new List<Event>();

        foreach (var ev in a)
        {
            var found = -1;
            for (var j = 0; j < b.Count; j++)
            {
                if (!claimed[j] && b[j].Uuid == ev.Uuid && b[j].Kind == ev.Kind && b[j].Delta == ev.Delta)
                {
                    found = j;
                    break;
                }
            }
            if (found >= 0)
            {
                claimed[found] = true;
                paired.Add((ev, b[found]));
            }
            else
            {
                onlyA.Add(ev);
            }
        }

        var onlyB = new List<Event>();
        for (var j = 0; j < b.Count; j++)
        {
            if (!claimed[j])
            {
                onlyB.Add(b[j]);
            }
        }
        return new PairResult(paired, onlyA, onlyB);
    }

    public static string Describe(Event e)
    {
        var stamp = e.Stamp is null ? "" : $" at {e.Stamp}";
        if (e.Kind == "death")
        {
            return $"s{e.Sample}{stamp}  {e.Uuid[..Math.Min(8, e.Uuid.Length)]}  removed from ({e.X},{e.Y})";
        }
        return $"s{e.Sample}{stamp}  {e.Uuid[..Math.Min(8, e.Uuid.Length)]}  health {e.Delta:+0;-0} at ({e.X},{e.Y})";
    }
}
