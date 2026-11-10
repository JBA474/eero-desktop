// Offline tests for EeroClient session refresh, using a local fake eero API (HttpListener on 127.0.0.1).
// Run: dotnet run --project C:\Users\Jess\Desktop\eero\tests\ApiTests
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

var port = FreePort();
Environment.SetEnvironmentVariable("EERO_DESKTOP_API_BASE", $"http://127.0.0.1:{port}/"); // before EeroClient is first used
// AppState saves renewed sessions; keep that away from the real %APPDATA%\EeroDesktop\session.bin.
Environment.SetEnvironmentVariable("EERO_DESKTOP_SESSION_FILE", Path.Combine(Path.GetTempPath(), $"eero-apitests-{Environment.ProcessId}.bin"));
Environment.SetEnvironmentVariable("EERO_DESKTOP_DATA_DIR", Path.Combine(Path.GetTempPath(), $"eero-apitests-{Environment.ProcessId}"));
var fake = new FakeEero(port);
fake.Start();

var results = new List<(string Name, bool Pass, string Detail)>();
async Task Test(string name, Func<Task<string>> body)
{
    fake.Reset();
    try { results.Add((name, true, await body())); }
    catch (Exception ex) { results.Add((name, false, ex.Message)); }
}
void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

await Test("Expired session is refreshed once, saved, and the GET is retried with the new token", async () =>
{
    fake.ValidToken = "new-token"; fake.RefreshableToken = "old-token"; fake.RefreshGives = "new-token";
    var api = new EeroDesktop.Api.EeroClient { Token = "old-token" };
    string? saved = null; api.TokenRefreshed += t => saved = t;
    var data = await api.GetAsync("2.2/account");
    Assert(data?["name"]?.GetValue<string>() == "Test Account", "account data not returned after retry");
    Assert(fake.RefreshCalls == 1, $"refresh called {fake.RefreshCalls} times");
    Assert(saved == "new-token" && api.Token == "new-token", $"new token not saved (saved={saved}, current={api.Token})");
    Assert(fake.TokensSeen.SequenceEqual(["old-token", "new-token"]), "token order: " + string.Join(",", fake.TokensSeen));
    return "401 → POST 2.2/login/refresh → retry OK; TokenRefreshed fired";
});

await Test("POST body is sent again unchanged on the retry", async () =>
{
    fake.ValidToken = "new-token"; fake.RefreshableToken = "old-token"; fake.RefreshGives = "new-token";
    var api = new EeroDesktop.Api.EeroClient { Token = "old-token" };
    await api.PutFormAsync("2.2/networks/1", new Dictionary<string, string?> { ["name"] = "Hello World" });
    var bodies = fake.Bodies.Where(b => b.Path == "/2.2/networks/1").Select(b => b.Body).ToList();
    Assert(bodies.Count == 2, $"expected 2 attempts, saw {bodies.Count}");
    Assert(bodies.All(b => b == "name=Hello+World"), "bodies: " + string.Join(" | ", bodies));
    return "both attempts sent name=Hello+World";
});

await Test("If eero refuses the refresh, the original 401 is reported and nothing loops", async () =>
{
    fake.ValidToken = "x"; fake.RefreshableToken = null;
    var api = new EeroDesktop.Api.EeroClient { Token = "dead-token" };
    var refreshed = false; api.TokenRefreshed += _ => refreshed = true;
    try { await api.GetAsync("2.2/account"); throw new Exception("expected EeroApiException"); }
    catch (EeroDesktop.Api.EeroApiException ex) { Assert(ex.IsUnauthorized, $"status {ex.Status}"); }
    Assert(fake.RefreshCalls == 1, $"refresh called {fake.RefreshCalls} times");
    Assert(!refreshed && api.Token == "dead-token", "token changed after failed refresh");
    return "IsUnauthorized surfaced (app then signs out), 1 refresh attempt";
});

await Test("Several calls failing at once share a single refresh", async () =>
{
    fake.ValidToken = "new-token"; fake.RefreshableToken = "old-token"; fake.RefreshGives = "new-token"; fake.RefreshDelayMs = 300;
    var api = new EeroDesktop.Api.EeroClient { Token = "old-token" };
    await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.GetAsync("2.2/account")));
    Assert(fake.RefreshCalls == 1, $"refresh called {fake.RefreshCalls} times");
    return "5 parallel calls → 1 refresh";
});

await Test("Login calls are never retried through refresh", async () =>
{
    fake.ValidToken = "x"; fake.RefreshableToken = "old-token"; fake.RefreshGives = "y";
    var api = new EeroDesktop.Api.EeroClient { Token = "old-token" };
    try { await api.PostFormAsync("2.2/login/verify", new Dictionary<string, string?> { ["code"] = "000000" }); } catch (EeroDesktop.Api.EeroApiException) { }
    Assert(fake.RefreshCalls == 0, "refresh was attempted for a login call");
    return "no refresh for 2.2/login/verify";
});

