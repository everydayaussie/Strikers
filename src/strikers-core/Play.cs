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
        var address = Regex.Match(cleaned, @"[A-Za-z0-9.\-]+:([0-9]{2,5})(?![0-9])");
        if (!address.Success || PortNumber(address.Groups[1].Value) is null)
        {
            return (null, null, null);
        }

        var rest = cleaned.Remove(address.Index, address.Length);
        var tokens = Regex.Matches(rest, @"[A-Za-z0-9]{4,}").Select(m => m.Value).ToList();

        var roomId = tokens.FirstOrDefault(t => t.Length >= 16);
        var codes = tokens.Where(t => IsRoomCode(t.ToUpperInvariant())).ToList();
        var code = codes.FirstOrDefault(t => t.Any(char.IsDigit))
                   ?? codes.FirstOrDefault(t => t.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                   ?? codes.FirstOrDefault();

        return (address.Value, roomId, code?.ToUpperInvariant());
    }

    private const string RoomCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private const int RoomCodeLength = 6;

    private static bool IsRoomCode(string text)
    {
        return text.Length == RoomCodeLength && text.All(c => RoomCodeAlphabet.Contains(c));
    }

    private static int? PortNumber(string text)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        if (port is < 1 or > 65535)
        {
            return null;
        }

        return port;
    }

    public enum InvitePin
    {
        Accept,
        LookUp,
        Refuse,
    }

    public static InvitePin PinInvite(string address, IReadOnlyCollection<string>? tunnelAnswers)
    {
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || PortNumber(address[(colon + 1)..]) is null)
        {
            return InvitePin.Refuse;
        }

        var host = address[..colon];
        if (string.Equals(host, TunnelDns.DefaultServer, StringComparison.OrdinalIgnoreCase))
        {
            return InvitePin.Accept;
        }

        if (!System.Net.IPAddress.TryParse(host, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || parsed.ToString() != host
            || IsPrivateOrLocal(parsed))
        {
            return InvitePin.Refuse;
        }

        if (tunnelAnswers is null)
        {
            return InvitePin.LookUp;
        }

        return tunnelAnswers.Contains(host, StringComparer.Ordinal) ? InvitePin.Accept : InvitePin.Refuse;
    }

    private static bool IsPrivateOrLocal(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var first = bytes[0];
        var second = bytes[1];
        return first is 0 or 10 or 127 or >= 224
               || (first == 100 && second is >= 64 and <= 127)
               || (first == 169 && second == 254)
               || (first == 172 && second is >= 16 and <= 31)
               || (first == 192 && second == 168);
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

    public const string ArmyNameHexFlag = "--army-name-hex";

    public static string[] PlayArgs(bool hosting, string code, string roomId, string server,
                                    string name, IReadOnlyList<string> army, string? binding, string first,
                                    string? armyName = null)
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

        var shownName = Army.CleanName(armyName);
        if (shownName.Length > 0)
        {
            args.Add(ArmyNameHexFlag);
            args.Add(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(shownName)));
        }

        return [.. args];
    }

    public static string? BindingProblem(string? binding)
    {
        if (binding is { Length: 64 } && binding.All(Uri.IsHexDigit))
        {
            return null;
        }

        return "Press Stop on both PCs and start again.";
    }

    private static readonly Regex WriteDoneLine = new(@"^\s*setup written\s*$", RegexOptions.Compiled);

    private static readonly Regex WriteFailedLine = new(
        @"^\s*(not both ready yet, run status"
        + @"|no challenge agreed"
        + @"|their setup is incomplete, refusing to write a partial army"
        + @"|your own setup is incomplete, run army and place first"
        + @"|nothing was written: both players must confirm the same safety code first"
        + @"|the game is still in a match, or on its victory screen\. Press Continue in the game, stand on the challenge list, then Set up the match again\. Nothing was written\."
        + "|" + Regex.Escape(NetplayLine.NoChallengeListLine)
        + @"|live-probe exited -?\d+\. Stopping, the later steps did not run, so the setup is incomplete\. Do not start the match\.)\s*$",
        RegexOptions.Compiled);

    public static (string Step, string Detail) MatchOverText()
    {
        return ("Match over",
                "Press Continue in the game. To play again, press Stop and start a new match.");
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

    public static string WriteFailedText(string line)
    {
        var said = line.Trim();
        if (said.StartsWith("the game is still in a match", StringComparison.Ordinal))
        {
            return "The game is still in a match or on its victory screen. Press Continue in the game, go to the "
                   + "challenge list, then press Set up the match again.";
        }

        if (said.StartsWith("not both ready yet", StringComparison.Ordinal))
        {
            return "Your opponent is not ready yet. Press Set up the match again in a moment.";
        }

        if (said.StartsWith(NetplayLine.NoChallengeListLine, StringComparison.Ordinal))
        {
            return NoChallengeListText;
        }

        if (said.StartsWith("live-probe exited", StringComparison.Ordinal))
        {
            return $"{DoNotEnterYet} Press Stop and start again.";
        }

        return "Press Stop and start again.";
    }

    public const string NoChallengeListText =
        "Your game is not on the challenge list at Salma's Machine Strike table. "
        + "Open it, then press Set up the match again.";

    public const string PartClosedHeadline = "Part of Strikers closed on this PC";

    public enum UnlockOutcome
    {
        Unreachable,
        AlreadyClear,
        Cleared,
        GameUpdated,
    }

    public static UnlockOutcome ReadUnlock(string? output)
    {
        if (output is null)
        {
            return UnlockOutcome.Unreachable;
        }

        if (output.Split('\n').Any(line => NetplayLine.UnknownGameBuildSaid(line.TrimEnd('\r'))))
        {
            return UnlockOutcome.GameUpdated;
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

    public static string ArmyLabel(string name)
    {
        return name == OpponentArmyEntry ? name + " (saved)" : name;
    }

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

    public const string NoCodeHeadline = "No safety code appeared";

    public static (bool Warning, string Note) SafetyScreenState(string fingerprint)
    {
        if (!IsCode(fingerprint))
        {
            return (true, "This link cannot be checked, so the match cannot go on. Press Stop and start again.");
        }

        return (false, "");
    }

    public const int SafetyTypedLength = 4;

    public static string SafetyTypedClean(string? typed)
    {
        var clean = new string((typed ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return clean.Length > SafetyTypedLength ? clean[..SafetyTypedLength] : clean;
    }

    public static (bool Continue, bool Good, bool Complete) SafetyTypedState(string code, string typed, bool hosting)
    {
        if (!IsCode(code))
        {
            return (false, false, false);
        }

        var wanted = SafetyTyped(code, hosting);
        if (wanted.Length < SafetyTypedLength)
        {
            return (false, false, false);
        }

        var given = SafetyTypedClean(typed);
        if (given.Length < SafetyTypedLength)
        {
            return (false, false, false);
        }

        if (given == wanted)
        {
            return (true, true, true);
        }

        return (false, false, true);
    }

    public static string[] SafetyReadOutCells(string code, bool hosting, bool shown)
    {
        var readOut = IsCode(code) ? SafetyReadOut(code, hosting) : "";
        var cells = new string[SafetyTypedLength];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = shown && i < readOut.Length ? readOut[i].ToString() : "";
        }

        return cells;
    }

    public const int OpponentLeftAfter = 8;

    public const int OpponentLeftAtPlayAfter = 30;

    public static int OpponentLeftLimit(bool waitsInPlace)
    {
        return waitsInPlace ? OpponentLeftAtPlayAfter : OpponentLeftAfter;
    }

    public static bool BothConfirmedAtSetUp(Stage stage, bool confirmedByMe, bool confirmedByPeer)
    {
        return stage == Stage.SetUp && confirmedByMe && confirmedByPeer;
    }

    public enum OpponentLeftVerdict
    {
        Nothing,
        WaitInPlace,
        GoBack,
    }

    public static OpponentLeftVerdict JudgeOpponentLeft(double secondsSinceLeft, bool atPlayStage,
                                                        bool bothConfirmedAtSetUp)
    {
        var inPlace = atPlayStage || bothConfirmedAtSetUp;
        if (secondsSinceLeft < OpponentLeftLimit(inPlace))
        {
            return OpponentLeftVerdict.Nothing;
        }

        return inPlace ? OpponentLeftVerdict.WaitInPlace : OpponentLeftVerdict.GoBack;
    }

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
                    "Strikers stopped the match on both PCs.");
        }

        return ("Stopped: your opponent left the match",
                "Strikers stopped the match on both PCs.");
    }

    public static (string Headline, string Detail) ClosedText(bool closedHere)
    {
        if (closedHere)
        {
            return ("Stopped: the game closed",
                    "Strikers stopped the match on both PCs. If the game crashed, press Send report.");
        }

        return ("Stopped: your opponent's game closed",
                "Strikers stopped the match on both PCs.");
    }

    public static (string Headline, string Detail) HaltText(bool disagreement)
    {
        if (disagreement)
        {
            return ("Stopped: the games went out of sync",
                    "Strikers stopped the match on both PCs.");
        }

        return ("Stopped: Strikers could not follow the last turn", "Strikers stopped the match on both PCs.");
    }

    public static (string Headline, string Detail) SetupsDifferText()
    {
        return ("Stopped: the two setups do not match",
                "The armies, the board or the rules differ between the PCs.");
    }

    public static (string Headline, string Detail) StopText(bool setupsDiffer, bool disagreement)
    {
        if (setupsDiffer)
        {
            return SetupsDifferText();
        }

        return HaltText(disagreement);
    }

    public static (string Headline, string Detail) GameBuildsText()
    {
        return ("Your games are on different builds",
                "Let Steam update Horizon Forbidden West on both PCs, then try again.");
    }

    public const string HaltStatus = "Press Stop, then start a new match.";

    public const string HaltStatusFinishing = "Finishing the report.";

    public readonly record struct StopScreen(string Detail, string Status, bool StopEnabled, bool ReportVisible,
                                            bool Spinner);

    public static StopScreen StopScreenFor(string detail, bool ask, bool pending, bool reportSaved,
                                           bool theirsLanded)
    {
        var finishing = ask && pending;
        var shown = detail;
        if (ask && !pending && !reportSaved)
        {
            shown = $"{detail} {ReportNotSaved}";
        }
        else if (ask && !pending)
        {
            shown = $"{detail} {ReportSaved(theirsLanded)}";
        }

        var status = finishing ? HaltStatusFinishing : HaltStatus;

        return new StopScreen(shown, status, StopEnabled: !finishing, ReportVisible: !pending, Spinner: finishing);
    }

    public static readonly TimeSpan ReportSettles = TimeSpan.FromSeconds(35);

    public static bool ReportSettled(DateTime since, DateTime now)
    {
        return now - since >= ReportSettles;
    }

    public static string ReportSaved(bool bothSides)
    {
        if (bothSides)
        {
            return "Press Send report. Only one of you needs to.";
        }

        return "Press Send report, and ask your opponent to send theirs.";
    }

    public static readonly string ReportNotSaved =
        $"The report could not be saved. Press Send report and attach {MatchLog.Name} instead.";

    public static string[] StopLines(ReportFacts facts)
    {
        var version = facts.Version ?? "not set";
        var commit = facts.Commit ?? "not stamped";
        var netplay = facts.NetplayId ?? "not read";
        var liveProbe = facts.LiveProbeId ?? "not read";
        return
        [
            $"  version {version}, commit {commit}",
            $"  Strikers {facts.LauncherId}, netplay {netplay}, live-probe {liveProbe}",
            $"  this PC: {Report.Seat(facts.Hosted)}",
        ];
    }

    public static (string Headline, string Detail) StoppedHereText()
    {
        return ("Stopped: part of Strikers closed on this PC", "The match cannot go on.");
    }

    public static bool PlayEndedUnexpectedly(int? code, bool alreadyStopped)
    {
        return !alreadyStopped && code is not (0 or 2);
    }

    public static bool ShowsRefusalScreen(bool refused, bool playStarted)
    {
        return refused && !playStarted;
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
        return $"Could not read {name}, so nothing was saved over it. Close anything that has it " +
               "open, or move it out of the Strikers folder, then try again.";
    }

    public static (string Headline, string Detail)? MatchListNote(int count, bool attemptUnderWay)
    {
        if (count > 0 || attemptUnderWay)
        {
            return null;
        }

        return ("Cannot reach netplay", "netplay.exe did not answer. It should be in the Strikers folder.");
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

    private const uint ClipboardErrorFirst = 0x800401D0;

    private const uint ClipboardErrorLast = 0x800401DF;

    public static bool ClipboardBusy(Exception? thrown)
    {
        if (thrown is not System.Runtime.InteropServices.COMException)
        {
            return false;
        }

        var code = unchecked((uint)thrown.HResult);
        return code >= ClipboardErrorFirst && code <= ClipboardErrorLast;
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

        if (found.Problem() is not null)
        {
            return (null, BoardCannotBePlayed);
        }

        return (found, null);
    }

    public const string BoardCannotBePlayed =
        "That board cannot be played. Fix it in the Boards panel, or pick another board.";

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
        return RandomToken(RoomCodeAlphabet, RoomCodeLength);
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

    public const string NoArmyToStart = "Build an army in the Armies panel first.";

    public static string ImportedText(int machines)
    {
        var what = machines == 1 ? "1 machine" : $"{machines} machines";
        return $"Imported {what}. Name the army and press Save.";
    }

    public const string NoArmyToJoin =
        "You have no armies. You can use your opponent's army once they choose it.";

    public const string WaitingForTheirArmyToBorrow =
        "You have no armies. Your opponent's army will show here once they choose it.";

    public static int PlacingSquares(int width, int placementRows)
    {
        if (width < 1 || placementRows < 1)
        {
            return -1;
        }

        return width * placementRows;
    }

    private static readonly Regex TheirNameLine = new(
        @"^\s*<- their name: ((?:[0-9A-F]{2}){1,8})\s*$", RegexOptions.Compiled);

    public static string? TheirName(string line)
    {
        var m = TheirNameLine.Match(line);
        if (!m.Success)
        {
            return null;
        }

        var spelled = m.Groups[1].Value;
        var name = new System.Text.StringBuilder(spelled.Length / 2);
        for (var i = 0; i < spelled.Length; i += 2)
        {
            var c = (char)Convert.ToInt32(spelled.Substring(i, 2), 16);
            if (c is not (>= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return null;
            }

            name.Append(c);
        }

        return name.ToString();
    }

    public static readonly TimeSpan InviteWindow = TimeSpan.FromMinutes(10);

    public sealed class InviteClock
    {
        public enum Event
        {
            Made,
            PeerLeft,
            KeyedPeerHere,
            KeyedPeerGone,
            RanOut,
            NewAttempt,
        }

        private bool live;
        private TimeSpan spent;
        private DateTime? runningSince;

        public bool InviteShown { get; private set; }

        public bool Running
        {
            get
            {
                return live && runningSince is not null;
            }
        }

        public void Heard(Event what, DateTime now)
        {
            switch (what)
            {
                case Event.Made:
                    {
                        InviteShown = true;
                        live = true;
                        spent = TimeSpan.Zero;
                        runningSince = now;
                        return;
                    }

                case Event.KeyedPeerHere:
                    {
                        if (runningSince is { } since)
                        {
                            spent += now - since;
                            runningSince = null;
                        }

                        return;
                    }

                case Event.KeyedPeerGone:
                    {
                        if (live && runningSince is null)
                        {
                            runningSince = now;
                        }

                        return;
                    }

                case Event.RanOut:
                    {
                        live = false;
                        runningSince = null;
                        return;
                    }

                case Event.NewAttempt:
                    {
                        InviteShown = false;
                        live = false;
                        spent = TimeSpan.Zero;
                        runningSince = null;
                        return;
                    }

                default:
                    {
                        return;
                    }
            }
        }

        public TimeSpan? Left(DateTime now)
        {
            if (!live)
            {
                return null;
            }

            var used = spent;
            if (runningSince is { } since)
            {
                used += now - since;
            }

            return InviteWindow - used;
        }

        public bool RunOut(DateTime now)
        {
            return Running && Left(now) is { } left && left <= TimeSpan.Zero;
        }
    }

    public const string InviteWentStale = "Press New invite for a new one.";

    public static (string Headline, string Detail)? InviteExpiredText(bool halted)
    {
        if (!ScreenMayChange(halted, startIdle: false))
        {
            return null;
        }

        return ("This invite has expired", InviteWentStale);
    }

    public static (string Text, bool Urgent) InviteClockText(TimeSpan left)
    {
        var shown = left < TimeSpan.Zero ? TimeSpan.Zero : left;
        return ($"{(int)shown.TotalMinutes}m {shown.Seconds:00}s left", shown.TotalSeconds < 60);
    }

    public static string StopPressedLine(Stage stage, bool halted)
    {
        var after = halted ? ", halted" : "";
        return $"  stop pressed (stage {stage}{after})";
    }

    public static string JoinedHeadline(string? name)
    {
        return name is { Length: > 0 } ? $"{name} has joined" : "Your opponent has joined";
    }

    private static readonly Regex TooManyForTheRowsLine = new(
        @"^\s*(\d{1,3}) machines is more than the (\d{1,4}) squares the placing rows of this "
        + @"\d{1,2}x\d{1,2} board hold\s*$", RegexOptions.Compiled);

    private static readonly Regex NoPlacementsLine = new(
        @"^\s*not ready: (\d{1,3}) machine\(s\) but 0 placement\(s\), the game places one per "
        + @"record, in order\.\s*$", RegexOptions.Compiled);

    public static (int Machines, int PlacingSquares)? ArmyFit(string line)
    {
        var tooMany = TooManyForTheRowsLine.Match(line);
        if (tooMany.Success)
        {
            return (int.Parse(tooMany.Groups[1].Value), int.Parse(tooMany.Groups[2].Value));
        }

        var noPlacements = NoPlacementsLine.Match(line);
        if (noPlacements.Success)
        {
            return (int.Parse(noPlacements.Groups[1].Value), -1);
        }

        return null;
    }

    public static bool RingsOnHalt(bool alreadyHalted)
    {
        return !alreadyHalted;
    }

    public const string ArmyDoesNotFitHere =
        "That army has more machines than this board has placing squares. Pick a smaller army "
        + "or a board with more placing rows.";

    public static string? ArmyDoesNotFit(int machines, int placingSquares)
    {
        if (placingSquares < 1 || machines <= placingSquares)
        {
            return null;
        }

        var room = placingSquares == 1 ? "1 square" : $"{placingSquares} squares";
        return $"That army has {machines} machines, but this board fits {room} a side. "
               + "Pick a smaller army or a board with more placing rows.";
    }

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

        var hint = headline == PeerInSetupHeadline && seconds >= PeerInSetupHintAfter
            ? "\n" + PeerInSetupHint
            : "";

        return (step, waitingFor + slow + hint);
    }

    public const string PeerInSetupHeadline = "Waiting for your opponent to finish setting up";

    public const int PeerInSetupHintAfter = 60;

    public const string PeerInSetupHint =
        "Your opponent is taking a while. Their game may not be on the challenge list at Salma's Machine Strike "
        + "table. Ask them to open it and press Set up the match again.";

    public const string DoNotEnterYet = "Do not enter the challenge yet.";

    public const string OpponentAheadHeadline = "Your opponent is ready and waiting";

    public static string? OpponentAheadDetail(Stage stage, bool setupFailed = false)
    {
        if (stage == Stage.SetUp && setupFailed)
        {
            return null;
        }

        return stage switch
        {
            Stage.Safety => "Type the code they read to you, then press Continue.",
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
                    "Press Stop, ask for a new invite, and join again.");
        }

        return ("Lost the connection",
                "Trying again. If it does not come back, press Stop and start again.");
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060", Justification = "the check takes the secrets to prove them absent")]
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
