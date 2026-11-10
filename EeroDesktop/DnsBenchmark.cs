using System.IO;
using System.Text.Json.Nodes;
using EeroDesktop.Api;

namespace EeroDesktop;

/// <summary>A DNS provider the benchmark can test and eero can be switched to.</summary>
public sealed record DnsProvider(string Name, IReadOnlyList<string> Servers, bool IsIsp = false, string Note = "", bool BlocksAds = false);

/// <summary>Combined result for a provider (its primary and secondary servers).</summary>
public sealed record ProviderResult(DnsProvider Provider, IReadOnlyList<DnspyreResult> Runs, string? Error)
{
    public bool Ok => Error is null && Runs.Count > 0 && SuccessRate >= DnsBenchmark.MinSuccessRate;
    public double MedianMs => Runs.Count == 0 ? double.NaN : Runs.Average(r => r.P50Ms);
    public double P95Ms => Runs.Count == 0 ? double.NaN : Runs.Max(r => r.P95Ms);
    public double MeanMs => Runs.Count == 0 ? double.NaN : Runs.Average(r => r.MeanMs);
    public double SuccessRate => Runs.Count == 0 ? 0 : Runs.Min(r => r.SuccessRate);

    /// <summary>Lower is better: typical speed matters most, slow outliers count too.</summary>
    public double Score => Ok ? 0.6 * MedianMs + 0.4 * P95Ms : double.MaxValue;

    public string? GraphsDir => Runs.FirstOrDefault(r => r.GraphsDir is not null)?.GraphsDir;
}

/// <summary>Builds the provider list from the network and ranks dnspyre results.</summary>
public static class DnsBenchmark
{
    public const double MinSuccessRate = 0.98;

    /// <summary>Popular sites, so the test reflects everyday browsing (each asked several times).</summary>
    public static readonly string[] Domains =
    [
        "google.com", "youtube.com", "facebook.com", "amazon.com", "wikipedia.org", "netflix.com", "apple.com",
        "microsoft.com", "instagram.com", "reddit.com", "linkedin.com", "yahoo.com", "bing.com", "zoom.us",
        "office.com", "cloudflare.com", "github.com", "spotify.com", "ebay.com", "weather.com",
    ];

    private static readonly DnsProvider[] Fallback =
    [
        new("Cloudflare", ["1.1.1.1", "1.0.0.1"]),
        new("Google", ["8.8.8.8", "8.8.4.4"]),
        new("Quad9", ["9.9.9.9", "149.112.112.112"], Note: "Blocks known malicious domains"),
        new("OpenDNS", ["208.67.222.222", "208.67.220.220"]),
    ];

    public static readonly DnsProvider AdGuard = new("AdGuard DNS", ["94.140.14.14", "94.140.15.15"], Note: "Blocks ads and trackers", BlocksAds: true);

    /// <summary>Control D's free "Ads & Tracking" resolvers (https://controld.com/free-dns) — the other free ad-blocking DNS.</summary>
    public static readonly DnsProvider ControlD = new("Control D", ["76.76.2.2", "76.76.10.2"], Note: "Blocks ads and trackers", BlocksAds: true);

    /// <summary>Ad-blocking DNS providers (offered on the Advanced page and compared separately in the benchmark).</summary>
    public static readonly DnsProvider[] AdBlockers = [AdGuard, ControlD];

    /// <summary>Every public provider eero Desktop knows by name (for describing a DNS setting).</summary>
    public static IEnumerable<DnsProvider> KnownProviders => Fallback.Concat(AdBlockers);

    private static List<string> Ips(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : [];

    /// <summary>The ISP's servers (eero's "automatic"), eero's suggested public servers, AdGuard, and any current custom servers.</summary>
    public static List<DnsProvider> Providers(JsonNode? network)
    {
        var list = new List<DnsProvider>();
        var isp = Ips(network.At("dns.parent.ips"));
        if (isp.Count > 0) list.Add(new DnsProvider("Your ISP (eero automatic)", isp.Take(2).ToList(), IsIsp: true));

        // eero publishes the public resolvers it suggests (default_test_servers); fall back to the same four.
        var suggested = network.Arr("dns.default_test_servers")
            .Select(s => new DnsProvider(s.S("name") ?? "DNS", Ips(s.At("ipv4")).Take(2).ToList(),
                Note: string.Equals(s.S("name"), "Quad9", StringComparison.OrdinalIgnoreCase) ? "Blocks known malicious domains" : ""))
            .Where(p => p.Servers.Count > 0).ToList();
        list.AddRange(suggested.Count > 0 ? suggested : Fallback);
        list.AddRange(AdBlockers);

        var custom = Ips(network.At("dns.custom.ips"));
        if (string.Equals(network.S("dns.mode"), "custom", StringComparison.OrdinalIgnoreCase) && custom.Count > 0
            && !list.Any(p => p.Servers.Contains(custom[0])))
            list.Add(new DnsProvider("Your current custom DNS", custom.Take(2).ToList()));
        return list;
    }

    /// <summary>The provider the network is using now (automatic = ISP).</summary>
    public static DnsProvider? Current(JsonNode? network, IEnumerable<DnsProvider> providers)
    {
        if (!string.Equals(network.S("dns.mode"), "custom", StringComparison.OrdinalIgnoreCase))
            return providers.FirstOrDefault(p => p.IsIsp);
        var first = Ips(network.At("dns.custom.ips")).FirstOrDefault();
        return first is null ? null : providers.FirstOrDefault(p => !p.IsIsp && p.Servers.Contains(first));
    }

    public static List<ProviderResult> Rank(IEnumerable<ProviderResult> results)
        => results.OrderBy(r => r.Ok ? 0 : 1).ThenBy(r => r.Score).ThenBy(r => r.MeanMs).ToList();

    /// <summary>Runs dnspyre against every server of every provider, one after another.</summary>
    public static async Task<List<ProviderResult>> RunAsync(IReadOnlyList<DnsProvider> providers, string graphsRoot,
        int repeats, IProgress<string> progress, CancellationToken ct)
    {
        var results = new List<ProviderResult>();
        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var runs = new List<DnspyreResult>();
            string? error = null;
            for (var s = 0; s < p.Servers.Count; s++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"Testing {p.Name} ({p.Servers[s]}) — {i + 1} of {providers.Count}…");
                try
                {
                    // dnspyre graphs for each provider's primary server.
                    var graphs = s == 0 ? Path.Combine(graphsRoot, Safe(p.Name)) : null;
                    runs.Add(await Dnspyre.RunAsync(p.Servers[s], Domains, repeats, graphs, ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { error = ex.Message; }
            }
            results.Add(new ProviderResult(p, runs, runs.Count == 0 ? error ?? "No results" : null));
        }
        return Rank(results);
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
