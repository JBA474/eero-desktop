using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class PortsPage : PageBase
{
    public sealed record ReservationRow(JsonObject Json, string Description, string Ip, string Mac);
    public sealed record ForwardRow(JsonObject Json, string Description, string Ip, string GatewayPort, string ClientPort, string Protocol, string Enabled);
    public sealed record PinholeRow(JsonObject Json, string Device, string Port, string Protocol, string Service);

    /// <summary>Every device on the network keyed by its API URL and by its id (the MAC without colons).</summary>
    private static async Task<Dictionary<string, JsonObject>> DevicesByUrlAsync()
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var devices = (await Api.GetAsync(S.Res("devices"), new Dictionary<string, string?> { ["thread"] = "false", ["proxied_node"] = "false" })).Arr();
            foreach (var d in devices)
            {
                if (d.S("url") is { Length: > 0 } url) map[url.TrimStart('/')] = d;
                if (d.S("mac") is { Length: > 0 } mac) map[mac.Replace(":", "")] = d;
            }
        }
        catch (EeroApiException) { /* names are a nicety; fall back to the MAC */ }
        return map;
    }

    /// <summary>"NAS (90:09:d0:58:1e:bb)" from "/2.2/networks/…/devices/9009d0581ebb".</summary>
    public static string PinholeDevice(string? deviceUrl, IReadOnlyDictionary<string, JsonObject> devices)
    {
        if (string.IsNullOrEmpty(deviceUrl)) return "";
        var id = deviceUrl.TrimEnd('/').Split('/')[^1];
        var mac = id.Length == 12 && id.All(Uri.IsHexDigit) ? string.Join(":", Enumerable.Range(0, 6).Select(i => id.Substring(i * 2, 2))).ToLowerInvariant() : id;
        var d = devices.GetValueOrDefault(deviceUrl.TrimStart('/')) ?? devices.GetValueOrDefault(id);
        return d is null ? $"Unknown device ({mac})" : $"{DevicesPage.DeviceName(d)} ({mac})";
    }

    /// <summary>Well-known services by port, so a pinhole or forward says what it's for.</summary>
    public static string PortService(string? port) => port switch
    {
        "32400" => "Plex Media Server",
        "8096" or "8920" => "Jellyfin / Emby",
        "5000" => "Synology DSM (HTTP)",
        "5001" => "Synology DSM (HTTPS)",
        "22" => "SSH",
        "80" => "Web (HTTP)",
        "443" => "Web (HTTPS)",
        "3389" => "Remote Desktop",
        "51820" => "WireGuard VPN",
        "1194" => "OpenVPN",
        "25565" => "Minecraft",
        "8123" => "Home Assistant",
        _ => "",
    };

    private static readonly string[] Protocols = ["tcp", "udp", "both"];

    public PortsPage()
    {
        InitializeComponent();
        HostActions.Attach(ReservationGrid, o => o is ReservationRow r ? new HostTarget(r.Description.Length > 0 ? r.Description : r.Ip, r.Ip) : null);
        // A forward's browser links go to its internal port (e.g. Plex at :32400) when it forwards TCP.
        HostActions.Attach(ForwardGrid, o => o is ForwardRow f
            ? new HostTarget(f.Description.Length > 0 ? f.Description : f.Ip, f.Ip,
                !f.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase) && int.TryParse(f.ClientPort, out var p) ? p : null)
            : null);
    }

    public override string Title => "Reservations & Ports";

    public override async Task LoadAsync()
    {
        List<ReservationRow> res = [];
        List<ForwardRow> fwd = [];
        List<PinholeRow> pin = [];
        await Run("Loading reservations and port forwards…", async () =>
        {
            res = (await Api.GetAsync(S.Res("reservations"))).Arr()
                .Select(r => new ReservationRow(r, r.S("description") ?? "", r.S("ip") ?? "", r.S("mac") ?? ""))
                .OrderBy(r => r.Ip).ToList();
            fwd = (await Api.GetAsync(S.Res("forwards"))).Arr()
                .Select(f => new ForwardRow(f, f.S("description") ?? "", f.S("ip") ?? "", f.S("gateway_port") ?? "", f.S("client_port") ?? "",
                    (f.S("protocol") ?? "").ToUpperInvariant(), f.B("enabled", true) ? "Yes" : "No"))
                .ToList();
            try
            {
                var pinholes = (await Api.GetAsync(S.NetworkUrl + "/ipv6/pinholes")).Arr().ToList();
                // eero names the device only by its API URL (…/devices/9009d0581ebb); look up its name.
                var devices = pinholes.Count == 0 ? [] : await DevicesByUrlAsync();
                pin = pinholes.Select(p => new PinholeRow(p, PinholeDevice(p.S("device"), devices), p.S("port") ?? "",
                        (p.S("protocol") ?? "").ToUpperInvariant(), PortService(p.S("port")))).ToList();
            }
            catch (EeroApiException) { /* IPv6 pinholes not available */ }
        });
        ReservationGrid.ItemsSource = res;
        ForwardGrid.ItemsSource = fwd;
        PinholeGrid.ItemsSource = pin;
    }

    // ----- reservations -----

    private async void AddReservation_Click(object sender, RoutedEventArgs e)
    {
        var d = new FormDialog(Owner, "Add IP Reservation", "Tip: you can also reserve an IP from the Devices page.")
            .Text("mac", "Device MAC address", required: true)
            .Text("ip", "IP address", required: true)
            .Text("desc", "Description");
        if (!d.Prompt()) return;
        await Run("Creating reservation…", () => Api.PostJsonAsync(S.Res("reservations"),
            new JsonObject { ["mac"] = d.Get("mac"), ["ip"] = d.Get("ip"), ["description"] = d.Get("desc") }), "Reservation created.");
        await LoadAsync();
    }

    private async void EditReservation_Click(object sender, RoutedEventArgs e)
    {
        if (ReservationGrid.SelectedItem is not ReservationRow r) { S.Report("Select a reservation first."); return; }
        var d = new FormDialog(Owner, "Edit IP Reservation")
            .Text("ip", "IP address", r.Ip, required: true)
            .Text("desc", "Description", r.Description);
        if (!d.Prompt()) return;
        var body = (JsonObject)r.Json.DeepClone();
        body["ip"] = d.Get("ip");
        body["description"] = d.Get("desc");
        await Run("Saving reservation…", () => Api.PutJsonAsync(r.Json.S("url")!, body), "Reservation saved.");
        await LoadAsync();
    }

    private async void DeleteReservation_Click(object sender, RoutedEventArgs e)
    {
        if (ReservationGrid.SelectedItem is not ReservationRow r) { S.Report("Select a reservation first."); return; }
        var forwards = ((IEnumerable<ForwardRow>?)ForwardGrid.ItemsSource ?? []).Count(f => f.Ip == r.Ip);
        var deleteForwards = false;
        if (forwards > 0)
        {
            var answer = MessageBox.Show(Owner!, $"{forwards} port forward(s) point to {r.Ip}. Delete them too?\n\nYes = delete reservation and forwards\nNo = delete only the reservation",
                "Delete Reservation", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            deleteForwards = answer == MessageBoxResult.Yes;
        }
        else if (!Confirm($"Delete the reservation for {r.Description} ({r.Ip})?", "Delete Reservation")) return;

        await Run("Deleting reservation…", () => Api.DeleteAsync(r.Json.S("url")!,
            new Dictionary<string, string?> { ["delete_forwards"] = EeroClient.Bool(deleteForwards) }), "Reservation deleted.");
        await LoadAsync();
    }

    // ----- port forwards -----

    private FormDialog ForwardDialog(string title, JsonNode? f) => new FormDialog(Owner, title,
            "Port forwarding requires the device to have an IP reservation. Ports can be a single port or a range like 8000-8010.")
        .Text("desc", "Description", f.S("description"))
        .Text("ip", "Device IP (reserved)", f.S("ip"), required: true)
        .Text("gw", "External (gateway) port", f.S("gateway_port"), required: true)
        .Text("cl", "Internal (device) port", f.S("client_port"), required: true)
        .Choice("proto", "Protocol", Protocols, f.S("protocol") ?? "both")
        .Check("enabled", "Enabled", f?.B("enabled", true) ?? true);

    private async void AddForward_Click(object sender, RoutedEventArgs e)
    {
        var d = ForwardDialog("Add Port Forward", null);
        if (!d.Prompt()) return;
        await Run("Creating port forward…", () => Api.PostJsonAsync(S.Res("forwards"), new JsonObject
        {
            ["description"] = d.Get("desc"),
            ["ip"] = d.Get("ip"),
            ["gateway_port"] = d.Get("gw"),
            ["client_port"] = d.Get("cl"),
            ["protocol"] = d.Get("proto"),
            ["enabled"] = d.GetBool("enabled"),
        }), "Port forward created.");
        await LoadAsync();
    }

    private async void EditForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForwardGrid.SelectedItem is not ForwardRow f) { S.Report("Select a port forward first."); return; }
        var d = ForwardDialog("Edit Port Forward", f.Json);
        if (!d.Prompt()) return;
        var body = (JsonObject)f.Json.DeepClone();
        body["description"] = d.Get("desc");
        body["ip"] = d.Get("ip");
        body["gateway_port"] = d.Get("gw");
        body["client_port"] = d.Get("cl");
        body["protocol"] = d.Get("proto");
        body["enabled"] = d.GetBool("enabled");
        await Run("Saving port forward…", () => Api.PutJsonAsync(f.Json.S("url")!, body), "Port forward saved.");
        await LoadAsync();
    }

    private async void ToggleForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForwardGrid.SelectedItem is not ForwardRow f) { S.Report("Select a port forward first."); return; }
        var body = (JsonObject)f.Json.DeepClone();
        body["enabled"] = !f.Json.B("enabled", true);
        await Run("Updating port forward…", () => Api.PutJsonAsync(f.Json.S("url")!, body), "Port forward updated.");
        await LoadAsync();
    }

    private async void DeleteForward_Click(object sender, RoutedEventArgs e)
    {
        if (ForwardGrid.SelectedItem is not ForwardRow f) { S.Report("Select a port forward first."); return; }
        if (!Confirm($"Delete the port forward \"{f.Description}\" ({f.GatewayPort} → {f.Ip}:{f.ClientPort})?", "Delete Port Forward")) return;
        await Run("Deleting port forward…", () => Api.DeleteAsync(f.Json.S("url")!), "Port forward deleted.");
        await LoadAsync();
    }

    // ----- IPv6 pinholes -----

    /// <summary>
    /// Opens a port to one device over IPv6. Body as in the eero app (NetRules.Pinhole: url, device, protocol, port),
    /// POSTed to the network's pinholes list; device is the device's API URL.
    /// </summary>
    private async void AddPinhole_Click(object sender, RoutedEventArgs e)
    {
        Dictionary<string, JsonObject> devices = [];
        if (!await Run("Loading devices…", async () => devices = await DevicesByUrlAsync())) return;
        var choices = devices.Values.Distinct().Where(d => d.S("url") is { Length: > 0 })
            .Select(d => (Label: $"{DevicesPage.DeviceName(d)} ({d.S("mac")})", Url: d.S("url")!))
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (choices.Count == 0) { S.Report("No devices found."); return; }
        var d = new FormDialog(Owner, "Add IPv6 Pinhole",
                "Lets devices on the internet reach this device's port over IPv6 (IPv4 uses port forwarding instead). Only open ports for services you run, such as Plex (32400).")
            .Choice("device", "Device", choices.Select(c => c.Label).ToList(), choices[0].Label)
            .Text("port", "Port (or range like 8000-8010)", required: true)
            .Choice("proto", "Protocol", Protocols, "tcp");
        if (!d.Prompt()) return;
        var port = d.Get("port").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(port, @"^\d{1,5}(-\d{1,5})?$")) { S.Report("Enter a port number such as 32400, or a range such as 8000-8010."); return; }
        var device = choices.First(c => c.Label == d.Get("device"));
        var body = new JsonObject { ["url"] = "", ["device"] = device.Url, ["protocol"] = d.Get("proto"), ["port"] = port };
        await Run("Creating pinhole…", () => Api.PostJsonAsync(S.NetworkUrl + "/ipv6/pinholes", body),
            $"Pinhole created: {device.Label}, port {port}{(PortService(port) is { Length: > 0 } svc ? $" ({svc})" : "")}.");
        await LoadAsync();
    }

    private async void DeletePinhole_Click(object sender, RoutedEventArgs e)
    {
        if (PinholeGrid.SelectedItem is not PinholeRow p) { S.Report("Select a pinhole first."); return; }
        if (!Confirm($"Delete the IPv6 pinhole for port {p.Port}?", "Delete Pinhole")) return;
        await Run("Deleting pinhole…", () => Api.DeleteAsync(p.Json.S("url")!), "Pinhole deleted.");
        await LoadAsync();
    }

    private async void Rules_Click(object sender, RoutedEventArgs e)
    {
        JsonNode? data = null;
        if (await Run("Loading firewall rules…", async () => data = await Api.GetAsync(S.NetworkUrl + "/routing")))
            ShowJson("Reservations & Firewall Rules", data);
    }
}
