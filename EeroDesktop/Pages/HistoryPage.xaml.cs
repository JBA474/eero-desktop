using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

/// <summary>Local trends: speed tests, outages and DNS benchmarks, from <see cref="HistoryStore"/>.</summary>
public partial class HistoryPage : PageBase
{
    public sealed record SpeedRow(string Date, string Down, string Up);
    public sealed record OutageRow(string Started, string Ended, string Length, string What, string Detail);
    public sealed record DnsRow(string Date, string Connection, string Fastest, string InUse, string All);

    // Each DNS provider keeps the same color in every benchmark (color follows the entity, never its rank).
    private static readonly string[] DnsSlotOrder = ["Your ISP (eero automatic)", "Cloudflare", "Google", "Quad9", "OpenDNS", "AdGuard DNS", "Control D"];

    public HistoryPage()
    {
        InitializeComponent();
        // The address a device last joined with (eero hands out the same one while its lease lasts).
        HostActions.Attach(DeviceHistoryGrid, o => o is DeviceRow r
            ? new HostTarget(r.Name, r.Events.LastOrDefault(e => !string.IsNullOrEmpty(e.Ip))?.Ip ?? "")
            : null);
        SpeedChart.FormatY = v => $"{v:0} Mbps";
        DnsChart.FormatY = v => $"{v:0} ms";
    }

    public override string Title => "History";

    public override async Task LoadAsync()
    {
        // Pick up any new eero speed tests first (one read; history works offline if this fails).
        try
        {
            if (S.TryRes("speedtest") is { } url)
            {
                var end = DateTime.UtcNow;
                var tests = (await Api.GetAsync(url, new Dictionary<string, string?>
                {
                    ["limit"] = "30",
                    ["startTime"] = end.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    ["endTime"] = end.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                })).Arr().ToList();
                HistoryStore.MergeSpeedTests(NetworkMonitor.SpeedTests(S.NetworkId, tests));
            }
        }
        catch (Exception ex) when (ex is not EeroApiException { IsUnauthorized: true })
        {
            S.Report("Couldn't fetch new speed tests from eero (showing saved history): " + ex.Message);
        }
        Render();
    }

    private DateTime Since => RangeCombo.SelectedIndex switch
    {
        0 => DateTime.UtcNow.AddDays(-7),
        1 => DateTime.UtcNow.AddDays(-30),
        2 => DateTime.UtcNow.AddDays(-90),
        3 => DateTime.UtcNow.AddDays(-365),
        _ => DateTime.MinValue,
    };

