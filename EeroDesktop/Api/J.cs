using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EeroDesktop.Api;

/// <summary>Tolerant accessors for eero JSON (dotted paths, missing fields return null).</summary>
public static class J
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonNode? At(this JsonNode? node, string path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(part, out var v) ? v : null,
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static string? S(this JsonNode? node, string path = "")
    {
        var n = node.At(path);
        return n switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => n.ToJsonString(),
        };
    }

    public static bool B(this JsonNode? node, string path, bool fallback = false)
    {
        var n = node.At(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        }
        return fallback;
    }

    public static bool? NB(this JsonNode? node, string path)
        => node.At(path) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static double? D(this JsonNode? node, string path)
    {
        var n = node.At(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        return null;
    }

    public static int? I(this JsonNode? node, string path) => D(node, path) is { } d ? (int)d : null;

    public static DateTime? Dt(this JsonNode? node, string path)
        => S(node, path) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt.ToLocalTime() : null;

    public static IEnumerable<JsonObject> Arr(this JsonNode? node, string path = "")
    {
        var n = node.At(path);
        if (n is JsonObject o && o["data"] is JsonArray inner) n = inner; // eero "collection" objects: { count, data: [...] }
        return n is JsonArray a ? a.OfType<JsonObject>() : [];
    }

    public static string Pretty(this JsonNode? node) => node is null ? "null" : node.ToJsonString(PrettyOptions);

    public static string Fmt(DateTime? dt) => dt?.ToString("g", CultureInfo.CurrentCulture) ?? "";

    public static string Ago(DateTime? dt)
    {
        if (dt is null) return "";
        var span = DateTime.Now - dt.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 60) return $"{(int)span.TotalDays} d ago";
        return dt.Value.ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>Numeric id from an eero resource URL like /2.2/networks/12345 or /2.2/eeros/678.</summary>
    public static string? IdFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var last = url.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrEmpty(last) ? null : last;
    }

    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "nat", "dhcp", "dns", "ip", "ipv4", "ipv6", "isp", "wan", "lan", "upnp", "sqm", "ssid", "pppoe", "led", "vpn", "mac", "wpa2", "wpa3", "ota",
    };

    private static readonly HashSet<string> SmallWords = ["a", "an", "and", "as", "at", "but", "by", "for", "in", "of", "on", "or", "per", "the", "to", "vs", "via", "with"];

    /// <summary>
    /// Title Case for labels ("eero network" → "eero Network", "ISP / location" → "ISP / Location"). Words that already
    /// contain a capital (IP, WPA3, Wi-Fi) and proper names written in lowercase (eero, dnspyre) are left as they are;
    /// short connecting words stay lowercase except at the start.
    /// </summary>
    public static string Label(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var lead = w.TakeWhile(c => !char.IsLetterOrDigit(c)).Count(); // "(full)" → capitalize after "("
            if (lead >= w.Length) continue;
            var core = w[lead..];
            var bare = new string(core.TakeWhile(char.IsLetterOrDigit).ToArray());
            if (core.Any(char.IsUpper) || LowercaseNames.Contains(bare) || !char.IsLetter(core[0])) continue;
            if (i > 0 && lead == 0 && SmallWords.Contains(bare)) continue;
            words[i] = w[..lead] + char.ToUpperInvariant(core[0]) + core[1..];
        }
        return string.Join(' ', words);
    }

    /// <summary>Proper names that are written in lowercase.</summary>
    private static readonly HashSet<string> LowercaseNames = ["eero", "eeros", "dnspyre", "iOS"];

    public static string Title(string? s)
        => string.IsNullOrEmpty(s) ? "" : string.Join(' ', s.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Acronyms.Contains(w) ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
}
