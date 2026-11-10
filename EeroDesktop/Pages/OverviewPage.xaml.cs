using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class OverviewPage : PageBase
{
    public OverviewPage() => InitializeComponent();

    public override string Title => "Overview";

    public override async Task LoadAsync()
    {
        await Run("Loading network…", async () => await S.ReloadNetworkAsync());
        Render();
    }

    private void Render()
    {
        var n = S.Network;
        if (n is null) return;

        NameText.Text = n.S("name") ?? S.Selected?.Name ?? "Network";
        var eeroCount = n.Arr("eeros").Count();
        SubText.Text = $"{eeroCount} eero{(eeroCount == 1 ? "" : "s")} · {n.I("connected_device_count.on_network") ?? n.I("clients.count") ?? 0} devices online";

        InternetFacts.ItemsSource = Facts(
            ("Internet status", J.Title(n.S("health.internet.status"))),
            ("ISP reachable", n.NB("health.internet.isp_up") switch { true => "Yes", false => "No", _ => "" }),
            ("eero network", J.Title(n.S("health.eero_network.status"))),
            ("Public IP", n.S("ip_settings.public_ip") ?? n.S("wan_ip")),
            ("WAN IP", n.S("wan_ip")),
            ("WAN type", J.Title(n.S("wan_type"))),
            ("Connection mode", J.Title(n.S("connection.mode"))),
            ("Double NAT", n.B("ip_settings.double_nat") ? "Detected" : "No"),
            ("ISP / location", string.Join(", ", new[] { n.S("geo_ip.isp"), n.S("geo_ip.city"), n.S("geo_ip.region") }.Where(s => !string.IsNullOrEmpty(s)))));

        SpeedFacts.ItemsSource = Facts(
            ("Download", Speed(n, "speed.down")),
            ("Upload", Speed(n, "speed.up")),
            ("Tested", J.Fmt(n.Dt("speed.date"))),
            ("Status", n.S("speed.status") is { } st && !st.Equals("none", StringComparison.OrdinalIgnoreCase) ? J.Title(st) : null));

        NetworkFacts.ItemsSource = Facts(
            ("Gateway IP", n.S("gateway_ip") ?? n.S("lease.dhcp.router")),
            ("DHCP mode", J.Title(n.S("dhcp.mode"))),
            ("DNS mode", J.Title(n.S("dns.mode"))),
            ("Guest network", n.B("guest_network.enabled") ? $"On ({n.S("guest_network.name")})" : "Off"),
            ("IPv6", n.B("ipv6_upstream") ? "On" : "Off"),
            ("Time zone", n.S("timezone.value")),
            ("Last restart", J.Fmt(n.Dt("last_reboot"))),
            ("eero Plus", J.Title(n.S("premium_status"))));

        SoftwareFacts.ItemsSource = Facts(
            ("Firmware", n.S("updates.target_firmware") ?? n.Arr("eeros").FirstOrDefault().S("os_version")),
            ("Update available", n.B("updates.has_update") || n.Arr("eeros").Any(e => e.B("update_available")) ? "Yes" : "No"),
            ("Update status", J.Title(n.S("updates.update_status"))),
            ("Scheduled update", J.Fmt(n.Dt("updates.scheduled_update_time"))),
            ("Preferred update hour", n.I("updates.preferred_update_hour") is { } h ? $"{h:00}:00" : ""));
    }

    private static string Speed(System.Text.Json.Nodes.JsonNode n, string path)
        => n.D(path + ".value") is { } v ? $"{v:0.#} {n.S(path + ".units") ?? "Mbps"}" : "";

    private static List<KeyValuePair<string, string>> Facts(params (string Key, string? Value)[] items)
        => items.Where(i => !string.IsNullOrEmpty(i.Value)).Select(i => new KeyValuePair<string, string>(i.Key, i.Value!)).ToList();

    private async void SpeedTest_Click(object sender, RoutedEventArgs e)
    {
        var before = S.Network.S("speed.date");
        var ok = await Run("Starting speed test…", () => Api.PostActionAsync(S.Res("speedtest")),
            "Speed test started — checking for results every 10 seconds…");
        if (!ok) return;

        // Poll until eero reports a newer result (usually ~1 minute), instead of waiting a fixed time.
        var started = DateTime.Now;
        while (DateTime.Now - started < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try { await S.ReloadNetworkAsync(); }
            catch (Exception) { continue; } // transient errors: keep polling
            if (S.Network.S("speed.date") is { } now && now != before)
            {
                Render();
                S.Report($"Speed test finished: {Speed(S.Network!, "speed.down")} down, {Speed(S.Network!, "speed.up")} up.");
                return;
            }
            S.Report($"Speed test running… {(int)(DateTime.Now - started).TotalSeconds} s");
        }
        Render();
        S.Report("Speed test is taking longer than usual — press Refresh later to see the result.");
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Restart every eero on this network? Everyone will be offline for a few minutes.", "Restart Network")) return;
        await Run("Restarting network…", () => Api.PostActionAsync(S.Res("reboot")), "Restart requested. The network will be back in a few minutes.");
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var d = new FormDialog(Owner, "Rename Network").Text("name", "Network name", S.Network.S("name"), required: true);
        if (!d.Prompt()) return;
        await Run("Renaming network…", async () =>
        {
            await Api.PutFormAsync(S.NetworkUrl, new Dictionary<string, string?> { ["name"] = d.Get("name") });
            await S.ReloadNetworkAsync();
        }, "Network renamed.");
        Render();
    }

    private void Raw_Click(object sender, RoutedEventArgs e) => ShowJson("Network JSON", S.Network);
}
