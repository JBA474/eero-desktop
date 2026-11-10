using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class UpdatesPage : PageBase
{
    public sealed record Row(string Location, string Model, string Firmware, string UpdateAvailable, string State);

    private JsonNode? _updates;

    public UpdatesPage() => InitializeComponent();

    public override string Title => "Updates";

    public override async Task LoadAsync()
    {
        await Run("Loading update status…", async () =>
        {
            await S.ReloadNetworkAsync();
            _updates = S.TryRes("updates") is { } u ? await Api.GetAsync(u) : S.Network.At("updates");
        });
        var u = _updates ?? S.Network.At("updates");
        var hour = u.I("preferred_update_hour");
        var available = u.B("has_update") || u.B("update_required") || S.Network.Arr("eeros").Any(e => e.B("update_available"));
        Facts.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("Update available", available ? "Yes" : "No"),
            new("Status", J.Title(u.S("update_status")) is { Length: > 0 } s ? s : "Up to date"),
            new("Target firmware", u.S("target_firmware") ?? ""),
            new("Update to", u.S("update_to_firmware") ?? ""),
            new("Minimum required", u.S("min_required_firmware") ?? ""),
            new("Scheduled for", J.Fmt(u.Dt("scheduled_update_time"))),
            new("Last update started", J.Fmt(u.Dt("last_update_started"))),
            new("Preferred update hour", hour is null ? "Automatic" : $"{hour:00}:00"),
        }.Where(kv => kv.Value.Length > 0).ToList();
        UpdateNowButton.IsEnabled = available || u.B("can_update_now");

        Grid.ItemsSource = S.Network.Arr("eeros").Select(e => new Row(
            e.S("location") ?? "",
            e.S("model") ?? e.S("model_number") ?? "",
            e.S("os_version") ?? "",
            e.B("update_available") ? "Yes" : "No",
            J.Title(e.S("update_status.value") ?? e.S("update_status.state") ?? ""))).ToList();
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Install the update now? Each eero restarts and the network will be offline for several minutes.", "Update Now")) return;
        await Run("Starting update…", () => Api.PostActionAsync(S.Res("updates")), "Update started. eeros will restart when it's ready.");
        await LoadAsync();
    }

    private async void PreferredHour_Click(object sender, RoutedEventArgs e)
    {
        var hours = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
        var current = (_updates ?? S.Network.At("updates")).I("preferred_update_hour");
        var d = new FormDialog(Owner, "Preferred Update Hour", "eero will try to install updates during this hour (network time zone).")
            .Choice("h", "Hour", hours, current is null ? "03:00" : $"{current:00}:00");
        if (!d.Prompt()) return;
        var hour = hours.IndexOf(d.Get("h"));
        if (hour == current) { S.Report($"Preferred update hour is already {hour:00}:00 — nothing was sent."); return; }
        await Run("Saving preferred hour…", () => Api.PostJsonAsync(S.Res("updates") + "/preferred_update_hour",
            new JsonObject { ["preferred_update_hour"] = hour }), "Preferred update hour saved.");
        await LoadAsync();
    }

    private void Raw_Click(object sender, RoutedEventArgs e) => ShowJson("Update Status", _updates ?? S.Network.At("updates"));
}
