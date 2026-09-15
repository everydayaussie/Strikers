namespace Strikers.Netplay;

internal static class MoveDetector
{
    public sealed record Result(List<Move> Moves, string? Refusal, string? Warning = null)
    {
        public bool Ok
        {
            get
            {
                return Refusal is null;
            }
        }
    }

    public static Result ReadTurn(IReadOnlyList<BoardSnapshot> samples, int localOwner, out string? note)
    {
        note = null;
        var reading = CommitTranscript.ToMoves(samples, localOwner);
        if (reading.Refusal is not null)
        {
            return new Result([], reading.Refusal);
        }

        if (reading.NotCovered is null)
        {
            if (reading.Cancelled is { Count: > 0 } cancelled)
            {
                note = $"record {string.Join(", ", cancelled)} was an Overcharge the player cancelled, " +
                       "so it crosses as nothing";
            }

            return new Result(reading.Moves, null);
        }

        if (CommitTranscript.HasRing(samples))
        {
            note = $"the game's own records were not read for this turn ({reading.NotCovered}), " +
                   "so it was read from the board";
        }

        return DetectSequence(samples, localOwner);
    }

    public static Result Detect(BoardSnapshot before, BoardSnapshot after, int localOwner)
    {
        if (localOwner is not (0 or 1))
        {
            return new Result([], $"local owner is {localOwner}, the snapshot did not say which seat is the AI");
        }

        var moves = new List<Move>();

        var localBefore = before.Pieces.Where(p => p.Owner == localOwner).ToList();
        var localAfter = after.Pieces.Where(p => p.Owner == localOwner).ToList();
        var remoteBefore = before.Pieces.Where(p => p.Owner != localOwner).ToList();
        var remoteAfter = after.Pieces.Where(p => p.Owner != localOwner).ToList();

        if (localAfter.Count > localBefore.Count)
        {
            return new Result([], "the local side gained a piece, which no turn can do");
        }

        var bursted = new List<Piece>();
        foreach (var a in localAfter)
        {
            if (!a.Burst)
            {
                continue;
            }

            var b = a.Idx >= 0
                ? localBefore.FirstOrDefault(p => p.Idx == a.Idx, new Piece(-1, -1, 0, 0, -1))
                : localBefore.FirstOrDefault(p => p.X == a.X && p.Y == a.Y, new Piece(-1, -1, 0, 0, -1));

            if (b.Owner >= 0 && b.Burst)
            {
                continue;
            }

            bursted.Add(b.Owner >= 0 ? b : a);
        }

        var indexed = localBefore.All(p => p.Idx >= 0) && localAfter.All(p => p.Idx >= 0);

        var acted = new List<(Piece Before, Piece After)>();
        foreach (var b in localBefore)
        {
            if (!indexed)
            {
                break;
            }

            var a = localAfter.FirstOrDefault(p => p.Idx == b.Idx);
            if (a.Idx != b.Idx)
            {
                continue;
            }

            if (a.X != b.X || a.Y != b.Y || (a.Facing & 3) != (b.Facing & 3))
            {
                acted.Add((b, a));
            }
        }

        if (!indexed)
        {
            var vacated = localBefore.Where(b => localAfter.All(a => a.X != b.X || a.Y != b.Y)).ToList();
            var arrived = localAfter.Where(a => localBefore.All(b => b.X != a.X || b.Y != a.Y)).ToList();

            if (vacated.Count != arrived.Count)
            {
                return new Result([], $"{vacated.Count} pieces left a square but {arrived.Count} arrived, a piece was lost or the read is wrong");
            }

            if (vacated.Count > 1)
            {
                return new Result([], $"{vacated.Count} pieces moved and the snapshot carries no piece indices, " +
                                      "cannot tell which went where. Update live-probe.");
            }

            if (vacated.Count == 1)
            {
                acted.Add((vacated[0], arrived[0]));
            }
            else
            {
                foreach (var a in localAfter)
                {
                    var b = localBefore.FirstOrDefault(p => p.X == a.X && p.Y == a.Y);
                    if ((b.Facing & 3) != (a.Facing & 3))
                    {
                        acted.Add((b, a));
                    }
                }
            }

            if (acted.Count > 1)
            {
                return new Result([], $"{acted.Count} pieces changed facing without moving, ambiguous");
            }
        }

        acted = Order(acted);

        foreach (var (from, to) in acted)
        {
            moves.Add(new Move { SrcX = from.X, SrcY = from.Y, DstX = to.X, DstY = to.Y, Facing = to.Facing });
        }

        var hurt = new List<Piece>();
        foreach (var b in remoteBefore)
        {
            var a = b.Idx >= 0
                ? remoteAfter.FirstOrDefault(p => p.Idx == b.Idx, new Piece(-1, -1, 0, 0, -1))
                : remoteAfter.FirstOrDefault(p => p.X == b.X && p.Y == b.Y, new Piece(-1, -1, 0, 0, -1));

            if (a.Owner < 0)
            {
                hurt.Add(b);
            }
            else if (a.Health < b.Health)
            {
                hurt.Add(b);
            }
        }

        foreach (var victimBefore in hurt)
        {
            var victim = victimBefore.Idx >= 0
                ? after.Pieces.FirstOrDefault(p => p.Idx == victimBefore.Idx, victimBefore)
                : victimBefore;

            var candidates = localAfter.Where(p => InStrikeLine(p, victim, PushReach)).ToList();

            if (candidates.Count == 0)
            {
                var struckThenLeft = localBefore
                    .Where(b => DirectionTo(b, victimBefore, out _) && moves.Any(m => m.SrcX == b.X && m.SrcY == b.Y))
                    .ToList();

                if (struckThenLeft.Count != 1)
                {
                    return new Result([], $"a piece at ({victim.X},{victim.Y}) took damage but " +
                                          (struckThenLeft.Count == 0
                                              ? "no local piece ends up with it in its strike line, and none that acted started in line with it either"
                                              : $"{struckThenLeft.Count} local pieces that acted started in line with it, cannot say which struck"));
                }

                var from = struckThenLeft[0];
                if (!DirectionTo(from, victimBefore, out var strikeFacing))
                {
                    return new Result([], $"a piece at ({victim.X},{victim.Y}) took damage from " +
                                          $"({from.X},{from.Y}), which is not in an orthogonal line within range of it");
                }

                var mv = moves.First(m => m.SrcX == from.X && m.SrcY == from.Y);
                moves.Insert(moves.IndexOf(mv), new Move
                {
                    SrcX = from.X, SrcY = from.Y,
                    DstX = from.X, DstY = from.Y,
                    TargetX = victimBefore.X, TargetY = victimBefore.Y,
                    Facing = strikeFacing, Attack = true,
                });
                continue;
            }

            if (candidates.Count > 1)
            {
                return new Result([], $"{candidates.Count} local pieces could have struck ({victim.X},{victim.Y}), ambiguous");
            }

            var attacker = candidates[0];

            var existing = moves.FirstOrDefault(m => m.DstX == attacker.X && m.DstY == attacker.Y);
            var src = existing is not null ? (existing.SrcX, existing.SrcY) : (attacker.X, attacker.Y);
            var at = existing is not null ? moves.IndexOf(existing) : moves.Count;
            if (existing is not null)
            {
                moves.Remove(existing);
            }

            moves.Insert(at, new Move
            {
                SrcX = src.Item1, SrcY = src.Item2,
                DstX = attacker.X, DstY = attacker.Y,
                TargetX = victimBefore.X, TargetY = victimBefore.Y,
                Facing = attacker.Facing, Attack = true,
            });
        }

        if (moves.Count == 0)
        {
            return new Result([], "nothing changed on the board, no move to send");
        }

        var risky = new List<Piece>();
        foreach (var p in bursted)
        {
            var m = moves.FirstOrDefault(x => x.SrcX == p.X && x.SrcY == p.Y);

            if (m is null)
            {
                return new Result([], $"a machine at ({p.X},{p.Y}) bursted but no action of this turn starts " +
                                      "there, so there is no action for the 2 health to pay for. Send the turn " +
                                      "by hand with 'move' or 'attack', or play it again without burst.");
            }

            m.Burst = true;
            if (m.SrcX != m.DstX || m.SrcY != m.DstY)
            {
                risky.Add(p);
            }
        }

        var warning = risky.Count == 0
            ? null
            : "burst + movement on " + string.Join(", ", risky.Select(p => $"({p.X},{p.Y})")) +
              ": that machine acted twice and this is sent as one action covering both. An over-range " +
              "move is accepted by the injected path (verified live), so it should replay, the part " +
              "not yet tested is a net path that is blocked or not a straight line.";

        return new Result(moves, null, warning);
    }

