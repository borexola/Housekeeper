using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HearthSense.Api;
using HearthSense.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HearthSense.Tests;

/// <summary>Boots the real application with fake Home Assistant and model adapters.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"hearthsense-api-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public TestApp() => _dbPath = Path.Combine(_dataDirectory, "hearthsense.db");

    public FakeHomeAssistant HomeAssistant { get; } = new();
    public FakeLlm Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("HEARTHSENSE_HA_TOKEN", "test-token");

        // Settings and secrets written through the API belong to this test, not the repository.
        Environment.SetEnvironmentVariable("HEARTHSENSE_DATA_DIR", _dataDirectory);

        builder.UseSetting("HearthSense:Storage:Path", _dbPath);
        builder.UseSetting("HearthSense:Scan:Enabled", "false");
        builder.UseSetting("HearthSense:Scan:Include:0", "binary_sensor.*");
        builder.UseSetting("HearthSense:Scan:Include:1", "sensor.*");

        builder.ConfigureTestServices(services =>
        {
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

        Environment.SetEnvironmentVariable("HEARTHSENSE_DATA_DIR", null);
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
