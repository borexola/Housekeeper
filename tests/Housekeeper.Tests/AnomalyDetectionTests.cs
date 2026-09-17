using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Tests;

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
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-14), "Freezer Door", "door",
            deviceId: "dev-freezer", deviceName: "Freezer");

        var anomaly = AnomalyDetection.DetectStuckState(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Equal(AnomalyKind.StuckState, anomaly.Kind);
        Assert.Equal("stuck:binary_sensor.freezer_door", anomaly.DedupKey);
        // The card shows the name as its heading, so the summary starts with what happened.
        Assert.StartsWith("Has been 'on' for 14 minutes.", anomaly.Summary);
        Assert.Contains("\"entity_name\":\"Freezer Door\"", anomaly.EvidenceJson);
        Assert.Contains("\"device\":\"Freezer\"", anomaly.EvidenceJson);
        Assert.Contains("binary_sensor.freezer_door", anomaly.SuggestedRequest);
        Assert.Contains("stays 'on'", anomaly.SuggestedRequest);
    }

    /// <summary>
    /// The product's central judgement: is this longer than this door normally manages? The hold has to clear
    /// the ten-minute floor first, or the detector returns before the comparison is ever reached — which is
    /// what this test used to do, making it a duplicate of the minimum-duration one under a name that claimed
    /// to cover the thing that matters. Proven by mutation: with the multiplier set to zero it now fails.
    /// </summary>
    [Fact]
    public void Stays_quiet_while_the_door_is_open_a_normal_amount_of_time()
    {
        // Normally open for twenty minutes, so three times that is an hour. Thirty minutes is unremarkable.
        var history = DoorHistory(openPeriods: 12, openFor: TimeSpan.FromMinutes(20), closedFor: TimeSpan.FromHours(2));
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-30));

        Assert.True(Now - entity.LastChanged > Options.MinimumStuckDuration, "the duration floor must not be what stops it");
        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, Options, Now));

        // And past three times normal it is reported, so the bar is really being applied.
        var stuck = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-90));
        Assert.NotNull(AnomalyDetection.DetectStuckState(stuck, history, Options, Now));
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

    /// <summary>
    /// A motion sensor is clear nearly all day and a camera's person sensor more so, so the resting side of
    /// a binary sensor is never judged. Which side rests depends on the device class.
    /// </summary>
    [Fact]
    public void Never_judges_a_binary_sensor_sitting_at_rest()
    {
        var history = DoorHistory(openPeriods: 12, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));

        var clear = Build.Entity("binary_sensor.hall_motion", "off", Now.AddHours(-10), "Hall Motion", "motion");
        Assert.Null(AnomalyDetection.DetectStuckState(clear, history, Options, Now));

        var connected = Build.Entity("binary_sensor.hub_link", "on", Now.AddHours(-10), "Hub Link", "connectivity");
        Assert.Null(AnomalyDetection.DetectStuckState(connected, history, Options, Now));

        // The off side of a connectivity sensor is the active one: that hub has been unreachable for hours.
        var dropped = Build.Entity("binary_sensor.hub_link", "off", Now.AddHours(-10), "Hub Link", "connectivity");
        Assert.NotNull(AnomalyDetection.DetectStuckState(dropped, history, Options, Now));
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

    /// <summary>
    /// The point of <c>CanJudge</c>: the detectors do not share one bar. Counting entities with
    /// <c>MinimumSamples</c> recorded changes — the obvious approximation — calls this door unready while the
    /// stuck-state detector is perfectly willing to report it, which is exactly what it does here.
    /// </summary>
    [Fact]
    public void A_door_can_be_judged_on_completed_stretches_long_before_it_has_twelve_changes()
    {
        var history = DoorHistory(openPeriods: 4, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));
        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-14), "Freezer Door");

        // Fewer recorded changes than the sample floor, so a plain count would say "not enough history".
        Assert.True(history.Count < Options.MinimumSamples);

        // And yet the detector reaches a verdict, which is the only thing "ready to judge" can honestly mean.
        Assert.NotNull(AnomalyDetection.DetectStuckState(entity, history, Options, Now));
        Assert.True(AnomalyDetection.CanJudge(entity, history, Options, Now));
    }

    /// <summary>
    /// A numeric entity is judgeable once it has MinimumSamples readings to be judged AGAINST — earlier ones.
    /// Counting the reading itself said a verdict was reachable a sample before it really was, and the
    /// scanner then treated the detector's silence in that gap as "the condition has passed".
    /// </summary>
    [Fact]
    public void Enough_earlier_readings_is_what_makes_a_number_judgeable()
    {
        // The entity's own last change is the newest sample, which is how the scanner really leaves it.
        var history = Enumerable.Range(0, 13)
            .Select(i => new StateSample("21.5", 21.5, Now.AddHours(-13 + i)))
            .ToList();

        var entity = Build.Entity("sensor.thermometer", "21.5", history[^1].ChangedUtc);

        // Twelve earlier readings plus the one being judged.
        Assert.True(AnomalyDetection.CanJudge(entity, history, Options, Now));

        // One fewer, and the detector genuinely cannot speak yet.
        Assert.False(AnomalyDetection.CanJudge(entity, history[1..], Options, Now));
    }

    [Fact]
    public void A_sensor_resting_where_it_always_rests_is_not_judgeable_on_stretches_alone()
    {
        // Five completed "off" stretches, but "off" is where a motion sensor lives; it is never reported for
        // holding it, so those stretches buy nothing.
        var history = DoorHistory(openPeriods: 4, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));
        var resting = Build.Entity("binary_sensor.hall_motion", "off", Now.AddHours(-9), deviceClass: "motion");

        Assert.True(history.Count < Options.MinimumSamples);
        Assert.False(AnomalyDetection.CanJudge(resting, history, Options, Now));

        // A closed door is resting too, so it is judged the same way. It is the open one that is active,
        // and the same history is enough to judge that on stretches alone.
        var closed = Build.Entity("binary_sensor.back_door", "off", Now.AddHours(-9), deviceClass: "door");
        Assert.False(AnomalyDetection.CanJudge(closed, history, Options, Now));

        var open = Build.Entity("binary_sensor.back_door", "on", Now.AddHours(-9), deviceClass: "door");
        Assert.True(AnomalyDetection.CanJudge(open, history, Options, Now));
    }

    [Fact]
    public void Nothing_at_all_cannot_be_judged()
    {
        Assert.False(AnomalyDetection.CanJudge(Build.Entity("binary_sensor.new", "off"), [], Options, Now));
    }

    /// <summary>
    /// A disk that idles between 0.00 and 0.02 MB/s can score a large robust z on a move nobody can see.
    /// The old wording said so out loud: "Reads 0 MB/s, well outside its normal range. Usually near 0 MB/s."
    /// </summary>
    [Fact]
    public void A_move_too_small_to_write_down_is_not_a_finding()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => new StateSample("0", i % 8 == 0 ? 0.001 : 0.0, Now.AddHours(-40 + i)))
            .ToList();

        var entity = Build.Entity("sensor.sda_disk_read", "0.004", Now.AddMinutes(-2), unit: "MB/s");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));
    }

    [Fact]
    public void A_move_that_is_visible_is_still_reported()
    {
        var history = Enumerable.Range(0, 40)
            .Select(i => new StateSample("-19", -19 + ((i % 4) * 0.25), Now.AddHours(-40 + i)))
            .ToList();

        var entity = Build.Entity("sensor.freezer_temperature", "-4.2", Now.AddMinutes(-2), unit: "°C");

        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));
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

    /// <summary>
    /// The scanner stores the current reading before the detectors run, so the history a detector receives
    /// already holds the sample it is judging. Measuring the spread with that sample included let the outlier
    /// inflate the very scale it was measured against: for a near-flat baseline the score is capped at the
    /// square root of the sample count, which at the default twelve is 3.46 and never clears a threshold of 4.
    /// A boiler at more than double its usual pressure scored 3.5 and was never mentioned.
    /// </summary>
    [Fact]
    public void A_reading_is_judged_against_the_others_and_not_against_itself()
    {
        var changed = Now.AddMinutes(-2);

        // Readings hovering around 1.4 over most of a day, then the reading being judged, exactly as the
        // scanner leaves it.
        List<StateSample> history =
        [
            .. Enumerable.Range(0, 24).Select(i => new StateSample("1.4", 1.4, Now.AddHours(-20).AddMinutes(i * 20))),
            .. Enumerable.Range(0, 12).Select(i => new StateSample("1.5", 1.5, Now.AddHours(-12).AddMinutes(i * 20))),
            new StateSample("3.2", 3.2, changed),
        ];

        var entity = Build.Entity("sensor.boiler_pressure", "3.2", changed, unit: "bar");

        var anomaly = AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("3.2", anomaly.Summary);
        Assert.Contains("goes above", anomaly.SuggestedRequest);
    }

    /// <summary>
    /// A threshold three spreads out is sensible when there is spread. On a baseline that never moves at all
    /// the spread collapses to the precision the number is printed at, and three of those is a hundredth —
    /// so the offered automation fires on the first flicker of the last decimal place.
    /// </summary>
    [Fact]
    public void The_threshold_offered_is_never_nearer_to_normal_than_a_move_worth_mentioning()
    {
        var changed = Now.AddMinutes(-2);
        List<StateSample> history =
        [
            .. Enumerable.Range(0, 40).Select(i => new StateSample("20", 20.0, Now.AddHours(-20).AddMinutes(i * 20))),
            new StateSample("60", 60.0, changed),
        ];

        var anomaly = AnomalyDetection.DetectNumericOutlier(
            Build.Entity("sensor.flow", "60", changed), history, Options, Now);

        Assert.NotNull(anomaly);

        // The smallest move worth a finding is fifteen percent of 60, so the line sits at 20 + 9 rather
        // than at 20.02.
        Assert.Contains("above 29", anomaly.SuggestedRequest);
    }

    /// <summary>
    /// A sample is only stored when an entity CHANGES. A thermostat that has read the same number for a
    /// fortnight -- exactly the kind whose silence matters -- records almost none, so a bar made of sample
    /// counts meant it could never be reported as having gone quiet.
    /// </summary>
    [Fact]
    public void A_steady_sensor_that_goes_quiet_is_still_reported()
    {
        // Three recorded changes in ten days: a perfectly ordinary well-behaved sensor.
        List<StateSample> history =
        [
            new StateSample("21.5", 21.5, Now.AddDays(-10)),
            new StateSample("21.0", 21.0, Now.AddDays(-6)),
            new StateSample("21.5", 21.5, Now.AddDays(-2)),
        ];

        var entity = Build.Entity("sensor.hall_thermostat", "unavailable", Now.AddHours(-5));

        var anomaly = AnomalyDetection.DetectUnavailable(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("3 recorded changes", anomaly.Summary);
        Assert.True(AnomalyDetection.CanJudge(entity, history, Options, Now));
    }

    /// <summary>
    /// The scanner stores the current reading before the detectors run, so the outage itself was sitting in
    /// the history the detector judged — counted in the denominator and not the numerator. A spotless record
    /// therefore capped at (n-1)/n, which is under the 90% bar until ten recorded changes, so the detector
    /// could not speak at all about a steady entity. Which is precisely the entity it exists for.
    /// </summary>
    [Fact]
    public void The_outage_itself_is_not_counted_against_the_sensors_record()
    {
        var wentQuiet = Now.AddHours(-5);

        // Four spotless changes, then the outage the scanner has just recorded, exactly as it leaves it.
        List<StateSample> history =
        [
            new StateSample("21.5", 21.5, Now.AddDays(-9)),
            new StateSample("21.0", 21.0, Now.AddDays(-7)),
            new StateSample("21.5", 21.5, Now.AddDays(-4)),
            new StateSample("20.5", 20.5, Now.AddDays(-2)),
            new StateSample("unavailable", null, wentQuiet),
        ];

        var entity = Build.Entity("sensor.hall_thermostat", "unavailable", wentQuiet);

        var anomaly = AnomalyDetection.DetectUnavailable(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("100%", anomaly.Summary);
        Assert.Contains("4 recorded changes", anomaly.Summary);
    }

    /// <summary>
    /// Three spreads out is only sensible when it lands inside the move that was actually seen. The
    /// threshold setting allows values below 3, and there the offered limit sat BEYOND the reading that
    /// raised the finding — an automation that can never fire, produced by the app's own suggestion.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void The_threshold_offered_always_sits_between_normal_and_the_reading(double threshold)
    {
        var options = Options;
        options.OutlierThreshold = threshold;

        var changed = Now.AddMinutes(-2);
        List<StateSample> history =
        [
            .. Enumerable.Range(0, 40).Select(i =>
                new StateSample("20", 20 + ((i % 4) * 0.5), Now.AddHours(-20).AddMinutes(i * 20))),
            new StateSample("60", 60.0, changed),
        ];

        var anomaly = AnomalyDetection.DetectNumericOutlier(
            Build.Entity("sensor.flow", "60", changed), history, options, Now);

        Assert.NotNull(anomaly);

        using var evidence = JsonDocument.Parse(anomaly.EvidenceJson);
        var limit = evidence.RootElement.GetProperty("suggested_threshold").GetDouble();
        var median = evidence.RootElement.GetProperty("median").GetDouble();

        Assert.InRange(limit, median, 60.0);

        // And clear of everything the baseline ever did, so an ordinary day cannot cross it. This is the
        // half that was missing: the line used to be placed from the excursion rather than from the
        // baseline, which put it between normal and now and so already crossed the moment it was offered.
        Assert.True(limit > 21.5, $"threshold {limit} is inside the range the sensor routinely reaches");
    }

    /// <summary>
    /// 'running' and 'battery_charging' read as healthy, but the question IsResting asks is which side the
    /// sensor sits at nearly all the time — and a dishwasher, a pump or a charging phone is idle almost
    /// always. Listing them swapped which side was judged, and silenced the one case worth reporting:
    /// something left running far longer than it ever runs.
    /// </summary>
    [Theory]
    [InlineData("running")]
    [InlineData("battery_charging")]
    public void An_appliance_left_running_far_too_long_is_reported(string deviceClass)
    {
        var history = DoorHistory(openPeriods: 12, openFor: TimeSpan.FromMinutes(50), closedFor: TimeSpan.FromHours(6));
        var entity = Build.Entity("binary_sensor.dishwasher", "on", Now.AddHours(-9), deviceClass: deviceClass);

        Assert.NotNull(AnomalyDetection.DetectStuckState(entity, history, Options, Now));

        // And the idle side is what it does nearly all the time, so no length of that is a finding.
        var idle = Build.Entity("binary_sensor.dishwasher", "off", Now.AddDays(-3), deviceClass: deviceClass);
        Assert.Null(AnomalyDetection.DetectStuckState(idle, history, Options, Now));
    }

    /// <summary>
    /// Samples come from a poller, so a whole transition can happen between two polls and leave two
    /// neighbours carrying the same state. The gap between those is not one long stretch — it is two
    /// stretches with the middle never observed — and counting it inflated the bar every stuck-state
    /// finding is measured against, which is how a genuinely stuck door goes unreported.
    /// </summary>
    [Fact]
    public void A_stretch_that_was_never_seen_to_end_does_not_set_the_bar()
    {
        // Five short open stretches, and one pair of adjacent "on" samples eight hours apart that the poller
        // simply missed the middle of.
        List<StateSample> history = [];
        var at = Now.AddDays(-4);

        for (var i = 0; i < 6; i++)
        {
            history.Add(new StateSample("on", null, at));
            at = at.AddSeconds(40);
            history.Add(new StateSample("off", null, at));
            at = at.AddHours(3);
        }

        history.Add(new StateSample("on", null, at));
        history.Add(new StateSample("on", null, at.AddHours(8)));

        var entity = Build.Entity("binary_sensor.freezer_door", "on", Now.AddMinutes(-14), deviceClass: "door");
        var anomaly = AnomalyDetection.DetectStuckState(entity, history, Options, Now);

        Assert.NotNull(anomaly);

        // The bar is the longest stretch really seen to finish — forty seconds — not the eight-hour gap.
        using var evidence = JsonDocument.Parse(anomaly.EvidenceJson);
        Assert.Equal(40, evidence.RootElement.GetProperty("longest_previous_seconds").GetDouble());
    }

    [Fact]
    public void A_sensor_only_just_seen_is_not_yet_judged_on_its_silence()
    {
        // One change, an hour ago. Nothing here says it used to report reliably.
        List<StateSample> history = [new StateSample("21.5", 21.5, Now.AddHours(-1))];
        var entity = Build.Entity("sensor.brand_new", "unavailable", Now.AddMinutes(-40));

        Assert.Null(AnomalyDetection.DetectUnavailable(entity, history, Options, Now));
        Assert.False(AnomalyDetection.CanJudge(entity, history, Options, Now));
    }

    /// <summary>
    /// 'on' for a light device class means light was detected, which is neither healthy nor faulty. Listing
    /// it with connectivity and power inverted which side of a light sensor was ever judged.
    /// </summary>
    [Fact]
    public void A_light_sensor_in_the_dark_is_resting_like_every_other_binary_sensor()
    {
        var history = DoorHistory(openPeriods: 4, openFor: TimeSpan.FromSeconds(45), closedFor: TimeSpan.FromHours(2));

        var dark = Build.Entity("binary_sensor.porch_light_level", "off", Now.AddHours(-9), deviceClass: "light");
        Assert.False(AnomalyDetection.CanJudge(dark, history, Options, Now));

        var lit = Build.Entity("binary_sensor.porch_light_level", "on", Now.AddHours(-9), deviceClass: "light");
        Assert.True(AnomalyDetection.CanJudge(lit, history, Options, Now));
    }

    [Fact]
    public void A_number_that_rounds_to_negative_zero_is_written_as_zero()
    {
        // "-0" is not a reading anyone should be shown, and as text it compared unequal to "0", which let a
        // move too small to see past the guard that drops exactly that.
        Assert.Equal("0", Ha.Number(-0.004));
        Assert.Equal("0", Ha.Number(0.004));
        Assert.Equal("-0.01", Ha.Number(-0.0051));
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

    /// <summary>
    /// Rounding UP is the whole contract. A span past the end of the table used to come back as the table's
    /// last entry, which rounded a three-day stretch DOWN to one day -- putting the suggested threshold below
    /// the entity's ordinary behaviour, and producing an automation that fires constantly.
    /// </summary>
    [Theory]
    [InlineData(25, 2)]
    [InlineData(48, 2)]
    [InlineData(70, 3)]
    [InlineData(24 * 30, 30)]
    public void A_duration_longer_than_the_table_still_rounds_up(double hours, double expectedDays)
    {
        var span = TimeSpan.FromHours(hours);
        var rounded = AnomalyDetection.RoundUp(span);

        Assert.Equal(TimeSpan.FromDays(expectedDays), rounded);
        Assert.True(rounded >= span, $"{rounded} is shorter than the {span} it was meant to round up.");
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