    public static Result DetectSequence(IReadOnlyList<BoardSnapshot> samples, int localOwner)
    {
        if (localOwner is not (0 or 1))
        {
            return new Result([], $"local owner is {localOwner}, the snapshot did not say which seat is the AI");
        }

        var kept = new List<BoardSnapshot>(samples.Count);
        foreach (var s in samples)
        {
            if (kept.Count > 0 && s.IsTornAfter(kept[^1]))
            {
                continue;
            }

            kept.Add(s);
        }

        samples = kept;

        if (samples.Count < 2)
        {
            return new Result([], $"only {samples.Count} board sample(s), a turn cannot be read from that");
        }

        var by = new List<Dictionary<int, Piece>>(samples.Count);
        var slotToId = new Dictionary<int, int>();
        Dictionary<int, Piece>? rawPrev = null;

        Dictionary<int, Piece>? rawPrior = null;

        foreach (var s in samples)
        {
            var raw = new Dictionary<int, Piece>();
            foreach (var p in s.Pieces)
            {
                if (p.Idx < 0)
                {
                    return new Result([], "a sample carries no piece indices, the sequence detector pairs pieces by " +
                                          "their slot in the game's unit array and cannot work without one. Update live-probe.");
                }

                if (!raw.TryAdd(p.Idx, p))
                {
                    return new Result([], $"two pieces share slot {p.Idx} in one sample, the read is wrong");
                }

                if (p.Owner == localOwner && (p.Acts < 0 || p.Bursts < 0))
                {
                    return new Result([], "the snapshot carries no action counters (`acts`/`bursts`, unit+0x3B bits 2-5). " +
                                          "They are what cuts a turn into actions, `acted` saturates at one per machine. " +
                                          "Update live-probe.");
                }
            }

            if (rawPrev is null)
            {
                foreach (var idx in raw.Keys)
                {
                    slotToId[idx] = idx;
                }
            }
            else if (raw.Count > rawPrev.Count)
            {
                return new Result([], "a machine appeared mid-turn, which no turn can do, the read is wrong");
            }
            else if (raw.Count < rawPrev.Count)
            {
                var remap = new Dictionary<int, int>();
                var claimed = new HashSet<int>();
                var advanced = new List<KeyValuePair<int, Piece>>();

                foreach (var (idx, p) in raw)
                {
                    var bySquare = rawPrev.Where(q => q.Value.Owner == p.Owner &&
                                                      q.Value.X == p.X && q.Value.Y == p.Y).ToList();
                    if (bySquare.Count == 1 && slotToId.TryGetValue(bySquare[0].Key, out var id) &&
                        claimed.Add(bySquare[0].Key))
                    {
                        remap[idx] = id;
                        continue;
                    }

                    advanced.Add(new KeyValuePair<int, Piece>(idx, p));
                }

                foreach (var (idx, p) in advanced)
                {
                    var dying = rawPrev.Where(q => !claimed.Contains(q.Key) && q.Value.Health == 0).ToList();
                    var ontoVacated = dying.Any(r => (r.Value.X == p.X && r.Value.Y == p.Y) ||
                                                     (rawPrior is not null &&
                                                      rawPrior.TryGetValue(r.Key, out var was) &&
                                                      was.X == p.X && was.Y == p.Y));

                    var came = rawPrev.Where(q => !claimed.Contains(q.Key) &&
                                                  q.Value.Owner == p.Owner &&
                                                  q.Value.Health == p.Health &&
                                                  q.Value.Health != 0).ToList();

                    if (!ontoVacated || came.Count != 1 || !slotToId.TryGetValue(came[0].Key, out var id))
                    {
                        return new Result([], "a machine was destroyed and the piece list renumbered, and the survivors " +
                                              "could not be matched across it by square. Refusing rather than pairing " +
                                              "pieces by a slot that has changed meaning.");
                    }

                    claimed.Add(came[0].Key);
                    remap[idx] = id;
                }

                slotToId = remap;
            }

            var d = new Dictionary<int, Piece>();
            foreach (var (idx, p) in raw)
            {
                if (!slotToId.TryGetValue(idx, out var id))
                {
                    return new Result([], $"slot {idx} has no identity carried forward, the read is wrong");
                }

                d[id] = p;
            }

            rawPrior = rawPrev;
            rawPrev = raw;
            by.Add(d);
        }

        var acts = new List<Act>();
        string? extraClosing = null;
        for (var i = 1; i < samples.Count; i++)
        {
            var ticks = 0;
            var who = -1;
            var burst = false;

            foreach (var (idx, now) in by[i])
            {
                if (now.Owner != localOwner)
                {
                    continue;
                }

                if (!by[i - 1].TryGetValue(idx, out var was))
                {
                    continue;
                }

                if (now.Acts <= was.Acts && now.Bursts <= was.Bursts && !(now.Burst && !was.Burst))
                {
                    continue;
                }

                ticks++;
                who = idx;
                burst = now.Bursts > was.Bursts || (now.Burst && !was.Burst);
            }

            if (ticks == 0)
            {
                continue;
            }

            if (ticks > 1)
            {
                return new Result([], $"{ticks} of our machines took an action between two samples, " +
                                      "cannot say which acted first");
            }

            acts.Add(new Act { ActorIdx = who, Sample = i, IsBurst = burst });
        }

        string? dyingMove = null;
        var recordsPresent = samples.Any(s => s.Act is not null);
        for (var i = 1; recordsPresent && i < samples.Count; i++)
        {
            foreach (var (idx, was) in by[i - 1])
            {
                if (was.Owner != localOwner || was.Health == 0 || by[i].ContainsKey(idx)
                    || acts.Any(x => x.ActorIdx == idx && x.IsBurst))
                {
                    continue;
                }

                var record = OrphanRecordAt(samples, i, was.X, was.Y);
                if (record is null)
                {
                    return new Result([], $"one of our machines, on ({was.X},{was.Y}) with {was.Health} health, " +
                                          "left the board with no damage and no record of what it did. A machine " +
                                          "that Overcharges at 2 health dies of the cost, and the game's own " +
                                          "record of that move is what this reads; without one the turn cannot be sent");
                }

                var r = record.Value;
                if (r.Tx == r.Fx && r.Ty == r.Fy)
                {
                    continue;
                }

                var dying = new Act { ActorIdx = idx, Sample = i, IsBurst = true, ToX = r.Tx, ToY = r.Ty };
                var slot = acts.FindIndex(x => x.Sample > i);
                acts.Insert(slot < 0 ? acts.Count : slot, dying);
                dyingMove = $"our machine on ({was.X},{was.Y}) left the board at {was.Health} health as its " +
                            $"move to ({r.Tx},{r.Ty}) committed: read as an Overcharge move that killed it";
            }
        }

        if (acts.Count == 0)
        {
            var closing = ClosingBlow(by, localOwner, samples[^1].Match);
            if (closing is null)
            {
                return new Result([], "no machine of ours acted during the sampled turn, no move to send. " +
                                      "If the turn really did contain actions, the snapshot may predate the " +
                                      "`acts`/`bursts` counters: update live-probe.");
            }

            acts.Add(closing);
            extraClosing = $"no counter tick was sampled: read as the match-ending strike on " +
                           $"({closing.TargetX},{closing.TargetY}), which clears the counters as it lands";
        }

        for (var j = 0; j < acts.Count; j++)
        {
            acts[j].End = acts[j].Sample;
        }

        var enemyWipedOut = !by[^1].Values.Any(p => p.Owner != localOwner && p.Health > 0);

        var extra = new List<string>();
        if (extraClosing is not null)
        {
            extra.Add(extraClosing);
        }

        if (dyingMove is not null)
        {
            extra.Add(dyingMove);
        }

        FoldedSecondBlow = null;
        InferredNote = null;

        var sprayOwed = PredictSpray(by[0]);

        (string Why, int X, int Y)? deferredEnemyHealth = null;

        for (var i = 1; i < samples.Count; i++)
        {
            foreach (var (idx, now) in by[i])
            {
                if (by[i - 1].ContainsKey(idx))
                {
                    continue;
                }

                return new Result([], now.Owner == localOwner
                    ? "one of our machines appeared mid-turn, which no turn can do"
                    : "an enemy machine appeared mid-turn, the read is wrong");
            }

            foreach (var (idx, was) in by[i - 1])
            {
                if (was.Owner == localOwner)
                {
                    continue;
                }

                var gone = !by[i].TryGetValue(idx, out var now);
                if (!gone && now.Health >= was.Health)
                {
                    continue;
                }

                if (was.Health <= 0)
                {
                    continue;
                }

                if ((acts.Count == 0 || i < acts[0].Sample) &&
                    sprayOwed.TryGetValue(idx, out var owed) && owed > 0)
                {
                    var lost = gone ? was.Health : was.Health - now.Health;
                    var absorbed = Math.Min(owed, lost);
                    sprayOwed[idx] = owed - absorbed;

                    if (absorbed >= lost)
                    {
                        continue;
                    }
                }

                var why = Attribute(samples, acts, by, i, idx, was);
                if (why is null && InferredNote is { } inferred)
                {
                    extra.Add(inferred);
                    InferredNote = null;
                }

                if (why is not null)
                {
                    var deadByEnd = !by[^1].TryGetValue(idx, out var atEnd) || atEnd.Health == 0;

                    if (!deadByEnd || !enemyWipedOut ||
                        UntrackedKiller(by, localOwner, i, idx) is not { } late)
                    {
                        if (deferredEnemyHealth is null && HasDyingAttackerCandidate(by, localOwner, acts))
                        {
                            deferredEnemyHealth = (why, was.X, was.Y);
                            continue;
                        }

                        return new Result([], deferredEnemyHealth is { } held ? held.Why : why);
                    }

                    acts.Add(late);
                    extra.Add($"no counter tick was sampled for the strike on ({late.TargetX},{late.TargetY}), " +
                              "read as the blow that ended the match, which clears the counters as it lands");
                    continue;
                }

                if (LastAttributed is { } note)
                {
                    var consequenceDead = !by[^1].TryGetValue(idx, out var cEnd) || cEnd.Health == 0;

                    if (consequenceDead && enemyWipedOut && !SprayExplains(by, i, idx) &&
                        UntrackedKiller(by, localOwner, i, idx) is { } ending)
                    {
                        acts.Add(ending);
                        extra.Add($"a drop on ({ending.TargetX},{ending.TargetY}) that no counter tick " +
                                  "explains, read as the blow that ended the match rather than as a " +
                                  "consequence of an earlier action");
                        continue;
                    }

                    extra.Add(note);
                }

                if (FoldedSecondBlow is { } second)
                {
                    FoldedSecondBlow = null;

                    var victimDead = !by[^1].TryGetValue(idx, out var vEnd) || vEnd.Health == 0;

                    if (enemyWipedOut && victimDead)
                    {
                        acts.Add(second);
                        var what = second.IsBurst
                            ? "a traceless Overcharge second blow that ended the match, whose tick, " +
                              "burst flag and health cost the teardown all wiped"
                            : $"a traceless second blow that ended the match, a PLAIN attack struck from " +
                              $"({second.StrikeX},{second.StrikeY}), the once-per-machine rule choosing the " +
                              "actor";
                        extra.Add($"a second drop on ({second.TargetX},{second.TargetY}) after the last tick, " +
                                  $"read as {what}");
                    }
                }
            }
        }

        var committed = new Dictionary<int, (int X, int Y)>();
        foreach (var (idx, p) in by[0])
        {
            committed[idx] = (p.X, p.Y);
        }

        var moves = new List<Move>();

        for (var ai = 0; ai < acts.Count; ai++)
        {
            var a = acts[ai];

            var actsAgain = false;
            for (var k = ai + 1; k < acts.Count; k++)
            {
                if (acts[k].ActorIdx == a.ActorIdx)
                {
                    actsAgain = true;
                    break;
                }
            }

            var until = actsAgain ? acts[ai + 1].Sample - 1 : by.Count - 1;

            if (!TryLastKnown(by, a.Sample, a.ActorIdx, out var end))
            {
                return new Result([], $"the machine in slot {a.ActorIdx} acted and then could not be read at all");
            }

            var src = committed.TryGetValue(a.ActorIdx, out var c) ? c : (end.X, end.Y);
            var first = moves.Count;

            if (a.VictimIdx >= 0)
            {
                moves.Add(new Move
                {
                    SrcX = src.X, SrcY = src.Y,
                    DstX = a.StrikeX, DstY = a.StrikeY,
                    TargetX = a.TargetX, TargetY = a.TargetY,
                    Facing = a.StrikeFacing, Attack = true,
                });

                var pushedOnto = CarriedByItsHit(end) && end.X == a.TargetX && end.Y == a.TargetY;
                var chargedPast = FinishedOnItsCharge(a, end);

                if ((pushedOnto || chargedPast) && (end.X != a.StrikeX || end.Y != a.StrikeY))
                {
                    extra.Add(chargedPast
                                  ? $"the attacker finished on ({end.X},{end.Y}), the square its charge carries it " +
                                    "to, read as Dash's own advance and NOT sent as a move"
                                  : $"the attacker finished on ({end.X},{end.Y}), the square it struck, read as " +
                                    "Ram's own advance and NOT sent as a move");
                }
                else if (end.X != a.StrikeX || end.Y != a.StrikeY)
                {
                    var owed = ControllerMoveTo(samples, by, a.ActorIdx, end.X, end.Y) ?? (a.StrikeX, a.StrikeY);

                    moves.Add(new Move
                    {
                        SrcX = owed.X, SrcY = owed.Y,
                        DstX = end.X, DstY = end.Y,
                        Facing = SettledFacing(by, a.Sample, until, a.ActorIdx, end.X, end.Y, end.Facing),
                    });
                }
            }
            else
            {
                var dstX = a.ToX >= 0 ? a.ToX : end.X;
                var dstY = a.ToY >= 0 ? a.ToY : end.Y;
                moves.Add(new Move
                {
                    SrcX = src.X, SrcY = src.Y,
                    DstX = dstX, DstY = dstY,
                    Facing = SettledFacing(by, a.Sample, until, a.ActorIdx, dstX, dstY, end.Facing),
                });
            }

            if (a.IsBurst)
            {
                moves[first].Burst = true;
            }

            committed[a.ActorIdx] = (end.X, end.Y);

            if (a.VictimIdx >= 0 && TryLastKnown(by, a.End, a.VictimIdx, out var v))
            {
                committed[a.VictimIdx] = (v.X, v.Y);
            }
        }

        var deadRecovery = RecoverDyingAttacker(samples, by, localOwner,
                                                acts.Select(x => x.ActorIdx).ToHashSet(), committed, moves);
        if (deadRecovery.Refusal is not null)
        {
            return new Result([], deadRecovery.Refusal);
        }

        if (deferredEnemyHealth is { } deferred)
        {
            var explained = deadRecovery.Warning is not null && moves.Count > 0 &&
                            moves[^1] is { Attack: true } recovered &&
                            recovered.TargetX == deferred.X && recovered.TargetY == deferred.Y;
            if (!explained)
            {
                return new Result([], deferred.Why);
            }
        }

        if (deadRecovery.Warning is not null)
        {
            extra.Add(deadRecovery.Warning);
        }

        if (moves.Count == 0)
        {
            return new Result([], "nothing changed on the board, no move to send");
        }

        MarkStrikeSquares(samples, moves);

        var confirmation = ConfirmAgainstController(samples, by, moves);
        if (confirmation is not null)
        {
            return new Result([], confirmation);
        }

        return new Result(moves, null, extra.Count == 0 ? null : string.Join("; ", extra.Distinct()));
    }

