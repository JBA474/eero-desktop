using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

/// <summary>Read-only security review (see <see cref="SecurityCheck"/>).</summary>
public partial class SecurityPage : PageBase
{
    public SecurityPage() => InitializeComponent();

    public override string Title => "Security Check";

    /// <summary>Set by the main window so a finding can open the page that fixes it.</summary>
    public Action<string>? OpenPage { get; set; }

    public override async Task LoadAsync()
    {
        SecurityCheck.Input? input = null;
        await Run("Checking your network…", async () =>
        {
            var n = await S.ReloadNetworkAsync();
            async Task<List<JsonObject>> List(string? url, Dictionary<string, string?>? q = null)
            {
                if (url is null) return [];
                try { return (await Api.GetAsync(url, q)).Arr().ToList(); }
                catch (EeroApiException ex) when (!ex.IsUnauthorized) { return []; }
            }
            var devices = await List(S.TryRes("devices"), new() { ["thread"] = "false", ["proxied_node"] = "false" });
            var forwards = await List(S.TryRes("forwards"));
            var reservations = await List(S.TryRes("reservations"));
            var pinholes = await List(S.NetworkUrl + "/ipv6/pinholes");
            JsonNode? guest = n.At("guest_network");
            if (S.TryRes("guestnetwork") is { } g)
                try { guest = await Api.GetAsync(g); } catch (EeroApiException ex) when (!ex.IsUnauthorized) { }
            input = new(n, devices, forwards, reservations, pinholes, guest, S.HasEeroPlus);
        });
        if (input is null) return;
        var findings = SecurityCheck.Run(input);
        Findings.ItemsSource = findings;
        var problems = findings.Count(f => f.Status == "Problem");
        var warnings = findings.Count(f => f.Status == "Warning");
        Summary.Text = problems + warnings == 0 ? $"All {findings.Count} checks passed."
            : $"{Count(problems, "problem")}, {Count(warnings, "warning")}, {findings.Count - problems - warnings} OK";
        Summary.Foreground = StatusLights.BrushOf(problems > 0 ? StatusLights.Level.Bad : warnings > 0 ? StatusLights.Level.Warning : StatusLights.Level.Good);
    }

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private async void Rerun_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Findings_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Findings.SelectedItem is SecurityCheck.Finding f) OpenPage?.Invoke(f.Where);
    }
}
