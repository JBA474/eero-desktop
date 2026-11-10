using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class ActivityPage : PageBase
{
    public sealed record SpeedRow(string Date, string Down, string Up);

    public ActivityPage() => InitializeComponent();

    public override string Title => "Activity";

    public override async Task LoadAsync()
    {
        List<SpeedRow> rows = [];
        await Run("Loading speed test history…", async () =>
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-30);
            var tests = (await Api.GetAsync(S.Res("speedtest"), new Dictionary<string, string?>
            {
                ["limit"] = "30",
                ["startTime"] = Iso(start),
                ["endTime"] = Iso(end),
            })).Arr().ToList();
            HistoryStore.MergeSpeedTests(NetworkMonitor.SpeedTests(S.NetworkId, tests)); // keep them past eero's ~30 days
            rows =tests.OrderByDescending(t => t.Dt("date"))
                .Select(t => new SpeedRow(J.Fmt(t.Dt("date")), Num(t.D("down_mbps")), Num(t.D("up_mbps")))).ToList();
        });
        SpeedGrid.ItemsSource = rows;
        SpeedSummary.Text = rows.Count == 0 ? "No speed tests in the last 30 days." : $"{rows.Count} tests in the last 30 days";
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string Num(double? d) => d is null ? "" : d.Value.ToString("0.0", CultureInfo.CurrentCulture);

    private (DateTime Start, DateTime End, string Cadence) Range()
    {
        var end = DateTime.UtcNow;
        return RangeCombo.SelectedIndex switch
        {
            1 => (end.AddDays(-7), end, "daily"),
            2 => (end.AddDays(-30), end, "daily"),
            _ => (end.AddHours(-24), end, "hourly"),
        };
    }

    private async void RunSpeedTest_Click(object sender, RoutedEventArgs e)
    {
        var ok = await Run("Starting speed test…", () => Api.PostActionAsync(S.Res("speedtest")),
            "Speed test started — the result will appear here in about a minute.");
        if (!ok) return;
        await Task.Delay(TimeSpan.FromSeconds(60));
        await LoadAsync();
    }

    private async void Usage_Click(object sender, RoutedEventArgs e)
    {
        var (start, end, cadence) = Range();
        JsonNode? data = null;
        if (!await Run("Loading data usage…", async () => data = await Api.GetAsync($"2.2/networks/{S.NetworkId}/data_usage", new Dictionary<string, string?>
            {
                ["start"] = Iso(start),
                ["end"] = Iso(end),
                ["cadence"] = cadence,
                ["timezone"] = S.Network.S("timezone.value") ?? TimeZoneInfo.Local.Id,
            }))) return;
        DetailGrid.ItemsSource = Flatten(data);
    }

    private async void UsageDevices_Click(object sender, RoutedEventArgs e)
    {
        var (start, end, cadence) = Range();
        JsonNode? data = null;
        if (!await Run("Loading usage by device…", async () => data = await Api.GetAsync($"2.2/networks/{S.NetworkId}/data_usage/breakdown", new Dictionary<string, string?>
            {
                ["start"] = Iso(start),
                ["end"] = Iso(end),
                ["cadence"] = cadence,
                ["timezone"] = S.Network.S("timezone.value") ?? TimeZoneInfo.Local.Id,
            }))) return;
        DetailGrid.ItemsSource = Flatten(data);
    }

    private async void Notifications_Click(object sender, RoutedEventArgs e)
    {
        JsonNode? data = null;
        if (!await Run("Loading notifications…", async () => data = await Api.GetAsync($"2.2/networks/{S.NetworkId}/notifications_history",
                new Dictionary<string, string?> { ["limit"] = "50" }))) return;
        var items = data.Arr("notifications").Concat(data.Arr("data")).Concat(data.Arr()).ToList();
        DetailGrid.ItemsSource = items.Count > 0
            ? items.Select(n => new KeyValuePair<string, string>(
                J.Fmt(n.Dt("created") ?? n.Dt("timestamp") ?? n.Dt("date")),
                n.S("title") is { } t ? $"{t} — {n.S("body") ?? n.S("message") ?? ""}" : n.ToJsonString())).ToList()
            : Flatten(data);
    }

    /// <summary>Flattens arbitrary JSON into path/value rows so unfamiliar responses are still readable.</summary>
    private static List<KeyValuePair<string, string>> Flatten(JsonNode? node, int limit = 2000)
    {
        var rows = new List<KeyValuePair<string, string>>();
        void Walk(JsonNode? n, string path)
        {
            if (rows.Count >= limit) return;
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o) Walk(v, path.Length == 0 ? k : $"{path}.{k}");
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    break;
                default:
                    rows.Add(new(path, n?.ToString() ?? "null"));
                    break;
            }
        }
        Walk(node, "");
        return rows;
    }
}