    private static bool HasDyingAttackerCandidate(List<Dictionary<int, Piece>> by, int localOwner,
                                                  List<Act> acts)
    {
        foreach (var id in by[0].Keys)
        {
            if (!by[^1].ContainsKey(id) && by[0][id].Owner == localOwner &&
                acts.All(a => a.ActorIdx != id))
            {
                return true;
            }
        }

        return false;
    }

    private static (string? Refusal, string? Warning) RecoverDyingAttacker(
        IReadOnlyList<BoardSnapshot> samples, List<Dictionary<int, Piece>> by, int localOwner,
        HashSet<int> actorIds, Dictionary<int, (int X, int Y)> committed, List<Move> moves)
    {
        foreach (var id in by[0].Keys)
        {
            if (by[^1].ContainsKey(id) || by[0][id].Owner != localOwner || actorIds.Contains(id))
            {
                continue;
            }

            var lastAlive = -1;
            for (var i = by.Count - 1; i >= 0; i--)
            {
                if (by[i].ContainsKey(id))
                {
                    lastAlive = i;
                    break;
                }
            }

            if (lastAlive < 0)
            {
                continue;
            }

            var dead = by[lastAlive][id];

            ActRecord? orphan = null;
            for (var i = lastAlive; i < samples.Count; i++)
            {
                if (samples[i].Act is { On: false, Unit: -1 } r &&
                    ((r.Fx == dead.X && r.Fy == dead.Y) || (r.Tx == dead.X && r.Ty == dead.Y)))
                {
                    orphan = r;
                    break;
                }
            }

            if (orphan is not { } rec)
            {
                continue;
            }

            var isDive = rec.Fx == dead.X && rec.Fy == dead.Y && (rec.Tx != rec.Fx || rec.Ty != rec.Fy);
            var landing = isDive ? (X: rec.Fx, Y: rec.Fy) : (X: rec.Tx, Y: rec.Ty);
            var firing = isDive ? (X: rec.Tx, Y: rec.Ty) : landing;

            var arrival = 0;
            for (var i = lastAlive; i >= 1; i--)
            {
                if (by[i - 1].TryGetValue(id, out var earlier) && (earlier.X != dead.X || earlier.Y != dead.Y))
                {
                    arrival = i;
                    break;
                }
            }

            var victims = new List<Piece>();
            foreach (var (vid, atArrival) in by[arrival])
            {
                if (atArrival.Owner == localOwner)
                {
                    continue;
                }

                var vLast = -1;
                for (var i = by.Count - 1; i >= arrival; i--)
                {
                    if (by[i].ContainsKey(vid))
                    {
                        vLast = i;
                        break;
                    }
                }

                var lostHealth = vLast >= arrival && by[vLast][vid].Health < atArrival.Health;
                var vanished = vLast < by.Count - 1;
                if (lostHealth || vanished)
                {
                    victims.Add(atArrival);
                }
            }

            if (victims.Count != 1)
            {
                return ($"a machine of ours died at ({dead.X},{dead.Y}) with a committed attack record and " +
                        $"{victims.Count} enemy candidates for its victim. One machine's death cannot be " +
                        "read apart from the rest, and sending a guess would diverge the boards.", null);
            }

            var victim = victims[0];

            if (isDive && Math.Max(Math.Abs(landing.X - victim.X), Math.Abs(landing.Y - victim.Y)) > 1)
            {
                isDive = false;
                firing = landing;
            }

            if (!DirectionTo(new Piece(firing.X, firing.Y, 1, 0, localOwner), victim, out var facing) ||
                !InStrikeLine(new Piece(firing.X, firing.Y, 1, facing, localOwner), victim))
            {
                return ($"a machine of ours died at ({dead.X},{dead.Y}) after an attack recorded from " +
                        $"({firing.X},{firing.Y}), but the enemy that lost health at ({victim.X},{victim.Y}) " +
                        "is not on the strike line from there. Refusing rather than sending a guess.", null);
            }

            var src = committed.TryGetValue(id, out var c) ? c : (X: by[0][id].X, Y: by[0][id].Y);
            moves.Add(new Move
            {
                SrcX = src.X, SrcY = src.Y,
                DstX = landing.X, DstY = landing.Y,
                TargetX = victim.X, TargetY = victim.Y,
                Facing = facing, Attack = true,
                AtkX = isDive ? firing.X : -1, AtkY = isDive ? firing.Y : -1,
            });

            return (null, $"a machine of ours at ({dead.X},{dead.Y}) died in its own attack; the activation " +
                          "was reconstructed from the controller's orphan record");
        }

        return (null, null);
    }

