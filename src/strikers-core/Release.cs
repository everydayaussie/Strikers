using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Strikers.Core;

public static class Release
{
    public const string VersionKey = "StrikersVersion";

    public const string NexusGame = "horizonforbiddenwest";

    public const string ModIdKey = "NexusModId";

    public const string CommitKey = "StrikersCommit";

    public const string NexusApi = "https://api.nexusmods.com/v2/graphql";

    public const int MaxAnswerBytes = 64 * 1024;

    public const int MaxParts = 4;

    public const int MaxPartDigits = 5;

    public const string OtherVersionTried = "relay refused a player on another version of Strikers";

    public static string? Version(Assembly? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        foreach (var a in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (a.Key == VersionKey)
            {
                return Clean(a.Value);
            }
        }

        return null;
    }

    public static string? Commit(Assembly? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        foreach (var a in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (a.Key == CommitKey)
            {
                return CleanCommit(a.Value);
            }
        }

        return null;
    }

    public static string? CleanCommit(string? text)
    {
        var value = text?.Trim();
        if (value is null || value.Length < 7 || value.Length > 40)
        {
            return null;
        }

        foreach (var c in value)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
            {
                return null;
            }
        }

        return value;
    }

    public static int ModId(Assembly? assembly)
    {
        if (assembly is null)
        {
            return 0;
        }

        foreach (var a in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (a.Key == ModIdKey && int.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return 0;
    }

    public static int[]? Numbered(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        if (s.Length == 0 || s.Length > (MaxParts * (MaxPartDigits + 1)))
        {
            return null;
        }

        var parts = s.Split('.');
        if (parts.Length > MaxParts)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > MaxPartDigits || !part.All(char.IsAsciiDigit))
            {
                return null;
            }

            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return numbers;
    }

    public static string? Clean(string? text)
    {
        if (Numbered(text) is not { } numbers)
        {
            return null;
        }

        return string.Join('.', numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
    }

    public static bool Newer(string? candidate, string? than)
    {
        var a = Numbered(candidate);
        var b = Numbered(than);
        if (a is null || b is null)
        {
            return false;
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return false;
    }

    public static bool Offer(string? ours, string? newest)
    {
        return Newer(newest, ours);
    }

    public static string Query(int modId)
    {
        var id = modId.ToString(CultureInfo.InvariantCulture);
        var query = "{ legacyModsByDomain(ids: [{gameDomain: \"" + NexusGame + "\", modId: " + id + "}]) "
                    + "{ nodes { modId version status } } }";
        return new JsonObject { ["query"] = query }.ToJsonString();
    }

    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan QuietWithin = TimeSpan.FromSeconds(10);

    public static async Task<(string? Newest, string Note)> FetchNewestAsync(string api, int modId, string agent,
                                                                          TimeSpan total, TimeSpan quiet)
    {
        try
        {
            using var deadline = new CancellationTokenSource(total);
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = quiet,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            };
            using var http = new HttpClient(handler)
            {
                Timeout = total,
                MaxResponseContentBufferSize = MaxAnswerBytes,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(agent);

            using var request = new HttpRequestMessage(HttpMethod.Post, api)
            {
                Content = new StringContent(Query(modId), System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return (null, $"update check: Nexus answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is { } announced && announced > MaxAnswerBytes)
            {
                return (null, $"update check: Nexus sent more than {MaxAnswerBytes} bytes");
            }

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await Streams.ReadCappedAsync(body, MaxAnswerBytes, quiet, deadline.Token);
            if (bytes is null)
            {
                return (null, $"update check: Nexus sent more than {MaxAnswerBytes} bytes");
            }

            var found = NewestFrom(System.Text.Encoding.UTF8.GetString(bytes), modId);
            return (found, found is null
                ? "update check: the answer named no published version"
                : $"update check: the newest on Nexus is {found}");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException
                                      or TimeoutException or InvalidOperationException)
        {
            return (null, "update check: could not reach Nexus");
        }
    }

    public static string PageUrl(int modId)
    {
        return $"https://www.nexusmods.com/{NexusGame}/mods/{modId.ToString(CultureInfo.InvariantCulture)}?tab=files";
    }

    public static string? NewestFrom(string? answer, int modId)
    {
        if (answer is null || answer.Length > MaxAnswerBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(answer, new JsonDocumentOptions { MaxDepth = 16 });
            var nodes = Child(Child(Child(document.RootElement, "data"), "legacyModsByDomain"), "nodes");
            if (nodes is not { ValueKind: JsonValueKind.Array } list)
            {
                return null;
            }

            foreach (var node in list.EnumerateArray())
            {
                if (Child(node, "modId") is not { ValueKind: JsonValueKind.Number } id
                    || !id.TryGetInt32(out var found) || found != modId)
                {
                    continue;
                }

                if (Child(node, "status") is not { ValueKind: JsonValueKind.String } status
                    || status.GetString() != "published")
                {
                    return null;
                }

                if (Child(node, "version") is not { ValueKind: JsonValueKind.String } version)
                {
                    return null;
                }

                return Clean(version.GetString());
            }

            return null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static JsonElement? Child(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty(name, out var child))
        {
            return null;
        }

        return child;
    }

    public static string OfferText(string? ours, string? newest)
    {
        var mine = Clean(ours) ?? "an older version";
        var top = Clean(newest) ?? "newer";
        return $"You have {mine}. The newest is {top}, on Nexus Mods.";
    }

    public static (string Headline, string Detail) DifferentVersionsText(string? ours, string? newest)
    {
        return ("You and the other player have different versions of Strikers", Advice(ours, newest));
    }

    public static string TriedToJoinText(string? ours, string? newest)
    {
        return $"Someone tried to join with a different version of Strikers. {Advice(ours, newest)}";
    }

    private static string Advice(string? ours, string? newest)
    {
        var mine = Clean(ours);
        var top = Clean(newest);
        if (mine is null)
        {
            return "Both of you update to the newest version from the Nexus page, then try again.";
        }

        if (top is not null && Newer(top, mine))
        {
            return $"You have {mine} and the newest is {top}. Update from the Nexus page, then try again.";
        }

        if (top is not null)
        {
            return $"You have {mine}, the newest. The other player needs to update from the Nexus page, then try again.";
        }

        return $"You have {mine}. Whoever has the older version updates from the Nexus page, then you both try again.";
    }
}
