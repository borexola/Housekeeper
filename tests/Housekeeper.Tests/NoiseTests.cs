using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// The findings a real house actually produced, and which of them should survive.
///
/// After a day watching 3,425 entities, Housekeeper raised nineteen findings and the user promoted none of
/// them into an automation. Every case below is one of those nineteen, reconstructed from the numbers on the
/// card. Most are here to stay quiet; the two that were worth reading are here to make sure the quieting did
/// not take them with it, which is the only way this file means anything.
/// </summary>
public class NoiseTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 2, 0, 0, TimeSpan.Zero);

    /// <summary>The range gates alone: the readings here are dated a minute or two ago, and how long a reading has stayed out is tested on its own.</summary>
    private static ScanOptions Options => new() { MinimumExcursion = TimeSpan.Zero };

    /// <summary>Readings ending just before <paramref name="changed"/>, oldest first, as the store returns them.</summary>
    private static List<StateSample> Readings(DateTimeOffset changed, TimeSpan step, params double[] values)
    {
        var start = changed - step * values.Length;
        return [.. values.Select((value, i) => new StateSample(Ha.Number(value), value, start + (step * i)))];
    }

    private static double[] Wobble(int count, double around, double by) =>
        [.. Enumerable.Range(0, count).Select(i => around + (((i % 4) - 1.5) * by))];

    // ---- running totals ----

    /// <summary>
    /// "Family Room Tv Plug Energy reads 6.54 kWh, well outside its usual range: it normally sits near
    /// 6.42 kWh." An energy meter's newest reading is the largest it has ever been by construction, so the
    /// median trails it permanently, the gap widens on its own, and once raised the finding can never close.
    /// </summary>
    [Fact]
    public void An_energy_meter_is_never_an_outlier()
    {
        var changed = Now.AddMinutes(-2);
        var climbing = Enumerable.Range(0, 40).Select(i => 6.20 + (i * 0.0055)).ToArray();

        var entity = Build.Entity("sensor.smart_plug_mini_energy_3", "6.54", changed,
            "Family Room Tv Plug Energy", "energy", "kWh");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(
            entity, Readings(changed, TimeSpan.FromMinutes(20), climbing), Options, Now));
    }

    /// <summary>
    /// "NAS / disk used reads 158.5 GiB ... it normally sits near 157.3 GiB." Disk usage is reported as
    /// an ordinary measurement rather than a total, so nothing in Home Assistant labels it — it has to be
    /// recognised from the shape of the numbers.
    /// </summary>
    [Fact]
    public void A_disk_filling_up_is_recognised_as_a_ratchet_without_being_labelled_one()
    {
        var changed = Now.AddMinutes(-2);
        var filling = Enumerable.Range(0, 40).Select(i => 157.0 + (i * 0.01)).ToArray();

        Assert.True(Baselines.IsRatchet(filling));

        var entity = Build.Entity("sensor.10_1_0_60_disk_used", "158.5", changed, "/ disk used", unit: "GiB");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(
            entity, Readings(changed, TimeSpan.FromMinutes(20), filling), Options, Now));
    }

    /// <summary>
    /// The case where the ratchet gate is the only thing standing between the user and a finding.
    ///
    /// A counter that sits still and then climbs in steps — a data allowance during a big download, a water
    /// meter while the garden is watered — is the shape that defeats every other gate at once. The flat
    /// stretch collapses the spread, so the score is enormous; the climb is a large share of the value, so
    /// the significance floor is cleared; and no earlier reading is anywhere near the new one, so it is not
    /// a known mode. Only the direction of travel gives it away.
    /// </summary>
    [Fact]
    public void A_counter_that_sits_still_and_then_climbs_is_caught_by_direction_alone()
    {
        var changed = Now.AddMinutes(-2);

        // Twenty-five readings at 100 GB, then fifteen stepping up to 115. Now it reads 130.
        double[] stepping =
        [
            .. Enumerable.Repeat(100.0, 25),
            .. Enumerable.Range(1, 15).Select(i => 100.0 + i),
        ];

        var history = Readings(changed, TimeSpan.FromMinutes(20), stepping);
        var entity = Build.Entity("sensor.router_data_used", "130", changed, "Router Data Used", unit: "GB");

        Assert.True(Baselines.IsRatchet(stepping));
        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));

        // Every other gate would have let this through, so the direction check is load-bearing here and not
        // merely agreeing with one of the others. The same numbers shuffled out of order are reported.
        double[] shuffled = [.. stepping.Select((value, i) => stepping[(i * 17) % stepping.Length])];

        Assert.False(Baselines.IsRatchet(shuffled));
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(
            entity, Readings(changed, TimeSpan.FromMinutes(20), shuffled), Options, Now));
    }

    /// <summary>A battery is a ratchet pointing the other way: "3009 mV, it normally sits near 3019.5 mV".</summary>
    [Fact]
    public void A_draining_battery_is_a_ratchet_too()
    {
        var draining = Enumerable.Range(0, 40).Select(i => 3040.0 - (i * 0.8)).ToArray();

        Assert.True(Baselines.IsRatchet(draining));
    }

    /// <summary>A few steps the wrong way — a counter reset, a cold morning — do not stop it being a ratchet.</summary>
    [Fact]
    public void A_ratchet_survives_the_occasional_step_backwards()
    {
        var values = Enumerable.Range(0, 40).Select(i => 100.0 + i + (i == 17 ? -3 : 0)).ToArray();

        Assert.True(Baselines.IsRatchet(values));
        Assert.False(Baselines.IsRatchet(Wobble(40, 100, 2)));
    }

    /// <summary>A total is skipped on the label alone, before any of the numbers are looked at.</summary>
    [Fact]
    public void A_total_increasing_sensor_is_skipped_on_its_state_class()
    {
        var changed = Now.AddMinutes(-2);
        var entity = Build.Entity("sensor.house_energy", "900", changed, unit: "kWh", stateClass: "total_increasing");

        Assert.True(entity.IsCumulative);
        Assert.Null(AnomalyDetection.DetectNumericOutlier(
            entity, Readings(changed, TimeSpan.FromMinutes(20), Wobble(40, 5, 0.2)), Options, Now));
    }

    // ---- diagnostics and settings ----

    /// <summary>
    /// "Family Room Presence Sensor Linkquality reads 255 lqi ... it normally sits near 156 lqi." A mesh
    /// sensor that found a better route to the coordinator. There is no automation anyone wants from this.
    /// </summary>
    [Theory]
    [InlineData("sensor.0x54ef44100129b748_linkquality", "lqi", null)]
    [InlineData("sensor.lock_pro_f6c7_bluetooth_signal", "dBm", "signal_strength")]
    [InlineData("sensor.hall_sensor_battery", "%", "battery")]
    [InlineData("sensor.pi_uptime", "s", null)]
    public void Diagnostic_readings_are_never_findings(string entityId, string unit, string? deviceClass)
    {
        var changed = Now.AddMinutes(-2);
        var entity = Build.Entity(entityId, "255", changed, deviceClass: deviceClass, unit: unit);

        Assert.NotNull(Baselines.DiagnosticReason(entity));
        Assert.Null(AnomalyDetection.DetectNumericOutlier(
            entity, Readings(changed, TimeSpan.FromMinutes(20), Wobble(40, 156, 2)), Options, Now));
    }

    /// <summary>
    /// "Fridge Plug Auto-off enabled has been off for 50 minutes. Between 6 pm and 10 pm it is usually off
    /// for about 87 seconds." A checkbox on a plug, not a fridge. Two of the nineteen were this.
    /// </summary>
    [Fact]
    public void A_device_setting_is_never_stuck()
    {
        var changed = Now.AddMinutes(-50);
        var entity = Build.Entity("switch.fridge_plug_auto_off_enabled", "off", changed, "Fridge Plug Auto-off enabled");

        Assert.NotNull(Baselines.DiagnosticReason(entity));

        List<StateSample> history = [];
        for (var i = 20; i > 0; i--)
        {
            history.Add(new StateSample("off", null, Now.AddHours(-i)));
            history.Add(new StateSample("on", null, Now.AddHours(-i).AddMinutes(2)));
        }

        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, Options, Now));
    }

    // ---- moves too small to matter ----

    /// <summary>
    /// "Masters Bathroom Presence Sensor Humidity reads 68.75%, it normally sits near 65.3%." Three and a
    /// half points of humidity clears four sigma easily on a baseline that has been sitting still, and means
    /// nothing at all. The floor for humidity is eight points.
    /// </summary>
    [Fact]
    public void A_move_too_small_to_act_on_is_not_reported_however_well_it_scores()
    {
        var changed = Now.AddMinutes(-2);
        var steady = Wobble(40, 65.3, 0.1);
        var history = Readings(changed, TimeSpan.FromMinutes(20), steady);
        var entity = Build.Entity("sensor.masters_bathroom_presence_sensor_humidity", "68.75", changed,
            deviceClass: "humidity", unit: "%");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));

        // The gate really is the size of the move: a shower taking it to 85% is over the eight-point floor.
        var soaked = entity with { State = "85" };
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(soaked, history, Options, Now));
    }

    /// <summary>Floors are stated in the device class's base unit and converted into whatever it reports in.</summary>
    [Theory]
    [InlineData("voltage", "mV", 2000)]
    [InlineData("voltage", "V", 2)]
    [InlineData("power", "W", 5)]
    [InlineData("current", "A", 0.5)]
    [InlineData("current", "mA", 500)]
    [InlineData("temperature", "°C", 2)]
    [InlineData("temperature", "°F", 3.6)]
    public void A_floor_is_expressed_in_the_unit_the_entity_reports(string deviceClass, string unit, double expected)
    {
        var entity = Build.Entity("sensor.thing", "1", deviceClass: deviceClass, unit: unit);

        Assert.Equal(expected, Baselines.AbsoluteFloor(entity), 3);
    }

    // ---- more than one normal ----

    /// <summary>
    /// "HrvPlug Power reads 71.8 W, it normally sits near 173.75 W." A heat-recovery ventilator on a lower
    /// fan speed. The median lands in the mode the device spends most of its time in, and every other mode
    /// then scores as an excursion for ever.
    /// </summary>
    [Fact]
    public void A_speed_the_device_already_runs_at_is_not_an_excursion()
    {
        var changed = Now.AddMinutes(-2);

        // Nine readings in ten at full speed, one in ten on low — which is where it is now.
        double[] modes = [.. Enumerable.Range(0, 40).Select(i => i % 10 == 3 ? 71.9 : 173.75 + ((i % 3) * 0.1))];
        var history = Readings(changed, TimeSpan.FromMinutes(20), modes);
        var entity = Build.Entity("sensor.hrvplug_power", "71.8", changed, "HrvPlug Power", "power", "W");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now));

        // A speed it has never run at before is still reported, so this is suppressing the known mode
        // rather than suppressing the sensor.
        var unheardOf = entity with { State = "420" };
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(unheardOf, history, Options, Now));
    }

    /// <summary>"NAS sdd disk write reads 0 MB/s, it normally sits near 19.84 MB/s." The disk is idle.</summary>
    [Fact]
    public void An_idle_disk_is_not_a_fault()
    {
        var changed = Now.AddMinutes(-2);
        double[] bursty = [.. Enumerable.Range(0, 40).Select(i => i % 8 == 0 ? 0.0 : 19.84 + (i % 3))];

        var entity = Build.Entity("sensor.10_1_0_60_sdd_disk_write", "0", changed, unit: "MB/s");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(
            entity, Readings(changed, TimeSpan.FromMinutes(20), bursty), Options, Now));
    }

    // ---- baselines that are not baselines ----

    /// <summary>
    /// "Judged over 250 readings" was four hours of a sensor that changes every minute. The count was the
    /// only bar, and a count measures how much an entity fidgets rather than how long it has been watched.
    /// </summary>
    [Fact]
    public void A_baseline_has_to_reach_back_as_well_as_add_up()
    {
        var changed = Now.AddMinutes(-2);
        var values = Wobble(40, 7.0, 0.1);

        // Forty readings, all from the last four hours: plenty of rows, no reach.
        var crowded = Readings(changed, TimeSpan.FromMinutes(6), values);
        var entity = Build.Entity("sensor.smart_plug_mini_power_4", "13.31", changed,
            "Office Desk 2 Smart Plug Power", "power", "W");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, crowded, Options, Now));

        // The same forty readings spread over half a day are a baseline, and the load really is new.
        var spread = Readings(changed, TimeSpan.FromMinutes(20), values);
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(entity, spread, Options, Now));
    }

    /// <summary>Thirteen, sixteen and nineteen readings each produced a finding. A median absolute deviation needs more.</summary>
    [Fact]
    public void A_handful_of_readings_is_not_enough_however_far_apart_they_are()
    {
        var changed = Now.AddMinutes(-2);
        var few = Readings(changed, TimeSpan.FromHours(2), Wobble(16, 3019.5, 1));

        var entity = Build.Entity("sensor.lobby_presence_sensor_voltage", "3009", changed, unit: "mV");

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, few, Options, Now));
    }

    /// <summary>
    /// "Backyard Meter Temperature reads 8.6 °C, it normally sits near 23.25 °C." An outdoor thermometer at
    /// two in the morning, judged against a baseline that is mostly afternoons. Pooled, the afternoons
    /// dominate the median and the nights become the outliers; banded, the night is compared to other nights.
    /// </summary>
    [Fact]
    public void An_outdoor_sensor_is_judged_against_the_same_time_of_day()
    {
        var changed = Now.AddMinutes(-2);
        List<StateSample> nights = [], afternoons = [];

        for (var day = 1; day <= 3; day++)
        {
            var midnight = new DateTimeOffset(Now.UtcDateTime.Date.AddDays(-day), TimeSpan.Zero);

            // Ten night readings ranging over a few degrees, and forty warm afternoon ones.
            for (var i = 0; i < 10; i++)
                nights.Add(Reading(7.0 + (i * 0.45), midnight.AddMinutes(30 + (i * 18))));

            for (var i = 0; i < 40; i++)
                afternoons.Add(Reading(23.25 + ((i % 3) * 0.1), midnight.AddHours(12).AddMinutes(i * 5)));
        }

        var entity = Build.Entity("sensor.indoor_outdoor_meter_4b56_temperature", "8.6", changed,
            "Backyard Meter Temperature", "temperature", "°C");

        List<StateSample> everything = [.. nights, .. afternoons];
        everything.Sort((left, right) => left.ChangedUtc.CompareTo(right.ChangedUtc));

        Assert.Null(AnomalyDetection.DetectNumericOutlier(entity, everything, Options, Now));

        // And the banding really is what saves it. Given only the afternoons — the shape the old
        // newest-250-readings fetch produced from a sensor that changes every minute — the same perfectly
        // ordinary night reads as ninety sigma out and gets a card.
        var reported = AnomalyDetection.DetectNumericOutlier(entity, afternoons, Options, Now);

        Assert.NotNull(reported);
        Assert.Contains("8.6 °C", reported.Summary);

        // A night colder than any on record is the case only banding can turn down. At 6 °C the reading is
        // outside everything the whole history contains, so the threshold has somewhere to go and every
        // other gate waves it through — but against the other nights it is under two sigma, which is a
        // cold night and not a fault.
        var colder = entity with { State = "6" };

        Assert.Null(AnomalyDetection.DetectNumericOutlier(colder, everything, Options, Now));
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(colder, afternoons, Options, Now));

        static StateSample Reading(double value, DateTimeOffset at) => new(Ha.Number(value), value, at);
    }

    // ---- one device, one card ----

    /// <summary>
    /// "Office Desk 2 Smart Plug Power", "Office Desk 2 Smart Plug Active current" and "Office Desk 2 Smart
    /// Plug Energy" are one plug being switched on, shown three times. So are the backyard light's
    /// <c>light.</c> and <c>switch.</c> entities. Five of the nineteen cards were duplicates of another card.
    /// </summary>
    [Fact]
    public void One_device_gets_one_card_and_names_the_rest()
    {
        var plug = Build.Entity("sensor.smart_plug_mini_power_4", deviceId: "dev-desk", deviceName: "Office Desk 2");
        var current = Build.Entity("sensor.smart_plug_mini_active_current_4", deviceId: "dev-desk", deviceName: "Office Desk 2");
        var elsewhere = Build.Entity("sensor.kitchen_temperature", deviceId: "dev-kitchen", deviceName: "Kitchen");

        var collapsed = AnomalyScanner.Collapse(
        [
            (plug, Outlier(plug, severity: 6)),
            (current, Outlier(current, severity: 2)),
            (elsewhere, Outlier(elsewhere, severity: 3)),
        ], Options);

        Assert.Equal(2, collapsed.Count);

        var lead = Assert.Single(collapsed, found => found.EntityId == plug.EntityId);
        Assert.Contains("sensor.smart_plug_mini_active_current_4", lead.Summary);
        Assert.Contains("\"also_moved\"", lead.EvidenceJson);

        // A different device keeps its own card.
        Assert.Contains(collapsed, found => found.EntityId == elsewhere.EntityId);
    }

    /// <summary>
    /// Findings on the same device but of different kinds are different news — one entity stuck and another
    /// reading oddly are two things to look at, not one.
    /// </summary>
    [Fact]
    public void Different_kinds_on_one_device_are_not_collapsed_together()
    {
        var sensor = Build.Entity("binary_sensor.desk_occupancy", deviceId: "dev-desk", deviceName: "Office Desk 2");
        var power = Build.Entity("sensor.desk_power", deviceId: "dev-desk", deviceName: "Office Desk 2");

        var collapsed = AnomalyScanner.Collapse(
        [
            (sensor, Outlier(sensor, 2) with { Kind = AnomalyKind.StuckState, DedupKey = "stuck:x" }),
            (power, Outlier(power, 2)),
        ], Options);

        Assert.Equal(2, collapsed.Count);
    }

    /// <summary>Without a device there is nothing to group by, and grouping anyway would fold the whole house into one card.</summary>
    [Fact]
    public void Entities_with_no_known_device_are_left_alone()
    {
        var one = Build.Entity("sensor.one");
        var two = Build.Entity("sensor.two");

        Assert.Equal(2, AnomalyScanner.Collapse([(one, Outlier(one, 2)), (two, Outlier(two, 5))], Options).Count);
    }

    private static Anomaly Outlier(HaEntity entity, double severity) => new()
    {
        DedupKey = $"outlier:{entity.EntityId}",
        EntityId = entity.EntityId,
        Kind = AnomalyKind.NumericOutlier,
        Summary = "Reads oddly.",
        SuggestedRequest = $"Notify me when {entity.EntityId} goes above 1.",
        Severity = severity,
    };

    // ---- the ones worth keeping ----

    /// <summary>
    /// "Kids Room Presence Sensor Occupancy has been on for 51 minutes. Between 6 pm and 10 pm it is usually
    /// on for about 53 seconds; the longest before now was 9 minutes, across 28 earlier stretches." A latched
    /// presence sensor, and the one finding out of nineteen that was unambiguously worth reading. None of the
    /// quieting above may touch it.
    /// </summary>
    [Fact]
    public void The_latched_presence_sensor_is_still_reported()
    {
        var changed = Now.AddMinutes(-51);
        var entity = Build.Entity("binary_sensor.smart_presence_sensor_occupancy_2", "on", changed,
            "Kids Room Presence Sensor Occupancy", "occupancy", deviceId: "dev-kids", deviceName: "Kids Room Presence Sensor");

        List<StateSample> history = [];
        for (var i = 28; i > 0; i--)
        {
            var at = Now.AddHours(-i * 2);
            history.Add(new StateSample("on", null, at));
            history.Add(new StateSample("off", null, at.AddSeconds(53)));
        }

        var anomaly = AnomalyDetection.DetectStuckState(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Equal(AnomalyKind.StuckState, anomaly.Kind);
        Assert.Contains("51 minutes", anomaly.Summary);
        Assert.True(anomaly.Severity > 1, "a stretch this far past the bar should outrank a borderline one");
    }

    /// <summary>
    /// A freezer that has stopped cooling is the case the product exists for, and it is a numeric one: a
    /// large, sustained move on a sensor with a tight history and no other mode to hide in.
    /// </summary>
    [Fact]
    public void A_freezer_that_has_stopped_cooling_is_still_reported()
    {
        var changed = Now.AddMinutes(-2);
        var history = Readings(changed, TimeSpan.FromMinutes(20), Wobble(40, -18.0, 0.4));
        var entity = Build.Entity("sensor.freezer_temperature", "-4.5", changed,
            "Freezer Temperature", "temperature", "°C");

        var anomaly = AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("goes above", anomaly.SuggestedRequest);

        // And the automation it offers is one that was not already true when it was offered.
        Assert.DoesNotContain("above -4.5", anomaly.SuggestedRequest);
    }

    /// <summary>
    /// The defect behind every numeric card the real house produced: the line was placed between normal and
    /// the reading that had just been seen, so it was already crossed at the moment it was suggested.
    /// </summary>
    [Fact]
    public void The_offered_threshold_is_never_already_crossed()
    {
        var changed = Now.AddMinutes(-2);
        var history = Readings(changed, TimeSpan.FromMinutes(20), Wobble(40, 20.0, 1.0));

        foreach (var reading in new[] { "60", "-20" })
        {
            var entity = Build.Entity("sensor.flow", reading, changed, unit: "L/min");
            var anomaly = AnomalyDetection.DetectNumericOutlier(entity, history, Options, Now);

            Assert.NotNull(anomaly);

            using var evidence = System.Text.Json.JsonDocument.Parse(anomaly.EvidenceJson);
            var limit = evidence.RootElement.GetProperty("suggested_threshold").GetDouble();
            var current = evidence.RootElement.GetProperty("current").GetDouble();

            // Strictly between the baseline it was placed outside of and the reading that raised it, and
            // clear of everything the baseline ever actually did.
            Assert.True(
                current > 20 ? limit < current && limit > 21.5 : limit > current && limit < 18.5,
                $"threshold {limit} against a reading of {current}");
        }
    }
}
