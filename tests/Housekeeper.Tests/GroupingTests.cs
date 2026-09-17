using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

/// <summary>
/// One event, one card. Six virtual network interfaces on one host went unavailable together and became six
/// cards, each above a boiler at twice its usual pressure; these pin the grouping that stops that, and the
/// scanner rules that retire a card nothing could otherwise close.
/// </summary>
public class GroupingTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 2, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    private static Anomaly Quiet(HaEntity entity, double severity) => new()
    {
        DedupKey = $"unavailable:{entity.EntityId}",
        EntityId = entity.EntityId,
        Kind = AnomalyKind.Unavailable,
        Summary = "Has been 'unavailable' for a while.",
        SuggestedRequest = $"Notify me when {entity.EntityId} becomes unavailable for more than 30 minutes.",
        Severity = severity,
    };

    [Fact]
    public void Entities_that_went_unavailable_together_are_one_card_even_across_devices()
    {
        var gone = Now.AddHours(-30);
        var a = Build.Entity("sensor.host_br0_rx", "unavailable", gone);
        var b = Build.Entity("sensor.host_veth1_rx", "unavailable", gone.AddSeconds(40));
        var c = Build.Entity("sensor.host_veth1_tx", "unavailable", gone.AddMinutes(3));
        var later = Build.Entity("sensor.garden_temperature", "unavailable", gone.AddHours(2));

        var collapsed = AnomalyScanner.Collapse(
        [
            (a, Quiet(a, 3)), (b, Quiet(b, 3)), (c, Quiet(c, 5)), (later, Quiet(later, 2)),
        ], Options);

        Assert.Equal(2, collapsed.Count);

        // The most severe stands for the run, names the others, and carries the worst severity.
        var lead = Assert.Single(collapsed, found => found.EntityId == c.EntityId);
        Assert.Contains("2 other entities went unavailable at the same moment", lead.Summary);
        Assert.Contains(a.EntityId, lead.Summary);
        Assert.Contains(b.EntityId, lead.Summary);
        Assert.Equal([a.EntityId, b.EntityId], AnomalyScanner.Siblings(lead.EvidenceJson));
        Assert.Equal(5, lead.Severity);

        // Two hours later is a different event.
        Assert.Contains(collapsed, found => found.EntityId == later.EntityId);
    }

    [Fact]
    public void A_card_the_user_already_has_wins_over_a_new_one_and_the_rest_are_reported_absorbed()
    {
        var gone = Now.AddHours(-1);
        var seen = Build.Entity("sensor.host_veth1_rx", "unavailable", gone);
        var newer = Build.Entity("sensor.host_br0_rx", "unavailable", gone.AddMinutes(1));

        var open = new HashSet<string>(StringComparer.Ordinal) { $"unavailable:{seen.EntityId}" };

        var collapsed = AnomalyScanner.Collapse([(seen, Quiet(seen, 2)), (newer, Quiet(newer, 9))], Options, open);

        var lead = Assert.Single(collapsed.Kept);
        Assert.Equal(seen.EntityId, lead.EntityId);
        Assert.Equal(9, lead.Severity);
        Assert.Equal(seen.EntityId, Assert.Single(collapsed.Absorbed).Value);
        Assert.Equal($"unavailable:{newer.EntityId}", Assert.Single(collapsed.Absorbed).Key);
    }

    [Fact]
    public void Device_grouping_and_outage_grouping_compose_without_losing_anyone()
    {
        var gone = Now.AddHours(-1);
        var power = Build.Entity("sensor.plug_power", "unavailable", gone, deviceId: "plug", deviceName: "Plug");
        var current = Build.Entity("sensor.plug_current", "unavailable", gone, deviceId: "plug", deviceName: "Plug");
        var other = Build.Entity("sensor.elsewhere", "unavailable", gone.AddMinutes(2));

        var collapsed = AnomalyScanner.Collapse([(power, Quiet(power, 4)), (current, Quiet(current, 4)), (other, Quiet(other, 1))], Options);

        // Equal severities tie on the entity id, so the current sensor leads its own device.
        var lead = Assert.Single(collapsed);
        Assert.Equal(current.EntityId, lead.EntityId);
        Assert.Equal([other.EntityId, power.EntityId], AnomalyScanner.Siblings(lead.EvidenceJson));
        Assert.Contains("on the same device", lead.Summary);
        Assert.Contains("at the same moment", lead.Summary);
    }
}

