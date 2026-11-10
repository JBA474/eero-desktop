using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using EeroDesktop.Api;
using EeroDesktop.Pages;

namespace EeroDesktop;

/// <summary>
/// "Undo Last Change": before each Advanced-page switch or DNS change is sent, the value it replaces is saved, so it can
/// be put back with one click (also after restarting eero Desktop). Undo is itself one more one-at-a-time change, so
/// undoing twice redoes. Kept per network in undo.json in the data folder; only the most recent change is kept.
/// </summary>
public static class UndoService
{
    /// <summary>
    /// The change that was made, and what undoing it does. Kind "toggle": Args {key, value} = a feature switch's previous
    /// value. Kind "dns": Args {mode, ips} = the previous DNS setting.
    /// </summary>
    public sealed record Entry(string NetworkId, DateTime Utc, string Done, string UndoText, string Kind, JsonObject Args, bool RestartsAll);

    private static string FilePath => Path.Combine(AppSettings.DataDir, "undo.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Raised when the saved undo changes (the main window shows or hides its Undo button).</summary>
    public static event Action? Changed;

    public static Entry? For(string networkId) => Load().GetValueOrDefault(networkId);

    public static void Record(Entry entry)
    {
        var all = Load();
        all[entry.NetworkId] = entry;
        Save(all);
        AppSettings.Log("CHANGE", $"{entry.Done} (undo: {entry.UndoText})");
        Changed?.Invoke();
    }

    public static void Forget(string networkId)
    {
        var all = Load();
        if (all.Remove(networkId)) { Save(all); Changed?.Invoke(); }
    }

    // ------------------------------------------------------------------ building entries

    private static List<string> Ips(JsonNode? n) => n.At("dns.custom.ips") is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : [];

    /// <summary>Describes a DNS setting for people: "automatic (your ISP)", "AdGuard DNS (94.140.14.14, …)".</summary>
    public static string DescribeDns(string? mode, IReadOnlyList<string> ips)
    {
        if (!string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase) || ips.Count == 0) return "automatic (your ISP's servers)";
        var known = DnsBenchmark.KnownProviders.FirstOrDefault(p => p.Servers.Count > 0 && ips.Contains(p.Servers[0]));
        return (known is null ? "custom" : known.Name) + " (" + string.Join(", ", ips) + ")";
    }

    /// <summary>Undo for a DNS change: put back the DNS setting the network had before.</summary>
    public static Func<JsonObject, Entry?> Dns(string newMode, IReadOnlyList<string> newIps) => before =>
    {
        var mode = before.S("dns.mode") ?? "automatic";
        var ips = Ips(before);
        var args = new JsonObject { ["mode"] = mode, ["ips"] = new JsonArray(ips.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) };
        var from = DescribeDns(mode, ips);
        return new(AppState.Current.NetworkId, DateTime.UtcNow, $"DNS changed from {from} to {DescribeDns(newMode, newIps)}",
            $"Switch DNS back to {from}", "dns", args, RestartsAll: true);
    };

    /// <summary>Undo for an Advanced-page switch: set it back to the value it had.</summary>
    public static Func<JsonObject, Entry?> Toggle(AdvancedPage.Feature f, bool newValue) => before =>
        f.Read(before) is not { } was ? null
            : new(AppState.Current.NetworkId, DateTime.UtcNow, $"{J.Label(f.Label)} turned {(newValue ? "on" : "off")}",
                $"Turn {J.Label(f.Label)} {(was ? "on" : "off")} again", "toggle", new JsonObject { ["key"] = f.Key, ["value"] = was },
                f.Risk == AdvancedPage.Risk.RestartsAll);

    // ------------------------------------------------------------------ undoing

    /// <summary>Puts back what the entry saved. Returns false when the network already had that value (nothing sent).</summary>
    public static async Task<bool> UndoAsync(Entry e)
    {
        var s = AppState.Current;
        if (s.NetworkId != e.NetworkId) throw new InvalidOperationException("Select the network this change was made on first.");
        switch (e.Kind)
        {
            case "dns":
                var mode = e.Args.S("mode");
                var ips = e.Args["ips"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : [];
                return string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase) && ips.Count > 0
                    ? await s.SetCustomDnsAsync(ips)
                    : await s.SetAutomaticDnsAsync();
            case "toggle":
                var f = AdvancedPage.FindFeature(e.Args.S("key") ?? "") ?? throw new InvalidOperationException("That setting is no longer available.");
                var value = e.Args.B("value");
                return await s.ChangeIfNeededAsync(n => f.Read(n) == value, () => f.Write(value), Toggle(f, value));
            default:
                throw new InvalidOperationException($"Unknown change type '{e.Kind}'.");
        }
    }

    // ------------------------------------------------------------------ storage

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    private static void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
    }
}
