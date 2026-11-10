using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EeroDesktop;

/// <summary>
/// Local history kept by eero Desktop (eero's own speed test history only reaches back about 30 days, and it keeps no
/// outage or DNS history at all). Stored as JSON Lines under the data folder's "history" folder, one record per line,
/// so a damaged line costs one record, not the file. Records older than <see cref="KeepDays"/> are dropped.
/// </summary>
public static class HistoryStore
{
    public const int KeepDays = 400;

    public sealed record SpeedTest(string Network, DateTime Utc, double DownMbps, double UpMbps);

    /// <summary>A state change seen by the background monitor. Kind is "internet" or "eero"; State is "down" or "up".</summary>
    public sealed record StatusEvent(string Network, DateTime Utc, string Kind, string Subject, string State, string Detail = "");

    public sealed record DnsProviderResult(string Name, double MedianMs, double P95Ms, double SuccessRate, bool Ok);

    public sealed record DnsRun(string Network, DateTime Utc, string Connection, string? InUse, List<DnsProviderResult> Providers);

    /// <summary>A down→up pair (Until is null while it is still down).</summary>
    public sealed record Outage(string Kind, string Subject, DateTime StartUtc, DateTime? EndUtc, string Detail)
    {
        public TimeSpan Duration(DateTime nowUtc) => (EndUtc ?? nowUtc) - StartUtc;
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Dir => Path.Combine(AppSettings.DataDir, "history");
    private static string SpeedFile => Path.Combine(Dir, "speedtests.jsonl");
    private static string EventsFile => Path.Combine(Dir, "events.jsonl");
    private static string DnsFile => Path.Combine(Dir, "dns-benchmarks.jsonl");

    // ------------------------------------------------------------------ speed tests

    /// <summary>Adds speed tests not already stored (same network and time). Returns how many were new.</summary>
    public static int MergeSpeedTests(IEnumerable<SpeedTest> tests)
    {
        var existing = Read<SpeedTest>(SpeedFile).Select(t => (t.Network, t.Utc)).ToHashSet();
        var fresh = tests.Where(t => t.Utc != default && existing.Add((t.Network, t.Utc))).OrderBy(t => t.Utc).ToList();
        Append(SpeedFile, fresh);
        return fresh.Count;
    }

    public static List<SpeedTest> SpeedTests(string network, DateTime sinceUtc)
        => Read<SpeedTest>(SpeedFile).Where(t => t.Network == network && t.Utc >= sinceUtc).OrderBy(t => t.Utc).ToList();

    // ------------------------------------------------------------------ outages

