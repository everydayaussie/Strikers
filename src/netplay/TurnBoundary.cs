namespace Strikers.Netplay;

internal static class TurnBoundary
{
    internal static bool Marked(Piece p)
    {
        return p.Acts > 0 || p.Bursts > 0 || p.Burst;
    }

    public static bool AnyMarked(BoardSnapshot s, int localOwner)
    {
        return s.Pieces.Any(p => p.Owner == localOwner && Marked(p));
    }

    public static bool AnyMarkedAtAll(BoardSnapshot s)
    {
        return s.Pieces.Any(Marked);
    }

    public static bool Ended(BoardSnapshot before, BoardSnapshot after, int localOwner)
    {
        if (!AnyMarked(before, localOwner) || AnyMarked(after, localOwner))
        {
            return false;
        }

        return before.Pieces.Any(p => p.Owner == localOwner && Marked(p) &&
                                      after.Pieces.Any(q => q.Owner == localOwner && q.X == p.X && q.Y == p.Y));
    }

    public static bool AnyTurnEnded(BoardSnapshot before, BoardSnapshot after)
    {
        if (!before.Pieces.Any(Marked) || after.Pieces.Any(Marked))
        {
            return false;
        }

        return before.Pieces.Any(p => Marked(p) &&
                                      after.Pieces.Any(q => q.Owner == p.Owner && q.X == p.X && q.Y == p.Y));
    }

    public static List<(int Index, Placement Square, string Uuid)> CommittedPlacements(
        BoardSnapshot? before, BoardSnapshot after, int localOwner,
        IReadOnlyCollection<(int X, int Y)> alreadyPlaced)
    {
        var sent = new List<(int Index, Placement Square, string Uuid)>();
        if (localOwner is not (0 or 1))
        {
            return sent;
        }

        if (after.PlacingUnreadable || before?.PlacingUnreadable == true)
        {
            return sent;
        }

        var unsent = after.Pieces.Where(p => p.Owner == localOwner && !alreadyPlaced.Contains((p.X, p.Y)))
                                 .OrderBy(p => p.Idx)
                                 .ToList();
        List<Piece> committed;
        if (after.Placing is not { } now)
        {
            committed = before?.Placing is not null ? unsent : [];
        }
        else if (now.Active is not (0 or 1))
        {
            committed = [];
        }
        else if (now.Active != localOwner)
        {
            committed = unsent;
        }
        else if (before?.Placing is { } prev && now.LeftFor(localOwner) < prev.LeftFor(localOwner))
        {
            if (unsent.Any(p => p.Idx < 0))
            {
                return sent;
            }

            var beforeIdx = before.Pieces.Where(p => p.Owner == localOwner).Select(p => p.Idx).ToHashSet();
            committed = unsent.Where(p => beforeIdx.Contains(p.Idx)).ToList();
        }
        else
        {
            committed = [];
        }

        foreach (var p in committed)
        {
            sent.Add((alreadyPlaced.Count + sent.Count, new Placement { X = p.X, Y = p.Y, Dir = p.Facing }, p.Uuid));
        }

        return sent;
    }

