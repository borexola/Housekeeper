using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Housekeeper.Tests;

/// <summary>Boots the real application with fake Home Assistant and model adapters.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"housekeeper-api-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public TestApp()
    {
        _dbPath = Path.Combine(_dataDirectory, "housekeeper.db");
        Adapters = new FakeAdapters(HomeAssistant, Llm);
    }

    public FakeHomeAssistant HomeAssistant { get; } = new();
    public FakeLlm Llm { get; } = new();
    public FakeAdapters Adapters { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("HOUSEKEEPER_HA_TOKEN", "test-token");

        // Settings and secrets written through the API belong to this test, not the repository.
        Environment.SetEnvironmentVariable("HOUSEKEEPER_DATA_DIR", _dataDirectory);

        builder.UseSetting("Housekeeper:Storage:Path", _dbPath);
        builder.UseSetting("Housekeeper:Scan:Enabled", "false");

        // Pinned narrow on purpose. Watching everything is the shipped default, but these tests are about
        // what a chosen watch list does, so the host stands in for someone who chose one.
        builder.UseSetting("Housekeeper:Scan:IncludeAll", "false");
        builder.UseSetting("Housekeeper:Scan:Include:0", "binary_sensor.*");
        builder.UseSetting("Housekeeper:Scan:Include:1", "sensor.*");
        builder.UseSetting("Housekeeper:Llm:Model", "fake-model");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAdapters>();
            services.AddSingleton<IAdapters>(Adapters);
            services.RemoveAll<IHomeAssistant>();
            services.AddSingleton<IHomeAssistant>(HomeAssistant);
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(Llm);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Environment.SetEnvironmentVariable("HOUSEKEEPER_DATA_DIR", null);
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>
/// One application shared by every test that boots the host. They run one at a time because the settings
/// tests change the running configuration, and because the data directory is chosen by an environment variable.
/// </summary>
[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<TestApp>;

[Collection("api")]
public class ApiTests
{
    private readonly TestApp _app;
    private readonly HttpClient _client;

    private const string GoodDraft = """
        {"alias":"Lights off when away","description":"Turns the hall light off.",
         "triggers":[{"trigger":"state","entity_id":"person.sam","to":"not_home"}],
         "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}]}
        """;

    public ApiTests(TestApp app)
    {
        _app = app;
        _client = app.CreateClient();

        if (app.HomeAssistant.Entities.Count == 0)
            app.HomeAssistant.Entities.AddRange([
                Build.Entity("light.hall", friendlyName: "Hall Light"),
                Build.Entity("person.sam", "home", friendlyName: "Sam"),
            ]);
    }

    [Fact]
    public async Task Health_and_readiness_answer()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_is_served_as_html()
    {
        var response = await _client.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// On the default loopback binding there is no token and no authentication, which is what makes this
    /// matter: writing an automation is a plain POST, and a browser sends those cross-site without asking.
    /// Any page the user had open could otherwise post to 127.0.0.1 and confirm a draft into their house.
    /// </summary>
    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    // "same-site" means only that the registrable domain matches, and a host with no registrable domain --
    // every IP literal, and localhost -- satisfies that on an equal host alone. So any OTHER service on
    // 127.0.0.1, a dev server or a local model UI, counts as same-site. Ingress is same-ORIGIN, so nothing
    // legitimate needs this to be allowed.
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "https://somewhere-else.example")]
    public async Task A_write_arriving_from_another_site_is_refused(string header, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/scan");
        request.Headers.Add(header, value);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Cross-site", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task The_dashboards_own_requests_are_not_caught_by_that(string site)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/scan");
        request.Headers.Add("Sec-Fetch-Site", site);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// DNS rebinding defeats every cross-site signal there is. A page on an attacker's domain whose DNS is
    /// flipped to 127.0.0.1 mid-visit is genuinely same-origin with a loopback install, so the browser says
    /// same-origin and means it — and on loopback there is no token behind that. What still gives it away is
    /// the Host header carrying a name this instance is not served at. Reads are refused too, because reading
    /// is the point: the rebound page can see every reply, including the Home Assistant token's reach.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/status")]
    [InlineData("GET", "/dashboard")]
    [InlineData("POST", "/api/scan")]
    public async Task A_request_addressed_to_a_name_this_instance_is_not_served_at_is_refused(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = "evil.example";
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Contains("not served at", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Reaching_a_loopback_install_the_way_anyone_really_does_is_fine(string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = host;

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_host_the_user_added_for_their_own_proxy_is_accepted()
    {
        try
        {
            var saved = await _client.PutAsJsonAsync("/api/settings", new Dictionary<string, object>
            {
                ["Housekeeper:Api:AllowedHosts"] = new[] { "housekeeper.mylan" },
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            request.Headers.Host = "housekeeper.mylan";

            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
        }
        finally
        {
            await _client.PostAsJsonAsync("/api/settings/reset", new { section = "Api" });
        }
    }

    /// <summary>
    /// The browser that sends no Sec-Fetch-Site falls back to Origin, and behind any proxy — Home Assistant
    /// ingress, or the nginx SECURITY.md recommends for TLS — the Host we see is the one the proxy dialled,
    /// not the one the browser was on. Comparing against that refused every write from the older web views
    /// in the Home Assistant companion app, with advice ("open it directly") that an add-on user cannot take.
    /// </summary>
    [Fact]
    public async Task An_older_browser_behind_a_proxy_is_judged_on_the_host_it_actually_addressed()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/scan");
        request.Headers.Add("Origin", "https://homeassistant.local:8123");
        request.Headers.Add("X-Forwarded-Host", "homeassistant.local:8123");

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// Behind Home Assistant ingress the page and this API share the Home Assistant origin, while the
    /// Supervisor proxies the request under an address of its own. Comparing Origin against Host there would
    /// refuse every write from the add-on's own dashboard, so the browser's own verdict is what counts.
    /// </summary>
    [Fact]
    public async Task A_write_proxied_under_a_different_host_is_allowed_when_the_browser_calls_it_same_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/scan");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "https://homeassistant.local:8123");

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Reading_is_never_refused_for_where_it_came_from()
    {
        // A cross-site GET cannot change anything, and the browser will not let the page read the answer.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Test_configuration_reaches_the_running_application()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/status");

        // Proves the options the app actually uses come from the host's configuration, not a stale early bind.
        Assert.False(status.GetProperty("scanning").GetBoolean());
        Assert.Equal("fake-model", status.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Full_journey_from_a_sentence_to_a_live_automation()
    {
        _app.Llm.Response = GoodDraft;

        var draftResponse = await _client.PostAsJsonAsync("/api/proposals",
            new { request = "Turn off lights when no one is home" });

        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        var draft = await draftResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal("Lights off when away", draft.GetProperty("alias").GetString());

        // The user is shown YAML, and it is the YAML of what will actually be written.
        var yaml = draft.GetProperty("yaml").GetString();
        Assert.Contains("alias: Lights off when away", yaml);
        Assert.Contains("entity_id: light.hall", yaml);

        var id = draft.GetProperty("id").GetInt64();
        var before = _app.HomeAssistant.Created.Count;

        var confirmed = await _client.PostAsync($"/api/proposals/{id}/confirm", null);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(before + 1, _app.HomeAssistant.Created.Count);

        var view = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Created", view.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("haAutomationId").GetString()));
    }

    [Fact]
    public async Task Insight_reports_what_the_scanner_is_watching_and_what_it_has_to_work_with()
    {
        var insight = await _client.GetFromJsonAsync<JsonElement>("/api/insight");

        Assert.False(insight.GetProperty("scanning").GetBoolean());
        Assert.Equal(12, insight.GetProperty("minimumSamples").GetInt32());
        Assert.Equal(
            ["binary_sensor.*", "sensor.*"],
            insight.GetProperty("watching").EnumerateArray().Select(x => x.GetString()));

        // How much history is usable is the scan's answer, not the database's; it does not live here.
        Assert.False(insight.GetProperty("history").TryGetProperty("ready", out _));
    }

    /// <summary>
    /// The history block is hand-mapped into a differently named object and its window is computed from the
    /// retention setting, and none of that was checked: the assertions were <c>&gt;= 0</c> against two SQL
    /// counts, which is true of every value a broken endpoint could return.
    /// </summary>
    [Fact]
    public async Task Insight_counts_the_history_it_actually_holds()
    {
        var store = _app.Services.GetRequiredService<IStore>();
        var scan = _app.Services.GetRequiredService<ISettingsProvider>().Current.Scan;
        var now = DateTimeOffset.UtcNow;

        // Measured as a delta, not as an absolute. Every test in this collection shares one database and
        // several of them write samples, so asserting exact totals passed only for as long as xUnit happened
        // to order the class a particular way.
        async Task<(int Entities, long Samples)> CountAsync()
        {
            await _client.PostAsync("/api/scan", null);
            var block = (await _client.GetFromJsonAsync<JsonElement>("/api/insight")).GetProperty("history");
            return (block.GetProperty("entities").GetInt32(), block.GetProperty("samples").GetInt64());
        }

        var before = await CountAsync();

        // Two entities of our own inside the retention window, three samples between them, and one
        // deliberately older than the window so it must not be counted at all.
        var mark = Guid.NewGuid().ToString("N")[..8];
        await store.AddSamplesAsync(
        [
            ($"sensor.{mark}_a", new StateSample("1", 1, now - TimeSpan.FromHours(3))),
            ($"sensor.{mark}_a", new StateSample("2", 2, now - TimeSpan.FromHours(2))),
            ($"sensor.{mark}_b", new StateSample("3", 3, now - TimeSpan.FromHours(1))),
            ($"sensor.{mark}_old", new StateSample("4", 4, now - scan.History - TimeSpan.FromDays(1))),
        ], CancellationToken.None);

        var after = await CountAsync();

        Assert.Equal(before.Entities + 2, after.Entities);
        Assert.Equal(before.Samples + 3, after.Samples);
    }

    [Fact]
    public async Task Insight_reports_the_last_scan_once_one_has_run()
    {
        // Checked against the scan's own report rather than against inequalities that hold for every value
        // the endpoint could possibly produce. Every one of these numbers is printed to the user on the
        // dashboard, and the previous assertions (tookMs >= 0, visible >= observed, judged <= observed) were
        // all true by construction — the fields could have been swapped for each other and nothing failed.
        var report = await (await _client.PostAsync("/api/scan", null)).Content.ReadFromJsonAsync<JsonElement>();
        var last = (await _client.GetFromJsonAsync<JsonElement>("/api/insight")).GetProperty("last");

        Assert.Equal(JsonValueKind.Null, last.GetProperty("error").ValueKind);

        foreach (var (onScan, onInsight) in new[]
                 {
                     ("observed", "observed"), ("visible", "visible"), ("newSamples", "newSamples"),
                     ("raised", "raised"), ("resolved", "resolved"), ("judged", "judged"),
                 })
            Assert.Equal(report.GetProperty(onScan).GetInt32(), last.GetProperty(onInsight).GetInt32());
    }

    [Fact]
    public async Task Closed_findings_are_kept_out_of_the_default_list()
    {
        // Seeded here rather than inherited. The test used to query a database it never wrote to, so whether
        // it asserted anything at all depended on which other test in the collection had happened to run
        // first -- and xUnit does not promise an order.
        var store = _app.Services.GetRequiredService<IStore>();
        var mark = Guid.NewGuid().ToString("N")[..8];

        var seeded = new List<Anomaly>();
        foreach (var (status, name) in new[]
                 {
                     (AnomalyStatus.Open, "open"),
                     (AnomalyStatus.Dismissed, "dismissed"),
                     (AnomalyStatus.Resolved, "resolved"),
                 })
        {
            seeded.Add(await store.UpsertAnomalyAsync(new Anomaly
            {
                DedupKey = $"test:{mark}:{name}",
                EntityId = $"sensor.{mark}_{name}",
                Kind = AnomalyKind.StuckState,
                Summary = $"seeded {name}",
                SuggestedRequest = "notify me",
                Status = status,
                DetectedUtc = DateTimeOffset.UtcNow,
                DecidedUtc = status == AnomalyStatus.Open ? null : DateTimeOffset.UtcNow,
            }, CancellationToken.None));
        }

        static long[] Ids(JsonElement[] found) => [.. found.Select(a => a.GetProperty("id").GetInt64())];

        var open = Ids((await _client.GetFromJsonAsync<JsonElement[]>("/api/anomalies?limit=200"))!);
        Assert.Contains(seeded[0].Id, open);
        Assert.DoesNotContain(seeded[1].Id, open);
        Assert.DoesNotContain(seeded[2].Id, open);

        // And are there when asked for.
        var all = Ids((await _client.GetFromJsonAsync<JsonElement[]>("/api/anomalies?limit=200&closed=true"))!);
        foreach (var anomaly in seeded) Assert.Contains(anomaly.Id, all);
    }

    [Fact]
    public async Task A_draft_can_be_refined_and_the_old_one_is_superseded()
    {
        _app.Llm.Response = GoodDraft;
        var first = await (await _client.PostAsJsonAsync("/api/proposals", new { request = "turn off lights when away" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = first.GetProperty("id").GetInt64();

        _app.Llm.Response = GoodDraft.Replace("Lights off when away", "Hall light off when away", StringComparison.Ordinal);
        var response = await _client.PostAsJsonAsync($"/api/proposals/{id}/refine", new { feedback = "call it hall light off" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refined = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", refined.GetProperty("status").GetString());
        Assert.Equal("Hall light off when away", refined.GetProperty("alias").GetString());
        Assert.Equal(id, refined.GetProperty("parentId").GetInt64());
        Assert.Equal("call it hall light off", refined.GetProperty("feedback").GetString());

        var old = await _client.GetFromJsonAsync<JsonElement>($"/api/proposals/{id}");
        Assert.Equal("Superseded", old.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/proposals/{id}/refine", new { feedback = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/proposals/{id}/refine", new { feedback = "again" })).StatusCode);
    }

    [Fact]
    public async Task A_created_automation_can_be_dismissed_and_brought_back_from_the_api()
    {
        _app.Llm.Response = GoodDraft;
        var draft = await (await _client.PostAsJsonAsync("/api/proposals", new { request = "turn off lights when out" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetInt64();
        await _client.PostAsync($"/api/proposals/{id}/confirm", null);

        var dismissed = await _client.PostAsync($"/api/proposals/{id}/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.Equal("Created", (await dismissed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var listed = await _client.GetFromJsonAsync<JsonElement[]>("/api/proposals?limit=50");
        Assert.DoesNotContain(listed!, p => p.GetProperty("id").GetInt64() == id);

        var withDismissed = await _client.GetFromJsonAsync<JsonElement[]>("/api/proposals?limit=50&dismissed=true");
        Assert.Contains(withDismissed!, p => p.GetProperty("id").GetInt64() == id);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/proposals/{id}/restore", null)).StatusCode);
        var restored = await _client.GetFromJsonAsync<JsonElement[]>("/api/proposals?limit=50");
        Assert.Contains(restored!, p => p.GetProperty("id").GetInt64() == id);

        // Tidy up so the shared application is left as it was found.
        await _client.PostAsync($"/api/proposals/{id}/dismiss", null);
    }

    [Fact]
    public async Task A_hallucinated_entity_is_reported_and_nothing_is_written()
    {
        _app.Llm.Response = GoodDraft.Replace("light.hall", "light.nope", StringComparison.Ordinal);
        var before = _app.HomeAssistant.Created.Count;

        var response = await _client.PostAsJsonAsync("/api/proposals", new { request = "turn off the lights" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Contains("light.nope", body.GetProperty("error").GetString());
        Assert.Equal(before, _app.HomeAssistant.Created.Count);
    }

    [Fact]
    public async Task An_empty_request_is_a_bad_request()
    {
        var response = await _client.PostAsJsonAsync("/api/proposals", new { request = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_status_filters_are_rejected_rather_than_ignored()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/proposals?status=banana")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/anomalies?status=banana")).StatusCode);
    }

    [Fact]
    public async Task Confirming_a_missing_proposal_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/proposals/424242/confirm", null)).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_home_assistant_is_a_502_with_a_reason_not_a_crash()
    {
        _app.HomeAssistant.EntitiesFailure = new HomeAssistantException("Home Assistant returned HTTP 503 when asked to read entity states.");
        try
        {
            var response = await _client.PostAsync("/api/scan", null);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("HTTP 503", body.GetProperty("error").GetString());
        }
        finally
        {
            _app.HomeAssistant.EntitiesFailure = null;
        }
    }

    [Fact]
    public async Task Scanning_then_promoting_a_finding_produces_a_draft()
    {
        _app.Llm.Response = """
            {"alias":"Freezer door left open","description":"Warns when the freezer door stays open.",
             "triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"on","for":"00:10:00"}],
             "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}]}
            """;

        var door = Build.Entity("binary_sensor.freezer_door", "off", DateTimeOffset.UtcNow.AddDays(-3), "Freezer Door");
        _app.HomeAssistant.Entities.Add(door);

        // Teach it what normal is, then leave the door open.
        for (var i = 0; i < 14; i++)
        {
            var opened = DateTimeOffset.UtcNow.AddHours(-40 + i);
            Replace(door with { State = "on", LastChanged = opened });
            await _client.PostAsync("/api/scan", null);

            Replace(door with { State = "off", LastChanged = opened.AddSeconds(30) });
            await _client.PostAsync("/api/scan", null);
        }

        Replace(door with { State = "on", LastChanged = DateTimeOffset.UtcNow.AddMinutes(-25) });
        await _client.PostAsync("/api/scan", null);

        var anomalies = await _client.GetFromJsonAsync<JsonElement[]>("/api/anomalies?status=Open");
        var stuck = anomalies!.Single(a => a.GetProperty("entityId").GetString() == "binary_sensor.freezer_door");

        Assert.Equal("StuckState", stuck.GetProperty("kind").GetString());
        Assert.Contains("stays 'on'", stuck.GetProperty("suggestedRequest").GetString());

        var anomalyId = stuck.GetProperty("id").GetInt64();
        var promoted = await _client.PostAsync($"/api/anomalies/{anomalyId}/automate", null);

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        var proposal = await promoted.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Anomaly", proposal.GetProperty("source").GetString());
        Assert.Equal(anomalyId, proposal.GetProperty("anomalyId").GetInt64());

        // Promoting twice would be a duplicate suggestion, not a second finding.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/anomalies/{anomalyId}/automate", null)).StatusCode);

        void Replace(HaEntity updated)
        {
            var index = _app.HomeAssistant.Entities.FindIndex(e => e.EntityId == updated.EntityId);
            _app.HomeAssistant.Entities[index] = updated;
        }
    }

    [Fact]
    public async Task Ignoring_a_finding_stops_watching_its_entity_or_its_whole_device()
    {
        var store = _app.Services.GetRequiredService<IStore>();
        var settings = _app.Services.GetRequiredService<SettingsContext>();

        _app.HomeAssistant.Entities.AddRange([
            Build.Entity("binary_sensor.garage_motion", friendlyName: "Garage Motion", deviceId: "dev-garage", deviceName: "Garage Cam"),
            Build.Entity("binary_sensor.garage_person", friendlyName: "Garage Person", deviceId: "dev-garage", deviceName: "Garage Cam"),
            Build.Entity("binary_sensor.porch_motion", friendlyName: "Porch Motion"),
        ]);

        try
        {
            var motion = await Seed("binary_sensor.garage_motion");
            await Seed("binary_sensor.garage_person");
            var porch = await Seed("binary_sensor.porch_motion");

            var response = await _client.PostAsync($"/api/anomalies/{porch}/ignore", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "binary_sensor.porch_motion" }, Ignored(body));
            Assert.Equal(1, body.GetProperty("dismissed").GetInt32());

            // An entity Home Assistant attaches to no device cannot be ignored by device.
            response = await _client.PostAsync($"/api/anomalies/{porch}/ignore?scope=device", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            response = await _client.PostAsync($"/api/anomalies/{motion}/ignore?scope=device", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Garage Cam", body.GetProperty("device").GetString());
            Assert.Equal(new[] { "binary_sensor.garage_motion", "binary_sensor.garage_person" }, Ignored(body));
            Assert.Equal(2, body.GetProperty("dismissed").GetInt32());

            var exclude = _app.Services.GetRequiredService<ISettingsProvider>().Current.Scan.Exclude;
            Assert.Contains("binary_sensor.porch_motion", exclude);
            Assert.Contains("binary_sensor.garage_motion", exclude);
            Assert.Contains("binary_sensor.garage_person", exclude);

            var open = await _client.GetFromJsonAsync<JsonElement[]>("/api/anomalies?status=Open");
            Assert.DoesNotContain(open!, a => a.GetProperty("entityId").GetString()!.StartsWith("binary_sensor.garage", StringComparison.Ordinal));
            Assert.DoesNotContain(open!, a => a.GetProperty("entityId").GetString() == "binary_sensor.porch_motion");

            response = await _client.PostAsync($"/api/anomalies/{motion}/ignore?scope=house", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            settings.Reset("Scan");
            _app.HomeAssistant.Entities.RemoveAll(e =>
                e.EntityId.StartsWith("binary_sensor.garage", StringComparison.Ordinal) || e.EntityId == "binary_sensor.porch_motion");
        }

        async Task<long> Seed(string entityId)
        {
            var saved = await store.UpsertAnomalyAsync(new Anomaly
            {
                DedupKey = $"stuck:{entityId}",
                EntityId = entityId,
                Kind = AnomalyKind.StuckState,
                Summary = "Has been 'off' for 20 minutes.",
                SuggestedRequest = $"Notify me when {entityId} stays 'off' for more than 10 minutes.",
                Status = AnomalyStatus.Open,
                DetectedUtc = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
            return saved.Id;
        }

        static string?[] Ignored(JsonElement body) =>
            [.. body.GetProperty("ignored").EnumerateArray().Select(e => e.GetString())];
    }
}

public class LlmClientTests
{
    [Fact]
    public void Reads_an_ollama_response()
    {
        var content = LlmClient.Extract("""{"model":"qwen","message":{"role":"assistant","content":"{\"alias\":\"x\"}"}}""");

        Assert.Equal("""{"alias":"x"}""", content);
    }

    [Fact]
    public void Reads_an_openai_compatible_response()
    {
        var content = LlmClient.Extract("""{"choices":[{"message":{"role":"assistant","content":"{\"alias\":\"y\"}"}}]}""");

        Assert.Equal("""{"alias":"y"}""", content);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("[1,2,3]")]
    public void Returns_null_for_a_shape_it_does_not_recognise(string payload) =>
        Assert.Null(LlmClient.Extract(payload));
}