await Test("A rotated session cookie (Set-Cookie: s=...) replaces and saves the token", async () =>
{
    fake.ValidToken = "tok-1"; fake.RotateTo = "tok-2";
    var api = new EeroDesktop.Api.EeroClient { Token = "tok-1" };
    string? saved = null; api.TokenRefreshed += t => saved = t;
    await api.GetAsync("2.2/account");
    Assert(api.Token == "tok-2" && saved == "tok-2", $"token={api.Token}, saved={saved}");
    return "tok-1 → tok-2 from Set-Cookie, TokenRefreshed fired";
});

await Test("Rate limiting (429) gives a clear message with the Retry-After wait", async () =>
{
    fake.ValidToken = "tok"; fake.RateLimitSeconds = 42;
    var api = new EeroDesktop.Api.EeroClient { Token = "tok" };
    try { await api.GetAsync("2.2/account"); throw new Exception("expected EeroApiException"); }
    catch (EeroDesktop.Api.EeroApiException ex)
    {
        Assert(ex.IsRateLimited, $"status {ex.Status}");
        Assert(ex.RetryAfter == TimeSpan.FromSeconds(42), $"RetryAfter {ex.RetryAfter}");
        Assert(ex.Message.Contains("42 seconds"), ex.Message);
        Assert(fake.RefreshCalls == 0, "429 must not trigger a session refresh");
        return ex.Message;
    }
});

await Test("Settings change sends only the one field, as a single PUT", async () =>
{
    fake.ValidToken = "tok"; fake.Network = new JsonObject { ["name"] = "Home", ["upnp"] = false, ["sqm"] = false, ["dns"] = new JsonObject { ["mode"] = "automatic" },
        ["resources"] = new JsonObject { ["settings"] = "/2.2/networks/1/settings" } };
    var s = EeroDesktop.AppState.Current; s.Api.Token = "tok";
    await s.SelectNetworkAsync(new EeroDesktop.NetworkRef("Home", "/2.2/networks/1"));
    var changed = await s.ChangeIfNeededAsync(n => n["upnp"]?.GetValue<bool>() == true, () => s.PutSettingsAsync(new JsonObject { ["upnp"] = true }));
    var puts = fake.Bodies.Where(b => b.Path == "/2.2/networks/1/settings").ToList();
    Assert(changed, "reported no change");
    Assert(puts.Count == 1, $"{puts.Count} PUTs");
    Assert(puts[0].Body == "{\"upnp\":true}", "body was " + puts[0].Body);
    return "PUT settings {\"upnp\":true} — nothing else in the body";
});

await Test("Nothing is sent when the network already has the value", async () =>
{
    fake.ValidToken = "tok"; fake.Network = new JsonObject { ["upnp"] = true, ["resources"] = new JsonObject { ["settings"] = "/2.2/networks/1/settings" } };
    var s = EeroDesktop.AppState.Current; s.Api.Token = "tok";
    var changed = await s.ChangeIfNeededAsync(n => n["upnp"]?.GetValue<bool>() == true, () => s.PutSettingsAsync(new JsonObject { ["upnp"] = true }));
    Assert(!changed, "reported a change");
    Assert(!fake.Bodies.Any(b => b.Path == "/2.2/networks/1/settings"), "a PUT was sent anyway");
    return "re-read the network, saw upnp already on, sent no PUT";
});

await Test("A failed settings write is reported once and never retried", async () =>
{
    fake.ValidToken = "tok"; fake.FailSettings = true;
    fake.Network = new JsonObject { ["upnp"] = false, ["resources"] = new JsonObject { ["settings"] = "/2.2/networks/1/settings" } };
    var s = EeroDesktop.AppState.Current; s.Api.Token = "tok";
    try { await s.ChangeIfNeededAsync(n => n["upnp"]?.GetValue<bool>() == true, () => s.PutSettingsAsync(new JsonObject { ["upnp"] = true })); throw new Exception("expected an error"); }
    catch (EeroDesktop.Api.EeroApiException ex) { Assert(ex.Status == 500, $"status {ex.Status}"); }
    var puts = fake.Bodies.Count(b => b.Path == "/2.2/networks/1/settings");
    Assert(puts == 1, $"{puts} PUT attempts");
    return "1 attempt, error surfaced";
});

// ---------- DNS benchmark (dnspyre) logic, no network needed ----------

await Test("dnspyre JSON output (3.12 field names) is parsed", () =>
{
    const string json = "{\"totalRequests\":40,\"totalSuccessResponses\":39,\"totalNegativeResponses\":0,\"totalErrorResponses\":0,\"totalIOErrors\":1,"
        + "\"totalIDmismatch\":0,\"totalTruncatedResponses\":0,\"responseRcodes\":{\"NOERROR\":39},\"questionTypes\":{\"A\":40},\"queriesPerSecond\":43.78,"
        + "\"benchmarkDurationSeconds\":0.9,\"latencyStats\":{\"minMs\":11,\"meanMs\":22,\"stdMs\":3,\"maxMs\":60,\"p99Ms\":55,\"p95Ms\":31,\"p90Ms\":27,\"p75Ms\":24,\"p50Ms\":21}}";
    var r = EeroDesktop.Api.Dnspyre.Parse("1.1.1.1", "some log line\n" + json)!;
    Assert(r.Requests == 40 && r.Successes == 39 && r.Errors == 1, $"counts {r.Requests}/{r.Successes}/{r.Errors}");
    Assert(r.P50Ms == 21 && r.P95Ms == 31 && r.MeanMs == 22, $"latency {r.P50Ms}/{r.P95Ms}/{r.MeanMs}");
    Assert(Math.Abs(r.SuccessRate - 0.975) < 1e-9, $"success {r.SuccessRate}");
    return Task.FromResult("40 requests, 39 answered (97.5%), median 21 ms, p95 31 ms");
});

