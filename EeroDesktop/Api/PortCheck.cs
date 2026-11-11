using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EeroDesktop.Api;

/// <summary>
/// Checks from the internet whether forwarded TCP ports answer, using portchecker.io (eero has no hairpin NAT, so a test
/// from inside the network to the public IP proves nothing). Also spots carrier-grade NAT: when eero's WAN address is
/// itself private or shared (100.64.0.0/10), the ISP shares one public IP among many customers and no forward can work.
/// </summary>
public static class PortCheck
{
    public const string Service = "portchecker.io";
    private static string ApiUrl => Environment.GetEnvironmentVariable("EERO_DESKTOP_PORTCHECK_API") is { Length: > 0 } u
        ? u : "https://portchecker.io/api/v1/query";

    public enum NatKind { Public, CarrierGrade, Private, Unknown }

    /// <summary>What kind of address eero's WAN port has.</summary>
    public static NatKind WanKind(string? wanIp)
    {
        if (!IPAddress.TryParse(wanIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return NatKind.Unknown;
        var b = ip.GetAddressBytes();
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return NatKind.CarrierGrade;                       // 100.64.0.0/10
        if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return NatKind.Private;
        return NatKind.Public;
    }

    /// <summary>Plain-English reason forwards can't be reached, or null when the WAN address is public.</summary>
    public static string? NatProblem(string? wanIp, string? publicIp) => WanKind(wanIp) switch
    {
        NatKind.CarrierGrade =>
            $"Your internet provider uses carrier-grade NAT: eero's internet address ({wanIp}) is a shared provider address, not your public one ({publicIp ?? "unknown"}). " +
            "The provider shares that public address among many customers, so port forwards can't be reached from the internet. " +
            "Ask your provider for a public (static or dedicated) IPv4 address, or use something that connects outward instead, such as Tailscale or Plex's relay.",
        NatKind.Private =>
            $"eero's internet address ({wanIp}) is a private one, so another router (the modem/gateway) sits in front of eero. " +
            "Forwards must also be set up on that router, or put it in bridge mode.",
        _ => null,
    };

    public static JsonObject Request(string host, IEnumerable<int> ports) =>
        new() { ["host"] = host, ["ports"] = new JsonArray(ports.Distinct().Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()) };

    /// <summary>Parses {"error":false,"check":[{"port":32400,"status":true},…]} into port → open.</summary>
    public static Dictionary<int, bool> Parse(JsonNode? response)
    {
        if (response?["error"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"{Service}: {response["msg"]?.ToString() ?? "check failed"}");
        var result = new Dictionary<int, bool>();
        if (response?["check"] is JsonArray checks)
            foreach (var c in checks.OfType<JsonObject>())
                if (c["port"]?.GetValue<int>() is { } port) result[port] = c["status"]?.GetValue<bool>() == true;
        return result;
    }

    public static async Task<Dictionary<int, bool>> CheckAsync(string host, IEnumerable<int> ports, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"EeroDesktop/{AppUpdate.Current}");
        using var resp = await http.PostAsJsonAsync(ApiUrl, Request(host, ports), ct);
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadFromJsonAsync<JsonNode>(ct));
    }
}
