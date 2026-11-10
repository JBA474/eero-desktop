using System.Net.Http;
using System.Text.RegularExpressions;

namespace EeroDesktop.Api;

/// <summary>
/// Looks up the current version of the official eero Android app on Google Play, so eero Desktop can tell you when eero
/// ships a new app (a new app is when eero is most likely to change the cloud API this app copies). Read-only: one
/// public web page request, at most once a week from the background monitor, or when you press "Check Now".
/// </summary>
public static partial class EeroAppRelease
{
    public const string PlayUrl = "https://play.google.com/store/apps/details?id=com.eero.android&hl=en_US&gl=US";

    /// <summary>Where to get the APK for tools\Compare-ApkApi.ps1 (eero doesn't publish APKs itself).</summary>
    public const string ApkMirrorUrl = "https://www.apkmirror.com/apk/eero-inc/eero-wifi-system/";

    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(7);

    public sealed record Release(string Version, string? Updated);

    // Google Play embeds the version as [[["26.13.0.36807"]],[[[37]] (version, then target SDK) in the page data.
    [GeneratedRegex(@"\[\[\[""(\d+\.\d+\.\d+)(?:\.\d+)?""\]\],\[\[\[\d+\]\]")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@">Updated on</div><div[^>]*>([^<]{4,30})</div>")]
    private static partial Regex UpdatedPattern();

    /// <summary>Parses the Play Store page. Null when the page layout changed and no version can be found.</summary>
    public static Release? Parse(string html)
    {
        var m = VersionPattern().Match(html);
        if (!m.Success) return null;
        var u = UpdatedPattern().Match(html);
        return new Release(m.Groups[1].Value, u.Success ? u.Groups[1].Value.Trim() : null);
    }

    public static async Task<Release?> FetchAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EeroDesktop");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        return Parse(await http.GetStringAsync(PlayUrl, ct));
    }

    /// <summary>True when <paramref name="version"/> is newer than the app version eero Desktop was built from.</summary>
    public static bool IsNewer(string? version, string than = EeroClient.AppVersion)
        => Version.TryParse(version, out var a) && Version.TryParse(than, out var b) && a > b;

    /// <summary>
    /// Background check (called by the monitor): at most once per <see cref="CheckEvery"/>. Returns the release when it
    /// is newer than the one eero Desktop was built from and you haven't been told about it yet; otherwise null.
    /// </summary>
    public static async Task<Release?> CheckIfDueAsync()
    {
        var settings = AppSettings.Load();
        if (!settings.EeroAppReleaseAlerts || DateTime.UtcNow - settings.EeroAppCheckedUtc < CheckEvery) return null;
        var release = await CheckNowAsync();
        if (release is null || !IsNewer(release.Version)) return null;
        settings = AppSettings.Load();
        if (settings.EeroAppNotifiedVersion == release.Version) return null;
        settings.EeroAppNotifiedVersion = release.Version;
        settings.Save();
        return release;
    }

    /// <summary>Checks now and remembers the result (shown in Settings › About).</summary>
    public static async Task<Release?> CheckNowAsync()
    {
        Release? release = null;
        try { release = await FetchAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppSettings.Log("EEROAPP", "Couldn't check Google Play: " + ex.Message);
        }
        var settings = AppSettings.Load();
        settings.EeroAppCheckedUtc = DateTime.UtcNow;
        if (release is not null) { settings.EeroAppLatestVersion = release.Version; settings.EeroAppLatestUpdated = release.Updated; }
        settings.Save();
        if (release is not null) AppSettings.Log("EEROAPP", $"Google Play has eero {release.Version}" + (release.Updated is { } u ? $" (updated {u})" : "") + $"; built from {EeroClient.AppVersion}");
        return release;
    }
}
