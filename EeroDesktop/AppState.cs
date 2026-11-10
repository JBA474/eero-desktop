using System.Text.Json.Nodes;
using EeroDesktop.Api;

namespace EeroDesktop;

public sealed record NetworkRef(string Name, string Url)
{
    public string Id => J.IdFromUrl(Url) ?? "";
    public override string ToString() => Name;
}

/// <summary>Session-wide state: API client, account, selected network.</summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public EeroClient Api { get; } = CreateClient();

    private static EeroClient CreateClient()
    {
        var api = new EeroClient();
        api.TokenRefreshed += TokenStore.Save; // keep the renewed session for next launch
        return api;
    }
    public JsonObject? Account { get; private set; }
    public List<NetworkRef> Networks { get; } = [];
    public NetworkRef? Selected { get; private set; }
    public JsonObject? Network { get; private set; }

    public event Action<string>? StatusChanged;
    public void Report(string message) => StatusChanged?.Invoke(message);

    public string NetworkUrl => Selected?.Url ?? throw new InvalidOperationException("No network selected.");
    public string NetworkId => Selected?.Id ?? throw new InvalidOperationException("No network selected.");

    /// <summary>Resource link from network.resources (e.g. "devices", "reboot").</summary>
    public string Res(string name)
        => Network.S("resources." + name) ?? throw new InvalidOperationException($"This network does not expose the '{name}' resource.");

    public string? TryRes(string name) => Network.S("resources." + name);

    public async Task LoadAccountAsync()
    {
        Account = await Api.GetAsync("2.2/account") as JsonObject ?? throw new InvalidOperationException("Empty account response.");
        Networks.Clear();
        foreach (var n in Account.Arr("networks"))
        {
            var url = n.S("url");
            if (string.IsNullOrEmpty(url)) continue;
            var name = n.S("name") is { Length: > 0 } nm ? nm : n.S("nickname_label") is { Length: > 0 } nick ? nick : url;
            Networks.Add(new NetworkRef(name, url));
        }
    }

    public async Task SelectNetworkAsync(NetworkRef network)
    {
        Selected = network;
        await ReloadNetworkAsync();
    }

    public async Task<JsonObject> ReloadNetworkAsync()
    {
        Network = await Api.GetAsync(NetworkUrl) as JsonObject ?? throw new InvalidOperationException("Empty network response.");
        return Network;
    }

    /// <summary>
    /// Changes one thing on the network safely (the approach of the eero apps and the Home Assistant integration):
    /// re-reads the network, skips the write when it already has the desired value, otherwise sends the write exactly
    /// once (never retried: some settings writes restart eeros, and repeated writes queue repeated restarts),
    /// then re-reads. Returns false when nothing needed to change.
    /// </summary>
    /// <param name="undo">Builds the "Undo Last Change" entry from the network as it was just before the write; it's
    /// saved only after the write succeeds.</param>
    public async Task<bool> ChangeIfNeededAsync(Func<JsonObject, bool> alreadySet, Func<Task> write, Func<JsonObject, UndoService.Entry?>? undo = null)
    {
        var current = await ReloadNetworkAsync();
        if (alreadySet(current)) return false;
        var entry = undo?.Invoke(current);
        await write();
        if (entry is not null) UndoService.Record(entry);
        await ReloadNetworkAsync();
        return true;
    }

    /// <summary>PUT only the given fields to the network's settings resource, e.g. {"upnp": true}.</summary>
    public Task PutSettingsAsync(JsonObject onlyTheseFields) => Api.PutJsonAsync(Res("settings"), onlyTheseFields);

    private static List<string> CustomDns(JsonNode? n) => n.At("dns.custom.ips") is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : [];

    /// <summary>Point the network at these DNS servers (skipped if already set). eero restarts every eero afterwards.</summary>
    public Task<bool> SetCustomDnsAsync(IReadOnlyList<string> servers)
        => ChangeIfNeededAsync(
            n => string.Equals(n.S("dns.mode"), "custom", StringComparison.OrdinalIgnoreCase) && CustomDns(n).SequenceEqual(servers),
            () => PutSettingsAsync(new JsonObject
            {
                ["dns"] = new JsonObject
                {
                    ["mode"] = "custom",
                    ["custom"] = new JsonObject { ["ips"] = new JsonArray(servers.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) },
                },
            }),
            UndoService.Dns("custom", servers));

    /// <summary>Use the ISP's DNS servers (eero "automatic"); skipped if already automatic. eero restarts every eero afterwards.</summary>
    public Task<bool> SetAutomaticDnsAsync()
        => ChangeIfNeededAsync(
            n => string.Equals(n.S("dns.mode"), "automatic", StringComparison.OrdinalIgnoreCase),
            () => PutSettingsAsync(new JsonObject { ["dns"] = new JsonObject { ["mode"] = "automatic" } }),
            UndoService.Dns("automatic", []));

    /// <summary>True when the account has an active eero Plus subscription (premium_status), as far as we can tell.</summary>
    public bool HasEeroPlus => Network.S("premium_status")?.ToLowerInvariant() is "active" or "subscribed" or "trialing" or "trial" or "premium";

    /// <summary>True when the network reports capabilities.{name}.capable (or doesn't report capabilities at all).</summary>
    public bool Capable(string name) => Network?.At("capabilities") is not JsonObject caps || caps[name] is null || Network.B($"capabilities.{name}.capable");

    public void SignOut()
    {
        Api.Token = null;
        TokenStore.Clear();
        Account = null;
        Networks.Clear();
        Selected = null;
        Network = null;
    }
}
