using System.Net.Http.Json;
using System.Text.Json;
using Housekeeper.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Housekeeper.Tests;

public class LogBufferTests
{
    [Fact]
    public void Keeps_the_newest_lines_newest_first_and_counts_what_fell_off()
    {
        var buffer = new LogBuffer(capacity: 16);
        for (var i = 0; i < 20; i++)
            buffer.Add(new LogEntry(DateTimeOffset.UnixEpoch.AddSeconds(i), LogLevel.Information, "Scan", "AnomalyScanner", "line " + i, null));

        var held = buffer.Snapshot();

        Assert.Equal(16, held.Count);
        Assert.Equal("line 19", held[0].Message);
        Assert.Equal("line 4", held[^1].Message);
        Assert.Equal(20, buffer.Total);
    }

    [Theory]
    [InlineData("Housekeeper.Api.HomeAssistantClient", "Home Assistant", "HomeAssistantClient")]
    [InlineData("Housekeeper.Api.HomeAssistantEventStream", "Home Assistant", "HomeAssistantEventStream")]
    [InlineData("Housekeeper.Api.LlmClient", "Model", "LlmClient")]
    [InlineData("Housekeeper.Core.AnomalyScanner", "Scan", "AnomalyScanner")]
    [InlineData("Housekeeper.Api.ScanWorker", "Scan", "ScanWorker")]
    [InlineData("Housekeeper.Core.ProposalService", "Drafting", "ProposalService")]
    [InlineData("Housekeeper.Core.ConcernService", "Concerns", "ConcernService")]
    [InlineData("Housekeeper.Api.StateFeedWorker", "Live feed", "StateFeedWorker")]
    [InlineData("Microsoft.Hosting.Lifetime", "Host", "Lifetime")]
    [InlineData("Housekeeper", "Service", "Housekeeper")]
    public void Categories_map_to_the_flows_the_page_offers(string category, string flow, string source)
    {
        Assert.Equal(flow, LogBuffer.FlowOf(category));
        Assert.Equal(source, LogBuffer.SourceOf(category));
        Assert.Contains(flow, LogBuffer.Flows);
    }

    [Fact]
    public void The_provider_writes_formatted_lines_with_the_exception_text()
    {
        var buffer = new LogBuffer();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var logger = new LogBufferProvider(buffer, clock).CreateLogger("Housekeeper.Core.AnomalyScanner");

        logger.LogWarning(new InvalidOperationException("boom"), "Scan {Number} failed", 7);

        var line = Assert.Single(buffer.Snapshot());
        Assert.Equal("Scan 7 failed", line.Message);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Scan", line.Flow);
        Assert.Contains("boom", line.Exception);
        Assert.Equal(clock.GetUtcNow(), line.AtUtc);
    }
}

public class SeriousnessTests
{
    private static Housekeeper.Core.Anomaly Finding(Housekeeper.Core.AnomalyKind kind, double severity, string evidence = "{}") => new()
    {
        DedupKey = "k",
        EntityId = "sensor.x",
        Kind = kind,
        Summary = "s",
        SuggestedRequest = "r",
        Severity = severity,
        EvidenceJson = evidence,
    };

    [Fact]
    public void The_badge_goes_red_for_what_was_asked_for_what_is_broken_and_what_is_far_past_its_bar()
    {
        Assert.True(Endpoints.IsSerious(Finding(Housekeeper.Core.AnomalyKind.Concern, 12)));
        Assert.True(Endpoints.IsSerious(Finding(Housekeeper.Core.AnomalyKind.MissingEntity, 11)));
        Assert.True(Endpoints.IsSerious(Finding(Housekeeper.Core.AnomalyKind.StuckState, 4)));
        Assert.True(Endpoints.IsSerious(Finding(Housekeeper.Core.AnomalyKind.NumericOutlier, 1.5, """{"concern":"the freezer"}""")));
        Assert.False(Endpoints.IsSerious(Finding(Housekeeper.Core.AnomalyKind.StuckState, 2.9)));
        Assert.False(Endpoints.IsSerious(Finding(Housekeeper.Core.AnomalyKind.Unavailable, 1)));
    }
}

