using System.Net;
using System.Text;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Housekeeper.Tests;

/// <summary>
/// A fresh install used to be blind for a day or two while Home Assistant's recorder held ten days of the
/// answer. These pin what is read, how it is thinned, and that nothing is asked twice.
/// </summary>
public class BackfillThinningTests
{
    private static readonly DateTimeOffset T = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Numeric_readings_are_thinned_per_hour_and_states_kept_whole()
    {
        List<StateSample> chatty = [];
        for (var i = 0; i < 120; i++) chatty.Add(new StateSample(Ha.Number(i), i, T.AddSeconds(i * 30)));

        var kept = Backfill.Thin(chatty, T.AddDays(-1), numericPerHour: 2);

        // An hour of readings every thirty seconds keeps two.
        Assert.Equal(2, kept.Count);
        Assert.Equal(0, kept[0].Numeric);
        Assert.Equal(1, kept[1].Numeric);

        List<StateSample> door =
        [
            new("off", null, T), new("on", null, T.AddMinutes(1)), new("on", null, T.AddMinutes(2)),
            new("off", null, T.AddMinutes(3)), new("on", null, T.AddMinutes(4)),
        ];

        // Every transition survives; only the repeated state is dropped.
        Assert.Equal(["off", "on", "off", "on"], Backfill.Thin(door, T.AddDays(-1), 2).Select(s => s.State));
    }

    [Fact]
    public void Nothing_older_than_the_retention_window_is_kept()
    {
        List<StateSample> history = [new("on", null, T.AddDays(-40)), new("off", null, T.AddDays(-1))];

        var kept = Assert.Single(Backfill.Thin(history, T.AddDays(-28), 2));
        Assert.Equal("off", kept.State);
    }
}

public class BackfillScanTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options(bool backfill = true)
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["binary_sensor.*", "sensor.*"];
        options.Scan.BackfillFromRecorder = backfill;
        return options;
    }

    [Fact]
    public async Task The_first_scan_reads_the_recorder_for_entities_with_no_history_and_only_once()
    {
        var now = Clock.GetUtcNow();
        _ha.Entities.Add(Build.Entity("binary_sensor.door", "off", now.AddMinutes(-5)));
        _ha.Entities.Add(Build.Entity("sensor.empty", "1", now.AddMinutes(-5)));

        List<StateSample> past = [];
        for (var i = 10; i > 0; i--)
        {
            past.Add(new StateSample("on", null, now.AddHours(-i * 6)));
            past.Add(new StateSample("off", null, now.AddHours(-i * 6).AddMinutes(1)));
        }

        _ha.History["binary_sensor.door"] = past;

        var scanner = Scanner(Options());
        var first = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(20, first.Backfilled);
        Assert.Single(_ha.HistoryRequests);
        Assert.Equal(["binary_sensor.door", "sensor.empty"], _ha.HistoryRequests[0].Order(StringComparer.Ordinal));

        var stored = await Store.GetSamplesAsync(now.AddDays(-28), 500, CancellationToken.None);
        Assert.Equal(21, stored["binary_sensor.door"].Count); // twenty from the recorder plus the current state

        // Both have been asked about this process, so neither is asked again however shallow they remain.
        var second = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(0, second.Backfilled);
        Assert.Single(_ha.HistoryRequests);
    }

    [Fact]
    public async Task A_recorder_that_cannot_be_read_costs_nothing_and_is_tried_again()
    {
        _ha.Entities.Add(Build.Entity("binary_sensor.door", "off", Clock.GetUtcNow()));
        _ha.HistoryFailure = new HomeAssistantException("recorder is down");

        var scanner = Scanner(Options());
        var report = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(0, report.Backfilled);
        Assert.Equal(1, report.Observed);

        // Nothing was marked as asked, so the door is tried again next scan, along with anything new.
        _ha.HistoryFailure = null;
        _ha.Entities.Add(Build.Entity("sensor.new", "unknown", Clock.GetUtcNow().AddDays(-40)));
        await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(2, _ha.HistoryRequests.Count);
        Assert.Equal(["binary_sensor.door", "sensor.new"], _ha.HistoryRequests[1].Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_entity_whose_stored_history_already_reaches_back_three_weeks_is_left_alone()
    {
        var now = Clock.GetUtcNow();
        _ha.Entities.Add(Build.Entity("binary_sensor.old", "off", now.AddMinutes(-1)));
        _ha.Entities.Add(Build.Entity("binary_sensor.young", "off", now.AddMinutes(-1)));
        await Store.AddSamplesAsync(
        [
            ("binary_sensor.old", new StateSample("on", null, now.AddDays(-25))),
            ("binary_sensor.young", new StateSample("on", null, now.AddDays(-5))),
        ], CancellationToken.None);

        await Scanner(Options()).ScanAsync(CancellationToken.None);

        // Only the one with a week of history is worth asking the recorder about.
        Assert.Equal(["binary_sensor.young"], Assert.Single(_ha.HistoryRequests));
    }

    [Fact]
    public async Task Backfill_can_be_turned_off()
    {
        _ha.Entities.Add(Build.Entity("binary_sensor.door", "off", Clock.GetUtcNow()));
        _ha.History["binary_sensor.door"] = [new StateSample("on", null, Clock.GetUtcNow().AddHours(-1))];

        var report = await Scanner(Options(backfill: false)).ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.Backfilled);
        Assert.Empty(_ha.HistoryRequests);
    }
}

