using System.Text.Json.Nodes;
using EeroDesktop.Api;
using EeroDesktop.Pages;

namespace EeroDesktop;

/// <summary>
/// Read-only security review of a network: risky settings, ports open to the internet and where they lead, devices
/// without a proper name, and pending updates. Pure function over data already loaded, so it can be tested offline.
/// </summary>
public static class SecurityCheck
{
    /// <summary>Status is "OK", "Warning" or "Problem" (green / yellow / red light). Where says which page fixes it.</summary>
    public sealed record Finding(string Status, string Check, string Detail, string Where);

    public sealed record Input(JsonNode? Network, IReadOnlyList<JsonObject> Devices, IReadOnlyList<JsonObject> Forwards,
        IReadOnlyList<JsonObject> Reservations, IReadOnlyList<JsonObject> Pinholes, JsonNode? Guest, bool HasEeroPlus);

    private const string Ok = "OK", Warn = "Warning", Bad = "Problem";

    /// <summary>Ports whose service is an administrator login (NAS admin, SSH, Remote Desktop, Telnet).</summary>
    private static readonly HashSet<string> AdminPorts = ["22", "23", "3389", "5000", "5001"];

    public static List<Finding> Run(Input i)
    {
        var n = i.Network;
        var f = new List<Finding>();

        // ---- Wi-Fi ----
        var pw = n.S("password") ?? "";
        if (pw.Length > 0)
            f.Add(pw.Length >= 12
                ? new(Ok, "Wi-Fi password length", $"{pw.Length} characters.", "Wi-Fi & Guest")
                : new(Warn, "Wi-Fi password length", $"Only {pw.Length} characters. 12 or more is much harder to guess; Wi-Fi & Guest › Generate makes a strong one (every device then needs it).", "Wi-Fi & Guest"));

        if (n.NB("wpa3") is { } wpa3)
            f.Add(wpa3 ? new(Ok, "WPA3", "On — the newest Wi-Fi security.", "Advanced")
                       : new(Warn, "WPA3", "Off. WPA3 protects your password from offline guessing; turn it on if your devices support it (very old ones may not connect).", "Advanced"));

        if (i.Guest.B("enabled"))
            f.Add(string.IsNullOrEmpty(i.Guest.S("password"))
                ? new(Bad, "Guest network password", $"The guest network \"{i.Guest.S("name")}\" is open — anyone nearby can join it.", "Wi-Fi & Guest")
                : new(Ok, "Guest network password", "The guest network has a password.", "Wi-Fi & Guest"));

        // ---- Internet-facing ----
        if (n.NB("upnp") is { } upnp)
            f.Add(upnp ? new(Warn, "UPnP", "On — any device on your network can open ports to the internet by itself. Keep it only if a game console or app needs it.", "Advanced")
                       : new(Ok, "UPnP", "Off — only ports you open yourself are reachable.", "Advanced"));

        var reservedIps = i.Reservations.Select(r => r.S("ip")).Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
        var onlineByIp = i.Devices.Where(d => d.B("connected")).Select(d => d.S("ip")).Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
        var enabled = i.Forwards.Where(fw => fw.B("enabled", true)).ToList();
        foreach (var fw in enabled)
        {
            var ip = fw.S("ip") ?? "";
            var port = fw.S("gateway_port") ?? "";
            var name = fw.S("description") is { Length: > 0 } desc ? desc : "Port forward";
            var what = $"{name} — port {port} → {ip}";
            var service = PortsPage.PortService(port);
            if (service.Length > 0) what += $" ({service})";
            if (AdminPorts.Contains(port))
                f.Add(new(Warn, "Port forward", $"{what}. This exposes an admin login to the whole internet, where it gets constant password-guessing attempts. Safer: reach it through a VPN (e.g. WireGuard on the NAS) and remove this forward.", "Reservations & Ports"));
            else if (!reservedIps.Contains(ip))
                f.Add(new(Bad, "Port forward", $"{what}. That address has no IP reservation, so it may later belong to a different device.", "Reservations & Ports"));
            else if (!onlineByIp.Contains(ip))
                f.Add(new(Warn, "Port forward", $"{what}. No device is online at that address right now — if this forward is no longer used, delete it.", "Reservations & Ports"));
            else
                f.Add(new(Ok, "Port forward", $"{what}. Open to the internet; make sure you still use it.", "Reservations & Ports"));
        }
        // Behind carrier-grade NAT (or another router) forwards can't be reached from the internet at all: safer, but useless.
        if (enabled.Count > 0 && EeroDesktop.Api.PortCheck.NatProblem(i.Network.S("wan_ip"), i.Network.S("ip_settings.public_ip")) is { } nat)
            f.Add(new(Warn, "Port forwards unreachable", nat + " Until then these forwards expose nothing, but they also don't work — check with Reservations & Ports › Check From Internet.", "Reservations & Ports"));
        if (i.Forwards.Count > enabled.Count)
            f.Add(new(Ok, "Disabled port forwards", $"{i.Forwards.Count - enabled.Count} saved but turned off.", "Reservations & Ports"));

        var byUrl = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in i.Devices)
        {
            if (d.S("url") is { Length: > 0 } u) byUrl[u.TrimStart('/')] = d;
            if (d.S("mac") is { Length: > 0 } m) byUrl[m.Replace(":", "")] = d;
        }
        foreach (var p in i.Pinholes)
        {
            var port = p.S("port") ?? "";
            var label = PortsPage.PinholeDevice(p.S("device"), byUrl);
            var service = PortsPage.PortService(port);
            var id = (p.S("device") ?? "").TrimEnd('/').Split('/')[^1];
            var dev = byUrl.GetValueOrDefault((p.S("device") ?? "").TrimStart('/')) ?? byUrl.GetValueOrDefault(id);
            var what = $"IPv6 port {port}{(service.Length > 0 ? $" ({service})" : "")} → {label}";
            f.Add(dev is null ? new(Bad, "IPv6 pinhole", $"{what}. That device isn't on the network any more — delete the pinhole.", "Reservations & Ports")
                : !dev.B("connected") ? new(Warn, "IPv6 pinhole", $"{what}. The device is offline right now.", "Reservations & Ports")
                : new(Ok, "IPv6 pinhole", $"{what}. Open to the internet over IPv6.", "Reservations & Ports"));
        }

        // ---- Devices ----
        var unnamed = i.Devices.Where(DevicesPage.NeedsName).ToList();
        f.Add(unnamed.Count == 0
            ? new(Ok, "Device names", "Every device has a recognizable name.", "Devices")
            : new(Warn, "Device names", $"{unnamed.Count} device{(unnamed.Count == 1 ? "" : "s")} known only by a machine-made name (e.g. {DevicesPage.DeviceName(unnamed[0])}). Name them so a stranger's device stands out — Devices › Needs a Name.", "Devices"));

        // ---- Updates and protection ----
        var update = n.B("updates.has_update");
        f.Add(update ? new(Warn, "eero software", "An update is available — updates include security fixes.", "Updates")
                     : new(Ok, "eero software", "Up to date.", "Updates"));
        if (n.NB("premium_dns.dns_policies.block_malware") is { } malware && i.HasEeroPlus)
            f.Add(malware ? new(Ok, "Threat blocking", "eero Plus blocks known malware and phishing sites.", "Advanced")
                          : new(Warn, "Threat blocking", "Your eero Plus threat blocking is off.", "Advanced"));

        return f.OrderBy(x => x.Status switch { Bad => 0, Warn => 1, _ => 2 }).ToList();
    }
}
