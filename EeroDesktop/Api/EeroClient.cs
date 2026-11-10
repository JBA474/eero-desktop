using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EeroDesktop.Api;

/// <summary>Error returned by the eero cloud API (HTTP failure or meta.code >= 400).</summary>
public sealed class EeroApiException(int status, string message, string? body, TimeSpan? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Body { get; } = body;
    public bool IsUnauthorized => Status == 401;
    public bool IsRateLimited => Status == 429;
    /// <summary>For 429: how long eero asked us to wait (Retry-After), if it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Thin client for the eero cloud API used by the eero Android app (api-user.e2ro.com).
/// Mirrors the app's transport: X-User-Token auth header, form-encoded @Field calls, JSON @Body calls,
/// and the { meta, data } response envelope.
/// </summary>
public sealed class EeroClient
{
    /// <summary>eero API base. EERO_DESKTOP_API_BASE overrides it for automated tests against a local fake server.</summary>
    public static readonly Uri BaseUri = new(Environment.GetEnvironmentVariable("EERO_DESKTOP_API_BASE") is { Length: > 0 } b ? b.TrimEnd('/') + "/" : "https://api-user.e2ro.com/");
    public const string AppVersion = "26.13.0";

    private static readonly string[] AllowedHostSuffixes = ["e2ro.com", "eero.com"];
    private readonly HttpClient _http;

    public string? Token { get; set; }

    /// <summary>Raised after eero issued a new session token via login/refresh (so it can be saved).</summary>
    public event Action<string>? TokenRefreshed;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>Raw text of the last response, for the API console.</summary>
    public string? LastRawResponse { get; private set; }

    public EeroClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        _http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(60) };
    }

    // ---------- public helpers ----------

    public Task<JsonNode?> GetAsync(string path, IDictionary<string, string?>? query = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, WithQuery(path, query), null, ct);

    public Task<JsonNode?> DeleteAsync(string path, IDictionary<string, string?>? query = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, WithQuery(path, query), null, ct);

    public Task<JsonNode?> PostFormAsync(string path, IDictionary<string, string?> fields, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, Form(fields), ct);

    public Task<JsonNode?> PutFormAsync(string path, IDictionary<string, string?> fields, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, Form(fields), ct);

    public Task<JsonNode?> PostJsonAsync(string path, JsonNode? body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, Json(body), ct);

    public Task<JsonNode?> PutJsonAsync(string path, JsonNode? body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, path, Json(body), ct);

    /// <summary>The app posts a Gson-serialized empty string ("") as the body for action calls (reboot, speed test, update).</summary>
    public Task<JsonNode?> PostActionAsync(string path, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, path, Json(JsonValue.Create("")), ct);

    public static string Bool(bool b) => b ? "true" : "false";

    // ---------- core ----------

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct = default)
    {
        // Buffer the body so the request can be re-sent after a session refresh.
        var body = content is null ? null : await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var tokenUsed = Token;
        try
        {
            return await SendOnceAsync(method, path, Clone(content, body), ct).ConfigureAwait(false);
        }
        catch (EeroApiException ex) when (ex.IsUnauthorized && !IsLoginPath(path) && !string.IsNullOrEmpty(tokenUsed))
        {
            // Like the eero apps: an expired session is renewed with POST 2.2/login/refresh, then the call is retried once.
            if (!await TryRefreshAsync(tokenUsed, ct).ConfigureAwait(false)) throw;
            return await SendOnceAsync(method, path, Clone(content, body), ct).ConfigureAwait(false);
        }
    }

    private static bool IsLoginPath(string path)
        => path.TrimStart('/').Contains("login", StringComparison.OrdinalIgnoreCase) || path.Contains("logout", StringComparison.OrdinalIgnoreCase);

    private static HttpContent? Clone(HttpContent? original, byte[]? body)
    {
        if (original is null || body is null) return null;
        var copy = new ByteArrayContent(body);
        foreach (var h in original.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }

    /// <summary>Renews the session once; concurrent callers wait for the same refresh. Returns false if eero refused.</summary>
    private async Task<bool> TryRefreshAsync(string expiredToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Token != expiredToken && !string.IsNullOrEmpty(Token)) return true; // another call already refreshed
            var data = await SendOnceAsync(HttpMethod.Post, "2.2/login/refresh", Json(new JsonObject()), ct).ConfigureAwait(false);
            if (data?["user_token"]?.GetValue<string>() is not { Length: > 0 } fresh) return false;
            Token = fresh;
            TokenRefreshed?.Invoke(fresh);
            return true;
        }
        catch (EeroApiException) { return false; }
        catch (HttpRequestException) { return false; }
        finally { _refreshLock.Release(); }
    }

    private async Task<JsonNode?> SendOnceAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var uri = Resolve(path);
        using var req = new HttpRequestMessage(method, uri) { Content = content };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", $"eero-android/{AppVersion} (EeroDesktop; Windows NT {Environment.OSVersion.Version})");
        req.Headers.TryAddWithoutValidation("X-Accept-Language", "en-US");
        if (!string.IsNullOrEmpty(Token) && IsAllowedHost(uri))
            req.Headers.TryAddWithoutValidation("X-User-Token", Token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        LastRawResponse = text;

        // eero can rotate the session on any response via "Set-Cookie: s=<token>"; keep the newest one.
        if (!string.IsNullOrEmpty(Token) && IsAllowedHost(uri) && resp.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var first = cookie.Split(';', 2)[0].Trim();
                if (!first.StartsWith("s=", StringComparison.Ordinal)) continue;
                var rotated = Uri.UnescapeDataString(first[2..]);
                if (rotated.Length > 0 && rotated != Token) { Token = rotated; TokenRefreshed?.Invoke(rotated); }
            }
        }

        JsonNode? root = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { root = JsonNode.Parse(text); } catch (JsonException) { /* non-JSON body */ }
        }

        int metaCode = root?["meta"]?["code"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode || metaCode >= 400)
        {
            var status = resp.IsSuccessStatusCode ? metaCode : (int)resp.StatusCode;
            var err = root?["meta"]?["error"]?.ToString() ?? root?["meta"]?["message"]?.ToString();
            TimeSpan? retryAfter = resp.Headers.RetryAfter is { } ra
                ? ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null)
                : null;
            var msg = status switch
            {
                401 => "Your eero session has expired or is not authorized. Please sign in again.",
                429 => "eero is limiting how often this computer can make requests. "
                       + (retryAfter is { } w ? $"Try again in about {Math.Max(1, (int)Math.Ceiling(w.TotalSeconds))} seconds." : "Wait a minute and try again."),
                _ => $"eero API error {status} on {method} {uri.PathAndQuery}" + (string.IsNullOrEmpty(err) ? "" : $": {err}"),
            };
            if (err is not null && err.Contains("premium", StringComparison.OrdinalIgnoreCase))
                msg = $"This needs an eero Plus subscription, which this network doesn't have. (eero said: {err})";
            // Not found / method not allowed / gone / upgrade required usually mean eero changed its API since this build.
            if (status is 404 or 405 or 410 or 426)
                msg += $"\n\neero may have changed this part of its API since eero Desktop was built (based on the Android app {AppVersion}). "
                     + "Run tools\\Compare-ApkApi.ps1 against a newer eero APK to see what changed.";
            throw new EeroApiException(status, msg, text, retryAfter);
        }

        if (root is JsonObject obj && obj.ContainsKey("data"))
            return obj["data"];
        return root;
    }

    public static Uri Resolve(string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp))
            return abs;
        return new Uri(BaseUri, pathOrUrl.TrimStart('/'));
    }

    private static bool IsAllowedHost(Uri uri)
        => uri.Host.Equals(BaseUri.Host, StringComparison.OrdinalIgnoreCase) || AllowedHostSuffixes.Any(s => uri.Host.Equals(s, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    private static string WithQuery(string path, IDictionary<string, string?>? query)
    {
        if (query is null || query.Count == 0) return path;
        var qs = string.Join("&", query.Where(kv => kv.Value is not null)
            .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!)));
        if (qs.Length == 0) return path;
        return path + (path.Contains('?') ? "&" : "?") + qs;
    }

    private static HttpContent Form(IDictionary<string, string?> fields)
        => new FormUrlEncodedContent(fields.Where(kv => kv.Value is not null).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!)));

    private static HttpContent Json(JsonNode? body)
        => new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
}