await Test("Providers come from the network: ISP, eero's suggested servers, AdGuard, current custom", () =>
{
    var network = JsonNode.Parse("""
        { "dns": { "mode": "custom", "custom": { "ips": ["192.0.2.53"] }, "parent": { "ips": ["206.225.75.225", "206.225.75.226"] },
          "default_test_servers": [ { "name": "Cloudflare", "ipv4": ["1.1.1.1", "1.0.0.1"] }, { "name": "Quad9", "ipv4": ["9.9.9.9", "149.112.112.112"] } ] } }
        """);
    var providers = EeroDesktop.DnsBenchmark.Providers(network);
    var names = providers.Select(p => p.Name).ToList();
    Assert(names.SequenceEqual(["Your ISP (eero automatic)", "Cloudflare", "Quad9", "AdGuard DNS", "Control D", "Your current custom DNS"]), string.Join(", ", names));
    Assert(EeroDesktop.DnsBenchmark.Current(network, providers)?.Name == "Your current custom DNS", "current not detected");
    network!["dns"]!["mode"] = "automatic";
    Assert(EeroDesktop.DnsBenchmark.Current(network, providers)?.IsIsp == true, "automatic should map to the ISP");
    return Task.FromResult(string.Join(", ", names));
});

await Test("Ranking: unreliable providers last, then by speed (median weighted with 95th percentile)", () =>
{
    EeroDesktop.ProviderResult R(string name, double p50, double p95, int ok, int total) =>
        new(new EeroDesktop.DnsProvider(name, ["x"]), [new EeroDesktop.Api.DnspyreResult("x", total, ok, total - ok, 1, p50, p50, p95, p95, null)], null);
    var ranked = EeroDesktop.DnsBenchmark.Rank(
    [
        R("Fast but flaky", 5, 8, 80, 100),     // 80% answered -> not eligible
        R("Steady", 20, 25, 100, 100),          // score 22
        R("Quick median, slow tail", 15, 60, 100, 100), // score 33
        R("Fastest", 12, 18, 100, 100),         // score 14.4
    ]);
    var order = ranked.Select(r => r.Provider.Name).ToList();
    Assert(order.SequenceEqual(["Fastest", "Steady", "Quick median, slow tail", "Fast but flaky"]), string.Join(" > ", order));
    return Task.FromResult(string.Join(" > ", order));
});

// ---------- Local history, eero app release check, benchmark note (no network needed) ----------
await Test("History: speed tests are merged without duplicates and filtered by network and date", () =>
{
    EeroDesktop.HistoryStore.Clear();
    var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    var added1 = EeroDesktop.HistoryStore.MergeSpeedTests([new("net1", t0, 500, 40), new("net1", t0.AddDays(1), 480, 38), new("net2", t0, 100, 10)]);
    var added2 = EeroDesktop.HistoryStore.MergeSpeedTests([new("net1", t0, 500, 40), new("net1", t0.AddDays(2), 510, 41)]);
    var all = EeroDesktop.HistoryStore.SpeedTests("net1", DateTime.MinValue);
    var recent = EeroDesktop.HistoryStore.SpeedTests("net1", t0.AddHours(30));
    Assert(added1 == 3 && added2 == 1, $"added {added1} then {added2}");
    Assert(all.Count == 3 && all[0].Utc == t0 && all[0].Utc.Kind == DateTimeKind.Utc, $"net1 has {all.Count}; first {all.FirstOrDefault()}");
    Assert(recent.Count == 1 && recent[0].DownMbps == 510, $"since filter gave {recent.Count}");
    return Task.FromResult("3 + 1 new, duplicate skipped, per-network and date filters OK");
});

await Test("History: outages pair down/up per subject, open outages stay open, damaged lines are skipped", () =>
{
    EeroDesktop.HistoryStore.Clear();
    var t = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
    EeroDesktop.HistoryStore.AddEvent(new("net1", t, "internet", "Internet", "down", "eero reported Disconnected"));
    EeroDesktop.HistoryStore.AddEvent(new("net1", t.AddMinutes(6), "eero", "Office", "down"));
    File.AppendAllText(Path.Combine(EeroDesktop.HistoryStore.Dir, "events.jsonl"), "{not json\n");
    EeroDesktop.HistoryStore.AddEvent(new("net1", t.AddMinutes(14), "internet", "Internet", "up"));
    EeroDesktop.HistoryStore.AddEvent(new("net2", t, "internet", "Internet", "down"));
    var outages = EeroDesktop.HistoryStore.Outages("net1", DateTime.MinValue);
    var internet = outages.Single(o => o.Kind == "internet");
    var office = outages.Single(o => o.Kind == "eero");
    Assert(outages.Count == 2, $"{outages.Count} outages");
    Assert(internet.EndUtc == t.AddMinutes(14) && internet.Detail == "eero reported Disconnected", $"internet {internet}");
    Assert(office.EndUtc is null && office.Subject == "Office", $"office {office}");
    Assert(EeroDesktop.Pages.HistoryPage.Duration(internet.Duration(DateTime.UtcNow)) == "14 min", "duration text " + EeroDesktop.Pages.HistoryPage.Duration(internet.Duration(DateTime.UtcNow)));
    var last = EeroDesktop.HistoryStore.LastStates("net1");
    Assert(last[("internet", "Internet")] == "up" && last[("eero", "Office")] == "down", "last states wrong");
    return Task.FromResult("internet 14 min (closed), Office eero still open, net2 kept separate");
});

