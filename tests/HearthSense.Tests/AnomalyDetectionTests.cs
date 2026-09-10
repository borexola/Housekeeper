using HearthSense.Core;

namespace HearthSense.Tests;

public class AnomalyDetectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    /// <summary>
    /// A freezer door that normally opens for under a minute has now been open for a quarter of an hour.
    /// This is the scenario the product is built around, end to end through the detector.
    /// </summary>
    [Fact]
    public void Reports_a_door_that_has_been_open_far_too_long()
    {
        var history = DoorHistory(openPeriods: 12, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-14), "Freezer Door", "door");

        var anomaly = AnomalyDetection.DetectStuckState(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Equal(AnomalyKind.StuckState, anomaly.Kind);
        Assert.Equal("stuck:binary_sensor.freezer_door", anomaly.DedupKey);
        Assert.Contains("Freezer Door", anomaly.Summary);
        Assert.Contains("14 minutes", anomaly.Summary);
        Assert.Contains("binary_sensor.freezer_door", anomaly.SuggestedRequest);
        Assert.Contains("stays 'on'", anomaly.SuggestedRequest);
    }

    [Fact]
    public void Stays_quiet_while_the_door_is_open_a_normal_amount_of_time()
    {
        var history = DoorHistory(openPeriods: 12, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddSeconds(-40));

        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, Options, Now));
    }

    [Fact]
    public void Stays_quiet_below_the_minimum_duration_however_unusual()
    {
        // Historically open for a single second, now open for five minutes: unusual, but under the floor.
        var history = DoorHistory(openPeriods: 12, openFor: TimeSpan.FromSeconds(1), closedFor: TimeSpan.FromHours(2));
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-5));

        var options = new ScanOptions { MinimumStuckDuration = TimeSpan.FromMinutes(10) };

        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, options, Now));
    }

    [Fact]
    public void Stays_quiet_without_enough_history_to_know_what_normal_is()
    {
        var history = DoorHistory(openPeriods: 2, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddHours(-6));

        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, Options, Now));
    }

    /// <summary>
    /// A patio door that is open for two hours every evening and under a minute every morning has two
    /// normals. Judged against the whole history, a morning stuck-open would hide behind the evenings.
    /// </summary>
    [Fact]
    public void Judges_against_the_same_time_of_day_when_the_history_supports_it()
    {
        var morning = new DateTimeOffset(2026, 3, 1, 8, 14, 0, TimeSpan.Zero);
        var history = TwoNormalsHistory(morning.Date, currentlyOpenSince: morning.AddMinutes(-14));
        var entity = Build.Entity("binary_sensor.patio_door", "on", morning.AddMinutes(-14), "Patio Door");

        var anomaly = AnomalyDetection.DetectStuckState(entity, history, Options, morning);

        Assert.NotNull(anomaly);
        Assert.Contains("this time of day", anomaly.Summary);
        Assert.Contains("14 minutes", anomaly.Summary);
    }

    [Fact]
    public void Stays_quiet_when_a_long_period_is_normal_for_that_time_of_day()
    {
        var evening = new DateTimeOffset(2026, 3, 1, 20, 50, 0, TimeSpan.Zero);
        var history = TwoNormalsHistory(evening.Date, currentlyOpenSince: evening.AddMinutes(-50));
        var entity = Build.Entity("binary_sensor.patio_door", "on", evening.AddMinutes(-50), "Patio Door");

        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, Options, evening));
    }

    [Fact]
    public void Flags_a_created_automation_whose_entity_has_disappeared()
    {
        var proposal = new Proposal
        {
            Id = 7,
            Request = "turn off the hall light when everyone leaves",
            Status = ProposalStatus.Created,
            Alias = "Away lights",
            Entities = ["light.hall", "person.sam"],
            HaAutomationId = "1699",
        };

        var anomaly = AnomalyDetection.DetectMissingEntities(proposal, new HashSet<string>(["person.sam"], StringComparer.Ordinal), Now);

        Assert.NotNull(anomaly);
        Assert.Equal(AnomalyKind.MissingEntity, anomaly.Kind);
        Assert.Equal("missing:7", anomaly.DedupKey);
        Assert.Equal("light.hall", anomaly.EntityId);
        Assert.Contains("Away lights", anomaly.Summary);
        Assert.Contains("light.hall", anomaly.Summary);
        Assert.Equal(proposal.Request, anomaly.SuggestedRequest);
    }

    [Fact]
    public void Ignores_automations_that_are_intact_or_were_never_created()
    {
        var known = new HashSet<string>(["light.hall"], StringComparer.Ordinal);
        var intact = new Proposal { Id = 1, Request = "r", Status = ProposalStatus.Created, Entities = ["light.hall"] };
        var draftOnly = new Proposal { Id = 2, Request = "r", Status = ProposalStatus.Draft, Entities = ["light.gone"] };

        Assert.Null(AnomalyDetection.DetectMissingEntities(intact, known, Now));
        Assert.Null(AnomalyDetection.DetectMissingEntities(draftOnly, known, Now));
    }

    [Fact]
    public void Reports_a_numeric_reading_far_outside_its_usual_range()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => new StateSample("-19", -19 + ((i % 4) * 0.25), Now.AddHours(-40 + i)))
            .ToList();

        var entity = Build.Entity("sensor.freezer_temperature", "-4.2", Now.AddMinutes(-2), unit: "°C");

        var anomaly = AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Equal(AnomalyKind.NumericOutlier, anomaly.Kind);
        Assert.Contains("-4.2", anomaly.Summary);
        Assert.Contains("°C", anomaly.Summary);
        Assert.Contains("goes above", anomaly.SuggestedRequest);
    }

    [Fact]
    public void Stays_quiet_when_a_numeric_reading_is_normal()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => new StateSample("-19", -19 + ((i % 4) * 0.25), Now.AddHours(-40 + i)))
            .ToList();

        var entity = Build.Entity("sensor.freezer_temperature", "-18.9", Now.AddMinutes(-2), unit: "°C");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));
    }

    [Fact]
    public void Stays_quiet_on_a_perfectly_flat_history_rather_than_dividing_by_zero()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => new StateSample("5", 5.0, Now.AddHours(-40 + i)))
            .ToList();

        var entity = Build.Entity("sensor.constant", "5", Now.AddMinutes(-2));

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));
    }

    [Fact]
    public void Reports_a_reliable_sensor_that_has_gone_quiet()
    {
        var history = Enumerable.Range(0, 30)
            .Select(i => new StateSample("21.5", 21.5, Now.AddHours(-30 + i)))
            .ToList();

        var entity = Build.Entity("sensor.back_door_battery", "unavailable", Now.AddHours(-3));

        var anomaly = AnomalyDetection.DetectUnavailable(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Equal(AnomalyKind.Unavailable, anomaly.Kind);
        Assert.Contains("unavailable", anomaly.SuggestedRequest);
    }

    [Fact]
    public void Stays_quiet_about_a_sensor_that_is_unreliable_anyway()
    {
        var history = Enumerable.Range(0, 30)
            .Select(i => new StateSample(i % 2 == 0 ? "unavailable" : "21.5", null, Now.AddHours(-30 + i)))
            .ToList();

        var entity = Build.Entity("sensor.flaky", "unavailable", Now.AddHours(-3));

        Assert.Null(AnomalyDetection.DetectUnavailable(entity, history, Options, Now));
    }

    [Fact]
    public void Suggested_durations_snap_to_values_a_person_would_type()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AnomalyDetection.RoundUp(TimeSpan.FromMinutes(7.4)));
        Assert.Equal(TimeSpan.FromMinutes(30), AnomalyDetection.RoundUp(TimeSpan.FromMinutes(21)));
        Assert.Equal(TimeSpan.FromMinutes(1), AnomalyDetection.RoundUp(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Median_and_deviation_are_robust_to_an_extreme_value()
    {
        double[] values = [10, 10, 11, 10, 10, 900];

        Assert.Equal(10, AnomalyDetection.Median(values));
        Assert.True(AnomalyDetection.MedianAbsoluteDeviation(values, 10) < 1);
    }

    /// <summary>
    /// Two weeks in which the door opens for 45 seconds every morning at 08:00 and for two hours every
    /// evening at 20:00, ending with the door open since <paramref name="currentlyOpenSince"/>.
    /// </summary>
    private static List<StateSample> TwoNormalsHistory(DateTime today, DateTimeOffset currentlyOpenSince)
    {
        List<StateSample> samples = [];

        for (var day = -14; day < 0; day++)
        {
            var date = new DateTimeOffset(today.AddDays(day), TimeSpan.Zero);
            samples.Add(new StateSample("on", null, date.AddHours(8)));
            samples.Add(new StateSample("off", null, date.AddHours(8).AddSeconds(45)));
            samples.Add(new StateSample("on", null, date.AddHours(20)));
            samples.Add(new StateSample("off", null, date.AddHours(22)));
        }

        samples.Add(new StateSample("on", null, currentlyOpenSince));
        return samples;
    }

    /// <summary>
    /// Alternating closed/open periods, ending with the door currently open. The final sample is the
    /// in-progress period, so the detector must not count it as historical evidence.
    /// </summary>
    private static List<StateSample> DoorHistory(int openPeriods, TimeSpan openFor, TimeSpan closedFor)
    {
        List<StateSample> samples = [];
        var cursor = Now - ((closedFor + openFor) * (openPeriods + 1));

        for (var i = 0; i < openPeriods; i++)
        {
            samples.Add(new StateSample("off", null, cursor));
            cursor += closedFor;
            samples.Add(new StateSample("on", null, cursor));
            cursor += openFor;
        }

        samples.Add(new StateSample("off", null, cursor));
        samples.Add(new StateSample("on", null, Now.AddMinutes(-14)));

        return samples;
    }
}
