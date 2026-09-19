using System.Text.RegularExpressions;

namespace Strikers.Core;

public enum LineMeaning
{
    Nothing,

    TunnelAddress,

    Halt,

    PortInUse,
    Crashed,
    TunnelDown,

    LinkDown,

    BothPlayersReady,
    PeerStillInSetup,
    PeerMovedToPlay,

    Fingerprint,
    TheirSetup,

    TheirArmy,

    BothReady,

    PeerConfirmed,

    SessionBound,

    PeerLeft,

    PeerStartedOver,

    CodeChanged,

    WriteSucceeded,
    WriteFailed,
    BothSeatsFilled,
    Paired,
    InSync,
    MatchRunning,

    MatchOver,
    UnsharedMatch,

    OtherVersionTried,
}

public readonly record struct LineReading(
    LineMeaning Meaning,
    string? Address = null,
    string? Code = null,
    string? Challenge = null,
    int VictoryPoints = -1,
    int DraftPoints = -1,
    bool Disagreement = false,
    bool Inactive = false,
    string? Binding = null,
    string? First = null,
    IReadOnlyList<string>? Army = null,
    bool DifferentVersions = false,
    bool DifferentGameBuilds = false,
    bool PlayerLeft = false,
    bool LeftHere = false);

public static class NetplayLine
{
    public const string PlayerLeftReason = "a player left the match before it ended";
    public const string PeerStoppedPrefix = "the other PC stopped the match";

    public static bool ProvesTheLink(LineMeaning meaning)
    {
        switch (meaning)
        {
            case LineMeaning.Nothing:
            case LineMeaning.TunnelAddress:
            case LineMeaning.Halt:
            case LineMeaning.PortInUse:
            case LineMeaning.Crashed:
            case LineMeaning.TunnelDown:
            case LineMeaning.LinkDown:

            case LineMeaning.CodeChanged:
            case LineMeaning.OtherVersionTried:
                return false;

            case LineMeaning.BothPlayersReady:
            case LineMeaning.PeerStillInSetup:
            case LineMeaning.PeerMovedToPlay:
            case LineMeaning.Fingerprint:
            case LineMeaning.TheirSetup:
            case LineMeaning.TheirArmy:
            case LineMeaning.BothReady:
            case LineMeaning.PeerConfirmed:
            case LineMeaning.SessionBound:
            case LineMeaning.PeerLeft:
            case LineMeaning.PeerStartedOver:
            case LineMeaning.WriteSucceeded:
            case LineMeaning.WriteFailed:
            case LineMeaning.BothSeatsFilled:
            case LineMeaning.Paired:
            case LineMeaning.InSync:
            case LineMeaning.MatchRunning:
            case LineMeaning.MatchOver:
            case LineMeaning.UnsharedMatch:
                return true;

            default:
                return false;
        }
    }

    private static readonly Regex Served = new(@"^\s*tunnel open: --server ([A-Za-z0-9.\-]+):([0-9]{1,5})\s*$",
                                               RegexOptions.Compiled);