await Test("History: DNS benchmark runs round-trip (failed providers kept, marked not OK)", () =>
{
    EeroDesktop.HistoryStore.Clear();
    var now = DateTime.UtcNow;
    EeroDesktop.HistoryStore.AddDnsRun(new("net1", now, "Ethernet", "Quad9",
        [new("Quad9", 11.2, 30.5, 1, true), new("Broken", -1, -1, 0.2, false)]));
    var runs = EeroDesktop.HistoryStore.DnsRuns("net1", now.AddMinutes(-1));
    Assert(runs.Count == 1 && runs[0].Providers.Count == 2 && runs[0].InUse == "Quad9" && runs[0].Connection == "Ethernet", "run not stored");
    Assert(runs[0].Providers[0].MedianMs == 11.2 && !runs[0].Providers[1].Ok, "provider values wrong");
    return Task.FromResult("stored and read back");
});

await Test("eero app release: Play Store version and date are parsed; only newer versions count", () =>
{
    const string page = """...null,[[["26.13.0.36807"]],[[[37]],[[[28,"9"]]]]],null... ,"26.7.0.36786",null... <div class="lXlx5">Updated on</div><div class="xg1aie">Oct 28, 2026</div>""";
    var r = EeroDesktop.Api.EeroAppRelease.Parse(page);
    Assert(r is { Version: "26.13.0", Updated: "Oct 28, 2026" }, $"parsed {r}");
    Assert(EeroDesktop.Api.EeroAppRelease.Parse("<html>no version here</html>") is null, "garbage parsed");
    Assert(!EeroDesktop.Api.EeroAppRelease.IsNewer("26.13.0", "26.13.0") && EeroDesktop.Api.EeroAppRelease.IsNewer("26.14.0", "26.13.0")
           && EeroDesktop.Api.EeroAppRelease.IsNewer("27.0.0", "26.13.0") && !EeroDesktop.Api.EeroAppRelease.IsNewer("26.9.0", "26.13.0"), "version compare");
    var s = new EeroDesktop.AppSettings { EeroAppLatestVersion = "27.1.0", EeroAppCheckedUtc = DateTime.UtcNow };
    Assert(EeroDesktop.Pages.SettingsPage.EeroAppStatusText(s).StartsWith("eero has released app 27.1.0"), "newer status text");
    s.EeroAppLatestVersion = EeroDesktop.Api.EeroClient.AppVersion;
    Assert(EeroDesktop.Pages.SettingsPage.EeroAppStatusText(s).StartsWith("Up to date"), "same-version status text");
    return Task.FromResult("26.13.0 (Oct 28, 2026); 26.9 < 26.13 < 26.14 < 27.0");
});

await Test("DNS benchmark: the note says results come from this PC, and what Wi-Fi adds", () =>
{
    var wifi = EeroDesktop.DnsBenchmarkWindow.ConnectionNote("Wi-Fi");
    var wired = EeroDesktop.DnsBenchmarkWindow.ConnectionNote("Ethernet");
    Assert(wifi.Contains("from this PC") && wifi.Contains("Wi-Fi") && wifi.Contains("ranking still holds"), wifi);
    Assert(wired.Contains("from this PC") && wired.Contains("cable"), wired);
    return Task.FromResult($"this PC is on {EeroDesktop.HistoryStore.ConnectionType()}");
});

// ---------- Undo, Control D, backup of history, device presence, monthly summary ----------
await Test("Undo: a switch change saves its old value; Undo puts it back with one PUT; undoing again redoes", async () =>
{
    fake.ValidToken = "tok"; fake.Network = new JsonObject { ["name"] = "Home", ["upnp"] = false, ["dns"] = new JsonObject { ["mode"] = "automatic" },
        ["resources"] = new JsonObject { ["settings"] = "/2.2/networks/1/settings" } };
    var s = EeroDesktop.AppState.Current; s.Api.Token = "tok";
    await s.SelectNetworkAsync(new EeroDesktop.NetworkRef("Home", "/2.2/networks/1"));
    EeroDesktop.UndoService.Forget("1");
    var upnp = EeroDesktop.Pages.AdvancedPage.FindFeature("upnp")!;
    await s.ChangeIfNeededAsync(n => upnp.Read(n) == true, () => upnp.Write(true), EeroDesktop.UndoService.Toggle(upnp, true));
    var entry = EeroDesktop.UndoService.For("1");
    Assert(entry is { Kind: "toggle", Done: "UPnP turned on", UndoText: "Turn UPnP off again" }, $"entry {entry}");
    var seen = fake.Bodies.Count;
    Assert(await EeroDesktop.UndoService.UndoAsync(entry!), "undo reported nothing changed");
    var puts = fake.Bodies.Skip(seen).Where(b => b.Path == "/2.2/networks/1/settings").Select(b => b.Body).ToList();
    Assert(puts.SequenceEqual(["{\"upnp\":false}"]), "undo sent: " + string.Join(" | ", puts));
    var redo = EeroDesktop.UndoService.For("1");
    Assert(redo?.UndoText == "Turn UPnP on again", $"after undo: {redo?.UndoText}");
    return Task.FromResult("on → undo sent {\"upnp\":false} once → next undo would turn it on again").Result;
});