public class NameBookTests
{
    [Fact]
    public void Remembers_device_and_area_as_well_as_the_name()
    {
        var names = new NameBook();
        names.Remember([
            Build.Entity("sensor.plug_power", friendlyName: "Plug power", deviceName: "Office plug", area: "Office"),
            Build.Entity("sensor.nameless", deviceName: "Hub"),
            Build.Entity("sensor.nothing"),
        ]);

        Assert.Equal("Plug power", names.NameOf("sensor.plug_power"));
        Assert.Equal(new NameBook.Known("Plug power", "Office plug", "Office"), names.About("sensor.plug_power"));
        Assert.Equal("Hub", names.About("sensor.nameless")?.Device);
        Assert.Null(names.About("sensor.nothing"));
    }
}

public class RecentSamplesTests : StoreFixture
{
    [Fact]
    public async Task Newest_changes_come_first_and_the_id_filter_narrows_them()
    {
        var now = Clock.GetUtcNow();
        await Store.AddSamplesAsync(
        [
            ("binary_sensor.freezer_door", new Housekeeper.Core.StateSample("on", null, now.AddMinutes(-3))),
            ("sensor.hall_temperature", new Housekeeper.Core.StateSample("21.5", 21.5, now.AddMinutes(-2))),
            ("binary_sensor.freezer_door", new Housekeeper.Core.StateSample("off", null, now.AddMinutes(-1))),
        ], CancellationToken.None);

        var all = await Store.ListRecentSamplesAsync(null, 10, CancellationToken.None);
        Assert.Equal(["off", "21.5", "on"], all.Select(r => r.Sample.State));
        Assert.Equal(21.5, all[1].Sample.Numeric);

        var freezer = await Store.ListRecentSamplesAsync("freezer", 10, CancellationToken.None);
        Assert.Equal(2, freezer.Count);
        Assert.All(freezer, r => Assert.Equal("binary_sensor.freezer_door", r.EntityId));

        // LIKE wildcards in the search are literal, not wild.
        Assert.Empty(await Store.ListRecentSamplesAsync("%zz%", 10, CancellationToken.None));
        Assert.Single(await Store.ListRecentSamplesAsync(null, 1, CancellationToken.None));
    }
}

[Collection("api")]
public class LogsApiTests(TestApp app)
{
    private readonly HttpClient _client = app.CreateClient();

    [Fact]
    public async Task State_changes_from_home_assistant_are_listed_with_names()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/logs/states?limit=5");
        Assert.True(body.TryGetProperty("entries", out var entries));
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
    }

    [Fact]
    public async Task The_logs_page_and_its_api_answer_and_filter()
    {
        Assert.Equal("text/html", (await _client.GetAsync("/logs")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/html", (await _client.GetAsync("/concerns")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/html", (await _client.GetAsync("/noticed")).Content.Headers.ContentType?.MediaType);

        var summary = await _client.GetFromJsonAsync<JsonElement>("/api/anomalies/summary");
        Assert.True(summary.GetProperty("open").GetInt32() >= 0);
        Assert.True(summary.GetProperty("serious").GetInt32() <= summary.GetProperty("open").GetInt32());

        // Something is certain to have been logged by now: the host announced itself on start.
        var all = await _client.GetFromJsonAsync<JsonElement>("/api/logs?level=Debug&limit=50");
        Assert.True(all.GetProperty("held").GetInt32() > 0);
        Assert.True(all.GetProperty("entries").GetArrayLength() > 0);

        var none = await _client.GetFromJsonAsync<JsonElement>("/api/logs?q=zzz-nothing-says-this-zzz");
        Assert.Equal(0, none.GetProperty("matched").GetInt32());

        var scan = await _client.GetFromJsonAsync<JsonElement>("/api/logs?flow=Scan&level=Debug");
        foreach (var entry in scan.GetProperty("entries").EnumerateArray())
            Assert.Equal("Scan", entry.GetProperty("flow").GetString());
    }
}
