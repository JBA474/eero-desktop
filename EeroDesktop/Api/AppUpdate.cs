using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EeroDesktop.Api;

/// <summary>
/// Updates for eero Desktop itself, from the GitHub releases of https://github.com/JBA474/eero-desktop.
/// Checks the latest release, downloads its EeroDesktop.exe, verifies the SHA-256 GitHub publishes for that file
/// (refusing anything that doesn't match or has no checksum), then swaps the exe in place and restarts.
/// </summary>
public static class AppUpdate
{
    public const string Repo = "JBA474/eero-desktop";
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases";
    public const string AssetName = "EeroDesktop.exe";

    /// <summary>EERO_DESKTOP_UPDATE_API overrides the GitHub API address (offline tests only).</summary>
    private static string LatestUrl => Environment.GetEnvironmentVariable("EERO_DESKTOP_UPDATE_API") is { Length: > 0 } custom
        ? custom : $"https://api.github.com/repos/{Repo}/releases/latest";

    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    private const long MaxDownloadBytes = 300L * 1024 * 1024;

    public sealed record Release(Version Version, string Tag, string PageUrl, string Notes, string? AssetUrl, long AssetSize, string? Sha256, DateTime? Published);

    /// <summary>The running version, e.g. 0.1.0 (from &lt;Version&gt; in the project file).</summary>
    public static Version Current
    {
        get
        {
            var info = (Assembly.GetEntryAssembly() ?? typeof(AppUpdate).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Version.TryParse(info?.Split('+')[0], out var v) ? v : new Version(0, 0);
        }
    }

    /// <summary>Parses GitHub's "latest release" JSON. Null when it isn't a usable release.</summary>
    public static Release? Parse(JsonNode? r)
    {
        var tag = r.S("tag_name");
        if (tag is null || r.B("draft") || r.B("prerelease")) return null;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        var asset = r.Arr("assets").FirstOrDefault(a => string.Equals(a.S("name"), AssetName, StringComparison.OrdinalIgnoreCase));
        var digest = asset.S("digest");
        var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
        return new Release(version, tag, r.S("html_url") ?? ReleasesUrl, r.S("body") ?? "", asset.S("browser_download_url"),
            (long)(asset.D("size") ?? 0), sha, r.Dt("published_at"));
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"EeroDesktop/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The latest published release, or null when the repository has none yet.</summary>
    public static async Task<Release?> FetchLatestAsync(CancellationToken ct = default)
    {
        using var http = Http();
        using var resp = await http.GetAsync(LatestUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return Parse(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)));
    }

    public static bool IsNewer(Release? r) => r is not null && r.Version > Current;

    /// <summary>Checks now and remembers the result (shown in Settings › About). Errors are logged and return null.</summary>
    public static async Task<Release?> CheckNowAsync()
    {
        Release? release = null;
        try { release = await FetchLatestAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppSettings.Log("UPDATE", "Couldn't check GitHub for updates: " + ex.Message);
            return null;
        }
        var s = AppSettings.Load();
        s.AppUpdateCheckedUtc = DateTime.UtcNow;
        s.AppUpdateLatestVersion = release?.Version.ToString() ?? Current.ToString();
        s.Save();
        return release;
    }

    /// <summary>Background check (daily at most): the release when it's newer and not announced yet, else null.</summary>
    public static async Task<Release?> CheckIfDueAsync()
    {
        var s = AppSettings.Load();
        if (!s.AppUpdateAlerts || DateTime.UtcNow - s.AppUpdateCheckedUtc < CheckEvery) return null;
        var r = await CheckNowAsync();
        if (!IsNewer(r)) return null;
        s = AppSettings.Load();
        if (s.AppUpdateNotifiedVersion == r!.Version.ToString()) return null;
        s.AppUpdateNotifiedVersion = r.Version.ToString();
        s.Save();
        return r;
    }

    // ------------------------------------------------------------------ download and verify

    public static string UpdatesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EeroDesktop", "updates");

    /// <summary>Downloads the release's exe and verifies its SHA-256. Returns the verified file's path.</summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (r.AssetUrl is null) throw new InvalidOperationException($"Release {r.Tag} has no {AssetName} to download.");
        if (r.Sha256 is null) throw new InvalidOperationException($"GitHub didn't publish a checksum for {AssetName} in {r.Tag}, so it can't be verified. Download it from the release page instead.");
        Directory.CreateDirectory(UpdatesDir);
        foreach (var old in Directory.GetFiles(UpdatesDir, "EeroDesktop-*.exe*"))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var target = Path.Combine(UpdatesDir, $"EeroDesktop-{r.Version}.exe");
        var part = target + ".part";

        using (var http = Http())
        {
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            using var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.AssetSize;
            if (total > MaxDownloadBytes) throw new InvalidOperationException("The download is unexpectedly large; not installing it.");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buffer = new byte[81920];
            long done = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (done > MaxDownloadBytes) throw new InvalidOperationException("The download is unexpectedly large; not installing it.");
                if (done - lastReport > 2 * 1024 * 1024 || done == total)
                {
                    lastReport = done;
                    progress?.Report(total > 0 ? $"Downloading eero Desktop {r.Version}… {done * 100 / total}%" : $"Downloading eero Desktop {r.Version}… {done / (1024 * 1024)} MB");
                }
            }
        }

        progress?.Report("Verifying the download…");
        if (!VerifySha256(part, r.Sha256))
        {
            try { File.Delete(part); } catch (IOException) { }
            throw new InvalidOperationException("The downloaded file doesn't match the checksum GitHub published for it, so it was deleted and nothing was installed.");
        }
        File.Move(part, target, overwrite: true);
        return target;
    }

    public static bool VerifySha256(string file, string expectedHex)
    {
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// True when this copy can replace itself: it's the packaged single-file EeroDesktop.exe (not a development build
    /// under bin\) in a folder it can write to.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var exe = Environment.ProcessPath;
        reason = "";
        if (exe is null || !string.Equals(Path.GetFileName(exe), AssetName, StringComparison.OrdinalIgnoreCase))
        { reason = "This copy isn't EeroDesktop.exe."; return false; }
        if (exe.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        { reason = "This is a development build; rebuild from source or use tools\\Publish.ps1."; return false; }
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { reason = $"eero Desktop can't write to {Path.GetDirectoryName(exe)}."; return false; }
        return true;
    }

    /// <summary>
    /// Replaces the running exe with the verified download and starts it. Windows lets a running exe be renamed but not
    /// overwritten, so the current one is renamed to *.old (deleted by the new copy at startup). Rolls back on failure.
    /// The caller exits right after this returns.
    /// </summary>
    public static void InstallAndRestart(string verifiedExe)
    {
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Copy(verifiedExe, exe);
            AppSettings.Log("UPDATE", $"Installed {Path.GetFileName(verifiedExe)}; restarting");
            Process.Start(new ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch
        {
            try { if (File.Exists(exe)) File.Delete(exe); File.Move(old, exe); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>At startup: wait for the previous copy to exit (after an update), then remove its renamed exe.</summary>
    public static void FinishPendingUpdate(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--wait-for", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
            try { Process.GetProcessById(pid).WaitForExit(20000); } catch (ArgumentException) { /* already gone */ }
        if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old"))
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { File.Delete(exe + ".old"); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
            }
    }
}
