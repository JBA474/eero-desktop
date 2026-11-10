using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using EeroDesktop.Api;
using EeroDesktop.Pages;

namespace EeroDesktop;

/// <summary>
/// Checks every network in the account in the background (every 2 minutes by default) and raises alerts when the
/// internet or an eero goes down or comes back, and when a device joins that has never been seen before.
/// Uses its own GET calls, so it never changes what the open page is showing.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    public sealed record Alert(string Title, string Body, bool Warning);

    private readonly DispatcherTimer _timer;
    private readonly Action<Alert> _notify;
    private readonly Dictionary<string, bool> _internetOk = [];
    private readonly Dictionary<string, bool> _eeroOk = [];
    private readonly Dictionary<string, bool> _historyState = [];
    private readonly Dictionary<string, DateTime> _speedRecorded = [];
    private bool _busy;
    private DateTime _pausedUntil = DateTime.MinValue;

    /// <summary>One-line summary of the last check, for the tray tooltip.</summary>
    public event Action<string>? Summary;

    private static string KnownDevicesPath => Path.Combine(AppSettings.DataDir, "known-devices.json");

    public NetworkMonitor(Action<Alert> notify)
    {
        _notify = notify;
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("EERO_DESKTOP_MONITOR_SECONDS"), out var s) && s >= 5 ? s : 120;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void Dispose() => _timer.Stop();

    public async Task CheckAsync()
    {
        var state = AppState.Current;
        if (_busy || string.IsNullOrEmpty(state.Api.Token) || DateTime.Now < _pausedUntil) return;
        _busy = true;
        try
        {
            var parts = new List<string>();
            foreach (var net in state.Networks.ToList())
                if (await CheckNetworkAsync(net) is { } line) parts.Add(line);
            if (parts.Count > 0) Summary?.Invoke(string.Join("; ", parts));
            if (parts.Count > 0) SendMonthlySummaryIfDue();
            await CheckEeroAppAsync();
        }
        finally { _busy = false; }
    }

    /// <summary>Once a month: last month's speed, outages and DNS benchmarks, one notification per network.</summary>
    private void SendMonthlySummaryIfDue()
    {
        try
        {
            var s = AppSettings.Load();
            if (s.HistoryStartedUtc == default) { s.HistoryStartedUtc = DateTime.UtcNow; s.Save(); }
            var month = DateTime.Now.AddMonths(-1);
            var key = MonthlySummary.MonthKey(month);
            if (!s.MonthlySummary || s.LastSummaryMonth == key) return;
            foreach (var net in AppState.Current.Networks.ToList())
                if (MonthlySummary.Build(net.Id, net.Name, month.Year, month.Month, s.HistoryStartedUtc) is { } summary)
                {
                    AppSettings.Log("SUMMARY", $"{summary.Title} — {summary.Body}");
                    _notify(new(summary.Title, summary.Body, false));
                }
            s = AppSettings.Load();
            s.LastSummaryMonth = key;
            s.Save();
        }
        catch (Exception ex) { AppSettings.Log("ERROR", "Monthly summary failed: " + ex.Message); }
    }

    /// <summary>Weekly: tell the user when eero ships a new Android app (the most likely moment the API changes).</summary>
    private async Task CheckEeroAppAsync()
    {
        try
        {
            if (await EeroAppRelease.CheckIfDueAsync() is { } r)
                Raise(new($"eero released app version {r.Version}",
                    $"eero Desktop was built from eero app {EeroClient.AppVersion}. Most updates change nothing here; if something stops working, see Settings › About.", false));
        }
        catch (Exception ex) { AppSettings.Log("ERROR", "eero app release check failed: " + ex.Message); }
    }

    /// <summary>Copies eero's speed test results into local history (eero itself keeps about 30 days). Every 6 hours.</summary>
    private async Task RecordSpeedTestsAsync(NetworkRef net, JsonNode? n)
    {
        if (_speedRecorded.TryGetValue(net.Url, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(6)) return;
        _speedRecorded[net.Url] = DateTime.UtcNow;
        if (n.S("resources.speedtest") is not { } url) return;
        var end = DateTime.UtcNow;
        var tests = (await AppState.Current.Api.GetAsync(url, new Dictionary<string, string?>
        {
            ["limit"] = "30",
            ["startTime"] = end.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["endTime"] = end.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        })).Arr();
        var added = HistoryStore.MergeSpeedTests(SpeedTests(net.Id, tests));
        if (added > 0) AppSettings.Log("HISTORY", $"{net.Name}: {added} new speed test(s) saved");
    }

    /// <summary>eero speed test JSON → history records (shared with the Activity page).</summary>
    public static IEnumerable<HistoryStore.SpeedTest> SpeedTests(string networkId, IEnumerable<JsonObject> tests)
        => tests.Where(t => t.Dt("date") is not null && (t.D("down_mbps") is not null || t.D("up_mbps") is not null))
            .Select(t => new HistoryStore.SpeedTest(networkId, t.Dt("date")!.Value.ToUniversalTime(), t.D("down_mbps") ?? 0, t.D("up_mbps") ?? 0));

    private async Task<string?> CheckNetworkAsync(NetworkRef net)
    {
        var api = AppState.Current.Api;
        var settings = AppSettings.Load();
        try
        {
            var n = await api.GetAsync(net.Url);

            // Internet
            var internet = n.S("health.internet.status");
            var online = string.Equals(internet, "connected", StringComparison.OrdinalIgnoreCase);
            History(net, "internet", "Internet", online, $"eero reported {J.Title(internet ?? "unknown")}");
            Transition(_internetOk, net.Url, online, settings.OutageAlerts,
                down: new($"Internet is down on {net.Name}", $"eero reports the internet connection as {J.Title(internet ?? "unknown")}.", true),
                up: new($"Internet is back on {net.Name}", "The internet connection is working again.", false));

            // eeros
            var eerosUrl = n.S("resources.eeros");
            var eeros = (eerosUrl is not null ? (await api.GetAsync(eerosUrl)).Arr() : n.Arr("eeros")).ToList();
            foreach (var e in eeros)
            {
                var status = (e.S("status.value") ?? e.S("status") ?? "").ToLowerInvariant();
                var ok = status is "" or "green";
                var where = e.S("location") ?? e.S("serial") ?? "An eero";
                History(net, "eero", where, ok, $"status {J.Title(status)}");
                Transition(_eeroOk, net.Url + "|" + (e.S("url") ?? e.S("serial") ?? where), ok, settings.OutageAlerts,
                    down: new($"{where} eero needs attention", $"Status {J.Title(status)} on {net.Name}. It may be offline or have no connection to the other eeros.", true),
                    up: new($"{where} eero is back", $"Status is green again on {net.Name}.", false));
            }

            // Devices never seen before on this network
            var devicesUrl = n.S("resources.devices") ?? net.Url + "/devices";
            var devices = (await api.GetAsync(devicesUrl, new Dictionary<string, string?> { ["thread"] = "false", ["proxied_node"] = "false" })).Arr().ToList();
            CheckNewDevices(net, devices, settings.NewDeviceAlerts);
            RecordDevicePresence(net, devices);

            try { await RecordSpeedTestsAsync(net, n); }
            catch (Exception ex) when (ex is not EeroApiException { IsRateLimited: true }) { AppSettings.Log("ERROR", $"Speed test history for {net.Name}: {ex.Message}"); }

            var connected = devices.Count(d => d.B("connected"));
            var bad = eeros.Count(e => (e.S("status.value") ?? e.S("status") ?? "green").ToLowerInvariant() != "green");
            return $"{net.Name}: internet {(online ? "up" : "DOWN")}, {connected} online" + (bad > 0 ? $", {bad} eero(s) need attention" : "");
        }
        catch (EeroApiException ex) when (ex.IsUnauthorized) { return null; } // the window handles sign-out
        catch (EeroApiException ex) when (ex.IsRateLimited)
        {
            // Back off for as long as eero asks (at least 2 minutes) instead of adding to the load.
            var wait = ex.RetryAfter is { } ra && ra > TimeSpan.FromMinutes(2) ? ra : TimeSpan.FromMinutes(5);
            _pausedUntil = DateTime.Now + wait;
            AppSettings.Log("PAUSED", $"eero rate-limited background checks; pausing for {(int)wait.TotalMinutes} min");
            return null;
        }
        catch (Exception ex)
        {
            AppSettings.Log("ERROR", $"Check of {net.Name} failed: {ex.Message}");
            return null;
        }
    }

    private void Transition(Dictionary<string, bool> seen, string key, bool ok, bool alertsOn, Alert down, Alert up)
    {
        var known = seen.TryGetValue(key, out var was);
        seen[key] = ok;
        if (!alertsOn) return;
        if (!ok && (!known || was)) Raise(down);       // went down (or was already down when monitoring started)
        else if (ok && known && !was) Raise(up);      // came back
    }

    /// <summary>
    /// Records outages in the local history whether or not alerts are on. The first time a subject is seen in this run,
    /// it's compared with the last stored state, so an outage that ended while eero Desktop was closed is still closed
    /// (at the first check after it started).
    /// </summary>
    private void History(NetworkRef net, string kind, string subject, bool ok, string detail)
    {
        var key = $"{net.Url}|{kind}|{subject}";
        var first = !_historyState.TryGetValue(key, out var was);
        var previous = first ? HistoryStore.LastStates(net.Id).GetValueOrDefault((kind, subject)) : was ? "up" : "down";
        _historyState[key] = ok;

        var now = ok ? "up" : "down";
        if (now == previous || (ok && previous is null)) return; // no change (healthy and never down: nothing to note)
        HistoryStore.AddEvent(new(net.Id, DateTime.UtcNow, kind, subject, now,
            ok && first ? detail + " — first check after eero Desktop started; it may have come back earlier" : detail));
    }

    /// <summary>Checks a device must be missing before it counts as gone (sleeping phones drop off briefly).</summary>
    private const int OfflineAfterChecks = 2;

    private readonly Dictionary<string, Dictionary<string, string>> _deviceState = [];   // network → mac → online/offline
    private readonly Dictionary<string, int> _missingCount = [];                          // network|mac → consecutive misses

    /// <summary>
    /// Records devices joining and leaving (for History › Devices). A device counts as gone only after missing
    /// <see cref="OfflineAfterChecks"/> checks in a row. On the first check of a run, states are compared with the
    /// last stored ones, so changes while eero Desktop was closed are recorded at that first check (and say so).
    /// </summary>
    private void RecordDevicePresence(NetworkRef net, List<JsonObject> devices)
    {
        var first = !_deviceState.TryGetValue(net.Id, out var state);
        state ??= _deviceState[net.Id] = HistoryStore.LastDeviceStates(net.Id);
        var note = first ? "first check after eero Desktop started" : "";
        var now = DateTime.UtcNow;
        var events = new List<HistoryStore.DeviceEvent>();
        foreach (var d in devices)
        {
            if (d.S("mac")?.ToLowerInvariant() is not { Length: > 0 } mac) continue;
            var key = net.Id + "|" + mac;
            var connected = d.B("connected");
            var was = state.GetValueOrDefault(mac);
            if (connected)
            {
                _missingCount.Remove(key);
                if (was != "online") events.Add(new(net.Id, now, mac, DevicesPage.DeviceName(d), "online", d.S("ip"), note));
                state[mac] = "online";
            }
            else if (was == "online")
            {
                var misses = _missingCount[key] = _missingCount.GetValueOrDefault(key) + 1;
                if (misses < OfflineAfterChecks && !first) continue;
                _missingCount.Remove(key);
                events.Add(new(net.Id, now, mac, DevicesPage.DeviceName(d), "offline", d.S("ip"), note));
                state[mac] = "offline";
            }
        }
        HistoryStore.AddDeviceEvents(events);
    }

    private void CheckNewDevices(NetworkRef net, List<JsonObject> devices, bool alertsOn)
    {
        var all = LoadKnown();
        var macs = devices.Select(d => d.S("mac")).Where(m => !string.IsNullOrEmpty(m)).Select(m => m!.ToLowerInvariant()).ToHashSet();
        if (!all.TryGetValue(net.Id, out var known))
        {
            // First check of this network: remember everything already there without alerting.
            all[net.Id] = macs;
            SaveKnown(all);
            AppSettings.Log("BASELINE", $"{net.Name} ({net.Id}): {macs.Count} known devices");
            return;
        }

        var fresh = devices.Where(d => d.S("mac") is { Length: > 0 } m && !known.Contains(m.ToLowerInvariant())).ToList();
        if (fresh.Count == 0) return;
        known.UnionWith(fresh.Select(d => d.S("mac")!.ToLowerInvariant()));
        SaveKnown(all);
        if (!alertsOn) return;

        if (fresh.Count == 1)
        {
            var d = fresh[0];
            var details = string.Join(" · ", new[] { d.S("manufacturer"), d.S("ip"), d.S("mac") }.Where(s => !string.IsNullOrEmpty(s)));
            Raise(new($"New device on {net.Name}", $"{DevicesPage.DeviceName(d)} joined for the first time.\n{details}", false));
        }
        else
        {
            var names = string.Join(", ", fresh.Take(3).Select(DevicesPage.DeviceName)) + (fresh.Count > 3 ? $" and {fresh.Count - 3} more" : "");
            Raise(new($"{fresh.Count} new devices on {net.Name}", $"{names} joined for the first time.", false));
        }
    }

    private void Raise(Alert a)
    {
        AppSettings.Log("ALERT", $"{a.Title} — {a.Body}");
        _notify(a);
    }

    private static Dictionary<string, HashSet<string>> LoadKnown()
    {
        try
        {
            if (File.Exists(KnownDevicesPath))
                return (JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(KnownDevicesPath)) ?? [])
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => m.ToLowerInvariant()).ToHashSet());
        }
        catch (Exception) { }
        return [];
    }

    private static void SaveKnown(Dictionary<string, HashSet<string>> all)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        File.WriteAllText(KnownDevicesPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }
}
