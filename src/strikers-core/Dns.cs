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

        var viaHttps = await OverHttpsAsync(host, token);
        if (viaHttps is not null && await ReachableAsync(viaHttps, token))
        {
            var system = await SystemAnswerAsync(host, token);
            var note = system is null
                ? $"Your network could not look up {host}, so Strikers found it another way ({viaHttps}). "
                  + "Nothing for you to fix."
                : $"Your network answered {host} with {system}, which is not the real server. Strikers "
                  + $"found the right one ({viaHttps}) and is using it. Nothing for you to fix.";
            return (viaHttps, note);
        }

        return (host, $"Strikers could not reach {host} by any route. Your network may be blocking it "
                    + "outright, or the server may be down.");
    }

    private static async Task<string?> SystemAnswerAsync(string host, CancellationToken token)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, token);
            return addresses.FirstOrDefault(a => a.AddressFamily
                == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
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

    private static async Task<string?> OverHttpsAsync(string host, CancellationToken token)
    {
        foreach (var url in QueryUrls(host))
        {
            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(4),
                    MaxResponseContentBufferSize = MaxAnswerBytes,
                };
                http.DefaultRequestHeaders.Add("Accept", "application/dns-json");
                var text = await http.GetStringAsync(url, token);
                if (JsonNode.Parse(text) is not JsonObject reply || reply["Answer"] is not JsonArray answers)
                {
                    continue;
                }

                foreach (var answer in answers)
                {
                    if (answer?["type"]?.GetValue<int>() == 1
                        && answer["data"]?.GetValue<string>() is { } address
                        && System.Net.IPAddress.TryParse(address, out _))
                    {
                        return address;
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                          or System.Text.Json.JsonException or InvalidOperationException)
            {
            }
        }

        return null;
    }
}