    public static int ArmySlotFor(string uuid, IReadOnlyList<string> army,
                                  IReadOnlyCollection<int> alreadyUsed, int fallback, out string? complaint)
    {
        complaint = null;

        if (uuid.Length == 0 || army.Count == 0)
        {
            complaint = uuid.Length == 0
                ? "this piece carries no machine id (an older live-probe?), so the peer is being told " +
                  "the placement ORDER instead. If you place out of army order the boards will differ."
                : "no army is known here, so the peer is being told the placement ORDER instead. " +
                  "If you place out of army order the boards will differ.";
            return fallback;
        }

        for (var i = 0; i < army.Count; i++)
        {
            if (!alreadyUsed.Contains(i) && string.Equals(army[i], uuid, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        complaint = $"machine {uuid} is not in this side's army, or every slot for it is already " +
                    "placed; falling back to the placement order.";
        return fallback;
    }

    public static int PlacementRecordFor(int squaresArrivedBefore)
    {
        return squaresArrivedBefore;
    }

    public static bool MoveBoundsPatchWanted(int width, int height)
    {
        return width != Preset.BoardSide || height != Preset.BoardSide;
    }

    public static bool NoOpponentLeft(BoardSnapshot s, int localOwner)
    {
        return !s.Pieces.Any(p => p.Owner != localOwner);
    }

    public static bool LocalForfeit(BoardSnapshot s, int localOwner)
    {
        if (s.Match is not { Over: true } m || localOwner is not (0 or 1) || m.Winner is not (0 or 1)
            || m.Winner == localOwner || m.VpMax <= 0)
        {
            return false;
        }

        var winnerVp = m.Winner == 0 ? m.Vp0 : m.Vp1;
        if (winnerVp < 0 || winnerVp >= m.VpMax)
        {
            return false;
        }

        return s.Pieces.Any(p => p.Owner == localOwner && p.Health > 0)
               && s.Pieces.Any(p => p.Owner != localOwner && p.Health > 0);
    }

    public static bool MatchOver(BoardSnapshot s, int localOwner)
    {
        if (s.Match is { } m)
        {
            return m.Over;
        }

        return NoOpponentLeft(s, localOwner);
    }

    public static bool EndedWithoutABoundary(BoardSnapshot before, BoardSnapshot after, int localOwner)
    {
        if (!MatchOver(after, localOwner) || MatchOver(before, localOwner))
        {
            return false;
        }

        return !AnyTurnEnded(before, after);
    }

    public static bool EndsTheMatch(BoardSnapshot s, int localOwner, bool sawEnemy)
    {
        if (s.Match is { } m)
        {
            return m.Over;
        }

        return sawEnemy && NoOpponentLeft(s, localOwner);
    }

    public static bool NothingOfOursIn(IReadOnlyList<BoardSnapshot> slice, int localOwner)
    {
        foreach (var s in slice)
        {
            if (s.Act is { } act && s.Pieces.Any(p => p.Idx == act.Unit && p.Owner == localOwner))
            {
                return false;
            }

            if (AnyMarked(s, localOwner))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class TurnPassWatch
{
    private readonly int _owner;
    private bool _orphaned;
    private int _markedTurn = -1;

    public TurnPassWatch(int owner)
    {
        _owner = owner;
    }

    public bool Observe(BoardSnapshot before, BoardSnapshot after, bool marksEdge)
    {
        var markedBefore = Marked(before);

        if (Marked(after))
        {
            _orphaned = false;
            if ((!markedBefore || _markedTurn is not (0 or 1)) && after.Turn == MarkedSeat(after))
            {
                _markedTurn = after.Turn;
            }

            return false;
        }

        if (marksEdge)
        {
            _orphaned = false;
            return false;
        }

        if (markedBefore)
        {
            _orphaned = true;
        }

        if (!_orphaned || after.Turn is not (0 or 1) || _markedTurn is not (0 or 1) || after.Turn == _markedTurn)
        {
            return false;
        }

        if (TurnBoundary.MatchOver(after, after.LocalOwner))
        {
            return false;
        }

        _orphaned = false;
        return true;
    }

    private int MarkedSeat(BoardSnapshot s)
    {
        foreach (var p in s.Pieces)
        {
            if (TurnBoundary.Marked(p) && (_owner is not (0 or 1) || p.Owner == _owner))
            {
                return p.Owner;
            }
        }

        return -1;
    }

    private bool Marked(BoardSnapshot s)
    {
        if (_owner is 0 or 1)
        {
            return TurnBoundary.AnyMarked(s, _owner);
        }

        return TurnBoundary.AnyMarkedAtAll(s);
    }
}

internal sealed class TurnTracker
{
    private readonly List<BoardSnapshot> _buffer = [];
    private readonly int _localOwner;
    private readonly TurnPassWatch _pass;

    public TurnTracker(int localOwner)
    {
        _localOwner = localOwner;
        _pass = new TurnPassWatch(localOwner);
    }

    public int Buffered
    {
        get
        {
            return _buffer.Count;
        }
    }

    public IReadOnlyList<BoardSnapshot>? Push(BoardSnapshot s)
    {
        if (_buffer.Count > 0 && s.IsTornAfter(_buffer[^1]))
        {
            return null;
        }

        _buffer.Add(s);
        if (_buffer.Count < 2)
        {
            return null;
        }

        var last = _buffer.Count - 1;
        var marksEdge = TurnBoundary.Ended(_buffer[last - 1], _buffer[last], _localOwner);
        var passEdge = _pass.Observe(_buffer[last - 1], _buffer[last], marksEdge);
        if (!marksEdge && !passEdge)
        {
            return null;
        }

        var firstMark = FirstMark(last);

        if (firstMark <= 0)
        {
            Reset(last);
            return null;
        }

        var from = SliceStart(firstMark);
        var slice = _buffer.GetRange(from, last - from);
        Reset(last);
        return slice;
    }

    private int FirstMark(int upTo)
    {
        for (var i = 0; i < upTo; i++)
        {
            if (TurnBoundary.AnyMarked(_buffer[i], _localOwner))
            {
                return i;
            }
        }

        return -1;
    }

    private int SliceStart(int firstMark)
    {
        var from = BaselineFor(firstMark);
        var handOver = HandOverAtOrBefore(from);
        if (handOver < 0 || !OursCommittedIn(handOver + 1, from))
        {
            return from;
        }

        return handOver;
    }

    private int HandOverAtOrBefore(int upTo)
    {
        if (_localOwner is not (0 or 1) || _buffer[upTo].Turn != _localOwner)
        {
            return -1;
        }

        var i = upTo;
        while (i > 0 && _buffer[i - 1].Turn == _localOwner)
        {
            i--;
        }

        return i;
    }

    private bool OursCommittedIn(int first, int last)
    {
        for (var k = first; k <= last; k++)
        {
            if (_buffer[k].Commits is { } batch && batch.Records.Any(r => r.IsHuman))
            {
                return true;
            }
        }

        return false;
    }

    private int BaselineFor(int firstMark)
    {
        var i = firstMark - 1;
        while (i > 0)
        {
            var prior = i >= 2 ? _buffer[i - 2] : null;
            if (!StillOurTurn(prior, _buffer[i - 1], _buffer[i]) && !OurSplashEndsBehind(i))
            {
                break;
            }

            i--;
        }

        return i;
    }

    private const int SplashReach = 8;

    private bool OurSplashEndsBehind(int i)
    {
        if (!OwnLossOnly(_buffer[i - 1], _buffer[i]))
        {
            return false;
        }

        var k = i - 1;
        var steps = 1;
        while (k >= 1 && steps <= SplashReach)
        {
            if (EnemyDamagedIn(_buffer[k - 1], _buffer[k]))
            {
                return true;
            }

            var prior = k >= 2 ? _buffer[k - 2] : null;
            if (!OwnLossOnly(_buffer[k - 1], _buffer[k]) && !StillOurTurn(prior, _buffer[k - 1], _buffer[k]))
            {
                return false;
            }

            k--;
            steps++;
        }

        return false;
    }

    private bool OwnLossOnly(BoardSnapshot before, BoardSnapshot after)
    {
        if (before.Pieces.Count != after.Pieces.Count)
        {
            return false;
        }

        var was = before.Pieces.Where(p => p.Idx >= 0).ToDictionary(p => p.Idx);
        var lost = false;
        foreach (var p in after.Pieces)
        {
            if (p.Idx < 0 || !was.TryGetValue(p.Idx, out var q) || q.Owner != p.Owner ||
                q.X != p.X || q.Y != p.Y || q.Facing != p.Facing ||
                q.Acts != p.Acts || q.Bursts != p.Bursts || q.Burst != p.Burst)
            {
                return false;
            }

            if (p.Owner != _localOwner)
            {
                if (q.Health != p.Health)
                {
                    return false;
                }

                continue;
            }

            if (p.Health > q.Health)
            {
                return false;
            }

            if (p.Health < q.Health)
            {
                lost = true;
            }
        }

        return lost;
    }

    private bool StillOurTurn(BoardSnapshot? prior, BoardSnapshot before, BoardSnapshot after)
    {
        if (before.Pieces.Count != after.Pieces.Count)
        {
            if (after.Pieces.Count > before.Pieces.Count)
            {
                return false;
            }

            var unmatched = before.Pieces.Where(p => p.Idx >= 0).ToList();
            var advanced = new List<Piece>();
            foreach (var p in after.Pieces)
            {
                var i = unmatched.FindIndex(q => q.X == p.X && q.Y == p.Y &&
                                                 q.Owner == p.Owner && q.Health == p.Health);
                if (i < 0)
                {
                    advanced.Add(p);
                    continue;
                }

                unmatched.RemoveAt(i);
            }

            foreach (var p in advanced)
            {
                if (p.Owner != _localOwner)
                {
                    return false;
                }

                var dying = unmatched.Where(q => q.Health == 0).ToList();
                var ontoVacated = dying.Any(r => (r.X == p.X && r.Y == p.Y) ||
                                                 (prior is not null &&
                                                  prior.Pieces.Any(z => z.Idx == r.Idx &&
                                                                        z.X == p.X && z.Y == p.Y)));
                if (!ontoVacated)
                {
                    return false;
                }

                var came = unmatched.FindIndex(q => q.Owner == p.Owner && q.Health == p.Health &&
                                                    q.Health != 0);
                if (came < 0)
                {
                    return false;
                }

                unmatched.RemoveAt(came);
            }

            foreach (var q in unmatched)
            {
                if (q.Health != 0)
                {
                    return false;
                }
            }

            return true;
        }

        var was = before.Pieces.Where(p => p.Idx >= 0).ToDictionary(p => p.Idx);

        var enemyDamaged = false;
        foreach (var p in after.Pieces)
        {
            if (p.Owner != _localOwner && p.Idx >= 0 &&
                was.TryGetValue(p.Idx, out var e) && e.Owner == p.Owner && p.Health < e.Health)
            {
                enemyDamaged = true;
            }
        }

        foreach (var p in after.Pieces)
        {
            if (p.Idx < 0 || !was.TryGetValue(p.Idx, out var q))
            {
                return false;
            }

            if (q.Owner != p.Owner)
            {
                return false;
            }

            if (p.Owner == _localOwner)
            {
                if (q.Health != p.Health && !enemyDamaged && !EnemyDamagedIn(prior, before))
                {
                    return false;
                }

                continue;
            }

            if (q.Health != p.Health)
            {
                continue;
            }

            var movedSquare = q.X != p.X || q.Y != p.Y;
            if (movedSquare && !ShovedByUs(prior, before, p.Idx))
            {
                return false;
            }

            if (q.Acts != p.Acts || q.Bursts != p.Bursts || q.Burst != p.Burst)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShovedByUs(BoardSnapshot? prior, BoardSnapshot before, int idx)
    {
        if (prior is null || idx < 0)
        {
            return false;
        }

        byte healthEarlier = 0;
        var foundEarlier = false;
        foreach (var x in prior.Pieces)
        {
            if (x.Idx == idx)
            {
                healthEarlier = x.Health;
                foundEarlier = true;
                break;
            }
        }

        if (!foundEarlier)
        {
            return false;
        }

        foreach (var x in before.Pieces)
        {
            if (x.Idx == idx)
            {
                return x.Health < healthEarlier;
            }
        }

        return false;
    }

    private bool EnemyDamagedIn(BoardSnapshot? prior, BoardSnapshot before)
    {
        if (prior is null)
        {
            return false;
        }

        foreach (var x in before.Pieces)
        {
            if (x.Owner == _localOwner || x.Idx < 0)
            {
                continue;
            }

            foreach (var e in prior.Pieces)
            {
                if (e.Idx == x.Idx && e.Owner == x.Owner && x.Health < e.Health)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Reset(int clearedAt)
    {
        var keep = _buffer[clearedAt];
        _buffer.Clear();
        _buffer.Add(keep);
    }

    public IReadOnlyList<BoardSnapshot>? Flush()
    {
        if (_buffer.Count < 2)
        {
            return null;
        }

        var firstMark = FirstMark(_buffer.Count);
        var from = firstMark > 0 ? SliceStart(firstMark) : LastOpponentTurnEnd();

        return _buffer.GetRange(from, _buffer.Count - from);
    }

    private int LastOpponentTurnEnd()
    {
        var opponent = 1 - _localOwner;
        for (var i = _buffer.Count - 1; i > 0; i--)
        {
            if (TurnBoundary.Ended(_buffer[i - 1], _buffer[i], opponent))
            {
                return i;
            }
        }

        return 0;
    }
}