await Test("Undo: a DNS change remembers the previous servers and switching back restores them exactly", async () =>
{
    fake.ValidToken = "tok"; fake.Network = new JsonObject { ["name"] = "Home",
        ["dns"] = new JsonObject { ["mode"] = "custom", ["custom"] = new JsonObject { ["ips"] = new JsonArray("94.140.14.14", "94.140.15.15") } },
        ["resources"] = new JsonObject { ["settings"] = "/2.2/networks/1/settings" } };
    var s = EeroDesktop.AppState.Current; s.Api.Token = "tok";
    await s.SelectNetworkAsync(new EeroDesktop.NetworkRef("Home", "/2.2/networks/1"));
    await s.SetCustomDnsAsync(EeroDesktop.DnsBenchmark.ControlD.Servers);
    var entry = EeroDesktop.UndoService.For("1")!;
    Assert(entry.Done == "DNS changed from AdGuard DNS (94.140.14.14, 94.140.15.15) to Control D (76.76.2.2, 76.76.10.2)", entry.Done);
    Assert(entry.RestartsAll && entry.UndoText.StartsWith("Switch DNS back to AdGuard DNS"), entry.UndoText);
    var seen = fake.Bodies.Count;
    await EeroDesktop.UndoService.UndoAsync(entry);
    var body = fake.Bodies.Skip(seen).Single(b => b.Path == "/2.2/networks/1/settings").Body;
    Assert(body == "{\"dns\":{\"mode\":\"custom\",\"custom\":{\"ips\":[\"94.140.14.14\",\"94.140.15.15\"]}}}", body);
    return Task.FromResult(entry.Done).Result;
});

await Test("Control D is offered as an ad-blocking provider in the benchmark", () =>
{
    var providers = EeroDesktop.DnsBenchmark.Providers(new JsonObject());
    var cd = providers.SingleOrDefault(p => p.Name == "Control D");
    Assert(cd is { BlocksAds: true } && cd.Servers.SequenceEqual(["76.76.2.2", "76.76.10.2"]), "Control D missing");
    Assert(providers.Where(p => p.BlocksAds).Select(p => p.Name).SequenceEqual(["AdGuard DNS", "Control D"]), "ad blockers");
    Assert(EeroDesktop.UndoService.DescribeDns("automatic", []) == "automatic (your ISP's servers)", "describe automatic");
    return Task.FromResult(string.Join(", ", providers.Select(p => p.Name + (p.BlocksAds ? " (ads)" : ""))));
});

await Test("Backup: history exports to JSON and importing merges without duplicating", () =>
{
    EeroDesktop.HistoryStore.Clear();
    var t = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
    EeroDesktop.HistoryStore.MergeSpeedTests([new("1", t, 900, 90)]);
    EeroDesktop.HistoryStore.AddEvent(new("1", t, "internet", "Internet", "down"));
    EeroDesktop.HistoryStore.AddDeviceEvents([new("1", t, "aa:bb", "Phone", "online", "192.168.1.5")]);
    var backup = EeroDesktop.HistoryStore.Export();
    var addedSame = EeroDesktop.HistoryStore.Import(backup);           // everything already here
    EeroDesktop.HistoryStore.Clear();
    var addedFresh = EeroDesktop.HistoryStore.Import(backup);          // restore onto an empty PC
    Assert(addedSame == 0, $"re-import added {addedSame}");
    Assert(addedFresh == 3, $"restore added {addedFresh}");
    Assert(EeroDesktop.HistoryStore.DeviceEvents("1", DateTime.MinValue).Single().Name == "Phone", "device event lost");
    return Task.FromResult("3 records; re-import adds 0, restore adds 3");
});

