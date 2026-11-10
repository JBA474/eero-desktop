using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class EerosPage : PageBase
{
    public sealed record Row(JsonObject Json, string Location, string Status, string Role, string Model, string Connection,
        string Ip, string Clients, string Firmware, string Light, string Serial, string LastReboot);

    public EerosPage() => InitializeComponent();

    public override string Title => "eeros";

    public override async Task LoadAsync()
    {
        List<Row> rows = [];
        await Run("Loading eeros…", async () =>
        {
            var url = S.TryRes("eeros");
            var list = url is not null ? (await Api.GetAsync(url)).Arr() : S.Network.Arr("eeros");
            rows = list.Select(ToRow).OrderByDescending(r => r.Role == "Gateway").ThenBy(r => r.Location).ToList();
        });
        Grid.ItemsSource = rows;
        // Only eero Beacons have a night light; hide the button when no eero on this network reports one.
        NightlightButton.Visibility = rows.Any(r => r.Json["nightlight"] is JsonObject) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Ports_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        new PortsWindow(Owner, r.Json).ShowDialog();
        await LoadAsync();
    }

    private static readonly string[] NightlightModes = ["Off", "Always on (adjusts to room light)", "On a schedule"];

    private async void Nightlight_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.Json["nightlight"] is not JsonObject nl)
        {
            MessageBox.Show(Owner!, $"The {r.Location} eero doesn't have a night light (only eero Beacon does).", "Nightlight", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var mode = !nl.B("enabled") ? NightlightModes[0] : nl.B("schedule.enabled") ? NightlightModes[2] : NightlightModes[1];
        var d = new FormDialog(Owner, $"Nightlight — {r.Location}", "Times are 24-hour HH:mm; brightness and times apply to the schedule.")
            .Choice("mode", "Mode", NightlightModes, mode)
            .Text("b", "Brightness (%)", (nl.I("brightness_percentage") ?? 50).ToString())
            .Text("on", "Turn on at", nl.S("schedule.on") ?? "20:00")
            .Text("off", "Turn off at", nl.S("schedule.off") ?? "07:00");
        if (!d.Prompt()) return;

        var chosen = d.Get("mode");
        JsonObject body;
        if (chosen == NightlightModes[0]) body = new JsonObject { ["enabled"] = false };
        else if (chosen == NightlightModes[1]) body = new JsonObject { ["enabled"] = true, ["schedule"] = new JsonObject { ["enabled"] = false } };
        else
        {
            if (!int.TryParse(d.Get("b"), out var b) || b is < 0 or > 100
                || !TimeOnly.TryParseExact(d.Get("on"), "HH:mm", out var on) || !TimeOnly.TryParseExact(d.Get("off"), "HH:mm", out var off))
            {
                MessageBox.Show(Owner!, "Enter a brightness from 0 to 100 and times like 20:00.", "Nightlight", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            body = new JsonObject
            {
                ["enabled"] = true,
                ["brightness_percentage"] = b,
                ["schedule"] = new JsonObject { ["enabled"] = true, ["on"] = on.ToString("HH:mm"), ["off"] = off.ToString("HH:mm") },
            };
        }
        // The nightlight sub-resource URL is published on the eero (eero-api); fall back to the Home Assistant path.
        var url = nl.S("url") ?? $"{r.Json.S("url")}/nightlight/settings";
        await Run("Saving nightlight…", () => Api.PutJsonAsync(url, body), "Nightlight saved.");
        await LoadAsync();
    }

    private static Row ToRow(JsonObject e)
    {
        var wired = e.B("wired") || string.Equals(e.S("connection_type"), "wired", StringComparison.OrdinalIgnoreCase);
        var bars = e.I("mesh_quality_bars");
        var connection = wired ? "Wired" : $"Wireless{(bars is null ? "" : $" ({bars}/5 bars)")}";
        var light = e.B("led_on") ? $"On {e.I("led_brightness")?.ToString() ?? ""}%".Replace(" %", "") : "Off";
        var model = e.S("model") ?? e.S("model_number") ?? "";
        if (e.S("model_number") is { } mn && mn != model) model += $" ({mn})";
        return new Row(e,
            e.S("location") ?? "(unnamed)",
            J.Title(e.S("status.value") ?? e.S("status") ?? e.S("state")),
            e.B("gateway") ? "Gateway" : "Node",
            model,
            connection,
            e.S("ip_address") ?? "",
            $"{e.I("connected_clients_count") ?? 0}",
            e.S("os_version") ?? "",
            light,
            e.S("serial") ?? "",
            J.Fmt(e.Dt("last_reboot")));
    }

    private Row? Selected()
    {
        if (Grid.SelectedItem is Row r) return r;
        MessageBox.Show(Owner!, "Select an eero first.", "eeros", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var warn = r.Role == "Gateway" ? " This is the gateway eero — the whole network will go offline while it restarts." : "";
        if (!Confirm($"Restart the eero in {r.Location}?{warn}", "Restart eero")) return;
        var url = r.Json.S("resources.reboot") ?? r.Json.S("url") + "/reboot";
        await Run($"Restarting {r.Location}…", () => Api.PostActionAsync(url), $"{r.Location} is restarting.");
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var d = new FormDialog(Owner, "Rename Location").Text("location", "Location name", r.Location, required: true);
        if (!d.Prompt()) return;
        await Run("Renaming…", () => Api.PutFormAsync(r.Json.S("url")!, new Dictionary<string, string?> { ["location"] = d.Get("location") }), "Location renamed.");
        await LoadAsync();
    }

    private async void Led_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var on = !r.Json.B("led_on");
        await Run(on ? "Turning status light on…" : "Turning status light off…",
            () => Api.PutFormAsync(LedUrl(r), new Dictionary<string, string?> { ["led_on"] = EeroClient.Bool(on) }));
        await LoadAsync();
    }

    private async void Brightness_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var d = new FormDialog(Owner, "Status Light Brightness", "Brightness from 0 to 100 (supported on newer eero models).")
            .Text("b", "Brightness (%)", (r.Json.I("led_brightness") ?? 100).ToString(), required: true);
        if (!d.Prompt()) return;
        if (!int.TryParse(d.Get("b"), out var b) || b < 0 || b > 100)
        {
            MessageBox.Show(Owner!, "Enter a number from 0 to 100.", "Brightness", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await Run("Setting brightness…", () => Api.PutFormAsync(LedUrl(r), new Dictionary<string, string?> { ["led_brightness"] = b.ToString() }));
        await LoadAsync();
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var id = J.IdFromUrl(r.Json.S("url"));
        JsonNode? data = null;
        if (await Run("Loading firmware history…", async () => data = await Api.GetAsync($"2.2/eeros/{id}/firmware_update/history")))
            ShowJson($"Firmware History — {r.Location}", data);
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is Row r) ShowJson($"eero — {r.Location}", r.Json);
    }

    private static string LedUrl(Row r) => r.Json.S("resources.led_action") ?? r.Json.S("url") + "/led";
}