    private void Range_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && S.Selected is not null) Render(); }

    private void Render()
    {
        if (S.Selected is null) return;
        var since = Since;
        var range = since == DateTime.MinValue ? ((DateTime, DateTime)?)null : (since, DateTime.UtcNow);
        RenderSpeed(since, range);
        RenderOutages(since);
        RenderDevices(since);
        RenderDns(since, range);
    }

    // ------------------------------------------------------------------ devices

    public sealed record DeviceRow(string Name, string Status, string LastJoined, string LastLeft, int Joins, string Mac, List<HistoryStore.DeviceEvent> Events);
    public sealed record DeviceEventRow(string Time, string Event, string Ip, string Detail);

    private void RenderDevices(DateTime since)
    {
        var selectedMac = (DeviceHistoryGrid.SelectedItem as DeviceRow)?.Mac;
        // Everything ever recorded decides "now"; the period decides what's listed and counted.
        var all = HistoryStore.DeviceEvents(S.NetworkId, DateTime.MinValue);
        var rows = all.GroupBy(e => e.Mac)
            .Select(g =>
            {
                var list = g.OrderBy(e => e.Utc).ToList();
                var inRange = list.Where(e => e.Utc >= since).ToList();
                var lastOn = list.LastOrDefault(e => e.State == "online");
                var lastOff = list.LastOrDefault(e => e.State == "offline");
                return (Row: new DeviceRow(DevicesPage.Readable(list[^1].Name), list[^1].State == "online" ? "Online" : "Offline",
                        lastOn is null ? "" : Local(lastOn.Utc), lastOff is null ? "" : Local(lastOff.Utc),
                        inRange.Count(e => e.State == "online"), g.Key, list),
                    Last: list[^1].Utc, Active: inRange.Count > 0);
            })
            .Where(x => x.Active || x.Row.Status == "Online")
            .OrderBy(x => x.Row.Status == "Online" ? 0 : 1).ThenByDescending(x => x.Last)
            .Select(x => x.Row).ToList();
        DeviceHistoryGrid.ItemsSource = rows;
        DeviceHistoryGrid.SelectedItem = rows.FirstOrDefault(r => r.Mac == selectedMac);
        if (DeviceHistoryGrid.SelectedItem is null) ShowDeviceTimeline(null);

        DeviceTiles.Children.Clear();
        if (all.Count == 0) return;
        Tile(DeviceTiles, rows.Count(r => r.Status == "Online").ToString(CultureInfo.CurrentCulture), "Online now", "DevicesOnline");
        Tile(DeviceTiles, rows.Count.ToString(CultureInfo.CurrentCulture), "Devices seen in this period", "DevicesSeen");
        if (rows.MaxBy(r => r.Joins) is { Joins: > 1 } busiest)
            Tile(DeviceTiles, busiest.Joins.ToString(CultureInfo.CurrentCulture), $"Joins by {busiest.Name}", "DevicesBusiest");
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDeviceTimeline(DeviceHistoryGrid.SelectedItem as DeviceRow);

    private void ShowDeviceTimeline(DeviceRow? row)
    {
        DeviceTimelineTitle.Text = row is null ? "Select a device above to see when it joined and left." : $"Timeline — {row.Name}";
        DeviceEventsGrid.ItemsSource = row?.Events.Where(e => e.Utc >= Since).OrderByDescending(e => e.Utc)
            .Select(e => new DeviceEventRow(Local(e.Utc), e.State == "online" ? "Joined" : "Left", e.Ip ?? "", e.Detail)).ToList();
    }

    private static string Local(DateTime utc) => J.Fmt(utc.ToLocalTime());

    // ------------------------------------------------------------------ speed tests

    private void RenderSpeed(DateTime since, (DateTime, DateTime)? range)
    {
        var tests = HistoryStore.SpeedTests(S.NetworkId, since);
        SpeedChart.XRange = range;
        SpeedChart.SetData(
        [
            new("Download", 1, tests.Select(t => (t.Utc, t.DownMbps)).ToList()),
            new("Upload", 2, tests.Select(t => (t.Utc, t.UpMbps)).ToList()),
        ], "No speed tests in this period yet. eero runs one now and then, or use Activity › Run Speed Test.");
        SpeedHistoryGrid.ItemsSource = tests.OrderByDescending(t => t.Utc)
            .Select(t => new SpeedRow(Local(t.Utc), t.DownMbps.ToString("0.0", CultureInfo.CurrentCulture), t.UpMbps.ToString("0.0", CultureInfo.CurrentCulture))).ToList();

        SpeedTiles.Children.Clear();
        if (tests.Count == 0) return;
        Tile(SpeedTiles, $"{tests.Average(t => t.DownMbps):0} Mbps", "Average download", "SpeedAvgDown");
        Tile(SpeedTiles, $"{tests.Average(t => t.UpMbps):0} Mbps", "Average upload", "SpeedAvgUp");
        Tile(SpeedTiles, $"{tests.Min(t => t.DownMbps):0} Mbps", "Slowest download", "SpeedMinDown");
        Tile(SpeedTiles, tests.Count.ToString(CultureInfo.CurrentCulture), tests.Count == 1 ? "Test" : "Tests", "SpeedCount");
    }

    // ------------------------------------------------------------------ outages

    private void RenderOutages(DateTime since)
    {
        var now = DateTime.UtcNow;
        var outages = HistoryStore.Outages(S.NetworkId, since);
        OutageGrid.ItemsSource = outages.Select(o => new OutageRow(
            Local(o.StartUtc),
            o.EndUtc is { } end ? Local(end) : "Still down",
            Duration(o.Duration(now)),
            o.Kind == "internet" ? "Internet" : $"{o.Subject} eero",
            o.Detail)).ToList();

        OutageTiles.Children.Clear();
        // Downtime inside the selected period only.
        TimeSpan InRange(HistoryStore.Outage o) => (o.EndUtc ?? now) - (o.StartUtc < since ? since : o.StartUtc);
        var internet = outages.Where(o => o.Kind == "internet").ToList();
        Tile(OutageTiles, internet.Count.ToString(CultureInfo.CurrentCulture), internet.Count == 1 ? "Internet outage" : "Internet outages", "OutageCount");
        Tile(OutageTiles, Duration(internet.Aggregate(TimeSpan.Zero, (sum, o) => sum + InRange(o))), "Internet downtime", "OutageDowntime");
        var eeros = outages.Count(o => o.Kind == "eero");
        Tile(OutageTiles, eeros.ToString(CultureInfo.CurrentCulture), eeros == 1 ? "eero problem" : "eero problems", "EeroProblemCount");
        if (outages.Count > 0) Tile(OutageTiles, Duration(outages.Max(o => o.Duration(now))), "Longest", "OutageLongest");
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return t <= TimeSpan.Zero ? "0 min" : "under 1 min";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} min";
        if (t < TimeSpan.FromDays(1)) return t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min";
        return t.Hours == 0 ? $"{(int)t.TotalDays} d" : $"{(int)t.TotalDays} d {t.Hours} h";
    }

    // ------------------------------------------------------------------ DNS benchmarks

    private void RenderDns(DateTime since, (DateTime, DateTime)? range)
    {
        var runs = HistoryStore.DnsRuns(S.NetworkId, since);
        var names = runs.SelectMany(r => r.Providers.Where(p => p.Ok).Select(p => p.Name)).Distinct()
            .OrderBy(n => Array.IndexOf(DnsSlotOrder, n) is var i && i >= 0 ? i : 100).ThenBy(n => n, StringComparer.CurrentCulture).ToList();
        var extra = 7;
        var series = names.Select(n => new ChartSeries(n == DnsSlotOrder[0] ? "Your ISP" : n,
                Array.IndexOf(DnsSlotOrder, n) is var i && i >= 0 ? i + 1 : Math.Min(8, extra++),
                runs.SelectMany(r => r.Providers.Where(p => p.Name == n && p.Ok).Select(p => (r.Utc, p.MedianMs))).ToList()))
            .ToList();
        DnsChart.XRange = range;
        DnsChart.SetData(series, "No DNS benchmarks in this period. Run one from Advanced › DNS › Benchmark DNS Servers.");

        DnsHistoryGrid.ItemsSource = runs.OrderByDescending(r => r.Utc).Select(r =>
        {
            var ok = r.Providers.Where(p => p.Ok).OrderBy(p => p.MedianMs).ToList();
            var inUse = r.InUse is null ? "Unknown" : ok.FirstOrDefault(p => p.Name == r.InUse) is { } u ? $"{Short(u.Name)} ({u.MedianMs:0} ms)" : Short(r.InUse);
            return new DnsRow(Local(r.Utc), r.Connection,
                ok.Count == 0 ? "None answered" : $"{Short(ok[0].Name)} ({ok[0].MedianMs:0} ms)",
                inUse,
                string.Join(" · ", ok.Select(p => $"{Short(p.Name)} {p.MedianMs:0}")) + (r.Providers.Any(p => !p.Ok) ? " · failed: " + string.Join(", ", r.Providers.Where(p => !p.Ok).Select(p => Short(p.Name))) : ""));
        }).ToList();
    }

    private static string Short(string name) => name == DnsSlotOrder[0] ? "Your ISP" : name;

    // ------------------------------------------------------------------ helpers & buttons

    private static void Tile(Panel host, string value, string label, string automationId)
    {
        var tile = new Border();
        tile.SetResourceReference(StyleProperty, "Tile");
        var stack = new StackPanel();
        var v = new TextBlock { Text = value };
        v.SetResourceReference(StyleProperty, "TileValue");
        var l = new TextBlock { Text = label };
        l.SetResourceReference(StyleProperty, "TileLabel");
        stack.Children.Add(v);
        stack.Children.Add(l);
        tile.Child = stack;
        AutomationProperties.SetAutomationId(v, automationId);
        AutomationProperties.SetName(v, $"{label}: {value}");
        host.Children.Add(tile);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(HistoryStore.Dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{HistoryStore.Dir}\""));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Delete all saved history — speed tests, outages, device activity and DNS benchmark results — from this PC?\n\n"
                     + "Speed tests from the last 30 days come back from eero next time; everything else is gone for good (unless it's in a backup).",
                "Clear History")) return;
        HistoryStore.Clear();
        S.Report("History cleared.");
        Render();
    }
}
