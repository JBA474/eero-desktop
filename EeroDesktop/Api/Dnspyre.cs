using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EeroDesktop.Api;

/// <summary>One dnspyre run against one DNS server (fields from dnspyre's --json output; latencies in ms).</summary>
public sealed record DnspyreResult(string Server, int Requests, int Successes, int Errors,
    double MinMs, double MeanMs, double P50Ms, double P95Ms, double MaxMs, string? GraphsDir)
{
    public double SuccessRate => Requests == 0 ? 0 : (double)Successes / Requests;
}

/// <summary>
/// dnspyre (https://github.com/Tantalor93/dnspyre, MIT License) — the DNS benchmark tool used by the DNS Benchmark.
/// The official Windows release is downloaded from GitHub on first use, verified against SHA-256 hashes pinned here
/// (from the release's checksums.txt), cached under %LOCALAPPDATA%\EeroDesktop\tools, and re-verified before each use.
/// </summary>
public static class Dnspyre
{
    public const string Version = "3.12.0";
    public const string SourceUrl = "https://github.com/Tantalor93/dnspyre";
    public const string DocsUrl = "https://tantalor93.github.io/dnspyre/";

    private sealed record Build(string Asset, string ArchiveSha256, string ExeSha256);

    private static readonly Build Amd64 = new("dnspyre_windows_amd64.tar.gz",
        "628dafb81218858cc22b9fb1cdbe78fb7033c1ca5d9d98ebc458243bcee0cbda",
        "d1a17f67166670293a4bed3ef98ae70dadcad172efefb2a068e88211f4ff351a");
    private static readonly Build Arm64 = new("dnspyre_windows_arm64.tar.gz",
        "e72d8dea46d34b031434f8ee4cf3dab2f07e53151e2aa99f47c3c3b455d90784",
        "6a2cb41759e81d5b95e9f16aa2381d556e717ec82f12d8dfe150817bbe140416");

    private static Build Current => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? Arm64 : Amd64;

    /// <summary>%LOCALAPPDATA%\EeroDesktop\tools (EERO_DESKTOP_TOOLS_DIR overrides it).</summary>
    public static string ToolsDir => Environment.GetEnvironmentVariable("EERO_DESKTOP_TOOLS_DIR") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EeroDesktop", "tools");

    public static string InstallDir => Path.Combine(ToolsDir, $"dnspyre-{Version}");
    public static string ExePath => Path.Combine(InstallDir, "dnspyre.exe");
    public static string LicensePath => Path.Combine(InstallDir, "LICENSE");

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>True when the cached dnspyre.exe exists and matches the pinned hash.</summary>
    public static bool IsInstalled => File.Exists(ExePath) && Sha256(ExePath) == Current.ExeSha256;

    /// <summary>Downloads and verifies dnspyre if it isn't already cached. Returns the path to dnspyre.exe.</summary>
    public static async Task<string> EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled) return ExePath;
        var build = Current;
        Directory.CreateDirectory(InstallDir);
        var archive = Path.Combine(InstallDir, build.Asset);
        progress?.Report($"Downloading dnspyre {Version} from GitHub…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EeroDesktop");
            await using var body = await http.GetStreamAsync($"{SourceUrl}/releases/download/v{Version}/{build.Asset}", ct);
            await using var file = File.Create(archive);
            await body.CopyToAsync(file, ct);
        }

        progress?.Report("Verifying the download…");
        if (Sha256(archive) != build.ArchiveSha256)
        {
            File.Delete(archive);
            throw new InvalidOperationException("The downloaded dnspyre file doesn't match its published checksum, so it wasn't used.");
        }

        // Extract only dnspyre.exe and its LICENSE from the release archive.
        await using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
        {
            using var tar = new TarReader(gz);
            while (tar.GetNextEntry() is { } entry)
            {
                var name = entry.Name.Replace('\\', '/').TrimStart('.', '/');
                if (name is "dnspyre.exe" or "LICENSE" && entry.DataStream is not null)
                    entry.ExtractToFile(Path.Combine(InstallDir, name), overwrite: true);
            }
        }
        File.Delete(archive);

        if (!File.Exists(ExePath) || Sha256(ExePath) != build.ExeSha256)
        {
            try { File.Delete(ExePath); } catch (IOException) { }
            throw new InvalidOperationException("dnspyre.exe from the release didn't match the expected hash, so it wasn't used.");
        }
        return ExePath;
    }

    /// <summary>
    /// Benchmarks one server: plain DNS over UDP, A records for <paramref name="domains"/>, each asked
    /// <paramref name="repeats"/> times, one at a time. With <paramref name="graphsParent"/>, dnspyre also writes its PNG graphs there.
    /// </summary>
    public static async Task<DnspyreResult> RunAsync(string server, IReadOnlyList<string> domains, int repeats,
        string? graphsParent = null, CancellationToken ct = default)
    {
        if (!IsInstalled) throw new InvalidOperationException("dnspyre isn't installed.");
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "--server", server, "--number", repeats.ToString(), "--concurrency", "1",
                     "--type", "A", "--no-progress", "--no-distribution", "--json" })
            psi.ArgumentList.Add(a);
        if (graphsParent is not null)
        {
            Directory.CreateDirectory(graphsParent);
            foreach (var a in new[] { "--plot", graphsParent, "--plotf", "png" }) psi.ArgumentList.Add(a);
        }
        foreach (var d in domains) psi.ArgumentList.Add(d);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start dnspyre.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var graphs = graphsParent is null ? null
            : new DirectoryInfo(graphsParent).GetDirectories("graphs-*").OrderByDescending(d => d.CreationTimeUtc).FirstOrDefault()?.FullName;
        return Parse(server, await stdout, graphs) ?? throw new InvalidOperationException(
            $"dnspyre didn't return results for {server}. {(await stderr).Trim()}");
    }

    /// <summary>Parses dnspyre --json output (field names as produced by dnspyre 3.12).</summary>
    public static DnspyreResult? Parse(string server, string json, string? graphsDir = null)
    {
        var line = json.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return null;
        if (JsonNode.Parse(line) is not JsonObject o) return null;
        int Int(string k) => o[k]?.GetValue<int>() ?? 0;
        double Ms(string k) => o["latencyStats"]?[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        var requests = Int("totalRequests");
        var successes = o.ContainsKey("totalSuccessResponses") ? Int("totalSuccessResponses") : Int("totalSuccessCodes");
        var errors = Int("totalErrorResponses") + Int("totalIOErrors") + Int("totalErrors");
        return new DnspyreResult(server, requests, successes, errors,
            Ms("minMs"), Ms("meanMs"), Ms("p50Ms"), Ms("p95Ms"), Ms("maxMs"), graphsDir);
    }
}