    private static void MarkStrikeSquares(IReadOnlyList<BoardSnapshot> samples, List<Move> moves)
    {
        foreach (var s in samples)
        {
            if (s.Act is not { On: false } act || (act.Fx == act.Tx && act.Fy == act.Ty))
            {
                continue;
            }

            var actor = s.Pieces.FirstOrDefault(p => p.Idx >= 0 && p.Idx == act.Unit,
                                                new Piece(-1, -1, 0, 0, -1));

            if (actor.Owner < 0 || actor.X != act.Fx || actor.Y != act.Fy)
            {
                continue;
            }

            var target = moves.FirstOrDefault(m => m.Attack && m.AtkX < 0 &&
                                                   m.DstX == act.Fx && m.DstY == act.Fy &&
                                                   (m.SrcX != m.DstX || m.SrcY != m.DstY));
            if (target is null)
            {
                continue;
            }

            var offVictim = Math.Max(Math.Abs(act.Fx - target.TargetX), Math.Abs(act.Fy - target.TargetY));
            var firingSquare = new Piece(act.Tx, act.Ty, 1, target.Facing, 0)
            {
                Range = actor.Range, Uuid = actor.Uuid,
            };
            var victim = new Piece(target.TargetX, target.TargetY, 1, 0, 1);
            if (offVictim <= 1 && (InStrikeLine(firingSquare, victim) || InDiveArc(firingSquare, victim)))
            {
                target.AtkX = act.Tx;
                target.AtkY = act.Ty;
            }
        }
    }

