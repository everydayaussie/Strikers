using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Strikers.Core;

public static class TunnelDns
{
    public const string DefaultServer = "bore.pub";

    private const int ControlPort = 7835;

    public static async Task<string> ResolveAsync(string host, CancellationToken token = default)
    {
        return (await LookUpAsync(host, token)).Host;
    }

    public static async Task<(string Host, string? Note)> LookUpAsync(string host, CancellationToken token = default)
    {
        if (System.Net.IPAddress.TryParse(host, out _))
        {
            return (host, null);
        }

        if (await ReachableAsync(host, token))
        {
            return (host, null);
        }

        var viaHttps = (await OverHttpsAsync(host, token)).FirstOrDefault();
        if (viaHttps is not null && await ReachableAsync(viaHttps, token))
        {
            var system = (await SystemAnswersAsync(host, token)).FirstOrDefault();
            return (viaHttps, Note(host, viaHttps, system));
        }

        return (host, $"Strikers could not reach {host} by any route. Your network may be blocking it "
                    + "outright, or the server may be down.");
    }

    internal static string Note(string host, string found, string? systemAnswer)
    {
        if (systemAnswer is null)
        {
            return $"Your network could not look up {host}, so Strikers found it another way ({found}). "
                   + "Nothing for you to fix.";
        }

        return $"Your network answered {host} with an address that is not the real server. Strikers "
               + $"found the right one ({found}) and is using it. Nothing for you to fix.";
    }

    public static async Task<IReadOnlyCollection<string>> AnswersAsync(string host, CancellationToken token = default)
    {
        var system = await SystemAnswersAsync(host, token);
        var viaHttps = await OverHttpsAsync(host, token);
        return [.. system.Concat(viaHttps).Distinct(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> SystemAnswersAsync(string host, CancellationToken token)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, token);
            return [.. addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                .Select(a => a.ToString())];
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException
                                      or OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<bool> ReachableAsync(string host, CancellationToken token)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, ControlPort, deadline.Token);
            return client.Connected;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException
                                      or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal const int MaxAnswerBytes = 64 * 1024;

    internal static string[] QueryUrls(string host)
    {
        var name = Uri.EscapeDataString(host);
        return
        [
            $"https://1.1.1.1/dns-query?name={name}&type=A",
            $"https://8.8.8.8/resolve?name={name}&type=A",
        ];
    }

    internal static List<string> AnswerAddresses(string text)
    {
        var found = new List<string>();
        try
        {
            if (JsonNode.Parse(text) is not JsonObject reply || reply["Answer"] is not JsonArray answers)
            {
                return found;
            }

            foreach (var answer in answers)
            {
                if (answer?["type"]?.GetValue<int>() == 1
                    && answer["data"]?.GetValue<string>() is { } address
                    && System.Net.IPAddress.TryParse(address, out var parsed)
                    && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    found.Add(parsed.ToString());
                }
            }

            return found;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<List<string>> OverHttpsAsync(string host, CancellationToken token)
    {
        foreach (var url in QueryUrls(host))
        {
            string text;
            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(4),
                    MaxResponseContentBufferSize = MaxAnswerBytes,
                };
                http.DefaultRequestHeaders.Add("Accept", "application/dns-json");
                text = await http.GetStringAsync(url, token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException
                                          or InvalidOperationException)
            {
                continue;
            }

            var found = AnswerAddresses(text);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }
}