public class RecorderClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string Body { get; set; } = "[]";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Asks_the_history_endpoint_for_exactly_these_entities_and_reads_the_minimal_shape()
    {
        var handler = new StubHandler
        {
            Body = """
                [[{"entity_id":"binary_sensor.door","state":"off","last_changed":"2026-02-20T10:00:00+00:00","last_updated":"2026-02-20T10:00:00+00:00"},
                  {"state":"on","last_changed":"2026-02-20T11:00:00+00:00"},
                  {"state":"off","last_changed":"2026-02-20T11:02:00+00:00"}],
                 [{"entity_id":"sensor.temp","state":"21.5","last_changed":"2026-02-21T10:00:00+00:00","last_updated":"2026-02-21T10:00:00+00:00"},
                  {"state":"unavailable","last_changed":"2026-02-21T12:00:00+00:00"}]]
                """,
        };

        var settings = new FakeSettings();
        settings.Current.HomeAssistant.BaseUrl = "http://ha.test:8123";
        var secrets = new SecretStore(Path.Combine(Path.GetTempPath(), $"hs-recorder-{Guid.NewGuid():N}.json"), NullLogger<SecretStore>.Instance);
        var client = new HomeAssistantClient(new HttpClient(handler), settings, secrets, new FakeTimeProvider(), NullLogger<HomeAssistantClient>.Instance);

        var since = new DateTimeOffset(2026, 2, 19, 0, 0, 0, TimeSpan.Zero);
        var history = await client.GetHistoryAsync(["binary_sensor.door", "sensor.temp"], since, CancellationToken.None);

        var asked = Assert.Single(handler.Requests);
        Assert.StartsWith("/api/history/period/2026-02-19T00:00:00", Uri.UnescapeDataString(asked.AbsolutePath), StringComparison.Ordinal);
        Assert.Contains("filter_entity_id=binary_sensor.door%2Csensor.temp", asked.Query, StringComparison.Ordinal);
        Assert.Contains("minimal_response", asked.Query, StringComparison.Ordinal);
        Assert.Contains("no_attributes", asked.Query, StringComparison.Ordinal);

        Assert.Equal(["off", "on", "off"], history["binary_sensor.door"].Select(s => s.State));
        Assert.Equal(21.5, history["sensor.temp"][0].Numeric);
        Assert.Null(history["sensor.temp"][1].Numeric);
        Assert.Equal(new DateTimeOffset(2026, 2, 20, 11, 2, 0, TimeSpan.Zero), history["binary_sensor.door"][2].ChangedUtc);
    }
}