await Test("Monthly summary: speed, outages (only while watched) and DNS for last month", () =>
{
    EeroDesktop.HistoryStore.Clear();
    var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
    EeroDesktop.HistoryStore.MergeSpeedTests([new("1", start.AddDays(3), 2000, 1000), new("1", start.AddDays(10), 1800, 1040), new("1", start.AddDays(40), 5, 5)]);
    EeroDesktop.HistoryStore.AddEvent(new("1", start.AddDays(20), "internet", "Internet", "down"));
    EeroDesktop.HistoryStore.AddEvent(new("1", start.AddDays(20).AddMinutes(25), "internet", "Internet", "up"));
    EeroDesktop.HistoryStore.AddDnsRun(new("1", start.AddDays(5), "Ethernet", "AdGuard DNS", [new("Your ISP (eero automatic)", 18, 22, 1, true), new("AdGuard DNS", 33, 40, 1, true)]));
    var watched = EeroDesktop.MonthlySummary.Build("1", "Home", 2026, 10, start.AddDays(-1));
    Assert(watched is not null && watched.Title == "October 2026 on Home", $"title {watched?.Title}");
    Assert(watched!.Body.Contains("1,900 Mbps down, 1,020 up on average (2 tests; slowest 1,800 down)"), watched.Body);
    Assert(watched.Body.Contains("1 outage, 25 min down in total.") && watched.Body.Contains("fastest was usually Your ISP"), watched.Body);
    var notWatched = EeroDesktop.MonthlySummary.Build("1", "Home", 2026, 10, start.AddMonths(1).AddDays(9));
    Assert(!notWatched!.Body.Contains("Internet:"), "outage line shown for a month that wasn't watched");
    Assert(EeroDesktop.MonthlySummary.Build("1", "Home", 2026, 8, start) is null, "empty month should give no summary");
    return Task.FromResult(watched.Body.Replace("\n", " | "));
});

await Test("Device history: joins and leaves round-trip and the last state per device is known", () =>
{
    EeroDesktop.HistoryStore.Clear();
    var t = DateTime.UtcNow.AddHours(-2);
    EeroDesktop.HistoryStore.AddDeviceEvents([new("1", t, "aa:bb", "Phone", "online"), new("1", t.AddMinutes(30), "aa:bb", "Phone", "offline"), new("1", t, "cc:dd", "TV", "online")]);
    var last = EeroDesktop.HistoryStore.LastDeviceStates("1");
    Assert(last["aa:bb"] == "offline" && last["cc:dd"] == "online", "last states");
    Assert(EeroDesktop.HistoryStore.DeviceEvents("1", t.AddMinutes(1)).Count == 1, "since filter");
    return Task.FromResult("Phone offline, TV online");
});

await Test("Labels use Title Case but keep eero, acronyms and small words as they should be", () =>
{
    var cases = new Dictionary<string, string>
    {
        ["eero network"] = "eero Network", ["Internet status"] = "Internet Status", ["ISP / location"] = "ISP / Location",
        ["Wi-Fi network scan"] = "Wi-Fi Network Scan", ["Network (full)"] = "Network (Full)", ["Optimize for conferencing and gaming (SQM)"] = "Optimize for Conferencing and Gaming (SQM)",
        ["eero Plus ad blocking (whole network)"] = "eero Plus Ad Blocking (Whole Network)", ["WPA3 per band"] = "WPA3 per Band", ["Fast transition (802.11r)"] = "Fast Transition (802.11r)",
    };
    var wrong = cases.Where(kv => EeroDesktop.Api.J.Label(kv.Key) != kv.Value).Select(kv => $"'{kv.Key}' → '{EeroDesktop.Api.J.Label(kv.Key)}'").ToList();
    Assert(wrong.Count == 0, string.Join("; ", wrong));
    return Task.FromResult($"{cases.Count} labels OK");
});

await Test("Status lights: connected is green, disconnected is red, anything in between is yellow", () =>
{
    var L = EeroDesktop.StatusLights.LevelOf;
    Assert(L("Connected") == EeroDesktop.StatusLights.Level.Good && L("green") == EeroDesktop.StatusLights.Level.Good && L("Online") == EeroDesktop.StatusLights.Level.Good, "good");
    Assert(L("Disconnected") == EeroDesktop.StatusLights.Level.Bad && L("red") == EeroDesktop.StatusLights.Level.Bad && L("Offline") == EeroDesktop.StatusLights.Level.Bad, "bad");
    Assert(L("yellow") == EeroDesktop.StatusLights.Level.Warning && L("Paused") == EeroDesktop.StatusLights.Level.Warning && L(null) == EeroDesktop.StatusLights.Level.Warning, "warning");
    return Task.FromResult("green / red / yellow");
});

await Test("IPv6 pinholes show the device's name and MAC, and well-known ports say what they're for", () =>
{
    var devices = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
    {
        ["2.2/networks/1/devices/9009d0581ebb"] = new JsonObject { ["nickname"] = "WindyCityJess NAS", ["mac"] = "90:09:d0:58:1e:bb" },
    };
    var named = EeroDesktop.Pages.PortsPage.PinholeDevice("/2.2/networks/1/devices/9009d0581ebb", devices);
    var unknown = EeroDesktop.Pages.PortsPage.PinholeDevice("/2.2/networks/1/devices/aabbccddeeff", devices);
    Assert(named == "WindyCityJess NAS (90:09:d0:58:1e:bb)", named);
    Assert(unknown == "Unknown device (aa:bb:cc:dd:ee:ff)", unknown);
    Assert(EeroDesktop.Pages.PortsPage.PortService("32400") == "Plex Media Server", "Plex port");
    return Task.FromResult($"{named}; {unknown}");
});

