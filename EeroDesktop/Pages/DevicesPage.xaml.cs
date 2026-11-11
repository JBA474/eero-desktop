using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class DevicesPage : PageBase
{
    public sealed record Row(JsonObject Json, string Name, string Status, string Ip, string Mac, string Manufacturer,
        string Connection, string Source, string Profile, string LastActive, bool Online, bool Paused, bool Blocked);

    private List<Row> _all = [];

    public DevicesPage()
    {
        InitializeComponent();
        HostActions.Attach(Grid, o => o is Row r ? new HostTarget(r.Name, r.Ip) : null);
    }

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

    /// <summary>The name shown for a device. Underscores read as spaces ("Android_7I5KBBQS" → "Android 7I5KBBQS").</summary>
    public static string DeviceName(JsonNode d) => Readable(
           d.S("nickname") is { Length: > 0 } n ? n
         : d.S("display_name") is { Length: > 0 } dn ? dn
         : d.S("hostname") is { Length: > 0 } h ? h
         : d.S("model_name") is { Length: > 0 } m ? m
         : d.S("mac") ?? "Unknown device");

    public static string Readable(string name) =>
        string.Join(' ', name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } s ? s : name;

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
        if (!IsInitialized || SearchBox is null || Grid is null) return; // Checked fires while the page is still being built
        var q = SearchBox.Text.Trim();
        var onlineOnly = OnlineOnly.IsChecked == true;
        var needsName = NeedsNameOnly.IsChecked == true;
        var rows = _all.Where(r => (!onlineOnly || r.Online || r.Blocked || r.Paused) && (!needsName || NeedsName(r.Json))
            && (q.Length == 0 || r.Name.Contains(q.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) || r.Ip.Contains(q) || r.Mac.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Manufacturer.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Profile.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        Grid.ItemsSource = rows;
        var unnamed = _all.Count(r => NeedsName(r.Json));
        CountText.Text = $"{_all.Count(r => r.Online)} online · {_all.Count} total · {_all.Count(r => r.Paused)} paused · {_all.Count(r => r.Blocked)} blocked"
                         + (unnamed > 0 ? $" · {unnamed} need a name" : "");
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
        var suggestion = NeedsName(r.Json) ? SuggestName(r.Json) : null;
        var d = new FormDialog(Owner, "Rename Device", suggestion is null ? "" : $"Suggested from what eero knows about it: \"{suggestion}\". Change it to anything you like.")
            .Text("name", "Device name", suggestion ?? r.Json.S("nickname") ?? r.Name, required: true);
        if (!d.Prompt()) return;
        var oldNick = r.Json.S("nickname") ?? "";
        var newName = d.Get("name");
        if (await Run("Renaming…", () => Api.PutJsonAsync(DeviceUrl(r), new JsonObject { ["nickname"] = newName }), "Device renamed."))
            UndoService.RecordRequest($"Renamed {r.Name} to {newName}", $"Rename it back to {r.Name}", $"Renamed {newName} back to {r.Name}", $"Rename it to {newName} again",
                new("PUT", DeviceUrl(r), new JsonObject { ["nickname"] = oldNick }), new("PUT", DeviceUrl(r), new JsonObject { ["nickname"] = newName }));
        await LoadAsync();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        var pause = !r.Paused;
        if (await Run(pause ? $"Pausing {r.Name}…" : $"Resuming {r.Name}…",
            () => Api.PutJsonAsync(DeviceUrl(r), new JsonObject { ["paused"] = pause }),
            pause ? $"{r.Name} is paused." : $"{r.Name} is back online."))
            UndoService.RecordRequest(pause ? $"Paused {r.Name}" : $"Resumed {r.Name}", pause ? $"Resume {r.Name}" : $"Pause {r.Name} again",
                pause ? $"Resumed {r.Name}" : $"Paused {r.Name} again", pause ? $"Pause {r.Name} again" : $"Resume {r.Name}",
                new("PUT", DeviceUrl(r), new JsonObject { ["paused"] = !pause }), new("PUT", DeviceUrl(r), new JsonObject { ["paused"] = pause }));
        await LoadAsync();
    }

    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } r) return;
        if (r.Blocked)
        {
            if (await Run($"Unblocking {r.Name}…", () => Api.DeleteAsync($"{S.NetworkUrl}/blacklist/{r.Mac}"), $"{r.Name} is unblocked."))
                UndoService.RecordRequest($"Unblocked {r.Name}", $"Block {r.Name} again", $"Blocked {r.Name} again", $"Unblock {r.Name}", BlockRequest(r), UnblockRequest(r));
        }
        else
        {
            if (!Confirm($"Block {r.Name} ({r.Mac}) from this network? It will be disconnected and unable to rejoin until unblocked.", "Block Device")) return;
            if (await Run($"Blocking {r.Name}…", () => Api.PostFormAsync($"{S.NetworkUrl}/blacklist", new Dictionary<string, string?> { ["mac"] = r.Mac }), $"{r.Name} is blocked."))
                UndoService.RecordRequest($"Blocked {r.Name}", $"Unblock {r.Name}", $"Unblocked {r.Name}", $"Block {r.Name} again", UnblockRequest(r), BlockRequest(r));
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
        JsonNode? oldRef = r.Json.S("profile.url") is { } oldUrl ? new JsonObject { ["url"] = oldUrl } : null;
        var oldName = string.IsNullOrEmpty(r.Profile) ? none : r.Profile;
        if (await Run("Updating profile…", () => Api.PutJsonAsync(DeviceUrl(r), new JsonObject { ["profile"] = profileRef?.DeepClone() }), "Profile updated."))
            UndoService.RecordRequest($"Moved {r.Name} to {choice}", $"Move {r.Name} back to {oldName}", $"Moved {r.Name} back to {oldName}", $"Move {r.Name} to {choice} again",
                new("PUT", DeviceUrl(r), new JsonObject { ["profile"] = oldRef }), new("PUT", DeviceUrl(r), new JsonObject { ["profile"] = profileRef }));
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

    private UndoService.Request BlockRequest(Row r) => new("POST", $"{S.NetworkUrl}/blacklist", Form: new() { ["mac"] = r.Mac });
    private UndoService.Request UnblockRequest(Row r) => new("DELETE", $"{S.NetworkUrl}/blacklist/{r.Mac}");

    // ---------- devices that need a name ----------

    private static readonly System.Text.RegularExpressions.Regex Generated = new(
        @"^[0-9a-f]{2}([:-]?[0-9a-f]{2}){5}$"                                  // a MAC address
        + @"|\.[a-z]{2,}\.?$"                                                    // a DNS hostname ("lp05vu8c.adhcscint.net.")
        + @"|^(esp|espressif|wlan|unknown|device|host|localhost|dhcp|lwip|tuya|wiz)[-_ ]?[0-9a-f]{4,}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex RandomToken = new(@"[-_ ]([A-Za-z0-9]{8,})$");

    /// <summary>
    /// True when the device has no name you gave it and eero only knows a machine-made one: a MAC address, a network
    /// hostname such as "lp05vu8c.adhcscint.net.", or a random-looking default like "Android_7I5KBBQS" or "ESP_3A9F21".
    /// </summary>
    public static bool NeedsName(JsonNode d)
    {
        if (d.S("nickname") is { Length: > 0 }) return false;
        var name = (d.S("display_name") is { Length: > 0 } dn ? dn : d.S("hostname") ?? "").Trim();
        if (name.Length == 0 || Generated.IsMatch(name)) return true;
        // "Android_7I5KBBQS": a suffix of 8+ letters and digits mixed together looks random.
        var token = RandomToken.Match(name).Groups[1].Value;
        return token.Length >= 8 && token.Any(char.IsDigit) && token.Any(char.IsLetter);
    }

    /// <summary>A starting name from the manufacturer and device type, e.g. "Intel Laptop" or "Apple iPhone".</summary>
    public static string SuggestName(JsonNode d)
    {
        var maker = (d.S("manufacturer") ?? "").Split(',', '(')[0].Trim();
        foreach (var suffix in new[] { " Inc.", " Inc", " Corporate", " Corporation", " Corp.", " Co., Ltd.", " Ltd.", " Technologies", " Technology", " Electronics", " LLC", " Limited" })
            if (maker.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) maker = maker[..^suffix.Length].Trim();
        var type = J.Title(d.S("device_type") ?? "");
        if (type.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || type.Equals("Generic", StringComparison.OrdinalIgnoreCase)) type = "";
        var host = (d.S("hostname") ?? "").ToLowerInvariant();
        if (type.Length == 0) type = host.StartsWith("iphone") ? "iPhone" : host.StartsWith("ipad") ? "iPad" : host.StartsWith("android") ? "Android Phone" : "";
        var name = string.Join(" ", new[] { maker, type }.Where(s => s.Length > 0));
        return name.Length > 0 ? name : "New Device";
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is Row r) ShowJson($"Device — {r.Name}", r.Json);
    }
}
