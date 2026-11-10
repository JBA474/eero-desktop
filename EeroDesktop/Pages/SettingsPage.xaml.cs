using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;
using Microsoft.Win32;

namespace EeroDesktop.Pages;

public partial class SettingsPage : PageBase
{
    private bool _rendering;

    public SettingsPage() => InitializeComponent();

    public override string Title => "Settings";

    public override Task LoadAsync()
    {
        Render();
        return Task.CompletedTask;
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            var s = AppSettings.Load();
            (s.Theme switch { "Light" => ThemeLight, "Dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;

            var registered = StartupRegistration.Current();
            StartWithWindows.IsChecked = registered is not null;
            StartWithWindowsNote.Text = registered switch
            {
                null => "Adds eero Desktop to your Windows startup apps (for your account only).",
                _ when StartupRegistration.PointsHere(registered) => "Starts this copy: " + Environment.ProcessPath,
                _ => $"Currently starts a different copy: {registered}. Untick and tick again to start this copy instead.",
            };
            StartInTray.IsChecked = s.StartInTray;
            CloseToTray.IsChecked = s.CloseToTray;
            OutageAlerts.IsChecked = s.OutageAlerts;
            NewDeviceAlerts.IsChecked = s.NewDeviceAlerts;
            EeroAppReleaseAlerts.IsChecked = s.EeroAppReleaseAlerts;
            MonthlySummaryOption.IsChecked = s.MonthlySummary;
            EeroAppStatus.Text = EeroAppStatusText(s);
            NotificationNote.Text = "Alerts appear as Windows notifications from the tray icon whenever eero Desktop is running — with the window open or in the tray. "
                                    + "The network is checked every 2 minutes.";

            VersionText.Text = $"Version {AppVersion}";
            AboutFacts.ItemsSource = new List<KeyValuePair<string, string>>
            {
                new("Version", AppVersion),
                new("Based on", $"eero Android app {EeroClient.AppVersion} (cloud API)"),
                new("DNS benchmark", Dnspyre.IsInstalled ? $"dnspyre {Dnspyre.Version} (installed)" : $"dnspyre {Dnspyre.Version} (downloaded on first benchmark)"),
                new(".NET", Environment.Version.ToString()),
                new("Runs from", Environment.ProcessPath ?? ""),
                new("Data folder", AppSettings.DataDir),
            };
        }
        finally { _rendering = false; }
    }

    private static string AppVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(SettingsPage).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? asm.GetName().Version?.ToString() ?? "unknown";
        }
    }

    /// <summary>Whether eero has shipped a newer Android app than the one eero Desktop's API calls were copied from.</summary>
    public static string EeroAppStatusText(AppSettings s)
    {
        var built = $"eero Desktop was built from eero app {EeroClient.AppVersion}.";
        if (s.EeroAppLatestVersion is not { } latest)
            return built + (s.EeroAppCheckedUtc == default ? " Not checked for a newer one yet." : " The last check couldn't read Google Play.");
        var checkedOn = $"Checked {s.EeroAppCheckedUtc.ToLocalTime():MMM d, h:mm tt}.";
        var updated = s.EeroAppLatestUpdated is { } u ? $" (updated {u})" : "";
        if (!EeroAppRelease.IsNewer(latest))
            return $"Up to date: Google Play has eero {latest}{updated}, the version eero Desktop was built from. {checkedOn}";
        return $"eero has released app {latest}{updated}. {built} Most eero updates change nothing here. If a feature stops working, "
               + "eero may have changed its API: get the new APK (APKMirror) and run tools\\Compare-ApkApi.ps1 to see exactly what changed. " + checkedOn;
    }

    private async void EeroAppCheck_Click(object sender, RoutedEventArgs e)
    {
        EeroAppCheckButton.IsEnabled = false;
        S.Report("Checking Google Play for the eero app version…");
        try
        {
            var r = await EeroAppRelease.CheckNowAsync();
            S.Report(r is null ? "Couldn't read the eero app version from Google Play." : $"Google Play has eero app {r.Version}.");
        }
        finally
        {
            EeroAppCheckButton.IsEnabled = true;
            EeroAppStatus.Text = EeroAppStatusText(AppSettings.Load());
        }
    }

    // ---------- Appearance ----------

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_rendering || sender is not RadioButton { Tag: string theme }) return;
        var s = AppSettings.Load();
        if (s.Theme == theme) return;
        s.Theme = theme;
        s.Save();
        App.ApplyTheme(theme);
        S.Report(theme == "System" ? "Theme follows the Windows setting." : $"{theme} theme on.");
    }

    // ---------- Window & Startup ----------

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        var s = AppSettings.Load();
        s.StartInTray = StartInTray.IsChecked == true;
        s.CloseToTray = CloseToTray.IsChecked == true;
        s.OutageAlerts = OutageAlerts.IsChecked == true;
        s.NewDeviceAlerts = NewDeviceAlerts.IsChecked == true;
        s.EeroAppReleaseAlerts = EeroAppReleaseAlerts.IsChecked == true;
        s.MonthlySummary = MonthlySummaryOption.IsChecked == true;
        s.Save(); // also updates the tray menu checkmarks
        S.Report("Settings saved.");
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.Set(StartWithWindows.IsChecked == true);
            S.Report(StartWithWindows.IsChecked == true ? "eero Desktop will start when you sign in to Windows." : "eero Desktop won't start with Windows.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Owner!, "Couldn't change the Windows startup setting: " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Render();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app && app.ShowTestNotification())
            S.Report("Test notification sent. If it didn't appear, check Windows Settings › System › Notifications.");
        else
            S.Report("The tray icon isn't running, so no notification could be shown.");
    }

    private void MonthlySummary_Click(object sender, RoutedEventArgs e)
    {
        if (S.Selected is null) return;
        var month = DateTime.Now.AddMonths(-1);
        var summary = EeroDesktop.MonthlySummary.Build(S.NetworkId, S.Selected.Name, month.Year, month.Month, AppSettings.Load().HistoryStartedUtc);
        if (summary is null)
        {
            S.Report($"Nothing to summarize for {month:MMMM yyyy} yet.");
            return;
        }
        if (Application.Current is App app) app.ShowNotification(summary.Title, summary.Body);
        S.Report($"{summary.Title}: {summary.Body.Replace("\n", " ")}");
    }

    private void AlertHistory_Click(object sender, RoutedEventArgs e)
    {
        var log = Path.Combine(AppSettings.DataDir, "alerts.log");
        if (!File.Exists(log))
        {
            MessageBox.Show(Owner!, "No alerts yet.", "Alert History", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    private void ForgetDevices_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Forget the list of known devices? On the next check eero Desktop quietly re-learns every device that's on the network now, so only devices that join after that trigger a \"new device\" alert.",
                "Forget Known Devices")) return;
        try { File.Delete(AppSettings.KnownDevicesFile); } catch (IOException) { }
        S.Report("Known devices forgotten; they'll be re-learned on the next check.");
    }

    // ---------- Backup & Export ----------

    private const string BackupFormat = "eero-desktop-backup";

    /// <summary>
    /// EERO_DESKTOP_TEST_FILE_DIR (automated tests only): skip the Windows file dialogs — saves go to that folder under
    /// the default name and "restore" opens the newest settings backup there. Tests must never type into native dialogs.
    /// </summary>
    private static readonly string? TestFileDir = Environment.GetEnvironmentVariable("EERO_DESKTOP_TEST_FILE_DIR") is { Length: > 0 } d ? d : null;

    private string? PickSavePath(string title, string defaultName, string filter)
    {
        if (TestFileDir is not null) { Directory.CreateDirectory(TestFileDir); return Path.Combine(TestFileDir, defaultName); }
        var dlg = new SaveFileDialog { Title = title, FileName = defaultName, Filter = filter };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    private string? PickOpenPath(string title, string filter)
    {
        if (TestFileDir is not null)
            return Directory.Exists(TestFileDir)
                ? new DirectoryInfo(TestFileDir).GetFiles("eero Desktop settings*.json").OrderByDescending(fi => fi.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonNode? ReadJsonFile(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    private void BackupApp_Click(object sender, RoutedEventArgs e)
    {
        if (PickSavePath("Back Up App Settings", $"eero Desktop settings {DateTime.Now:yyyy-MM-dd}.json", "eero Desktop backup (*.json)|*.json") is not { } path) return;
        var backup = new JsonObject
        {
            ["format"] = BackupFormat,
            ["version"] = 2, // 2: adds history
            ["exported"] = DateTime.Now.ToString("o"),
            ["app_version"] = AppVersion,
            ["settings"] = JsonSerializer.SerializeToNode(AppSettings.Load()),
            ["known_devices"] = ReadJsonFile(AppSettings.KnownDevicesFile) ?? new JsonObject(),
            ["history"] = HistoryStore.Export(),
        };
        File.WriteAllText(path, backup.ToJsonString(Indented));
        var records = backup["history"]!.AsObject().Sum(kv => (kv.Value as JsonArray)?.Count ?? 0);
        S.Report($"App settings and {records} history records backed up to " + path);
    }

    private void RestoreApp_Click(object sender, RoutedEventArgs e)
    {
        if (PickOpenPath("Restore App Settings", "eero Desktop backup (*.json)|*.json|All files (*.*)|*.*") is not { } path) return;
        var backup = ReadJsonFile(path);
        if (backup.S("format") != BackupFormat || backup?["settings"] is not JsonObject settingsJson)
        {
            MessageBox.Show(Owner!, "That file isn't an eero Desktop settings backup.", "Restore App Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Confirm($"Replace your current eero Desktop settings with the backup from {J.Fmt(backup.Dt("exported"))}?"
                     + (backup["history"] is JsonObject ? "\n\nHistory from the backup is added to the history on this PC (nothing here is removed)." : ""),
                "Restore App Settings")) return;

        var restored = settingsJson.Deserialize<AppSettings>() ?? new AppSettings();
        if (backup["known_devices"] is JsonObject known)
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(AppSettings.KnownDevicesFile, known.ToJsonString(Indented));
        }
        var historyAdded = backup["history"] is JsonObject history ? HistoryStore.Import(history) : 0;
        restored.Save();
        App.ApplyTheme(restored.Theme);
        Render();
        S.Report("App settings restored." + (historyAdded > 0 ? $" {historyAdded} history records added." : ""));
    }

    /// <summary>Replaces every "password"-like value with a placeholder (unless the user chose to include them).</summary>
    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Equals("psk", StringComparison.OrdinalIgnoreCase))
                    {
                        if (o[key] is JsonValue) o[key] = "(removed)";
                    }
                    else Redact(o[key]);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Redact(item);
                break;
        }
    }

    private async void ExportNetwork_Click(object sender, RoutedEventArgs e)
    {
        var name = S.Selected?.Name ?? "network";
        if (PickSavePath("Export Network Snapshot", $"eero {Sanitize(name)} snapshot {DateTime.Now:yyyy-MM-dd}.json", "JSON (*.json)|*.json") is not { } path) return;
        var includePasswords = IncludePasswords.IsChecked == true;

        JsonObject snapshot = new();
        var ok = await Run("Collecting network configuration…", async () =>
        {
            var network = await S.ReloadNetworkAsync();
            snapshot = new JsonObject
            {
                ["format"] = "eero-desktop-network-snapshot",
                ["exported"] = DateTime.Now.ToString("o"),
                ["app_version"] = AppVersion,
                ["passwords_included"] = includePasswords,
                ["network"] = network.DeepClone(),
            };
            // Each part is read separately; one failing (e.g. an eero Plus-only resource) doesn't stop the rest.
            foreach (var part in new[] { "eeros", "devices", "profiles", "reservations", "forwards", "guestnetwork", "routing" })
            {
                if (S.TryRes(part) is not { } url) continue;
                try { snapshot[part] = (await Api.GetAsync(url))?.DeepClone(); }
                catch (EeroApiException ex) { snapshot[part] = new JsonObject { ["error"] = ex.Message }; }
            }
            if (!includePasswords) Redact(snapshot);
            await File.WriteAllTextAsync(path, snapshot.ToJsonString(Indented));
        });
        if (ok) S.Report($"Network snapshot saved to {path}" + (includePasswords ? " (includes Wi-Fi passwords — keep it private)." : "."));
    }

    private async void ExportDevices_Click(object sender, RoutedEventArgs e)
    {
        if (PickSavePath("Export Devices to CSV", $"eero {Sanitize(S.Selected?.Name ?? "network")} devices {DateTime.Now:yyyy-MM-dd}.csv", "CSV (*.csv)|*.csv") is not { } path) return;
        var count = 0;
        var ok = await Run("Exporting devices…", async () =>
        {
            var devices = (await Api.GetAsync(S.Res("devices"))).Arr().OrderBy(DevicesPage.DeviceName, StringComparer.CurrentCultureIgnoreCase).ToList();
            var sb = new StringBuilder("Name,MAC,IP Address,Manufacturer,Online,Connection,Connected To,Profile,Last Active\r\n");
            foreach (var d in devices)
            {
                string[] cells =
                [
                    DevicesPage.DeviceName(d), d.S("mac") ?? "", d.S("ip") ?? "", d.S("manufacturer") ?? "",
                    d.B("connected") ? "Yes" : "No", J.Title(d.S("connection_type")), d.S("source.location") ?? "",
                    d.S("profile.name") ?? "", J.Fmt(d.Dt("last_active")),
                ];
                sb.Append(string.Join(',', cells.Select(Csv))).Append("\r\n");
            }
            count = devices.Count;
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel reads UTF-8
        });
        if (ok) S.Report($"{count} devices exported to {path}");
    }

    private static string Csv(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---------- About ----------

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void DnspyreLicense_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Dnspyre.LicensePath))
            Process.Start(new ProcessStartInfo(Dnspyre.LicensePath) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(Dnspyre.SourceUrl + "/blob/master/LICENSE") { UseShellExecute = true });
    }
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.DataDir}\"") { UseShellExecute = false });
    }
}

/// <summary>"Start with Windows": the current user's Run key (no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "eero Desktop";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static bool PointsHere(string command)
        => command.Trim().Trim('"').Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