await Test("Needs a Name: machine-made names are flagged, real ones aren't, and a name is suggested", () =>
{
    JsonObject D(string? host, string? nick = null, string? maker = null, string? type = null)
        => new() { ["hostname"] = host, ["nickname"] = nick, ["manufacturer"] = maker, ["device_type"] = type, ["mac"] = "98:43:fa:f3:28:1b" };
    var flagged = new[] { "lp05vu8c.adhcscint.net.", "Android_7I5KBBQS", "ESP_3A9F21", "98:43:fa:f3:28:1b", "" };
    var fine = new[] { "Bedroom-TV", "Bosch Dishwasher", "fireplacespeaker", "Doorbell Speaker", "dell-win11." };
    var wrongFlag = flagged.Where(h => !EeroDesktop.Pages.DevicesPage.NeedsName(D(h))).ToList();
    var wrongFine = fine.Where(h => EeroDesktop.Pages.DevicesPage.NeedsName(D(h))).ToList();
    Assert(wrongFlag.Count == 0, "not flagged: " + string.Join(", ", wrongFlag));
    Assert(wrongFine.Count == 0, "wrongly flagged: " + string.Join(", ", wrongFine));
    Assert(!EeroDesktop.Pages.DevicesPage.NeedsName(D("Android_7I5KBBQS", nick: "Jess's Phone")), "a nickname should count as named");
    var s1 = EeroDesktop.Pages.DevicesPage.SuggestName(D("lp05vu8c.adhcscint.net.", maker: "Intel Corporate", type: "laptop"));
    var s2 = EeroDesktop.Pages.DevicesPage.SuggestName(D("iPhone", maker: "Apple, Inc."));
    Assert(s1 == "Intel Laptop" && s2 == "Apple iPhone", $"{s1} / {s2}");
    return Task.FromResult($"{flagged.Length} flagged, {fine.Length} left alone; suggestions: {s1}, {s2}");
});

await Test("Undo for a device rename sends the saved request, and undoing again renames it back", async () =>
{
    fake.ValidToken = "tok"; fake.Network = new JsonObject { ["name"] = "Home", ["resources"] = new JsonObject { ["settings"] = "/2.2/networks/1/settings" } };
    var s = EeroDesktop.AppState.Current; s.Api.Token = "tok";
    await s.SelectNetworkAsync(new EeroDesktop.NetworkRef("Home", "/2.2/networks/1"));
    EeroDesktop.UndoService.RecordRequest("Renamed TV to Den TV", "Rename it back to TV", "Renamed Den TV back to TV", "Rename it to Den TV again",
        new("PUT", "/2.3/networks/1/devices/aa", new JsonObject { ["nickname"] = "TV" }), new("PUT", "/2.3/networks/1/devices/aa", new JsonObject { ["nickname"] = "Den TV" }));
    var seen = fake.Bodies.Count;
    await EeroDesktop.UndoService.UndoAsync(EeroDesktop.UndoService.For("1")!);
    var after = EeroDesktop.UndoService.For("1")!;
    await EeroDesktop.UndoService.UndoAsync(after);
    var sent = fake.Bodies.Skip(seen).Where(b => b.Path == "/2.3/networks/1/devices/aa").Select(b => b.Body).ToList();
    Assert(sent.SequenceEqual(["{\"nickname\":\"TV\"}", "{\"nickname\":\"Den TV\"}"]), "sent: " + string.Join(" | ", sent));
    Assert(after.Done == "Renamed Den TV back to TV" && after.UndoText == "Rename it to Den TV again", $"{after.Done} / {after.UndoText}");
    return $"undo → {sent[0]}, redo → {sent[1]}";
});

await Test("Security check flags risky settings and explains every open port", () =>
{
    var net = new JsonObject { ["password"] = "short123", ["wpa3"] = false, ["upnp"] = true, ["updates"] = new JsonObject { ["has_update"] = false } };
    var devices = new List<JsonObject>
    {
        new() { ["url"] = "/2.2/networks/1/devices/9009d0581ebb", ["mac"] = "90:09:d0:58:1e:bb", ["nickname"] = "NAS", ["ip"] = "192.168.1.69", ["connected"] = true },
        new() { ["url"] = "/2.2/networks/1/devices/9843faf3281b", ["mac"] = "98:43:fa:f3:28:1b", ["hostname"] = "lp05vu8c.adhcscint.net.", ["ip"] = "192.168.1.119", ["connected"] = true },
    };
    var forwards = new List<JsonObject>
    {
        new() { ["description"] = "Plex", ["ip"] = "192.168.1.69", ["gateway_port"] = "32400", ["enabled"] = true },
        new() { ["description"] = "Old game", ["ip"] = "192.168.1.200", ["gateway_port"] = "25565", ["enabled"] = true },
        new() { ["description"] = "Off", ["ip"] = "192.168.1.69", ["gateway_port"] = "5001", ["enabled"] = false },
        new() { ["description"] = "DSM", ["ip"] = "192.168.1.69", ["gateway_port"] = "5001", ["enabled"] = true },
    };
    var reservations = new List<JsonObject> { new() { ["ip"] = "192.168.1.69" } };
    var pinholes = new List<JsonObject> { new() { ["device"] = "/2.2/networks/1/devices/9009d0581ebb", ["port"] = "32400" } };
    var guest = new JsonObject { ["enabled"] = true, ["name"] = "JBA Guest" };
    var f = EeroDesktop.SecurityCheck.Run(new(net, devices, forwards, reservations, pinholes, guest, false));
    string StatusOf(string check, string contains = "") => f.First(x => x.Check == check && x.Detail.Contains(contains)).Status;
    Assert(StatusOf("Guest network password") == "Problem", "open guest network");
    Assert(StatusOf("Port forward", "Old game") == "Problem", "forward without reservation");
    Assert(StatusOf("Port forward", "Plex") == "OK" && f.First(x => x.Detail.StartsWith("Plex")).Detail.Contains("Plex Media Server"), "Plex forward");
    Assert(StatusOf("Wi-Fi password length") == "Warning" && StatusOf("UPnP") == "Warning" && StatusOf("WPA3") == "Warning", "warnings");
    Assert(StatusOf("IPv6 pinhole") == "OK" && f.First(x => x.Check == "IPv6 pinhole").Detail.Contains("NAS (90:09:d0:58:1e:bb)"), "pinhole");
    Assert(StatusOf("Device names") == "Warning" && StatusOf("Disabled port forwards") == "OK", "devices / disabled");
    Assert(StatusOf("Port forward", "DSM") == "Warning" && f.First(x => x.Detail.StartsWith("DSM")).Detail.Contains("VPN"), "admin login forward");
    Assert(f[0].Status == "Problem" && f[^1].Status == "OK", "problems first");
    return Task.FromResult($"{f.Count(x => x.Status == "Problem")} problems, {f.Count(x => x.Status == "Warning")} warnings, {f.Count(x => x.Status == "OK")} OK");
});