public class RetiringTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options()
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["sensor.*", "binary_sensor.*", "button.*"];
        return options;
    }

    private Task<Anomaly> OpenAsync(string entityId, AnomalyKind kind, DateTimeOffset? detected = null) =>
        Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = $"{kind.ToString().ToLowerInvariant()}:{entityId}",
            EntityId = entityId,
            Kind = kind,
            Summary = "something looked off",
            SuggestedRequest = "notify me",
            Status = AnomalyStatus.Open,
            DetectedUtc = detected ?? Clock.GetUtcNow(),
        }, CancellationToken.None);

    private async Task<Anomaly> AfterScanAsync(Anomaly finding, HousekeeperOptions? options = null)
    {
        await Scanner(options ?? Options()).ScanAsync(CancellationToken.None);
        return (await Store.GetAnomalyAsync(finding.Id, CancellationToken.None))!;
    }

    private static string? Reason(Anomaly finding) =>
        System.Text.Json.Nodes.JsonNode.Parse(finding.EvidenceJson)?[AnomalyScanner.ClosedBecause]?.GetValue<string>();

    [Fact]
    public async Task A_finding_whose_entity_left_the_watch_list_is_closed_and_says_so()
    {
        _ha.Entities.Add(Build.Entity("light.lamp", "on", Clock.GetUtcNow().AddDays(-2)));
        var finding = await OpenAsync("light.lamp", AnomalyKind.StuckState);

        var closed = await AfterScanAsync(finding);

        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Equal("It is no longer on the watch list.", Reason(closed));
    }

    [Fact]
    public async Task A_finding_about_an_entity_home_assistant_now_calls_diagnostic_is_closed_and_says_why()
    {
        _ha.Entities.Add(Build.Entity("sensor.router_linkquality", "255", Clock.GetUtcNow().AddHours(-1), unit: "lqi", entityCategory: "diagnostic"));
        var finding = await OpenAsync("sensor.router_linkquality", AnomalyKind.NumericOutlier, Clock.GetUtcNow().AddDays(-1));

        var closed = await AfterScanAsync(finding);

        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Equal("Housekeeper no longer judges it: Home Assistant classes it as diagnostic.", Reason(closed));
    }

    [Fact]
    public async Task A_numeric_finding_closes_once_the_sensor_has_reported_a_newer_reading()
    {
        var raised = Clock.GetUtcNow().AddHours(-2);
        _ha.Entities.Add(Build.Entity("sensor.dew_point", "7.4", Clock.GetUtcNow().AddMinutes(-5)));
        var finding = await OpenAsync("sensor.dew_point", AnomalyKind.NumericOutlier, raised);

        var closed = await AfterScanAsync(finding);

        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Contains("reported since", Reason(closed));
    }

    [Fact]
    public async Task A_numeric_finding_stays_open_while_the_sensor_has_not_reported_since()
    {
        var raised = Clock.GetUtcNow().AddHours(-2);
        _ha.Entities.Add(Build.Entity("sensor.dew_point", "2.8", raised.AddMinutes(-1)));
        var finding = await OpenAsync("sensor.dew_point", AnomalyKind.NumericOutlier, raised);

        Assert.Equal(AnomalyStatus.Open, (await AfterScanAsync(finding)).Status);
    }

    [Fact]
    public async Task An_unavailable_finding_closes_the_moment_the_entity_reports_again_whatever_its_history()
    {
        _ha.Entities.Add(Build.Entity("sensor.garden_temperature", "12.5", Clock.GetUtcNow().AddMinutes(-1)));
        var finding = await OpenAsync("sensor.garden_temperature", AnomalyKind.Unavailable, Clock.GetUtcNow().AddDays(-1));

        var closed = await AfterScanAsync(finding);

        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Null(Reason(closed));
    }

    [Fact]
    public async Task A_finding_that_another_card_now_covers_is_closed_pointing_at_it()
    {
        var gone = Clock.GetUtcNow().AddMinutes(-40);
        var a = Build.Entity("sensor.host_veth1_rx", "unavailable", gone);
        var b = Build.Entity("sensor.host_veth1_tx", "unavailable", gone.AddSeconds(30));
        _ha.Entities.AddRange([a, b]);

        // Enough recorded changes for the unavailable detector to speak about both.
        List<(string, StateSample)> samples = [];
        for (var i = 1; i <= 14; i++)
        {
            samples.Add((a.EntityId, new StateSample(Ha.Number(i), i, gone.AddHours(-i))));
            samples.Add((b.EntityId, new StateSample(Ha.Number(i), i, gone.AddHours(-i))));
        }

        await Store.AddSamplesAsync(samples, CancellationToken.None);

        // From an earlier version that gave each its own card.
        var ownCard = await OpenAsync(b.EntityId, AnomalyKind.Unavailable, gone.AddMinutes(31));
        await OpenAsync(a.EntityId, AnomalyKind.Unavailable, gone.AddMinutes(31));

        var report = await Scanner(Options()).ScanAsync(CancellationToken.None);

        var closed = (await Store.GetAnomalyAsync(ownCard.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Equal($"It is now covered by the finding for {a.EntityId}.", Reason(closed));
        Assert.Equal(1, report.Resolved);

        var lead = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, false, CancellationToken.None));
        Assert.Equal(a.EntityId, lead.EntityId);
        Assert.Equal([b.EntityId], AnomalyScanner.Siblings(lead.EvidenceJson));
    }
}