    private static string? ServedAddress(string host, string port, string? tunnelHost)
    {
        if (tunnelHost is null || !string.Equals(host, tunnelHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!int.TryParse(port, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
        {
            return null;
        }

        return $"{host}:{number}";
    }

    private static readonly Regex TunnelDied = new(@"^\s*Warning: the tunnel DIED, ", RegexOptions.Compiled);
    private static readonly Regex LinkDownLine = new(@"^\s*link down \([A-Za-z.]+\); retrying in \d+s\s*$", RegexOptions.Compiled);
    private static readonly Regex BothReadyLine = new(@"^\s*BOTH READY, challenge( [0-9A-Fa-f]{32})?\s*$", RegexOptions.Compiled);
    private static readonly Regex PeerConfirmedLine = new(@"^\s*<- they confirmed the safety code\s*$", RegexOptions.Compiled);
    private static readonly Regex PeerLeftLine = new(@"^\s*<- the other player left\s*$", RegexOptions.Compiled);
    private static readonly Regex PeerStartedOverLine = new(@"^\s*the other player started over, so our build and setup go out again\s*$", RegexOptions.Compiled);

    private static readonly Regex CodeChangedLine = new(@"^\s*nothing was confirmed: the safety code changed since it was compared\s*$", RegexOptions.Compiled);
    private static readonly Regex BothSeatsFilledLine = new(@"^room [0-9A-Fa-f]+: both seats filled\s*$", RegexOptions.Compiled);
    private static readonly Regex PairedLine = new(@"^\s*paired, seat \d\s*$", RegexOptions.Compiled);
    private static readonly Regex InSyncLine = new(@"^\s*in sync\s*$", RegexOptions.Compiled);
    private static readonly Regex AutoOnLine = new(@"^\s*auto ON \(seat \d\)\.", RegexOptions.Compiled);

    private static readonly Regex MatchOverLine = new(@"^\s*match over: ", RegexOptions.Compiled);

    private static readonly Regex InactiveLine = new(
        @"^\s*REFUSED: This test build of Strikers is no longer active\b", RegexOptions.Compiled);
    private static readonly Regex VersionRefusedLine = new(
        @"^\s*REFUSED: Strikers version mismatch: ", RegexOptions.Compiled);
    private static readonly Regex GameBuildRefusedLine = new(
        @"^\s*REFUSED: game build mismatch: ", RegexOptions.Compiled);
    private static readonly Regex ProtocolRefusedLine = new(
        @"^\s*HALT: the other PC stopped the match: protocol v-?\d{1,10}, relay speaks v\d{1,10}\s*$", RegexOptions.Compiled);
    private static readonly Regex OtherVersionTriedLine = new(
        @"^\s*" + Regex.Escape(Release.OtherVersionTried) + @" \(protocol v-?\d{1,10}\)\s*$", RegexOptions.Compiled);
    private static readonly Regex PortInUseLine = new(
        @"^(Unhandled exception\.|\s+at System\.|\s*--->).*Only one usage of each socket address",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnsharedMatchLine = new(
        @"^\s*a new match started on this PC after the shared one ended\b", RegexOptions.Compiled);

    public static LineReading Read(string line, bool hosting, bool haveInvite, string? tunnelHost = null)
    {
        if (line is null)
        {
            return new LineReading(LineMeaning.Nothing);
        }

        if (hosting && !haveInvite)
        {
            var served = Served.Match(line);
            if (served.Success && ServedAddress(served.Groups[1].Value, served.Groups[2].Value, tunnelHost) is { } address)
            {
                return new LineReading(LineMeaning.TunnelAddress, Address: address);
            }
        }

        if (line.Contains("HALT", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("REFUSED", StringComparison.OrdinalIgnoreCase))
        {
            var disagreement = line.Contains("desync", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("hash", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("board", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("diverge", StringComparison.OrdinalIgnoreCase);

            var inactive = InactiveLine.IsMatch(line);
            var differentVersions = VersionRefusedLine.IsMatch(line) || ProtocolRefusedLine.IsMatch(line);
            var differentGameBuilds = GameBuildRefusedLine.IsMatch(line);
            var playerLeft = line.Contains(PlayerLeftReason, StringComparison.OrdinalIgnoreCase);
            var leftHere = playerLeft && !line.Contains(PeerStoppedPrefix, StringComparison.OrdinalIgnoreCase);

            return new LineReading(LineMeaning.Halt, Disagreement: disagreement, Inactive: inactive,
                                   DifferentVersions: differentVersions, DifferentGameBuilds: differentGameBuilds,
                                   PlayerLeft: playerLeft, LeftHere: leftHere);
        }

        if (PortInUseLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.PortInUse);
        }

        if (line.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            return new LineReading(LineMeaning.Crashed);
        }

        if (TunnelDied.IsMatch(line))
        {
            return new LineReading(LineMeaning.TunnelDown);
        }

        if (LinkDownLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.LinkDown);
        }

        if (Play.BothPlayersReady(line))
        {
            return new LineReading(LineMeaning.BothPlayersReady);
        }

        if (Play.PeerStillInSetup(line))
        {
            return new LineReading(LineMeaning.PeerStillInSetup);
        }

        if (Play.PeerMovedToPlay(line))
        {
            return new LineReading(LineMeaning.PeerMovedToPlay);
        }

        if (Play.Fingerprint(line) is { } derived)
        {
            return new LineReading(LineMeaning.Fingerprint, Code: derived);
        }

        if (Play.TheirSetup(line) is { } told)
        {
            return new LineReading(LineMeaning.TheirSetup, Challenge: told.Challenge,
                                   VictoryPoints: told.VictoryPoints, DraftPoints: told.DraftPoints,
                                   First: told.First);
        }

        if (Play.TheirArmy(line) is { } theirArmy)
        {
            return new LineReading(LineMeaning.TheirArmy, Army: theirArmy);
        }

        if (BothReadyLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.BothReady);
        }

        if (PeerConfirmedLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.PeerConfirmed);
        }

        if (Play.SessionBinding(line) is { } bound)
        {
            return new LineReading(LineMeaning.SessionBound, Binding: bound);
        }

        if (PeerLeftLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.PeerLeft);
        }

        if (PeerStartedOverLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.PeerStartedOver);
        }

        if (CodeChangedLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.CodeChanged);
        }

        if (Play.WriteSucceeded(line))
        {
            return new LineReading(LineMeaning.WriteSucceeded);
        }

        if (Play.WriteFailed(line))
        {
            return new LineReading(LineMeaning.WriteFailed);
        }

        if (BothSeatsFilledLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.BothSeatsFilled);
        }

        if (PairedLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.Paired);
        }

        if (InSyncLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.InSync);
        }

        if (AutoOnLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.MatchRunning);
        }

        if (MatchOverLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.MatchOver);
        }

        if (UnsharedMatchLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.UnsharedMatch);
        }

        if (OtherVersionTriedLine.IsMatch(line))
        {
            return new LineReading(LineMeaning.OtherVersionTried);
        }

        return new LineReading(LineMeaning.Nothing);
    }
}