fake.Stop();
foreach (var r in results) Console.WriteLine($"{(r.Pass ? "PASS" : "FAIL")}  {r.Name}  —  {r.Detail}");
var failed = results.Count(r => !r.Pass);
Console.WriteLine($"{results.Count - failed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

/// <summary>Minimal fake of the eero API envelope: 401 for wrong tokens, login/refresh issues a new token.</summary>
sealed class FakeEero(int port)
{
    private readonly HttpListener _listener = new();
    public string? ValidToken, RefreshableToken, RefreshGives;
    public int RefreshDelayMs, RateLimitSeconds;
    public string? RotateTo;
    private int _refreshCalls;
    public int RefreshCalls => _refreshCalls;
    public ConcurrentQueue<string> TokensQueue = new();
    public List<string> TokensSeen => TokensQueue.ToList();
    public ConcurrentQueue<(string Path, string Body)> BodiesQueue = new();
    public List<(string Path, string Body)> Bodies => BodiesQueue.ToList();

    /// <summary>Stateful network for settings tests: GET returns it, PUT .../settings merges the body into it.</summary>
    public JsonObject? Network;
    public bool FailSettings;

    public void Reset() { ValidToken = RefreshableToken = RefreshGives = RotateTo = null; RefreshDelayMs = RateLimitSeconds = 0; _refreshCalls = 0; TokensQueue = new(); BodiesQueue = new(); Network = null; FailSettings = false; }

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        });
    }

    public void Stop() => _listener.Stop();

    private async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var token = ctx.Request.Headers["X-User-Token"] ?? "";
        var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        if (path == "/2.2/login/refresh")
        {
            Interlocked.Increment(ref _refreshCalls);
            await Task.Delay(RefreshDelayMs);
            if (RefreshableToken is not null && token == RefreshableToken)
                await Reply(ctx, 200, new JsonObject { ["user_token"] = RefreshGives });
            else
                await Reply(ctx, 401, null, "error.session.invalid");
            return;
        }
        TokensQueue.Enqueue(token);
        BodiesQueue.Enqueue((path, body));
        if (path.StartsWith("/2.2/login")) { await Reply(ctx, 401, null, "error.login"); return; }
        if (RateLimitSeconds > 0) { ctx.Response.Headers["Retry-After"] = RateLimitSeconds.ToString(); await Reply(ctx, 429, null, "error.rate.limit"); return; }
        if (token != ValidToken) { await Reply(ctx, 401, null, "error.session.refresh"); return; }
        if (RotateTo is not null) ctx.Response.Headers.Add("Set-Cookie", $"s={RotateTo}; Path=/; HttpOnly; Secure");
        if (Network is not null && path == "/2.2/networks/1" && ctx.Request.HttpMethod == "GET") { await Reply(ctx, 200, Network.DeepClone()); return; }
        if (Network is not null && path == "/2.2/networks/1/settings" && ctx.Request.HttpMethod == "PUT")
        {
            if (FailSettings) { await Reply(ctx, 500, null, "error.internal"); return; }
            if (JsonNode.Parse(body) is JsonObject changes)
                foreach (var kv in changes.ToList()) Network[kv.Key] = kv.Value?.DeepClone();
            await Reply(ctx, 200, Network.DeepClone());
            return;
        }
        await Reply(ctx, 200, new JsonObject { ["name"] = "Test Account" });
    }

    private static async Task Reply(HttpListenerContext ctx, int code, JsonNode? data, string? error = null)
    {
        var json = new JsonObject { ["meta"] = new JsonObject { ["code"] = code, ["error"] = error }, ["data"] = data }.ToJsonString();
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
