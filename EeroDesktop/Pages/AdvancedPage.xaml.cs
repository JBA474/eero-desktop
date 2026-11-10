using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class AdvancedPage : PageBase
{
    /// <summary>How disruptive a change is, so the user can be warned before it's sent.</summary>
    public enum Risk { None, MayRestart, RestartsAll }

    /// <summary>
    /// A switchable network feature: where its current value is read from in the network JSON, and the single,
    /// minimal write that changes it (formats from the eero Android app, the Home Assistant integration and eero-api).
    /// </summary>
    public sealed record Feature(string Key, string Label, string Help, Func<JsonObject, bool?> Read, Func<bool, Task> Write,
        Risk Risk = Risk.MayRestart, string? Capability = null, bool NeedsPlus = false);

    public sealed record ToggleItem(Feature Feature, string Label, string Help, bool IsOn, bool IsAvailable, string Note)
    {
        public string AutomationId => "Toggle_" + Feature.Key;
    }

    /// <summary>AdGuard DNS "default" servers: block ads and trackers (https://adguard-dns.io/kb/general/dns-providers/).</summary>
    public static readonly string[] AdGuardDns = ["94.140.14.14", "94.140.15.15"];

    public AdvancedPage() => InitializeComponent();

    public override string Title => "Advanced";

    public override async Task LoadAsync()
    {
        await Run("Loading settings…", () => S.ReloadNetworkAsync());
        Render();
    }

    // ---------- feature definitions ----------

    private static Feature Setting(string field, string label, string help, string? capability = null)
        => new(field, label, help, n => n.NB(field), on => S.PutSettingsAsync(new JsonObject { [field] = on }), Risk.MayRestart, capability);

    private static IEnumerable<Feature> NetworkFeatures() =>
    [
        Setting("upnp", "UPnP", "Lets devices such as game consoles open ports automatically."),
        Setting("band_steering", "Band steering", "Moves capable devices to the faster 5 GHz band.", "band_steering"),
        Setting("wpa3", "WPA3", "Newer, stronger Wi-Fi security. Some older devices can't connect with it on.", "wpa3"),
        Setting("ipv6_upstream", "IPv6", "Use IPv6 when your ISP supports it.", "ipv6_editable"),
        // The eero app sends SQM as a query parameter with no body: updateSqm(@Url settings, @Query("sqm") boolean).
        new("sqm", "Optimize for conferencing and gaming (SQM)", "Smart queue management to reduce lag when the connection is busy.",
            n => n.NB("sqm"), on => Api.SendAsync(HttpMethod.Put, S.Res("settings") + "?sqm=" + EeroClient.Bool(on), null),
            Risk.MayRestart, "sqm"),
        Setting("nat_port_randomization", "NAT port randomization", "Randomize outbound NAT ports."),
        new("thread", "Thread", "eero's Thread border router for Matter and Thread smart-home devices.",
            n => n.NB("thread") ?? n.NB("thread.enabled"),
            // eero app: enableThreadNetwork(resources.thread + "/enable", { enabled }).
            on => Api.PutJsonAsync((S.TryRes("thread") ?? $"2.2/networks/{S.NetworkId}/thread") + "/enable", new JsonObject { ["enabled"] = on }),
            Risk.MayRestart, "thread_network"),
        new("power_saving", "Power saving", "Lets eeros reduce power use when the network is quiet.",
            n => n.NB("power_saving") ?? n.NB("power_saving.enabled"),
            on => Api.PutJsonAsync(S.NetworkUrl + "/power_saving", new JsonObject { ["enable"] = on }),
            Risk.None, "power_saving"),
    ];

    private static IEnumerable<Feature> DnsFeatures() =>
    [
        new("dns_caching", "Local DNS caching", "eeros remember recent lookups so websites start loading faster.",
            n => n.NB("dns.caching"),
            on => S.PutSettingsAsync(new JsonObject { ["dns"] = new JsonObject { ["caching"] = on } }),
            Risk.RestartsAll, "dns_caching"),
    ];

    private static IEnumerable<Feature> AccessFeatures() =>
    [
        new("ddns", "Dynamic DNS", "Gives your network a hostname that follows your public IP address, for reaching it from outside.",
            n => n.NB("ddns.enabled"),
            on => Api.SendAsync(HttpMethod.Put, S.NetworkUrl + "/ddns/" + (on ? "enable" : "disable"), null),
            Risk.None, "ddns_enabled"),
        new("backup_internet", "Backup internet", "Switch to a backup connection (such as a phone hotspot) when your internet goes down.",
            n => n.NB("backup_internet_enabled"),
            on => Api.PutJsonAsync(S.NetworkUrl + "/backupinternet", new JsonObject { ["backup_internet_enabled"] = on }),
            Risk.None, NeedsPlus: true),
    ];

    private static IEnumerable<Feature> AdBlockFeatures() =>
    [
        new("ad_block", "eero Plus ad blocking (whole network)", "eero's own ad and tracker blocking for every device. For one person only, use Content Filters on the Profiles page.",
            n => n.At("premium_dns.ad_block_settings") is null ? null
                 : n.B("premium_dns.ad_block_settings.enabled") && n.At("premium_dns.ad_block_settings.profiles") is not JsonArray { Count: > 0 },
            on => Api.PostJsonAsync(S.NetworkUrl + "/dns_policies/adblock", new JsonObject { ["enable"] = on }),
            Risk.None, NeedsPlus: true),
    ];

    private static IEnumerable<Feature> SecurityFeatures() =>
    [
        new("block_malware", "Block threats", "Blocks known malware, phishing and scam sites for every device.",
            n => n.NB("premium_dns.dns_policies.block_malware"),
            // eero app: updateNetworkDnsPolicySettings(@FieldMap) -- form fields, not JSON.
            on => Api.PostFormAsync(S.NetworkUrl + "/dns_policies/network", new Dictionary<string, string?> { ["block_malware"] = EeroClient.Bool(on) }),
            Risk.None, NeedsPlus: true),
    ];

    // ---------- rendering ----------

    private static List<ToggleItem> Items(IEnumerable<Feature> features)
    {
        var n = S.Network!;
        return features
            .Where(f => f.Read(n) is not null && (f.Capability is null || S.Capable(f.Capability))) // only what this network has
            .Select(f =>
            {
                var note = f.Risk switch
                {
                    Risk.RestartsAll => "Changing this restarts every eero.",
                    Risk.MayRestart => "Changing this may briefly restart your eeros.",
                    _ => "",
                };
                if (f.NeedsPlus && !S.HasEeroPlus) note = "Needs eero Plus. " + note;
                return new ToggleItem(f, J.Label(f.Label), f.Help, f.Read(n) == true, true, note.Trim());
            })
            .ToList();
    }

    private static List<string> CustomDns(JsonNode? n) => n.At("dns.custom.ips") is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : [];

    /// <summary>The ad-blocking DNS provider (AdGuard DNS or Control D) the network resolves through now, if any.</summary>
    public static DnsProvider? AdBlocker(JsonNode? n)
        => !string.Equals(n.S("dns.mode"), "custom", StringComparison.OrdinalIgnoreCase) ? null
            : DnsBenchmark.AdBlockers.FirstOrDefault(p => CustomDns(n).Any(ip => p.Servers.Contains(ip)));

    /// <summary>True when the network currently resolves through AdGuard's ad-blocking DNS.</summary>
    public static bool UsingAdGuard(JsonNode? n) => AdBlocker(n) == DnsBenchmark.AdGuard;

    private void Render()
    {
        var n = S.Network;
        if (n is null) return;
        AdBlockToggles.ItemsSource = Items(AdBlockFeatures());
        Toggles.ItemsSource = Items(NetworkFeatures());
        DnsToggles.ItemsSource = Items(DnsFeatures());
        AccessToggles.ItemsSource = Items(AccessFeatures());
        Policies.ItemsSource = Items(SecurityFeatures());

        var blocker = AdBlocker(n);
        AdBlockStatus.Text = blocker is null ? "DNS ad blocking is off." : $"Ads are being blocked with {blocker.Name}.";
        AdGuardOnButton.IsEnabled = blocker != DnsBenchmark.AdGuard;
        ControlDOnButton.IsEnabled = blocker != DnsBenchmark.ControlD;
        AdGuardOffButton.IsEnabled = blocker is not null;

        var parent = n.At("dns.parent.ips") is JsonArray p ? string.Join(", ", p.Select(x => x?.ToString())) : "";
        DnsFacts.ItemsSource = Facts(
            ("Mode", J.Title(n.S("dns.mode")) + (blocker is null ? "" : $" ({blocker.Name} ad blocking)")),
            ("Custom servers", string.Join(", ", CustomDns(n))),
            ("ISP servers", parent),
            ("Dynamic DNS name", n.B("ddns.enabled") ? n.S("ddns.subdomain") : ""));

        DhcpFacts.ItemsSource = Facts(
            ("Mode", J.Title(n.S("connection.mode") ?? n.S("dhcp.mode"))),
            ("DHCP", J.Title(n.S("dhcp.mode"))),
            ("Subnet", n.S("dhcp.custom.subnet_ip") is { } sub ? $"{sub} / {n.S("dhcp.custom.subnet_mask")}" : ""),
            ("Range", n.S("dhcp.custom.start_ip") is { } st ? $"{st} – {n.S("dhcp.custom.end_ip")}" : ""),
            ("Gateway IP", n.S("gateway_ip")),
            ("WAN lease", n.S("lease.mode") is { } lm ? J.Title(lm) : ""),
            ("IPv6 prefix", n.S("ipv6_lease.prefix")));

        PlusNote.Text = S.HasEeroPlus
            ? "Network-wide protection from your eero Plus subscription. Per-person filters are under Profiles › Content Filters."
            : $"These need an eero Plus subscription (this network's status: {J.Title(n.S("premium_status") ?? "unknown")}). eero will refuse changes without one. Per-person filters are under Profiles › Content Filters.";
    }

    private static List<KeyValuePair<string, string>> Facts(params (string Key, string? Value)[] items)
        => items.Where(i => !string.IsNullOrEmpty(i.Value)).Select(i => new KeyValuePair<string, string>(i.Key, i.Value!)).ToList();

    // ---------- actions ----------

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ToggleItem item } box) return;
        var f = item.Feature;
        var on = box.IsChecked == true;

        var warning = f.Risk switch
        {
            Risk.RestartsAll => "eero will restart every eero a few minutes after this change. Wi-Fi and internet will drop for everyone for a few minutes.",
            Risk.MayRestart => "eero may briefly restart your eeros to apply this, which drops Wi-Fi for a moment.",
            _ => null,
        };
        if (f.Key == "wpa3" && on) warning = "Older devices that don't support WPA3 may be unable to connect. " + warning;
        if (warning is not null && !Confirm($"Turn {(on ? "on" : "off")} {f.Label}?\n\n{warning}", f.Label)) { Render(); return; }

        await ChangeAsync(f.Label, on ? "on" : "off", n => f.Read(n) == on, () => f.Write(on), f.Risk == Risk.RestartsAll, UndoService.Toggle(f, on));
    }

    /// <summary>Every switch on this page, for "Undo Last Change".</summary>
    public static Feature? FindFeature(string key)
        => NetworkFeatures().Concat(DnsFeatures()).Concat(AccessFeatures()).Concat(AdBlockFeatures()).Concat(SecurityFeatures()).FirstOrDefault(f => f.Key == key);

    /// <summary>Runs one change through AppState.ChangeIfNeededAsync and reports what actually happened.</summary>
    private async Task ChangeAsync(string label, string state, Func<JsonObject, bool> alreadySet, Func<Task> write, bool restartsAll,
        Func<JsonObject, UndoService.Entry?>? undo = null)
    {
        var changed = false;
        var ok = await Run($"Changing {label}…", async () => changed = await S.ChangeIfNeededAsync(alreadySet, write, undo));
        if (ok)
            S.Report(!changed ? $"{label} is already {state} — nothing was sent."
                : $"{label} is now {state}." + (restartsAll ? " Your eeros will restart in a few minutes." : ""));
        Render();
    }

    private const string DnsWarning = "eero will restart every eero a few minutes after a DNS change. Wi-Fi and internet will drop for everyone for a few minutes.";

    private Task SetCustomDnsAsync(string label, IReadOnlyList<string> servers)
        => ChangeAsync(label, "custom (" + string.Join(", ", servers) + ")",
            n => string.Equals(n.S("dns.mode"), "custom", StringComparison.OrdinalIgnoreCase) && CustomDns(n).SequenceEqual(servers),
            () => S.SetCustomDnsAsync(servers), restartsAll: true);

    private Task SetAutomaticDnsAsync(string label)
        => ChangeAsync(label, "automatic",
            n => string.Equals(n.S("dns.mode"), "automatic", StringComparison.OrdinalIgnoreCase),
            () => S.SetAutomaticDnsAsync(), restartsAll: true);
    private async void AdGuardOn_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Block ads on every device by switching the network's DNS to AdGuard DNS (94.140.14.14, 94.140.15.15)?\n\n"
                     + "Your current DNS setting is replaced; \"Stop Blocking Ads\" switches back to automatic DNS.\n\n" + DnsWarning,
                "Block Ads with AdGuard DNS")) return;
        await SetCustomDnsAsync("Ad blocking (AdGuard DNS)", AdGuardDns);
    }

    private async void ControlDOn_Click(object sender, RoutedEventArgs e)
    {
        var p = DnsBenchmark.ControlD;
        if (!Confirm($"Block ads on every device by switching the network's DNS to Control D's free ad-blocking servers ({string.Join(", ", p.Servers)})?\n\n"
                     + "Your current DNS setting is replaced; \"Stop Blocking Ads\" switches back to automatic DNS, and Undo puts back what you have now.\n\n" + DnsWarning,
                "Block Ads with Control D")) return;
        await SetCustomDnsAsync("Ad blocking (Control D)", p.Servers);
    }

    private async void AdGuardOff_Click(object sender, RoutedEventArgs e)
    {
        var name = AdBlocker(S.Network)?.Name ?? "DNS";
        if (!Confirm("Stop blocking ads and go back to your ISP's automatic DNS servers?\n\n" + DnsWarning, "Stop Blocking Ads")) return;
        await SetAutomaticDnsAsync($"Ad blocking ({name}) off — DNS");
    }

    private async void CustomDns_Click(object sender, RoutedEventArgs e)
    {
        var current = CustomDns(S.Network);
        var d = new FormDialog(Owner, "Custom DNS Servers",
                "For example Cloudflare (1.1.1.1, 1.0.0.1), Google (8.8.8.8, 8.8.4.4), Quad9 (9.9.9.9, 149.112.112.112), or AdGuard ad blocking (94.140.14.14, 94.140.15.15).\n\n" + DnsWarning)
            .Text("p", "Primary DNS server", current.ElementAtOrDefault(0) ?? "", required: true)
            .Text("s", "Secondary DNS server", current.ElementAtOrDefault(1) ?? "");
        if (!d.Prompt()) return;
        await SetCustomDnsAsync("DNS", new[] { d.Get("p"), d.Get("s") }.Where(x => x.Length > 0).ToList());
    }

    private void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        new DnsBenchmarkWindow(Owner).ShowDialog();
        Render(); // the benchmark may have switched DNS
    }
    private async void AutoDns_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(S.Network.S("dns.mode"), "automatic", StringComparison.OrdinalIgnoreCase))
        {
            S.Report("DNS is already automatic — nothing was sent.");
            return;
        }
        if (!Confirm("Switch to your ISP's automatic DNS servers?\n\n" + DnsWarning, "Automatic DNS")) return;
        await SetAutomaticDnsAsync("DNS");
    }

    private async void BlockAllow_Click(object sender, RoutedEventArgs e)
    {
        JsonNode? data = null;
        if (await Run("Loading block and allow lists…", async () => data = await Api.GetAsync($"2.2/networks/{S.NetworkId}/dns_policies/advanced_content_filter")))
            ShowJson("Blocked and Allowed Sites", data);
    }

    private async void BlockSite_Click(object sender, RoutedEventArgs e) => await DomainListAsync("blocked", "Block a Site");
    private async void AllowSite_Click(object sender, RoutedEventArgs e) => await DomainListAsync("allowed", "Allow a Site");

    private async Task DomainListAsync(string list, string title)
    {
        var d = new FormDialog(Owner, title, "Enter a domain such as example.com. This applies to the whole network.")
            .Text("domain", "Domain", required: true);
        if (!d.Prompt()) return;
        var domain = d.Get("domain");
        // Body mirrors BlockDomainForNetworkRequest / AllowDomainForNetworkRequest: { domain, is_delete }.
        await Run($"Updating {list} sites…", () => Api.PutJsonAsync($"2.2/networks/{S.NetworkId}/dns_policies/network/{list}",
            new JsonObject { ["domain"] = domain, ["is_delete"] = false }), $"{domain} added to {list} sites.");
    }
}
