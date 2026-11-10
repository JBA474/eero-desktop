using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;

namespace EeroDesktop;

/// <summary>
/// An eero's Ethernet ports (from ethernet_status.statuses) with per-port on/off and whole-eero power cycling.
/// Actions are the eero Android app's: POST eeros/{id}/ports/{n}/action and POST eeros/{id}/action.
/// </summary>
public sealed class PortsWindow : Window
{
    public sealed record PortRow(JsonObject Json, int Number, string Port, string Link, string Speed, string Role, string Neighbor, string Traffic,
        bool IsWan, bool IsUplink, bool IsThisPc);

    private readonly JsonObject _eero;
    private readonly string _location;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, MinHeight = 140 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _off = new() { Content = "Turn Port Off" };
    private readonly Button _on = new() { Content = "Turn Port On" };

    public PortsWindow(Window? owner, JsonObject eero)
    {
        _eero = eero;
        _location = eero.S("location") ?? "eero";
        Owner = owner;
        Title = $"Ports — {_location}";
        Width = 860;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        foreach (var (header, path, width) in new[]
        {
            ("Port", "Port", 0.6), ("Link", "Link", 0.9), ("Speed", "Speed", 0.9), ("Use", "Role", 1.4),
            ("Connected Device", "Neighbor", 2.0), ("Traffic", "Traffic", 1.4),
        })
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += (_, _) => UpdateButtons();

        var cycle = new Button { Content = "Power Cycle All Ports" };
        var cycleReboot = new Button { Content = "Power Cycle Ports and Restart", Style = (Style)Application.Current.FindResource("DangerButton") };
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0) };
        _off.Click += async (_, _) => await PortActionAsync(false);
        _on.Click += async (_, _) => await PortActionAsync(true);
        cycle.Click += async (_, _) => await NodeActionAsync("POWER_CYCLE_ALL_PORTS",
            $"Power cycle every port on the {_location} eero? Wired devices on it disconnect for a few seconds while the ports come back.");
        cycleReboot.Click += async (_, _) => await NodeActionAsync("POWER_CYCLE_ALL_PORTS_AND_REBOOT",
            $"Power cycle every port on the {_location} eero and restart it? Everything connected to it goes offline for a few minutes"
            + (eero.B("gateway") ? " — this is the gateway, so the whole network goes offline." : "."));

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _off, _on, cycle, cycleReboot }) buttons.Children.Add(b);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
            Text = "Port controls come from the eero app but may not be supported on every eero model. The internet port and an eero's link to the rest of the network can't be turned off here.",
        });

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(_grid);
        panel.Children.Add(buttons);
        panel.Children.Add(_status);
        panel.Children.Add(footer);
        Content = panel;

        Render(eero);
    }

    private static string SpeedText(string? s) => s switch
    {
        null or "" => "",
        _ when s.StartsWith('P') && int.TryParse(s[1..], out var mbps) => mbps >= 1000 ? $"{mbps / 1000.0:0.#} Gbps" : $"{mbps} Mbps",
        _ => s,
    };

    private static string Bytes(double? b) => b switch
    {
        null => "",
        >= 1e9 => $"{b / 1e9:0.0} GB",
        >= 1e6 => $"{b / 1e6:0.0} MB",
        >= 1e3 => $"{b / 1e3:0} KB",
        _ => $"{b:0} B",
    };

    private void Render(JsonObject eero)
    {
        var rows = eero.Arr("ethernet_status.statuses").Select(p =>
        {
            var carrier = p.B("hasCarrier");
            var wan = p.B("isWanPort");
            var uplink = p.B("isLeafWiredToUpstream");
            var lldp = p.At("lldpInfo") is JsonArray { Count: > 0 } l ? l[0] : null;
            var neighbor = lldp.S("systemName") is { Length: > 0 } sn ? sn : lldp.S("chassisId") is { Length: > 0 } ci ? ci : lldp.S("macAddress") ?? "";
            var thisPc = neighbor.Length > 0 && neighbor.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            var role = wan ? "Internet (WAN)" : uplink ? "Link to other eero" : "Wired devices";
            var traffic = p.D("bandwidth_usage.rx_bytes") is { } rx ? $"↓ {Bytes(rx)}  ↑ {Bytes(p.D("bandwidth_usage.tx_bytes"))}" : "";
            return new PortRow(p, p.I("interfaceNumber") ?? 0, p.S("port_name") ?? $"{(p.I("interfaceNumber") ?? 0) + 1}",
                carrier ? "Connected" : "No link", carrier ? SpeedText(p.S("speed")) : "", role,
                neighbor + (thisPc ? " (this PC)" : ""), traffic, wan, uplink, thisPc);
        }).ToList();
        _grid.ItemsSource = rows;
        if (rows.Count == 0) _status.Text = "This eero didn't report any Ethernet ports.";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var r = _grid.SelectedItem as PortRow;
        var allowed = r is { IsWan: false, IsUplink: false };
        _off.IsEnabled = _on.IsEnabled = allowed;
        _off.ToolTip = r is null ? "Select a port first" : allowed ? null : "The internet port and links between eeros can't be turned off here.";
    }

    private string EeroUrl => _eero.S("url") ?? throw new InvalidOperationException("This eero has no URL.");

    private async Task PortActionAsync(bool enable)
    {
        if (_grid.SelectedItem is not PortRow r || r.IsWan || r.IsUplink) return;
        var question = enable
            ? $"Turn port {r.Port} on the {_location} eero back on?"
            : $"Turn off port {r.Port} on the {_location} eero? Whatever is plugged into it ({(r.Neighbor.Length > 0 ? r.Neighbor : "nothing detected")}) loses its wired connection until you turn it back on.";
        if (!enable && r.IsThisPc)
            question += "\n\nWARNING: this is the port THIS computer is plugged into. It will lose its connection, and you won't be able to turn the port back on from this computer unless it also has Wi-Fi.";
        if (MessageBox.Show(this, question, enable ? "Turn Port On" : "Turn Port Off", MessageBoxButton.YesNo,
                enable ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SendAsync($"{EeroUrl}/ports/{r.Number}/action", enable ? "ENABLE_DATA" : "DISABLE_DATA",
            enable ? $"Port {r.Port} turned on." : $"Port {r.Port} turned off.");
    }

    private async Task NodeActionAsync(string action, string question)
    {
        if (MessageBox.Show(this, question, "Power Cycle", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SendAsync($"{EeroUrl}/action", action, "Request sent. Ports power cycle within a few seconds.");
    }

    /// <summary>Sends one action exactly once (never retried), then refreshes the port list.</summary>
    private async Task SendAsync(string url, string action, string done)
    {
        IsEnabled = false;
        _status.Text = "Sending…";
        try
        {
            await AppState.Current.Api.PostJsonAsync(url, new JsonObject { ["action"] = action });
            _status.Text = done;
            AppState.Current.Report($"{_location}: {done}");
            await Task.Delay(1500);
            if (await AppState.Current.Api.GetAsync(EeroUrl) is JsonObject fresh) Render(fresh);
        }
        catch (Exception ex)
        {
            _status.Text = "eero refused: " + ex.Message;
        }
        finally { IsEnabled = true; }
    }
}
