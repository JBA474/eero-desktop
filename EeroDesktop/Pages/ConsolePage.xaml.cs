using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class ConsolePage : PageBase
{
    private sealed record Preset(string Label, string Method, string Path, string Body = "", bool Form = false)
    {
        public override string ToString() => $"{Method,-6} {J.Label(Label)}";
    }

    private static readonly Preset[] Presets =
    [
        new("Account", "GET", "2.2/account"),
        new("Network (full)", "GET", "{networkUrl}"),
        new("Devices", "GET", "{res:devices}"),
        new("eeros", "GET", "{res:eeros}"),
        new("Profiles", "GET", "{res:profiles}"),
        new("Network members", "GET", "2.2/networks/{network}/members"),
        new("Radio settings", "GET", "2.2/networks/{network}/radio_settings"),
        new("Channel utilization", "GET", "2.2/networks/{network}/channel_utilization"),
        new("Wi-Fi network scan", "GET", "2.2/networks/{network}/network_scan"),
        new("Fast transition (802.11r)", "GET", "2.2/networks/{network}/fast_transition"),
        new("WPA3 per band", "GET", "2.2/networks/{network}/wpa3_per_band"),
        new("Backup internet", "GET", "2.2/networks/{network}/backupinternet"),
        new("Power saving schedules", "GET", "2.2/networks/{network}/power_saving/schedules"),
        new("Subnets (IoT/business SSIDs)", "GET", "2.2/networks/{network}/subnets_config"),
        new("Notification settings", "GET", "2.2/networks/{network}/notifications"),
        new("Notification history", "GET", "2.2/networks/{network}/notifications_history"),
        new("Insights", "GET", "2.2/networks/{network}/insights?insight_type=adblock&cadence=daily"),
        new("Data usage (eeros summary)", "GET", "2.2/networks/{network}/data_usage/eeros/summary"),
        new("Network invites", "GET", "2.2/networks/{network}/invites"),
        new("Pending network transfers", "GET", "2.2/network_transfers/pending"),
        new("eero model capabilities", "GET", "2.2/eero_models/capabilities"),
        new("Content filter levels", "GET", "2.2/networks/{network}/safe_filter_levels"),
        new("Blocked & allowed sites", "GET", "2.2/networks/{network}/dns_policies/advanced_content_filter"),
        new("eero Plus features", "GET", "2.2/entitlements/networks/{network}/features"),
        new("Thread network", "GET", "{res:thread}"),
        new("Diagnostics", "GET", "{res:diagnostics}"),
        new("Support info", "GET", "{res:support}"),
        new("Set fast transition", "PUT", "2.2/networks/{network}/fast_transition", "{\n  \"enabled\": true\n}"),
        new("Change one network setting (send only the fields to change)", "PUT", "{res:settings}", "{\n  \"upnp\": true\n}"),
        new("Run DNS performance test", "POST", "{networkUrl}/dns_perf_test", "{}"),
    ];

    public ConsolePage()
    {
        InitializeComponent();
        PresetCombo.ItemsSource = Presets;
    }

    public override string Title => "API Console";

    public override Task LoadAsync() => Task.CompletedTask;

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not Preset p) return;
        MethodCombo.SelectedIndex = p.Method switch { "POST" => 1, "PUT" => 2, "DELETE" => 3, _ => 0 };
        PathBox.Text = p.Path;
        BodyKind.SelectedIndex = p.Form ? 1 : 0;
        BodyBox.Text = p.Body == "{networkJson}" ? S.Network.Pretty() : p.Body;
    }

    private string Expand(string path)
    {
        path = path.Replace("{networkUrl}", S.NetworkUrl).Replace("{network}", S.NetworkId).Replace("{networkId}", S.NetworkId).Replace("{id}", S.NetworkId);
        while (path.IndexOf("{res:", StringComparison.Ordinal) is var i and >= 0)
        {
            var end = path.IndexOf('}', i);
            var name = path[(i + 5)..end];
            path = path[..i] + S.Res(name) + path[(end + 1)..];
        }
        return path;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var method = ((ComboBoxItem)MethodCombo.SelectedItem).Content.ToString()!;
        var raw = PathBox.Text.Trim();
        if (raw.Length == 0) return;
        string path;
        try { path = Expand(raw); }
        catch (Exception ex) { ResultBox.Text = ex.Message; return; }

        // Anything other than GET can change the network: show the exact resolved request and default to No.
        if (method != "GET" && MessageBox.Show(Owner!,
                $"Send this request?\n\n{method} {EeroClient.Resolve(path)}\n\nThis can change your network immediately.",
                "Send Request", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        HttpContent? content = null;
        var body = BodyBox.Text.Trim();
        if (method is "POST" or "PUT" && body.Length > 0)
        {
            if (BodyKind.SelectedIndex == 1)
            {
                var pairs = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Contains('='))
                    .Select(l => new KeyValuePair<string, string>(l[..l.IndexOf('=')].Trim(), l[(l.IndexOf('=') + 1)..].Trim()));
                content = new FormUrlEncodedContent(pairs);
            }
            else
            {
                try { JsonNode.Parse(body); }
                catch (JsonException ex) { ResultBox.Text = "Body is not valid JSON: " + ex.Message; return; }
                content = new StringContent(body, Encoding.UTF8, "application/json");
            }
        }

        var started = DateTime.Now;
        IsEnabled = false;
        S.Report($"{method} {path}…");
        try
        {
            var data = await Api.SendAsync(new HttpMethod(method), path, content);
            ResultLabel.Text = $"Response — {method} {path} ({(DateTime.Now - started).TotalMilliseconds:0} ms)";
            ResultBox.Text = data.Pretty();
            S.Report("Ready");
        }
        catch (EeroApiException ex)
        {
            ResultLabel.Text = $"Error {ex.Status} — {method} {path}";
            ResultBox.Text = ex.Message + Environment.NewLine + Environment.NewLine + (TryPretty(ex.Body) ?? "");
            S.Report("Error: " + ex.Message);
        }
        catch (Exception ex)
        {
            ResultBox.Text = ex.Message;
            S.Report("Error: " + ex.Message);
        }
        finally { IsEnabled = true; }
    }

    private static string? TryPretty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try { return JsonNode.Parse(text).Pretty(); } catch (JsonException) { return text; }
    }
}