    /// <summary>The last recorded state ("up"/"down") of each subject on a network, from the stored events.</summary>
    public static Dictionary<(string Kind, string Subject), string> LastStates(string network)
        => Read<StatusEvent>(EventsFile).Where(e => e.Network == network).GroupBy(e => (e.Kind, e.Subject))
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Utc).Last().State);

    public static void AddEvent(StatusEvent e) => Append(EventsFile, [e]);

    public static List<StatusEvent> Events(string network) => Read<StatusEvent>(EventsFile).Where(e => e.Network == network).OrderBy(e => e.Utc).ToList();

    /// <summary>Pairs each "down" with the next "up" for the same subject. Outages that overlap the range are included.</summary>
    public static List<Outage> Outages(string network, DateTime sinceUtc)
    {
        var result = new List<Outage>();
        foreach (var g in Events(network).GroupBy(e => (e.Kind, e.Subject)))
        {
            StatusEvent? down = null;
            foreach (var e in g)
            {
                if (e.State == "down") down ??= e;
                else if (down is not null)
                {
                    result.Add(new(g.Key.Kind, g.Key.Subject, down.Utc, e.Utc, down.Detail));
                    down = null;
                }
            }
            if (down is not null) result.Add(new(g.Key.Kind, g.Key.Subject, down.Utc, null, down.Detail));
        }
        return result.Where(o => (o.EndUtc ?? DateTime.MaxValue) >= sinceUtc).OrderByDescending(o => o.StartUtc).ToList();
    }

    // ------------------------------------------------------------------ DNS benchmarks

    public static void AddDnsRun(DnsRun run) => Append(DnsFile, [run]);

    public static List<DnsRun> DnsRuns(string network, DateTime sinceUtc)
        => Read<DnsRun>(DnsFile).Where(r => r.Network == network && r.Utc >= sinceUtc).OrderBy(r => r.Utc).ToList();

    // ------------------------------------------------------------------ devices joining and leaving

    /// <summary>A device seen joining ("online") or leaving ("offline") the network by the background monitor.</summary>
    public sealed record DeviceEvent(string Network, DateTime Utc, string Mac, string Name, string State, string? Ip = null, string Detail = "");

    /// <summary>Device events are kept for less time than the rest (busy networks log many).</summary>
    public const int DeviceKeepDays = 90;

    private static string DevicesFile => Path.Combine(Dir, "devices.jsonl");

    public static void AddDeviceEvents(IReadOnlyCollection<DeviceEvent> events) => Append(DevicesFile, events);

    public static List<DeviceEvent> DeviceEvents(string network, DateTime sinceUtc)
        => Read<DeviceEvent>(DevicesFile).Where(e => e.Network == network && e.Utc >= sinceUtc).OrderBy(e => e.Utc).ToList();

    /// <summary>Last recorded state ("online"/"offline") per device MAC on a network.</summary>
    public static Dictionary<string, string> LastDeviceStates(string network)
        => Read<DeviceEvent>(DevicesFile).Where(e => e.Network == network).GroupBy(e => e.Mac)
            .ToDictionary(g => g.Key, g => g.MaxBy(e => e.Utc)!.State);

    // ------------------------------------------------------------------ backup

    private static readonly (string Key, Func<string> File)[] BackupFiles =
    [
        ("speed_tests", () => SpeedFile), ("outage_events", () => EventsFile), ("dns_benchmarks", () => DnsFile), ("device_events", () => DevicesFile),
    ];

    /// <summary>All history as one JSON object (for Back Up App Settings): each file's records as an array.</summary>
    public static System.Text.Json.Nodes.JsonObject Export()
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, file) in BackupFiles)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            if (File.Exists(file()))
                foreach (var line in File.ReadLines(file()))
                    try { if (System.Text.Json.Nodes.JsonNode.Parse(line) is { } n) arr.Add(n); } catch (JsonException) { }
            o[key] = arr;
        }
        return o;
    }

    /// <summary>Merges history from a backup: records not already here are added; nothing here is removed. Returns records added.</summary>
    public static int Import(System.Text.Json.Nodes.JsonObject backup)
    {
        var added = 0;
        foreach (var (key, file) in BackupFiles)
        {
            if (backup[key] is not System.Text.Json.Nodes.JsonArray arr) continue;
            var existing = File.Exists(file()) ? File.ReadLines(file()).Select(Normalize).ToHashSet() : [];
            var fresh = arr.Where(n => n is System.Text.Json.Nodes.JsonObject).Select(n => n!.ToJsonString())
                .Where(line => existing.Add(Normalize(line))).ToList();
            if (fresh.Count == 0) continue;
            Directory.CreateDirectory(Dir);
            File.AppendAllLines(file(), fresh);
            added += fresh.Count;
        }
        return added;
    }

    private static string Normalize(string line)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(line)?.ToJsonString() ?? line; } catch (JsonException) { return line; }
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Drops records older than <see cref="KeepDays"/> (run once per start).</summary>
    public static void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
        Rewrite(SpeedFile, Read<SpeedTest>(SpeedFile), t => t.Utc >= cutoff);
        Rewrite(DnsFile, Read<DnsRun>(DnsFile), r => r.Utc >= cutoff);
        var deviceCutoff = DateTime.UtcNow.AddDays(-DeviceKeepDays);
        var devices = Read<DeviceEvent>(DevicesFile);
        var lastPerDevice = devices.GroupBy(e => (e.Network, e.Mac)).Select(g => g.MaxBy(e => e.Utc)).ToHashSet();
        Rewrite(DevicesFile, devices, e => e.Utc >= deviceCutoff || lastPerDevice.Contains(e));
        // Keep each subject's latest event even when old, so an outage still open isn't forgotten.
        var events = Read<StatusEvent>(EventsFile);
        var latest = events.GroupBy(e => (e.Network, e.Kind, e.Subject)).Select(g => g.MaxBy(e => e.Utc)).ToHashSet();
        Rewrite(EventsFile, events, e => e.Utc >= cutoff || latest.Contains(e));
    }

    public static void Clear()
    {
        foreach (var f in new[] { SpeedFile, EventsFile, DnsFile, DevicesFile })
            try { File.Delete(f); } catch (IOException) { }
    }

    private static List<T> Read<T>(string file)
    {
        var list = new List<T>();
        try
        {
            if (!File.Exists(file)) return list;
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<T>(line, Json) is { } item) list.Add(item); }
                catch (JsonException) { } // skip a damaged line, keep the rest
            }
        }
        catch (IOException) { }
        return list;
    }

    private static void Append<T>(string file, IReadOnlyCollection<T> items)
    {
        if (items.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllLines(file, items.Select(i => JsonSerializer.Serialize(i, Json)));
        }
        catch (IOException ex) { AppSettings.Log("ERROR", "Couldn't save history: " + ex.Message); }
    }

    private static void Rewrite<T>(string file, List<T> items, Func<T, bool> keep)
    {
        var kept = items.Where(keep).ToList();
        if (kept.Count == items.Count || !File.Exists(file)) return;
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllLines(tmp, kept.Select(i => JsonSerializer.Serialize(i, Json)));
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException) { }
    }

    /// <summary>How this PC reaches the network right now: "Wi-Fi", "Ethernet", or "Unknown".</summary>
    public static string ConnectionType()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && !System.Text.RegularExpressions.Regex.IsMatch(n.Description + " " + n.Name,
                                "virtual|hyper-v|vethernet|vpn|tap-|tun|wireguard|tailscale|zerotier|vmware|virtualbox|bluetooth",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)))
                .ToList();
            if (active.Any(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx))
                return "Ethernet";
            if (active.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) return "Wi-Fi";
        }
        catch (NetworkInformationException) { }
        return "Unknown";
    }
}
