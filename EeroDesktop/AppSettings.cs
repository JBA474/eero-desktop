using System.IO;
using System.Text.Json;

namespace EeroDesktop;

/// <summary>User preferences, stored as JSON in the app's data folder.</summary>
public sealed class AppSettings
{
    /// <summary>%APPDATA%\EeroDesktop, or EERO_DESKTOP_DATA_DIR (lets automated tests use a throwaway folder).</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("EERO_DESKTOP_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EeroDesktop");

    private static string FilePath => Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Notify when the internet or an eero goes offline (and when it comes back).</summary>
    public bool OutageAlerts { get; set; } = true;

    /// <summary>Notify when a device joins the network for the first time.</summary>
    public bool NewDeviceAlerts { get; set; } = true;

    /// <summary>Closing the window hides it to the tray so alerts keep working.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The "still running in the tray" tip has been shown once.</summary>
    public bool TrayTipShown { get; set; }

    /// <summary>App theme: "System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Open only the tray icon at startup (the window opens from the tray).</summary>
    public bool StartInTray { get; set; }

    /// <summary>Notify when eero releases a new version of its Android app (checked weekly on Google Play).</summary>
    public bool EeroAppReleaseAlerts { get; set; } = true;

    /// <summary>Last Google Play check, and what it found.</summary>
    public DateTime EeroAppCheckedUtc { get; set; }
    public string? EeroAppLatestVersion { get; set; }
    public string? EeroAppLatestUpdated { get; set; }

    /// <summary>Tell me when a new version of eero Desktop is released on GitHub (checked daily).</summary>
    public bool AppUpdateAlerts { get; set; } = true;
    public DateTime AppUpdateCheckedUtc { get; set; }
    public string? AppUpdateLatestVersion { get; set; }
    public string? AppUpdateNotifiedVersion { get; set; }

    /// <summary>Send a month-in-review notification at the start of each month.</summary>
    public bool MonthlySummary { get; set; } = true;

    /// <summary>The month ("yyyy-MM") whose summary was last sent.</summary>
    public string? LastSummaryMonth { get; set; }

    /// <summary>When eero Desktop first started watching for outages (for "watched since" in summaries).</summary>
    public DateTime HistoryStartedUtc { get; set; }

    /// <summary>The newer eero app version already announced (so it's announced once).</summary>
    public string? EeroAppNotifiedVersion { get; set; }

    /// <summary>Raised after settings are saved, so the tray menu and Settings page stay in sync.</summary>
    public static event Action? Changed;

    public static string SettingsFile => FilePath;
    public static string KnownDevicesFile => Path.Combine(DataDir, "known-devices.json");

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        Changed?.Invoke();
    }

    /// <summary>Appends a line to alerts.log (alert history, also used by the end-to-end tests).</summary>
    public static void Log(string kind, string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "alerts.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind,-8}  {message.Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
