using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class DevicesPage : PageBase
{
    public sealed record Row(JsonObject Json, string Name, string Status, string Ip, string Mac, string Manufacturer,
        string Connection, string Source, string Profile, string LastActive, bool Online, bool Paused, bool Blocked);

    private List<Row> _all = [];

    public DevicesPage() => InitializeComponent();

    public override string Title => "Devices";

    public override async Task LoadAsync()
    {
        await Run("Loading devices…", async () =>
        {
            var devices = (await Api.GetAsync(S.Res("devices"), new Dictionary<string, string?> { ["thread"] = "false", ["proxied_node"] = "false" })).Arr().ToList();
            // Blocked devices may not appear in the normal list.
            try
            {
                var blocked = (await Api.GetAsync(S.NetworkUrl + "/blacklist")).Arr();
                var known = devices.Select(d => d.S("mac")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var b in blocked)
                {
                    if (known.Contains(b.S("mac"))) continue;
                    b["blacklisted"] = true;
                    devices.Add(b);
                }
            }
            catch (EeroApiException) { /* not supported on every network */ }
            _all = devices.Select(ToRow).OrderByDescending(r => r.Online).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        });
        ApplyFilter();
    }

    public static string DeviceName(JsonNode d)
        => d.S("nickname") is { Length: > 0 } n ? n
         : d.S("display_name") is { Length: > 0 } dn ? dn
         : d.S("hostname") is { Length: > 0 } h ? h
         : d.S("model_name") is { Length: > 0 } m ? m
         : d.S("mac") ?? "Unknown device";

    private static Row ToRow(JsonObject d)
    {
        var online = d.B("connected");
        var paused = d.B("paused");
        var blocked = d.B("blacklisted");
        var status = blocked ? "Blocked" : paused ? "Paused" : online ? "Online" : "Offline";
        string connection;
        if (d.B("wireless"))
        {
            var freq = d.S("interface.frequency");
            var unit = d.S("interface.frequency_unit") ?? "GHz";
            var bars = d.I("connectivity.score_bars");
            connection = $"Wi-Fi{(freq is null ? "" : $" {freq} {unit}")}{(bars is null ? "" : $" · {bars}/5")}";
        }
        else connection = d.S("connection_type") is { } ct ? J.Title(ct) : "Wired";
        if (d.B("is_guest")) connection += " (guest)";

        return new Row(d, DeviceName(d), status,
            d.S("ip") ?? d.S("ipv4") ?? "",
            d.S("mac") ?? "",
            d.S("manufacturer") ?? "",
            connection,
            d.S("source.location") ?? "",
            d.S("profile.name") ?? "",
            online ? "Now" : J.Ago(d.Dt("last_active")),
            online, paused, blocked);
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var onlineOnly = OnlineOnly.IsChecked == true;
        var rows = _all.Where(r => (!onlineOnly || r.Online || r.Blocked || r.Paused)
            && (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Ip.Contains(q) || r.Mac.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Manufacturer.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Profile.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        Grid.ItemsSource = rows;
        CountText.Text = $"{_all.Count(r => r.Online)} online · {_all.Count} total · {_all.Count(r => r.Paused)} paused · {_all.Count(r => r.Blocked)} blocked";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private Row? Selected()
    {
        if (Grid.SelectedItem is Row r) return r;
        MessageBox.Show(Owner!, "Select a device first.", "Devices", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private string DeviceUrl(Row r) => $"/2.3/networks/{S.NetworkId}/devices/{r.Mac}";

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var d = new FormDialog(Owner, "Rename Device").Text("name", "Device name", r.Json.S("nickname") ?? r.Name, required: true);
        if (!d.Prompt()) return;
        await Run("Renaming…", () => Api.PutJsonAsync(DeviceUrl(r), new JsonObject { ["nickname"] = d.Get("name") }), "Device renamed.");
        await LoadAsync();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var pause = !r.Paused;
        await Run(pause ? $"Pausing {r.Name}…" : $"Resuming {r.Name}…",
            () => Api.PutJsonAsync(DeviceUrl(r), new JsonObject { ["paused"] = pause }),
            pause ? $"{r.Name} is paused." : $"{r.Name} is back online.");
        await LoadAsync();
    }

    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.Blocked)
        {
            await Run($"Unblocking {r.Name}…", () => Api.DeleteAsync($"{S.NetworkUrl}/blacklist/{r.Mac}"), $"{r.Name} is unblocked.");
        }
        else
        {
            if (!Confirm($"Block {r.Name} ({r.Mac}) from this network? It will be disconnected and unable to rejoin until unblocked.", "Block Device")) return;
            await Run($"Blocking {r.Name}…", () => Api.PostFormAsync($"{S.NetworkUrl}/blacklist", new Dictionary<string, string?> { ["mac"] = r.Mac }), $"{r.Name} is blocked.");
        }
        await LoadAsync();
    }

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        List<JsonObject> profiles = [];
        if (!await Run("Loading profiles…", async () => profiles = (await Api.GetAsync(S.Res("profiles"))).Arr().ToList())) return;
        const string none = "(No profile)";
        var names = new List<string> { none };
        names.AddRange(profiles.Select(p => p.S("name") ?? "?"));
        var d = new FormDialog(Owner, "Assign Profile", $"Choose a profile for {r.Name}.")
            .Choice("p", "Profile", names, string.IsNullOrEmpty(r.Profile) ? none : r.Profile);
        if (!d.Prompt()) return;
        var choice = d.Get("p");
        var profile = profiles.FirstOrDefault(p => p.S("name") == choice);
        JsonNode? profileRef = profile is null ? null : new JsonObject { ["url"] = profile.S("url") };
        await Run("Updating profile…", () => Api.PutJsonAsync(DeviceUrl(r), new JsonObject { ["profile"] = profileRef }), "Profile updated.");
        await LoadAsync();
    }

    private async void Reserve_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var d = new FormDialog(Owner, "Reserve IP Address", $"Always give {r.Name} the same IP address.")
            .Text("ip", "IP address", r.Ip, required: true)
            .Text("desc", "Description", r.Name);
        if (!d.Prompt()) return;
        await Run("Creating reservation…", () => Api.PostJsonAsync(S.Res("reservations"),
            new JsonObject { ["mac"] = r.Mac, ["ip"] = d.Get("ip"), ["description"] = d.Get("desc") }), "IP reservation created.");
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is Row r) ShowJson($"Device — {r.Name}", r.Json);
    }
}
