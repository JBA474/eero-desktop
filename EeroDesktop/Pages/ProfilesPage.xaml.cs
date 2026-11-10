using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class ProfilesPage : PageBase
{
    public sealed record ProfileRow(JsonObject Json, string Name, string State, int DeviceCount, bool Paused);
    public sealed record DeviceRow(JsonObject Json, string Name, string Mac, string Online);
    public sealed record ScheduleRow(JsonObject Json, string Name, string Days, string Start, string End, string Enabled);

    private static readonly string[] DayCodes = ["MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN"];

    public ProfilesPage() => InitializeComponent();

    public override string Title => "Profiles";

    public override async Task LoadAsync()
    {
        var selectedUrl = (ProfileGrid.SelectedItem as ProfileRow)?.Json.S("url");
        List<ProfileRow> rows = [];
        await Run("Loading profiles…", async () =>
        {
            var list = (await Api.GetAsync(S.Res("profiles"))).Arr();
            rows = list.Select(p =>
            {
                var paused = p.B("paused") || string.Equals(p.S("state.value"), "paused", StringComparison.OrdinalIgnoreCase);
                var state = paused ? "Paused" : J.Title(p.S("state.value") ?? "Active");
                return new ProfileRow(p, p.S("name") ?? "(unnamed)", state, p.Arr("devices").Count(), paused);
            }).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        });
        ProfileGrid.ItemsSource = rows;
        ProfileGrid.SelectedItem = rows.FirstOrDefault(r => r.Json.S("url") == selectedUrl) ?? rows.FirstOrDefault();
    }

    private void ProfileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        if (ProfileGrid.SelectedItem is not ProfileRow p)
        {
            DeviceGrid.ItemsSource = null;
            ScheduleGrid.ItemsSource = null;
            return;
        }
        DeviceGrid.ItemsSource = p.Json.Arr("devices")
            .Select(d => new DeviceRow(d, DevicesPage.DeviceName(d), d.S("mac") ?? "", d.B("connected") ? "Yes" : "No")).ToList();
        ScheduleGrid.ItemsSource = p.Json.Arr("schedule")
            .Select(s => new ScheduleRow(s, s.S("name") ?? "", DaysText(s),
                s.S("start") ?? "", s.S("end") ?? "", s.B("enabled") ? "Yes" : "No")).ToList();
    }

    private static string DaysText(JsonNode s) => s.At("days") is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString())) : "";

    private ProfileRow? Selected()
    {
        if (ProfileGrid.SelectedItem is ProfileRow r) return r;
        MessageBox.Show(Owner!, "Select a profile first.", "Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var d = new FormDialog(Owner, "New Profile").Text("name", "Profile name", required: true);
        if (!d.Prompt()) return;
        await Run("Creating profile…", () => Api.PostJsonAsync(S.Res("profiles"), new JsonObject { ["name"] = d.Get("name"), ["devices"] = new JsonArray() }), "Profile created.");
        await LoadAsync();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        var d = new FormDialog(Owner, "Rename Profile").Text("name", "Profile name", p.Name, required: true);
        if (!d.Prompt()) return;
        var url = p.Json.S("url")!;
        var newName = d.Get("name");
        if (await Run("Renaming…", () => Api.PutJsonAsync(url, new JsonObject { ["name"] = newName }), "Profile renamed."))
            UndoService.RecordRequest($"Renamed profile {p.Name} to {newName}", $"Rename it back to {p.Name}", $"Renamed profile {newName} back to {p.Name}",
                $"Rename it to {newName} again", new("PUT", url, new JsonObject { ["name"] = p.Name }), new("PUT", url, new JsonObject { ["name"] = newName }));
        await LoadAsync();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        var pause = !p.Paused;
        var url = p.Json.S("url")!;
        if (await Run(pause ? $"Pausing {p.Name}…" : $"Resuming {p.Name}…",
            () => Api.PutJsonAsync(url, new JsonObject { ["paused"] = pause }),
            pause ? $"Internet paused for {p.Name}." : $"Internet resumed for {p.Name}."))
            UndoService.RecordRequest(pause ? $"Paused profile {p.Name}" : $"Resumed profile {p.Name}", pause ? $"Resume {p.Name}" : $"Pause {p.Name} again",
                pause ? $"Resumed profile {p.Name}" : $"Paused profile {p.Name} again", pause ? $"Pause {p.Name} again" : $"Resume {p.Name}",
                new("PUT", url, new JsonObject { ["paused"] = !pause }), new("PUT", url, new JsonObject { ["paused"] = pause }));
        await LoadAsync();
    }

    /// <summary>Per-profile content filters (unified_content_filters.dns_policies), as in the eero app and Home Assistant.</summary>
    private static readonly (string Field, string Label)[] FilterDefs =
    [
        ("block_pornographic_content", "Block adult content"),
        ("block_illegal_content", "Block illegal content"),
        ("block_violent_content", "Block violent content"),
        ("block_gaming_content", "Block gaming"),
        ("block_messaging_content", "Block messaging and chat"),
        ("block_shopping_content", "Block shopping"),
        ("block_social_content", "Block social media"),
        ("block_streaming_content", "Block streaming video"),
        ("safe_search_enabled", "SafeSearch (filter search results)"),
        ("youtube_restricted", "YouTube Restricted Mode"),
    ];

    private async void Filters_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        var profileUrl = p.Json.S("url")!;
        var current = FilterDefs.ToDictionary(f => f.Field, f => p.Json.B("unified_content_filters.dns_policies." + f.Field));
        var adProfiles = (S.Network.At("premium_dns.ad_block_settings.profiles") as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? [];
        var adOn = p.Json.B("premium_dns.ad_block_settings.enabled") || adProfiles.Contains(profileUrl);

        var intro = $"Filters for {p.Name}'s devices. Only the boxes you change are sent to eero, one at a time.";
        if (!S.HasEeroPlus) intro += "\n\nMost filters need eero Plus, which this network doesn't have — eero may refuse them.";
        var d = new FormDialog(Owner, $"Content Filters — {p.Name}", intro);
        foreach (var f in FilterDefs) d.Check(f.Field, f.Label, current[f.Field]);
        d.Check("ad_block", "Block ads and trackers (eero Plus)", adOn);
        if (!d.Prompt()) return;

        var changes = FilterDefs.Where(f => d.GetBool(f.Field) != current[f.Field]).ToList();
        var adChange = d.GetBool("ad_block") != adOn;
        if (changes.Count == 0 && !adChange) { S.Report("No filter changes — nothing was sent."); return; }

        var policiesUrl = $"{S.NetworkUrl}/dns_policies/profiles/{J.IdFromUrl(profileUrl)}";
        await Run($"Updating filters for {p.Name}…", async () =>
        {
            foreach (var f in changes)
            {
                // eero app: updateProfileDnsPolicySettings(@FieldMap Map<String, Boolean>) -- form fields, one change per call.
                await Api.PostFormAsync(policiesUrl, new Dictionary<string, string?> { [f.Field] = EeroClient.Bool(d.GetBool(f.Field)) });
            }
            if (adChange)
            {
                // eero app: updateProfileAdBlockSettings(network + "/dns_policies/profiles/{id}/adblock", { enable }).
                await Api.PostJsonAsync(policiesUrl + "/adblock", new JsonObject { ["enable"] = d.GetBool("ad_block") });
            }
            await S.ReloadNetworkAsync();
        }, $"Updated {changes.Count + (adChange ? 1 : 0)} filter setting(s) for {p.Name}.");
        await LoadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        if (!Confirm($"Delete the profile \"{p.Name}\"? Its devices stay on the network.", "Delete Profile")) return;
        await Run("Deleting profile…", () => Api.DeleteAsync(p.Json.S("url")!), "Profile deleted.");
        await LoadAsync();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is ProfileRow p) ShowJson($"Profile — {p.Name}", p.Json);
    }

    private Task SetDevicesAsync(ProfileRow p, IEnumerable<string> deviceUrls)
    {
        var arr = new JsonArray(deviceUrls.Distinct().Select(u => (JsonNode)new JsonObject { ["url"] = u }).ToArray());
        return Api.PutJsonAsync(p.Json.S("url")!, new JsonObject { ["devices"] = arr });
    }

    private async void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        List<JsonObject> devices = [];
        if (!await Run("Loading devices…", async () => devices = (await Api.GetAsync(S.Res("devices"))).Arr().ToList())) return;
        var inProfile = p.Json.Arr("devices").Select(d => d.S("url")).ToHashSet();
        var candidates = devices.Where(d => !inProfile.Contains(d.S("url")))
            .OrderBy(d => DevicesPage.DeviceName(d), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (candidates.Count == 0) { S.Report("Every device is already in this profile."); return; }
        var labels = candidates.Select(d => $"{DevicesPage.DeviceName(d)}  ({d.S("mac")})").ToList();
        var dlg = new FormDialog(Owner, "Add Device", $"Add a device to {p.Name}. A device can only be in one profile.").Choice("d", "Device", labels);
        if (!dlg.Prompt()) return;
        var chosen = candidates[labels.IndexOf(dlg.Get("d"))];
        await Run("Adding device…", () => SetDevicesAsync(p, inProfile.Where(u => u is not null).Select(u => u!).Append(chosen.S("url")!)), "Device added.");
        await LoadAsync();
    }

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        if (DeviceGrid.SelectedItem is not DeviceRow dr) { S.Report("Select a device in the profile first."); return; }
        var remaining = p.Json.Arr("devices").Select(d => d.S("url")).Where(u => u is not null && u != dr.Json.S("url")).Select(u => u!);
        await Run("Removing device…", () => SetDevicesAsync(p, remaining), "Device removed from profile.");
        await LoadAsync();
    }

    private async void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } p) return;
        var d = new FormDialog(Owner, "Add Scheduled Pause", "Times are 24-hour HH:mm. Choose the days the pause applies.")
            .Text("name", "Name", "Bedtime", required: true)
            .Text("start", "Pause at (HH:mm)", "21:00", required: true)
            .Text("end", "Resume at (HH:mm)", "07:00", required: true);
        foreach (var day in DayCodes) d.Check(day, J.Title(day.ToLowerInvariant()), true);
        if (!d.Prompt()) return;
        var days = new JsonArray(DayCodes.Where(d.GetBool).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        var url = p.Json.S("resources.schedules") ?? p.Json.S("url") + "/schedules";
        await Run("Creating schedule…", () => Api.PostJsonAsync(url, new JsonObject
        {
            ["name"] = d.Get("name"),
            ["start"] = d.Get("start"),
            ["end"] = d.Get("end"),
            ["days"] = days,
            ["enabled"] = true,
        }), "Schedule created.");
        await LoadAsync();
    }

    private async void ToggleSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleGrid.SelectedItem is not ScheduleRow s) { S.Report("Select a schedule first."); return; }
        var body = (JsonObject)s.Json.DeepClone();
        body["enabled"] = !s.Json.B("enabled");
        await Run("Updating schedule…", () => Api.PutJsonAsync(s.Json.S("url")!, body), "Schedule updated.");
        await LoadAsync();
    }

    private async void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleGrid.SelectedItem is not ScheduleRow s) { S.Report("Select a schedule first."); return; }
        if (!Confirm($"Delete the schedule \"{s.Name}\"?", "Delete Schedule")) return;
        await Run("Deleting schedule…", () => Api.DeleteAsync(s.Json.S("url")!), "Schedule deleted.");
        await LoadAsync();
    }
}
