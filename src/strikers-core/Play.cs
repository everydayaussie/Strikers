using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Strikers.Core;

public static class Play
{
    public static readonly Stage[] PagedStages =
    [
        Stage.Start, Stage.HostChoose, Stage.HostInvite, Stage.JoinPaste, Stage.JoinWait,
        Stage.ChooseArmy, Stage.Safety, Stage.SetUp, Stage.Playing,
    ];

    public const int MaxInviteLength = 256;

    public static (string? Address, string? RoomId, string? Code) ParseInvite(string text)
    {
        var cleaned = (text ?? "").Replace("--server", " ").Replace("--join", " ")
                                  .Replace("--room-id", " ").Replace("--room", " ").Trim();
        var address = Regex.Match(cleaned, @"[A-Za-z0-9.\-]+:\d{2,5}");
        if (!address.Success)
        {
            return (null, null, null);
        }

        var rest = cleaned.Remove(address.Index, address.Length);
        var tokens = Regex.Matches(rest, @"[A-Za-z0-9]{4,}").Select(m => m.Value).ToList();

        var roomId = tokens.FirstOrDefault(t => t.Length >= 16);
        var shortTokens = tokens.Where(t => t.Length is >= 4 and <= 12).ToList();
        var code = shortTokens.FirstOrDefault(t => t.Any(char.IsDigit))
                   ?? shortTokens.FirstOrDefault(t => t.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                   ?? shortTokens.FirstOrDefault();

        return (address.Value, roomId, code?.ToUpperInvariant());
    }

    public static string[] LobbyArgs(bool hosting, string code, string roomId, string server,
                                     string name, string challenge, IReadOnlyList<int>? board,
                                     int victoryPoints, int draftPoints, string first,
                                     int boardWidth = StrikeBoard.Size,
                                     int boardHeight = StrikeBoard.Size,
                                     int placementRows = -1)
    {
        var args = new List<string>
        {
            "--lobby", hosting ? "--room" : "--join", code,
            "--room-id", roomId,
            "--server", server,
            "--yes",
            "--interactive-placement",
        };

        var cleaned = Settings.CleanName(name);
        if (cleaned.Length > 0)
        {
            args.Add("--name");
            args.Add(cleaned);
        }

        if (!hosting)
        {
            return [.. args];
        }

        args.Add("--challenge");
        args.Add(challenge);

        if (boardWidth != StrikeBoard.Size || boardHeight != StrikeBoard.Size)
        {
            args.Add("--board-width");
            args.Add(boardWidth.ToString());
            args.Add("--board-height");
            args.Add(boardHeight.ToString());
        }

        if (placementRows > 0)
        {
            args.Add("--placement-rows");
            args.Add(placementRows.ToString());
        }

        if (board is { Count: > 0 })
        {
            args.Add("--board");
            foreach (var t in board)
            {
                args.Add(t.ToString());
            }
        }

        if (victoryPoints > 0)
        {
            args.Add("--victory-points");
            args.Add(victoryPoints.ToString());
        }

        if (draftPoints > 0)
        {
            args.Add("--draft-points");
            args.Add(draftPoints.ToString());
        }

        args.Add("--first");
        args.Add(first);

        return [.. args];
    }

    public const string FirstHost = "host";
    public const string FirstJoiner = "joiner";

    public static string FirstFromPick(int pick, bool coin)
    {
        return pick switch
        {
            1 => FirstJoiner,
            2 => coin ? FirstJoiner : FirstHost,
            _ => FirstHost,
        };
    }

    public static string[] PlayArgs(bool hosting, string code, string roomId, string server,
                                    string name, IReadOnlyList<string> army, string? binding, string first)
    {
        var args = new List<string>
        {
            "--play",
            "--yes",
            "--server", server,
            hosting ? "--room" : "--join", code,
            "--room-id", roomId,
        };

        var cleaned = Settings.CleanName(name);
        if (cleaned.Length > 0)
        {
            args.Add("--name");
            args.Add(cleaned);
        }

        if (binding is not null)
        {
            args.Add("--binding");
            args.Add(binding);
        }

        args.Add("--first");
        args.Add(first);

        if (army.Count > 0)
        {
            args.Add("--army");
            args.AddRange(army);
        }

        return [.. args];
    }

    public static string? BindingProblem(string? binding)
    {
        if (binding is { Length: 64 } && binding.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "Press Stop on both PCs and start again. Both players then compare the safety code once more.";
    }

    private static readonly Regex WriteDoneLine = new(@"^\s*setup written\s*$", RegexOptions.Compiled);

    private static readonly Regex WriteFailedLine = new(
        @"^\s*(not both ready yet, run status"
        + @"|no challenge agreed"
        + @"|their setup is incomplete, refusing to write a partial army"
        + @"|your own setup is incomplete, run army and place first"
        + @"|the game is still in a match, or on its victory screen\. Press Continue in the game, stand on the challenge list, then Set up the match again\. Nothing was written\."
        + @"|live-probe exited -?\d+\. Stopping, the later steps did not run, so the setup is incomplete\. Do not start the match\.)\s*$",
        RegexOptions.Compiled);

    public static (string Step, string Detail) MatchOverText()
    {
        return ("Match over",
                "Press Continue in the game. To play again, press Stop here and set the match up again.");
    }

    public static (string Step, string Detail) UnsharedMatchText()
    {
        return ("This match is not shared with your opponent",
                "Press Continue in the game and set the match up again.");
    }

    public static bool WriteSucceeded(string line)
    {
        return WriteDoneLine.IsMatch(line);
    }

    public static bool WriteFailed(string line)
    {
        return WriteFailedLine.IsMatch(line);
    }

    public enum UnlockOutcome
    {
        Unreachable,
        AlreadyClear,
        Cleared,
    }

    public static UnlockOutcome ReadUnlock(string? output)
    {
        if (output is null)
        {
            return UnlockOutcome.Unreachable;
        }

        if (output.Contains("nothing to do", StringComparison.Ordinal))
        {
            return UnlockOutcome.AlreadyClear;
        }

        if (output.Contains("cleared", StringComparison.Ordinal))
        {
            return UnlockOutcome.Cleared;
        }

        return UnlockOutcome.Unreachable;
    }

    private static readonly Regex ReadyLine = new(
        @"^\s*both players are in the match \(peer role: play\)\s*$", RegexOptions.Compiled);

    private static readonly Regex StillSetupLine = new(
        @"^\s*waiting: the other player is still in setup \(peer role: lobby\)\s*$", RegexOptions.Compiled);

    private static readonly Regex MovedOnLine = new(
        @"^\s*<- the other player is in the match \(peer role: play\)\s*$",
        RegexOptions.Compiled);

    public static bool BothPlayersReady(string line)
    {
        return ReadyLine.IsMatch(line);
    }

    public static bool PeerStillInSetup(string line)
    {
        return StillSetupLine.IsMatch(line);
    }

    public static bool PeerMovedToPlay(string line)
    {
        return MovedOnLine.IsMatch(line);
    }

    public static string? Fingerprint(string line)
    {
        var m = Regex.Match(line, @"^\s*encrypted, fingerprint ([0-9A-F]{8,64})\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? SessionBinding(string line)
    {
        var m = Regex.Match(line, @"^\s*session bound ([0-9A-F]{64})\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex TheirSetupLine = new(@"^\s*<- their setup: \d+ machine\(s\)", RegexOptions.Compiled);

    public static (string Challenge, int VictoryPoints, int DraftPoints, string First)? TheirSetup(string line)
    {
        if (!TheirSetupLine.IsMatch(line))
        {
            return null;
        }

        var challenge = Regex.Match(line, @"challenge\s+([0-9A-Fa-f]{32})");
        if (!challenge.Success)
        {
            return null;
        }

        var victory = -1;
        var draft = -1;
        var rules = Regex.Match(line, @"rules\s+(-?\d+)/(-?\d+)");
        if (rules.Success)
        {
            var style = System.Globalization.NumberStyles.AllowLeadingSign;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (!int.TryParse(rules.Groups[1].Value, style, culture, out victory)
                || !int.TryParse(rules.Groups[2].Value, style, culture, out draft))
            {
                return null;
            }
        }

        var first = FirstHost;
        var firstClause = Regex.Match(line, @",\s*first\b(.*)$");
        if (firstClause.Success)
        {
            var word = firstClause.Groups[1].Value.Trim();
            if (word is not (FirstHost or FirstJoiner))
            {
                return null;
            }

            first = word;
        }

        return (challenge.Groups[1].Value.ToUpperInvariant(), victory, draft, first);
    }

    private static readonly Regex TheirArmyLine =
        new(@"^\s*<- their army:((?: [0-9A-F]{32}){1,16})\s*$", RegexOptions.Compiled);

    public static List<string>? TheirArmy(string line)
    {
        var m = TheirArmyLine.Match(line);
        if (!m.Success)
        {
            return null;
        }

        return [.. m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    public const string OpponentArmyEntry = "Opponent's army";

    public sealed class SafetyCode
    {
        private string _code = "";

        public string Code
        {
            get
            {
                return _code;
            }
        }

        public bool Take(string derived)
        {
            if (_code.Length > 0 || derived.Length == 0)
            {
                return false;
            }

            _code = derived;
            return true;
        }

        public void Reset()
        {
            _code = "";
        }
    }

    public static (string Text, bool Warning, string Note, string Press) SafetyScreenState(string fingerprint)
    {
        if (fingerprint.Length == 0)
        {
            return ("not shown", true,
                    "No safety code appeared, so this link is not verified. Continue only if you accept that.",
                    "Continue unverified");
        }

        return (fingerprint, false, "", "Continue");
    }

    public static (bool Continue, bool Good, string Press) SafetyTypedState(string code, string typed, bool hosting)
    {
        if (!IsCode(code))
        {
            return (false, false, "Continue");
        }

        var wanted = SafetyTyped(code, hosting);
        if (wanted.Length < 4)
        {
            return (false, false, "Continue");
        }

        var given = (typed ?? "").Trim().ToUpperInvariant();
        if (given.Length < 4)
        {
            return (false, false, "Continue");
        }

        if (given == wanted)
        {
            return (true, true, "Codes match, continue");
        }

        return (false, false, "Codes differ");
    }

    public const int OpponentLeftAfter = 8;

    public static bool ScreenMayChange(bool halted, bool startIdle)
    {
        return !halted && !startIdle;
    }

    public enum CodeEvent
    {
        NewAttempt,

        PeerStartedOver,

        OpponentLeft,
    }

    public static bool CodeIsCleared(CodeEvent e)
    {
        return e is CodeEvent.NewAttempt or CodeEvent.PeerStartedOver;
    }

    public static (string Headline, string Detail) LeftText(bool leftHere)
    {
        if (leftHere)
        {
            return ("Stopped: you left the match",
                    "The match ended on this PC before it was over, so Strikers stopped it on both PCs.");
        }

        return ("Stopped: your opponent left the match",
                "Their match ended before it was over, so Strikers stopped it on both PCs.");
    }

    public static (string Headline, string Detail) HaltText(bool disagreement)
    {
        if (disagreement)
        {
            return ("Stopped: the two games disagree",
                    "The two boards no longer match, so Strikers stopped the match on both PCs.");
        }

        return ("Stopped", "Strikers could not follow the last turn, so it stopped the match on both PCs.");
    }

    public static (string Headline, string Detail) InactiveText()
    {
        return ("This test build is no longer active", "Ask the person who gave it to you for a new one.");
    }

    public const string HaltStatus = "Halted. Press Stop, then start a new match.";

    public static bool ShowsVersionScreen(bool differentVersions, bool playStarted)
    {
        return differentVersions && !playStarted;
    }

    public static bool ShowsOtherVersionNotice(bool hosting, bool playStarted)
    {
        return hosting && !playStarted;
    }

    public static bool IsCode(string? code)
    {
        var s = (code ?? "").Trim();
        return s.Length >= 8 && s.All(Uri.IsHexDigit);
    }

    public static int SafetyTypedGroup(bool hosting, int groups)
    {
        return hosting ? 0 : Math.Max(0, groups - 1);
    }

    public static int SafetyReadOutGroup(bool hosting, int groups)
    {
        return hosting ? Math.Max(0, groups - 1) : 0;
    }

    public static string SafetyTyped(string code, bool hosting)
    {
        var groups = SafetyGroups(code);
        return groups.Count == 0 ? "" : groups[SafetyTypedGroup(hosting, groups.Count)];
    }

    public static string SafetyReadOut(string code, bool hosting)
    {
        var groups = SafetyGroups(code);
        return groups.Count == 0 ? "" : groups[SafetyReadOutGroup(hosting, groups.Count)];
    }

    public static string SafetyPlaceholder(bool hosting)
    {
        return hosting ? "Their first four" : "Their last four";
    }

    public static List<string> SafetyGroups(string? code)
    {
        var clean = new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var groups = new List<string>();
        for (var i = 0; i < clean.Length; i += 4)
        {
            groups.Add(clean.Substring(i, Math.Min(4, clean.Length - i)));
        }

        return groups;
    }

    public static string MatchLabel(ChallengeBridge.Challenge c)
    {
        return c.Slots > 0
            ? $"{c.Name}  ({c.Slots} machines each)"
            : $"{c.Name}  (build your own army)";
    }

    public static int ArmyCost(IReadOnlyList<string> army, IReadOnlyList<MachineBridge.Machine> roster)
    {
        var cost = 0;
        foreach (var uuid in army)
        {
            var m = roster.FirstOrDefault(r => r.Uuid == uuid);
            if (m is not null)
            {
                cost += m.Cost;
            }
        }

        return cost;
    }

    public static string DescribeArmy(IReadOnlyList<string> army,
                                      IReadOnlyList<MachineBridge.Machine> roster, int budget)
    {
        if (army.Count == 0)
        {
            return "No army chosen.";
        }

        var lines = new List<string>();
        var cost = 0;
        foreach (var uuid in army)
        {
            var m = roster.FirstOrDefault(r => r.Uuid == uuid);
            if (m is null)
            {
                lines.Add($"  {uuid}  (not a machine this save has)");
                continue;
            }

            cost += m.Cost;
            lines.Add($"  {m.Name,-22} cost {m.Cost}  hp {m.Health}  move {m.Move}  "
                    + $"range {m.Range}  power {m.Power}");
        }

        lines.Add(cost > budget
            ? $"  {"TOTAL",-22} cost {cost}, which is over the {budget} you have"
            : $"  {"TOTAL",-22} cost {cost} of {budget}");

        return string.Join('\n', lines);
    }

    public static string WithoutPath(string message, string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var cleaned = message.Replace(path, name);
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            cleaned = cleaned.Replace(folder, "");
        }

        return cleaned;
    }

    public static void WriteWhole(string path, string text)
    {
        var fresh = path + ".new";
        try
        {
            File.WriteAllText(fresh, text);
            File.Move(fresh, path, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.Delete(fresh);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    public static string UnreadableFile(string name)
    {
        return $"Strikers could not read {name}, so nothing was saved over it. Close anything that has it " +
               "open, or move it out of the Strikers folder, then try again.";
    }

    public static (string Headline, string Detail)? MatchListNote(int count, bool attemptUnderWay)
    {
        if (count > 0 || attemptUnderWay)
        {
            return null;
        }

        return ("Cannot reach netplay", "netplay.exe did not answer. It should sit beside Strikers.exe.");
    }

    public static List<string> ArmyIds(IEnumerable<string> army)
    {
        return [.. army.Where(u => u.Length == 32 && u.All(Uri.IsHexDigit))
                       .Select(u => u.ToUpperInvariant())];
    }

    public static async Task<Exception?> Caught(Func<Task> work)
    {
        try
        {
            await work();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    public const string BoardListChanged =
        "The board list changed since it was picked. Pick the board again.";

    public static (StrikeBoard? Board, string? Problem) PickedBoard(
        int index, string? shown, IReadOnlyList<StrikeBoard> saved)
    {
        if (index <= 0)
        {
            return (null, null);
        }

        if (index - 1 >= saved.Count)
        {
            return (null, BoardListChanged);
        }

        var found = saved[index - 1];
        if (!string.Equals(found.Name, shown, StringComparison.Ordinal))
        {
            return (null, BoardListChanged);
        }

        return (found, null);
    }

    public static List<int>? PlayableBoard(IReadOnlyList<int>? cells, int width = StrikeBoard.Size,
                                           int height = StrikeBoard.Size)
    {
        if (width is < 1 or > StrikeBoard.MaxSide || height is < 1 or > StrikeBoard.MaxSide)
        {
            return null;
        }

        if (cells is null || cells.Count != width * height)
        {
            return null;
        }

        if (cells.Any(t => t is < Terrain.Chasm or > Terrain.Mountains))
        {
            return null;
        }

        return [.. cells];
    }

    public static string NewRoomCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return RandomToken(alphabet, 6);
    }

    public static string NewRoomId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    }

    public const int FrostSide = 1600;

    public static double FrostScale(int width, int height)
    {
        var longest = Math.Max(1, Math.Max(width, height));
        return Math.Min(1.0, (double)FrostSide / longest);
    }

    private static string RandomToken(string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    public const string StockBoard = "Default";

    public const string NoArmyToStart =
        "Build an army in the Armies panel first: a match needs one to send.";

    public static readonly TimeSpan SetupWindow = TimeSpan.FromSeconds(600);

    public const int SlowAfter = 20;

    public const int WriteSlowAfter = 90;

    public static (string Step, string Detail) WaitingText(string headline, string waitingFor, bool technical,
                                                           int seconds, int slowAfter = SlowAfter)
    {
        var dots = new string('.', 1 + (seconds % 3));
        var step = headline.Length > 0 ? headline : $"Working{dots}";

        var slow = technical && seconds >= slowAfter
            ? "\nThis is slower than usual. " + WhereTheLogIs
            : "";

        return (step, waitingFor + slow);
    }

    public const string DoNotEnterYet = "Do not enter the challenge yet.";

    public const string OpponentAheadHeadline = "Your opponent is ready and waiting";

    public static string? OpponentAheadDetail(Stage stage)
    {
        return stage switch
        {
            Stage.Safety => "Type the four characters your opponent reads to you, then press Continue.",
            Stage.SetUp => $"Press Set up the match. {DoNotEnterYet}",
            _ => null,
        };
    }

    public const int LinkDownAfter = 3;

    public static (string Step, string Detail) LinkLost(bool everConnected)
    {
        if (!everConnected)
        {
            return ("Could not reach your opponent",
                    "The invite may be old, or their Strikers is closed. Press Stop, ask for a fresh invite, and join again.");
        }

        return ("Lost the connection",
                "Trying again. If this does not clear in a moment, press Stop and start again.");
    }

    public enum LinkDownVerdict
    {
        Nothing,
        OpponentStopped,
        LinkLost,
    }

    public static LinkDownVerdict JudgeLinkDown(int linkDowns, bool peerLeftPending, bool atPlayStage)
    {
        if (peerLeftPending && !atPlayStage)
        {
            return LinkDownVerdict.OpponentStopped;
        }

        if (linkDowns != LinkDownAfter)
        {
            return LinkDownVerdict.Nothing;
        }

        if (peerLeftPending)
        {
            return LinkDownVerdict.OpponentStopped;
        }

        return LinkDownVerdict.LinkLost;
    }

    public static readonly string WhereTheLogIs = $"{MatchLog.Name} beside Strikers.exe says why.";

    public const int MaxArmy = 9;

    public static (int Victory, int Draft) StockRules(int slots)
    {
        var victory = slots switch
        {
            2 => 2,
            4 => 6,
            _ => 7,
        };

        return (victory, 10);
    }

    public static string JoinWaitingFor(string address, string roomId, string code)
    {
        return "Connecting to your opponent.";
    }

    public static (string Text, bool Urgent) PlayClockText(TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
        {
            return ("Time ran out. Press Stop and start again.", true);
        }

        var text = $"{(int)left.TotalMinutes}m {left.Seconds:00}s to enter the challenge.";

        return (text, left.TotalSeconds < 120);
    }
}