    private static (int X, int Y)? ControllerMoveTo(IReadOnlyList<BoardSnapshot> samples,
                                                    List<Dictionary<int, Piece>> by, int actorIdx,
                                                    int endX, int endY)
    {
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.Act is not { On: false } act || !RecordNamesActor(by, i, act, actorIdx) ||
                (act.Fx == act.Tx && act.Fy == act.Ty) || act.Tx != endX || act.Ty != endY)
            {
                continue;
            }

            var actor = by[i][actorIdx];
            if (actor.X != act.Tx || actor.Y != act.Ty)
            {
                continue;
            }

            return (act.Fx, act.Fy);
        }

        return null;
    }

    private static bool RecordNamesActor(List<Dictionary<int, Piece>> by, int i, ActRecord act, int actorIdx)
    {
        return i < by.Count && by[i].TryGetValue(actorIdx, out var p) && p.Idx == act.Unit;
    }

    private static ActRecord? OrphanRecordAt(IReadOnlyList<BoardSnapshot> samples, int i, int x, int y)
    {
        for (var k = i; k <= i + 1 && k < samples.Count; k++)
        {
            if (samples[k].Act is { On: false, Unit: -1 } r && r.Fx == x && r.Fy == y)
            {
                return r;
            }
        }

        return null;
    }

    private static int RecordedActor(List<Dictionary<int, Piece>> by, int i, ActRecord act)
    {
        if (i >= by.Count)
        {
            return -1;
        }

        foreach (var (id, p) in by[i])
        {
            if (p.Idx == act.Unit)
            {
                return id;
            }
        }

        return -1;
    }

    private static string? ConfirmAgainstController(IReadOnlyList<BoardSnapshot> samples,
                                                    List<Dictionary<int, Piece>> by, List<Move> moves)
    {
        var disagreements = new List<string>();

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.Act is not { On: false } act || (act.Fx == act.Tx && act.Fy == act.Ty))
            {
                continue;
            }

            var recorded = RecordedActor(by, i, act);
            if (recorded < 0)
            {
                continue;
            }

            var actor = by[i][recorded];
            if (actor.X != act.Tx || actor.Y != act.Ty)
            {
                continue;
            }

            var beganOnTo = by[0].TryGetValue(recorded, out var began) &&
                            began.X == act.Tx && began.Y == act.Ty;
            var actedWithoutMoving = moves.Any(m => m.SrcX == act.Tx && m.SrcY == act.Ty &&
                                                    m.DstX == act.Tx && m.DstY == act.Ty);
            if (beganOnTo && actedWithoutMoving)
            {
                continue;
            }

            var matched = moves.Any(m => m.SrcX == act.Fx && m.SrcY == act.Fy &&
                                         m.DstX == act.Tx && m.DstY == act.Ty);
            if (!matched)
            {
                disagreements.Add($"({act.Fx},{act.Fy})->({act.Tx},{act.Ty})");
            }
        }

        if (disagreements.Count == 0)
        {
            return null;
        }

        return "the game's own controller recorded " + string.Join(", ", disagreements.Distinct()) +
               ", which this reading does not contain. One of the two is wrong about what was played, " +
               "and the controller is the game's own bookkeeping rather than an inference from the board. " +
               "Send it by hand with `move`/`attack` if you can see what was played, and keep the " +
               "capture: this is the check that exists because a wrong Src has twice been sent " +
               "confidently.";
    }

    private static byte SettledFacing(List<Dictionary<int, Piece>> by, int from, int until,
                                      int idx, int x, int y, byte fallback)
    {
        var facing = (byte)(fallback & 3);

        for (var i = from; i <= until && i < by.Count; i++)
        {
            if (by[i].TryGetValue(idx, out var p) && p.X == x && p.Y == y)
            {
                facing = (byte)(p.Facing & 3);
            }
        }

        return facing;
    }

    private sealed class Act
    {
        public int ActorIdx;
        public int Sample;
        public int End;
        public bool IsBurst;
        public int VictimIdx = -1;
        public int StrikeX = -1, StrikeY = -1;
        public byte StrikeFacing;
        public int TargetX = -1, TargetY = -1;

        public int ToX = -1, ToY = -1;
    }

    private const int SpillSkill = 15;

    private static Dictionary<int, int> PredictSpray(Dictionary<int, Piece> at)
    {
        var owed = new Dictionary<int, int>();

        foreach (var (carrierIdx, carrier) in at)
        {
            if (carrier.Skill != SpillSkill || carrier.Range < 0 || carrier.Health <= 0)
            {
                continue;
            }

            foreach (var (victimIdx, victim) in at)
            {
                if (victimIdx == carrierIdx || victim.Health <= 0)
                {
                    continue;
                }

                var steps = Math.Abs(victim.X - carrier.X) + Math.Abs(victim.Y - carrier.Y);
                if (steps > carrier.Range)
                {
                    continue;
                }

                owed[victimIdx] = owed.GetValueOrDefault(victimIdx) + 1;
            }
        }

        return owed;
    }

    private static string? LastAttributed;

    private static Act? FoldedSecondBlow;

    private static string? InferredNote;

    private static Act? DyingOverchargeBlow(IReadOnlyList<BoardSnapshot> samples, List<Dictionary<int, Piece>> by,
                                            List<Act> acts, Act a, int at, int victimIdx, Piece victim)
    {
        var deadByEnd = !by[^1].TryGetValue(a.ActorIdx, out var atEnd) || atEnd.Health == 0;
        if (!deadByEnd || acts.Any(x => x.ActorIdx == a.ActorIdx && x.IsBurst))
        {
            return null;
        }

        Piece actor = default;
        var standing = false;
        foreach (var when in (int[])[at, at - 1])
        {
            if (by[when].TryGetValue(a.ActorIdx, out actor) && InStrikeLine(actor, victim))
            {
                standing = true;
                break;
            }
        }

        if (!standing)
        {
            return null;
        }

        var secondRecord = false;
        for (var k = a.Sample + 1; k <= at && k < samples.Count; k++)
        {
            if (samples[k].Act is { On: true } r && RecordNamesActor(by, k, r, a.ActorIdx) &&
                r.Fx == actor.X && r.Fy == actor.Y)
            {
                secondRecord = true;
                break;
            }
        }

        if (!secondRecord)
        {
            return null;
        }

        return new Act
        {
            ActorIdx = a.ActorIdx, Sample = at, End = at, IsBurst = true,
            VictimIdx = victimIdx,
            TargetX = victim.X, TargetY = victim.Y,
            StrikeX = actor.X, StrikeY = actor.Y,
            StrikeFacing = (byte)(actor.Facing & 3),
        };
    }

    private static string? Attribute(IReadOnlyList<BoardSnapshot> samples, List<Act> acts,
                                     List<Dictionary<int, Piece>> by, int at, int victimIdx, Piece victim)
    {
        LastAttributed = null;

        var enclosing = -1;
        for (var j = 0; j < acts.Count; j++)
        {
            if (acts[j].Sample >= at)
            {
                enclosing = j;
                break;
            }
        }

        var candidates = new List<int>();
        if (enclosing >= 0)
        {
            candidates.Add(enclosing);
        }

        if ((enclosing < 0 ? acts.Count : enclosing) - 1 >= 0)
        {
            candidates.Add((enclosing < 0 ? acts.Count : enclosing) - 1);
        }

        foreach (var j in candidates)
        {
            var a = acts[j];

            if (enclosing < 0 && a.VictimIdx < 0 && !a.IsBurst
                && DyingOverchargeBlow(samples, by, acts, a, at, victimIdx, victim) is { } blow)
            {
                acts.Add(blow);
                InferredNote = $"a drop on ({victim.X},{victim.Y}) after every tick, read as an Overcharge blow " +
                               $"from ({blow.StrikeX},{blow.StrikeY}) by a machine that died making it; " +
                               "its earlier action stays a move";
                return null;
            }

            if (a.VictimIdx == victimIdx)
            {
                if (enclosing < 0 && FoldedSecondBlow is null)
                {
                    var asBurst = !acts.Any(x => x.ActorIdx == a.ActorIdx && x.IsBurst);
                    var actsSpent = acts.Count(x => !x.IsBurst);

                    var ourOwner = -1;
                    if (by[at].TryGetValue(a.ActorIdx, out var actorAtDrop))
                    {
                        ourOwner = actorAtDrop.Owner;
                    }
                    else if (by[at - 1].TryGetValue(a.ActorIdx, out var actorBeforeDrop))
                    {
                        ourOwner = actorBeforeDrop.Owner;
                    }

                    var ourCount = by[at].Values.Count(p => p.Owner == ourOwner);
                    var lone = ourCount == 1;

                    var chosen = -1;
                    if (asBurst || (lone && actsSpent < 2))
                    {
                        chosen = a.ActorIdx;
                    }
                    else if (actsSpent < 2)
                    {
                        var unused = new List<int>();
                        foreach (var (pidx, pc) in by[at])
                        {
                            if (pc.Owner != ourOwner || pidx == a.ActorIdx)
                            {
                                continue;
                            }

                            if (acts.Any(x => x.ActorIdx == pidx))
                            {
                                continue;
                            }

                            if (!InStrikeLine(pc, victim))
                            {
                                continue;
                            }

                            unused.Add(pidx);
                        }

                        if (unused.Count == 1)
                        {
                            chosen = unused[0];
                        }
                    }

                    if (chosen >= 0)
                    {
                        foreach (var when in (int[])[at, at - 1])
                        {
                            if (!by[when].TryGetValue(chosen, out var actor))
                            {
                                continue;
                            }

                            if (!InStrikeLine(actor, victim))
                            {
                                continue;
                            }

                            FoldedSecondBlow = new Act
                            {
                                ActorIdx = chosen, Sample = at, End = at, IsBurst = asBurst,
                                VictimIdx = victimIdx,
                                TargetX = victim.X, TargetY = victim.Y,
                                StrikeX = actor.X, StrikeY = actor.Y,
                                StrikeFacing = (byte)(actor.Facing & 3),
                            };
                            break;
                        }
                    }
                }

                return null;
            }

            foreach (var when in (int[])[at, at - 1])
            {
                if (!by[when].TryGetValue(a.ActorIdx, out var actor))
                {
                    continue;
                }

                var strike = (X: actor.X, Y: actor.Y);
                if (!InStrikeLine(actor, victim)
                    && !InSweepBand(actor, victim)
                    && !InDiveArc(actor, victim)
                    && !TryDashCharge(by, when, a.ActorIdx, actor, victim, out strike))
                {
                    continue;
                }

                if (a.VictimIdx >= 0)
                {
                    LastAttributed = $"more than one enemy lost health to one action; " +
                                     $"({victim.X},{victim.Y}) is treated as a consequence and not encoded";
                    return null;
                }

                a.VictimIdx = victimIdx;
                a.TargetX = victim.X; a.TargetY = victim.Y;
                a.StrikeX = strike.X; a.StrikeY = strike.Y;
                a.StrikeFacing = (byte)(actor.Facing & 3);
                return null;
            }
        }

        var stamped = at < samples.Count && samples[at].Stamp is { Length: > 0 } stamp ? $" at {stamp}" : "";
        var weighed = candidates.Count == 0
            ? "no action of ours had ticked yet"
            : string.Join("; ", candidates.Select(j => Weighed(acts[j], by, at)));
        return $"an enemy at ({victim.X},{victim.Y}) lost health{stamped} and no machine of ours had it on its facing line " +
               $"within range {MaxRange} at that moment (weighed: {weighed}). An unobserved ability reads exactly this way, and so does " +
               "a misread turn, refusing rather than guessing.";
    }

    private static string Weighed(Act a, List<Dictionary<int, Piece>> by, int at)
    {
        if (!by[at].TryGetValue(a.ActorIdx, out var actor)
            && (at < 1 || !by[at - 1].TryGetValue(a.ActorIdx, out actor)))
        {
            return $"slot {a.ActorIdx}, gone from the board";
        }

        var name = Machines.Find(actor.Uuid)?.Name ?? $"slot {a.ActorIdx}";
        var burst = a.IsBurst ? ", its Overcharge" : "";
        return $"{name} on ({actor.X},{actor.Y}) facing {Compass(actor.Facing)} range {actor.Range}{burst}";
    }

    private static string Compass(byte facing)
    {
        return (facing & 3) switch
        {
            0 => "N",
            1 => "E",
            2 => "S",
            _ => "W",
        };
    }

    private static Act? ClosingBlow(List<Dictionary<int, Piece>> by, int localOwner, MatchState? match)
    {
        var enemyLeft = by[^1].Values.Any(p => p.Owner != localOwner);
        if (enemyLeft && match?.Over != true)
        {
            return null;
        }

        var victims = by[0]
            .Where(kv => kv.Value.Owner != localOwner)
            .Select(kv => kv.Key)
            .Where(idx => !by[^1].TryGetValue(idx, out var atEnd) || atEnd.Health <= 0)
            .ToList();

        if (victims.Count != 1)
        {
            return null;
        }

        var victimIdx = victims[0];

        var at = -1;
        for (var i = 1; i < by.Count; i++)
        {
            if (!by[i].ContainsKey(victimIdx))
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            return null;
        }

        if (SprayExplains(by, at, victimIdx))
        {
            return null;
        }

        return UntrackedKiller(by, localOwner, at, victimIdx);
    }

    private static bool SprayExplains(List<Dictionary<int, Piece>> by, int at, int victimIdx)
    {
        var lastAlive = -1;
        for (var i = at - 1; i >= 0; i--)
        {
            if (by[i].TryGetValue(victimIdx, out var seen) && seen.Health > 0)
            {
                lastAlive = i;
                break;
            }
        }

        if (lastAlive < 0)
        {
            return false;
        }

        var sprayOwed = PredictSpray(by[lastAlive]).GetValueOrDefault(victimIdx);
        return by[lastAlive][victimIdx].Health <= sprayOwed;
    }

    private static Act? UntrackedKiller(List<Dictionary<int, Piece>> by, int localOwner, int at, int victimIdx)
    {
        if (at < 1 || !TryLastKnown(by, at, victimIdx, out var victim))
        {
            return null;
        }

        var actors = by[at - 1]
            .Where(kv => kv.Value.Owner == localOwner && InStrikeLine(kv.Value, victim))
            .ToList();
        if (actors.Count != 1)
        {
            return null;
        }

        var actor = actors[0].Value;
        return new Act
        {
            ActorIdx = actors[0].Key, Sample = at, End = at,
            VictimIdx = victimIdx,
            TargetX = victim.X, TargetY = victim.Y,
            StrikeX = actor.X, StrikeY = actor.Y,
            StrikeFacing = (byte)(actor.Facing & 3),
        };
    }

    private static bool TryLastKnown(List<Dictionary<int, Piece>> by, int from, int idx, out Piece piece)
    {
        for (var i = from; i >= 0; i--)
        {
            if (by[i].TryGetValue(idx, out piece))
            {
                return true;
            }
        }

        piece = default;
        return false;
    }

    private static List<(Piece Before, Piece After)> Order(List<(Piece Before, Piece After)> acted)
    {
        if (acted.Count < 2)
        {
            return acted;
        }

        var ordered = new List<(Piece Before, Piece After)>();
        var remaining = new List<(Piece Before, Piece After)>(acted);

        while (remaining.Count > 0)
        {
            var next = remaining.FindIndex(m =>
                !remaining.Any(o => (o.Before.X != m.Before.X || o.Before.Y != m.Before.Y)
                                    && o.Before.X == m.After.X && o.Before.Y == m.After.Y));

            if (next < 0)
            {
                ordered.AddRange(remaining);
                break;
            }
            ordered.Add(remaining[next]);
            remaining.RemoveAt(next);
        }

        return ordered;
    }

    private const int MaxRange = 3;
    private const int PushReach = MaxRange + 1;

    private static (int Dx, int Dy) Step(byte facing)
    {
        return (facing & 3) switch
        {
            0 => (0, -1),
            1 => (1, 0),
            2 => (0, 1),
            _ => (-1, 0),
        };
    }

    private static bool InSweepBand(Piece from, Piece to)
    {
        if (from.Range < 1 || Machines.Find(from.Uuid) is not { Ability: "Spread" })
        {
            return false;
        }

        var (dx, dy) = Step(from.Facing);
        var aimX = from.X + (dx * from.Range);
        var aimY = from.Y + (dy * from.Range);

        if (dx == 0)
        {
            return to.Y == aimY && Math.Abs(to.X - aimX) <= 1;
        }

        return to.X == aimX && Math.Abs(to.Y - aimY) <= 1;
    }

    internal static bool InDiveArc(Piece from, Piece to)
    {
        if (from.Range < 1 || Machines.Find(from.Uuid) is not { Pattern: "Dive", Ability: "Spread" })
        {
            return false;
        }

        var (dx, dy) = Step(from.Facing);
        var along = dx == 0 ? (to.Y - from.Y) * dy : (to.X - from.X) * dx;
        var aside = dx == 0 ? Math.Abs(to.X - from.X) : Math.Abs(to.Y - from.Y);
        return along >= 1 && along <= from.Range && aside <= 1;
    }

    internal static bool InDashPath(int endX, int endY, byte facing, int range, bool spread,
                                    int victimX, int victimY)
    {
        var (dx, dy) = Step(facing);
        for (var k = 1; k < range; k++)
        {
            var pathX = endX - (dx * k);
            var pathY = endY - (dy * k);
            if (victimX == pathX && victimY == pathY)
            {
                return true;
            }

            if (!spread)
            {
                continue;
            }

            var along = dx == 0 ? victimY == pathY : victimX == pathX;
            var aside = dx == 0 ? Math.Abs(victimX - pathX) : Math.Abs(victimY - pathY);
            if (along && aside == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDashCharge(List<Dictionary<int, Piece>> by, int at, int actorIdx,
                                      Piece actor, Piece victim, out (int X, int Y) from)
    {
        from = default;
        if (actor.Range < 2 || Machines.Find(actor.Uuid) is not { Pattern: "Dash" } machine)
        {
            return false;
        }

        if (!InDashPath(actor.X, actor.Y, actor.Facing, actor.Range, machine.Ability == "Spread",
                        victim.X, victim.Y))
        {
            return false;
        }

        var (dx, dy) = Step(actor.Facing);
        var start = (X: actor.X - (dx * actor.Range), Y: actor.Y - (dy * actor.Range));

        var arrived = at;
        while (arrived - 1 >= 0
               && by[arrived - 1].TryGetValue(actorIdx, out var held)
               && held.X == actor.X && held.Y == actor.Y)
        {
            arrived--;
        }

        if (arrived - 1 < 0
            || !by[arrived - 1].TryGetValue(actorIdx, out var launched)
            || launched.X != start.X || launched.Y != start.Y)
        {
            return false;
        }

        from = start;
        return true;
    }

    internal static bool CarriedByItsHit(Piece attacker)
    {
        if (string.IsNullOrEmpty(attacker.Uuid))
        {
            return true;
        }
        var machine = Machines.Find(attacker.Uuid);
        if (machine is null)
        {
            return true;
        }
        return machine.Pattern == "Ram";
    }

    private static bool FinishedOnItsCharge(Act a, Piece end)
    {
        if (a.VictimIdx < 0 || end.Range < 2 || Machines.Find(end.Uuid) is not { Pattern: "Dash" })
        {
            return false;
        }

        var (dx, dy) = Step(a.StrikeFacing);
        return end.X == a.StrikeX + (dx * end.Range) && end.Y == a.StrikeY + (dy * end.Range);
    }

    internal static bool InStrikeLine(Piece from, Piece to, int max = MaxRange)
    {
        var d = (from.Facing & 3) switch
        {
            0 => to.X == from.X ? from.Y - to.Y : -1,
            1 => to.Y == from.Y ? to.X - from.X : -1,
            2 => to.X == from.X ? to.Y - from.Y : -1,
            _ => to.Y == from.Y ? from.X - to.X : -1,
        };

        return d >= 1 && d <= max;
    }

    private static bool DirectionTo(Piece from, Piece to, out byte facing)
    {
        facing = 0;
        if (to.X == from.X && to.Y < from.Y && from.Y - to.Y <= MaxRange)
        {
            facing = 0;
            return true;
        }
        if (to.Y == from.Y && to.X > from.X && to.X - from.X <= MaxRange)
        {
            facing = 1;
            return true;
        }
        if (to.X == from.X && to.Y > from.Y && to.Y - from.Y <= MaxRange)
        {
            facing = 2;
            return true;
        }
        if (to.Y == from.Y && to.X < from.X && from.X - to.X <= MaxRange)
        {
            facing = 3;
            return true;
        }
        return false;
    }
}
