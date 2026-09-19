using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Housekeeper.Tests;

/// <summary>
/// The rules the routine miner gained from review: a cue with more history than its effect, persons as
/// subjects, the last band of the day, any cue transition in the window, random switching not read as a
/// clock routine, machine-punctual routines set aside, and the house's own time zone.
/// </summary>
public class HabitRuleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute, int second = 0) =>
        Start.AddDays(day).AddHours(hour).AddMinutes(minute).AddSeconds(second);

    private static readonly HaEntity Light = Build.Entity("light.pantry", "off", Start, friendlyName: "Pantry light", area: "Pantry");
    private static readonly HaEntity Motion = Build.Entity("binary_sensor.pantry_motion", "off", Start, friendlyName: "Pantry motion sensor", deviceClass: "motion", area: "Pantry");

    private sealed class Diary
    {
        private readonly Dictionary<string, List<StateSample>> _samples = new(StringComparer.Ordinal);

        public Diary Add(string entityId, DateTimeOffset at, string state)
        {
            if (!_samples.TryGetValue(entityId, out var list)) _samples[entityId] = list = [];
            list.Add(new StateSample(state, null, at));
            return this;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<StateSample>> Samples =>
            _samples.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<StateSample>)[.. pair.Value.OrderBy(s => s.ChangedUtc)], StringComparer.Ordinal);
    }

    private static Habits.Report Find(Diary diary, IReadOnlyList<HaEntity> entities, int days, TimeZoneInfo? zone = null, IReadOnlyList<ExistingAutomation>? automations = null) =>
        Habits.Find(entities, diary.Samples, automations ?? [], zone ?? TimeZoneInfo.Utc, new ScanOptions(), At(days, 12, 0));

    [Fact]
    public void A_cue_with_weeks_more_history_than_its_effect_does_not_drown_the_routine_in_misses()
    {
        // The motion sensor has fired every evening for four weeks; the light was added a week ago and has
        // followed it every evening since. Before the light existed, the motion asked nothing of anyone.
        var diary = new Diary().Add(Motion.EntityId, Start, "off");
        for (var day = 0; day < 28; day++)
        {
            diary.Add(Motion.EntityId, At(day, 19, 0), "on");
            diary.Add(Motion.EntityId, At(day, 19, 5), "off");
        }

        diary.Add(Light.EntityId, At(21, 0, 0), "off");
        for (var day = 21; day < 28; day++)
        {
            diary.Add(Light.EntityId, At(day, 19, 0, 20), "on");
            diary.Add(Light.EntityId, At(day, 22, 0).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        var report = Find(diary, [Light, Motion], 28);

        var cue = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");
        Assert.Contains("\"share\":1", cue.EvidenceJson);
        Assert.Contains("\"out_of\":7", cue.EvidenceJson);
        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:clock");
    }

    [Fact]
    public void A_person_is_the_subject_of_the_sentence_not_a_thing()
    {
        var sam = Build.Entity("person.sam", "home", Start, friendlyName: "Sam");
        var hall = Build.Entity("light.hall", "off", Start, friendlyName: "Hall light", area: "Hall");
        var diary = new Diary().Add(sam.EntityId, Start, "not_home").Add(hall.EntityId, Start, "off");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(sam.EntityId, At(day, 17, 30), "home");
            diary.Add(hall.EntityId, At(day, 17, 30, 40), "on");
            diary.Add(sam.EntityId, At(day, 8, 0), "not_home");
            diary.Add(hall.EntityId, At(day, 23, 0).AddMinutes(day % 2 == 0 ? -3 : 3), "off");
        }

        var report = Find(diary, [sam, hall], 8);

        var cue = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.hall:on:person.sam:home");
        Assert.Contains("You usually turn on the Hall light when Sam arrives home.", cue.Summary);
        Assert.DoesNotContain("the Sam", cue.Summary);
        Assert.Contains("\"spoken\":\"Turn on the Hall light when Sam arrives home.\"", cue.EvidenceJson);
        Assert.Equal("Turn on light.hall when person.sam arrives home.", cue.SuggestedRequest);
    }

    [Fact]
    public void The_last_band_of_the_day_ends_at_midnight_not_at_twenty_four()
    {
        // The TV goes off only in the evening, and the lamp follows it; by day the TV goes off with no lamp.
        var tv = Build.Entity("media_player.tv", "off", Start, friendlyName: "TV", area: "Living room");
        var lamp = Build.Entity("light.lamp", "off", Start, friendlyName: "Lamp", area: "Living room");
        var diary = new Diary().Add(tv.EntityId, Start, "off").Add(lamp.EntityId, Start, "on");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(tv.EntityId, At(day, 12, 0), "playing");
            diary.Add(tv.EntityId, At(day, 13, 0), "off");
            diary.Add(tv.EntityId, At(day, 20, 0), "playing");
            diary.Add(tv.EntityId, At(day, 22, 30 + (day % 3) * 5), "off");
            diary.Add(lamp.EntityId, At(day, 22, 31 + (day % 3) * 5), "off");
            diary.Add(lamp.EntityId, At(day + 1, 6, 0), "on");
        }

        var report = Find(diary, [tv, lamp], 9);

        var cue = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.lamp:off:media_player.tv:off");
        Assert.Contains("\"condition\":\"band5\"", cue.EvidenceJson);
        Assert.EndsWith("between 20:00 and 00:00.", cue.SuggestedRequest);
        Assert.Contains("between 20:00 and midnight", cue.Summary);
        Assert.DoesNotContain("24:00", cue.SuggestedRequest);
    }

    [Fact]
    public void A_motion_sensor_that_clears_before_the_light_goes_on_is_still_the_cue_for_movement()
    {
        // Ten seconds of motion, then the light twenty seconds after it started: the newest cue transition
        // before the light is "off", and the routine used to be learned as "when it stops detecting".
        var diary = new Diary().Add(Light.EntityId, Start, "off").Add(Motion.EntityId, Start, "off");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(Motion.EntityId, At(day, 19, 0), "on");
            diary.Add(Motion.EntityId, At(day, 19, 0, 10), "off");
            diary.Add(Light.EntityId, At(day, 19, 0, 20), "on");
            diary.Add(Light.EntityId, At(day, 22, 0).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        var report = Find(diary, [Light, Motion], 8);

        var offered = report.Found.Where(habit => habit.DedupKey.StartsWith("habit:light.pantry:on:binary_sensor", StringComparison.Ordinal)).ToList();
        var only = Assert.Single(offered);
        Assert.Equal("habit:light.pantry:on:binary_sensor.pantry_motion:on", only.DedupKey);
    }

    [Fact]
    public void A_weekday_routine_narrowed_to_weekdays_still_needs_enough_occurrences()
    {
        // Wednesday to Saturday: three weekday mornings and two Saturday switches, five events in all. Five
        // clears the bar before narrowing and three does not after it.
        var start = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero); // a Wednesday
        var kitchen = Build.Entity("light.kitchen", "off", start, friendlyName: "Kitchen light");
        var diary = new Diary().Add(kitchen.EntityId, start, "off");
        foreach (var day in new[] { 0, 1, 2 })
        {
            diary.Add(kitchen.EntityId, start.AddDays(day).AddHours(7), "on");
            diary.Add(kitchen.EntityId, start.AddDays(day).AddHours(7).AddMinutes(30), "off");
        }

        diary.Add(kitchen.EntityId, start.AddDays(3).AddHours(7), "on");
        diary.Add(kitchen.EntityId, start.AddDays(3).AddHours(7).AddMinutes(15), "off");
        diary.Add(kitchen.EntityId, start.AddDays(3).AddHours(7).AddMinutes(30), "on");
        diary.Add(kitchen.EntityId, start.AddDays(3).AddHours(8), "off");

        var report = Habits.Find([kitchen], diary.Samples, [], TimeZoneInfo.Utc, new ScanOptions(), start.AddDays(4).AddHours(12));

        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.kitchen:on:clock");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_light_switched_at_random_ten_times_a_day_is_not_a_clock_routine(int seed)
    {
        var random = new Random(seed);
        var hall = Build.Entity("light.hall", "off", Start, friendlyName: "Hall light");
        var diary = new Diary().Add(hall.EntityId, Start, "off");
        for (var day = 0; day < 28; day++)
        {
            // Twenty changes a day, on and off alternating, at random moments between six and eleven.
            var minutes = Enumerable.Range(0, 20).Select(_ => 360 + random.Next(17 * 60)).Order().ToList();
            for (var i = 0; i < minutes.Count; i++)
                diary.Add(hall.EntityId, Start.AddDays(day).AddMinutes(minutes[i]).AddSeconds(random.Next(60)), i % 2 == 0 ? "on" : "off");
        }

        var report = Find(diary, [hall], 28);

        Assert.Empty(report.Found);
    }

    [Fact]
    public void A_routine_that_still_holds_when_the_light_is_also_used_at_random_is_offered()
    {
        var random = new Random(7);
        var porch = Build.Entity("light.porch", "off", Start, friendlyName: "Porch light");
        var diary = new Diary().Add(porch.EntityId, Start, "off");
        for (var day = 0; day < 28; day++)
        {
            // Four random daytime uses, and every evening on at about ten past nine.
            var minutes = Enumerable.Range(0, 4).Select(_ => 420 + random.Next(11 * 60)).Order().ToList();
            for (var i = 0; i < minutes.Count; i++)
                diary.Add(porch.EntityId, Start.AddDays(day).AddMinutes(minutes[i]), i % 2 == 0 ? "on" : "off");

            diary.Add(porch.EntityId, At(day, 21, 5 + random.Next(12)), "on");
            diary.Add(porch.EntityId, At(day, 23, 30 + random.Next(20)), "off");
        }

        var report = Find(diary, [porch], 28);

        var evening = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.porch:on:clock");
        Assert.Contains("at about 21:1", evening.Summary);
    }

    [Fact]
    public void A_response_to_the_second_every_time_is_a_machine_not_a_person()
    {
        var diary = new Diary().Add(Light.EntityId, Start, "off").Add(Motion.EntityId, Start, "off");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(Motion.EntityId, At(day, 19, 0), "on");
            diary.Add(Light.EntityId, At(day, 19, 0, 3), "on");
            diary.Add(Motion.EntityId, At(day, 19, 5), "off");
            diary.Add(Light.EntityId, At(day, 22, 0).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        var report = Find(diary, [Light, Motion], 8);

        Assert.Contains("habit:light.pantry:on:binary_sensor.pantry_motion:on", report.MachineMade);
        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");
        // And the clock is not offered for it either: the machine explains those moments.
        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:clock");
    }

    [Fact]
    public void Something_done_at_exactly_the_same_minute_every_day_is_a_timer_not_a_person()
    {
        var porch = Build.Entity("light.porch", "off", Start, friendlyName: "Porch light");
        var diary = new Diary().Add(porch.EntityId, Start, "off");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(porch.EntityId, At(day, 21, 0), "on");
            diary.Add(porch.EntityId, At(day, 23, 0), "off");
        }

        var report = Find(diary, [porch], 8);

        Assert.Empty(report.Found);
        Assert.Contains("habit:light.porch:on:clock", report.MachineMade);
        Assert.Contains("habit:light.porch:off:clock", report.MachineMade);
    }

    [Fact]
    public void Clock_routines_are_told_in_the_house_time_zone()
    {
        // Six hours behind UTC: ten past nine in the evening is ten past three the next morning in UTC.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Regina", TimeSpan.FromHours(-6), "Regina (test)", "Regina (test)");
        var porch = Build.Entity("light.porch", "off", Start, friendlyName: "Porch light");
        var diary = new Diary().Add(porch.EntityId, Start, "off");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(porch.EntityId, At(day, 21 + 6, 8 + (day % 3) * 2), "on");
            diary.Add(porch.EntityId, At(day + 1, 23 + 6 - 24, 30 + (day % 2) * 4), "off");
        }

        var report = Find(diary, [porch], 9, zone);

        var evening = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.porch:on:clock");
        Assert.Contains("at about 21:10", evening.Summary);
        Assert.Equal("Turn on light.porch at 21:10.", evening.SuggestedRequest);
    }

    [Theory]
    [InlineData(28, 28, 0.04, true)]
    [InlineData(20, 29, 0.45, false)]
    [InlineData(27, 29, 0.45, true)]
    [InlineData(3, 4, 0.2, false)]
    public void Beating_chance_needs_a_clear_margin_over_what_random_switching_would_give(int hit, int days, double chance, bool expected) =>
        Assert.Equal(expected, Habits.BeatsChance(hit, days, chance));

    [Theory]
    [InlineData(new[] { 400, 410, 420 }, 20)]
    [InlineData(new[] { 1430, 10 }, 20)]
    [InlineData(new[] { 0, 720 }, 720)]
    public void The_active_span_is_the_shortest_arc_of_the_day_holding_every_minute(int[] minutes, int expected) =>
        Assert.Equal(expected, Habits.ActiveSpan(minutes));
}

/// <summary>Findings and routines living in the scanner: dismissals that teach, routines that close for the right reason, and put-aways that stick.</summary>
public class ScannerLifecycleTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options(params string[] include)
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = [.. include];
        options.Scan.BackfillFromRecorder = false;
        return options;
    }

    /// <summary>Nine days of the pantry light following its motion sensor every evening, ending yesterday.</summary>
    private async Task SeedRoutineAsync()
    {
        var now = Clock.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-9);

        _ha.Entities.Add(Build.Entity("light.pantry", "off", now.AddHours(-14), friendlyName: "Pantry light", area: "Pantry"));
        _ha.Entities.Add(Build.Entity("binary_sensor.pantry_motion", "off", now.AddHours(-1), friendlyName: "Pantry motion sensor", deviceClass: "motion", area: "Pantry"));

        List<(string EntityId, StateSample Sample)> samples = [];
        void Add(string id, DateTimeOffset at, string state) => samples.Add((id, new StateSample(state, null, at)));
        Add("light.pantry", start, "off");
        Add("binary_sensor.pantry_motion", start, "off");
        for (var day = 0; day < 9; day++)
        {
            var d = start.AddDays(day);
            Add("binary_sensor.pantry_motion", d.AddHours(19), "on");
            Add("light.pantry", d.AddHours(19).AddSeconds(20), "on");
            Add("binary_sensor.pantry_motion", d.AddHours(19).AddMinutes(5), "off");
            Add("light.pantry", d.AddHours(22).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        await Store.AddSamplesAsync(samples, CancellationToken.None);
    }

    private const string RoutineKey = "habit:light.pantry:on:binary_sensor.pantry_motion:on";

    [Fact]
    public async Task A_numeric_finding_survives_a_single_reading_back_in_range_and_closes_once_it_has_stayed_there()
    {
        var options = Options("sensor.*");
        var scanner = Scanner(options);
        var now = Clock.GetUtcNow();

        // Twenty hours of readings near a hundred, half an hour apart (the density the thinned numeric
        // history keeps), then the plug reads 160 W for four five-minute scans.
        List<(string, StateSample)> baseline = [];
        for (var i = 0; i < 40; i++)
        {
            var value = 100 + (((i % 4) - 1.5) * 1.0);
            baseline.Add(("sensor.plug_power", new StateSample(Ha.Number(value), value, now.AddHours(-21).AddMinutes(30 * i))));
        }

        await Store.AddSamplesAsync(baseline, CancellationToken.None);
        var plug = Build.Entity("sensor.plug_power", "160", now, deviceClass: "power", unit: "W");
        _ha.Entities.Add(plug);

        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(5));
            _ha.Entities[0] = plug with { State = "160", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        var open = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, false, CancellationToken.None));
        Assert.Equal(AnomalyKind.NumericOutlier, open.Kind);
        var detected = open.DetectedUtc;

        // One poll back at a hundred: the card stays.
        Clock.Advance(TimeSpan.FromMinutes(5));
        _ha.Entities[0] = plug with { State = "100", LastChanged = Clock.GetUtcNow() };
        var dip = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(0, dip.Resolved);
        Assert.Equal(AnomalyStatus.Open, (await Store.GetAnomalyAsync(open.Id, CancellationToken.None))!.Status);

        // Back out again, and long enough to re-qualify: the excursion is dated from after the dip, so the
        // detector really does fire, and it refreshes the card that is already there rather than opening a
        // second one. Asserting on the refreshed sentence is what tells those two apart.
        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(5));
            _ha.Entities[0] = plug with { State = "160", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        var same = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, false, CancellationToken.None));
        Assert.Equal(open.Id, same.Id);
        Assert.Equal(detected, same.DetectedUtc);
        Assert.Contains("and has for 10 minutes", same.Summary);

        // Back inside for good: closed once it has stayed there for the wait, not on the first reading.
        var closedOn = -1;
        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(5));
            _ha.Entities[0] = plug with { State = "100", LastChanged = Clock.GetUtcNow() };
            if ((await scanner.ScanAsync(CancellationToken.None)).Resolved > 0) { closedOn = i; break; }
        }

        Assert.InRange(closedOn, 1, 3);
        Assert.Equal(AnomalyStatus.Resolved, (await Store.GetAnomalyAsync(open.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_dismissed_finding_needs_to_be_further_over_the_line_to_come_back_and_three_dismissals_silence_it()
    {
        var options = Options("binary_sensor.*");
        var scanner = Scanner(options);

        var dismissed = await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = "stuck:binary_sensor.shed_door",
            EntityId = "binary_sensor.shed_door",
            Kind = AnomalyKind.StuckState,
            Summary = "open too long",
            SuggestedRequest = "notify me",
            Status = AnomalyStatus.Dismissed,
            Severity = 1.2,
            Dismissals = 1,
            DetectedUtc = Clock.GetUtcNow().AddDays(-9),
            DecidedUtc = Clock.GetUtcNow().AddDays(-8),
        }, CancellationToken.None);

        // Barely over the bar again, past the quiet period: stays dismissed.
        Assert.False(AnomalyScanner.ClearsTheDismissals(dismissed with { Severity = 1.2 }, dismissed));
        // Half a doubling over: comes back.
        Assert.True(AnomalyScanner.ClearsTheDismissals(dismissed with { Severity = 1.5 }, dismissed));
        // Dismissed twice: a full doubling.
        Assert.False(AnomalyScanner.ClearsTheDismissals(dismissed with { Severity = 1.9 }, dismissed with { Dismissals = 2 }));
        Assert.True(AnomalyScanner.ClearsTheDismissals(dismissed with { Severity = 2.0 }, dismissed with { Dismissals = 2 }));
        // Three: never, however far over.
        Assert.False(AnomalyScanner.ClearsTheDismissals(dismissed with { Severity = 8 }, dismissed with { Dismissals = 3 }));

        // And a silenced finding is not pruned, so the silence holds.
        await Store.UpdateAnomalyAsync(dismissed with { Dismissals = 3 }, CancellationToken.None);
        await Store.UpsertAnomalyAsync(dismissed with { DedupKey = "stuck:binary_sensor.other", EntityId = "binary_sensor.other", Dismissals = 1 }, CancellationToken.None);
        Assert.Equal(1, await Store.PruneAnomaliesAsync(Clock.GetUtcNow().AddDays(-1), CancellationToken.None));
        Assert.NotNull(await Store.FindAnomalyAsync("stuck:binary_sensor.shed_door", CancellationToken.None));
        Assert.Null(await Store.FindAnomalyAsync("stuck:binary_sensor.other", CancellationToken.None));

        _ = scanner;
    }

    [Fact]
    public async Task A_routine_put_away_survives_the_prune_and_stays_away()
    {
        await SeedRoutineAsync();
        var scanner = Scanner(Options("light.pantry", "binary_sensor.pantry_motion"));
        await scanner.ScanAsync(CancellationToken.None);

        var routine = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None), a => a.DedupKey == RoutineKey);
        await Store.UpdateAnomalyAsync(routine with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow(), Dismissals = 1 }, CancellationToken.None);

        // A prune whose cutoff is long past the decision keeps the put-away routine.
        Assert.Equal(0, await Store.PruneAnomaliesAsync(Clock.GetUtcNow().AddDays(1), CancellationToken.None));
        Assert.Equal(AnomalyStatus.Dismissed, (await Store.FindAnomalyAsync(RoutineKey, CancellationToken.None))!.Status);

        // And an hour later, when the search runs again, it is neither raised nor counted as an offer; the
        // one routine still on offer is the clock one for switching off.
        Clock.Advance(TimeSpan.FromMinutes(61));
        var report = await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(0, report.Routines);
        Assert.Equal(AnomalyStatus.Dismissed, (await Store.FindAnomalyAsync(RoutineKey, CancellationToken.None))!.Status);
        Assert.Equal(2, scanner.LastRoutineSearch!.Found);
        Assert.Equal(1, scanner.LastRoutineSearch.Offered);
    }

    [Fact]
    public async Task A_routine_whose_cue_leaves_the_watch_list_closes_with_that_reason()
    {
        await SeedRoutineAsync();
        var options = Options("light.pantry", "binary_sensor.pantry_motion");
        var scanner = Scanner(options);
        await scanner.ScanAsync(CancellationToken.None);
        var routine = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None), a => a.DedupKey == RoutineKey);

        options.Scan.Exclude = ["binary_sensor.pantry_motion"];
        Clock.Advance(TimeSpan.FromMinutes(61));
        await scanner.ScanAsync(CancellationToken.None);

        var closed = (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Contains("no longer on the watch list", closed.EvidenceJson);
    }

    [Fact]
    public async Task A_promoted_routine_closes_once_its_automation_exists()
    {
        await SeedRoutineAsync();
        var scanner = Scanner(Options("light.pantry", "binary_sensor.pantry_motion"));
        await scanner.ScanAsync(CancellationToken.None);
        var routine = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None), a => a.DedupKey == RoutineKey);
        await Store.UpdateAnomalyAsync(routine with { Status = AnomalyStatus.Promoted, ProposalId = 42, DecidedUtc = Clock.GetUtcNow() }, CancellationToken.None);

        _ha.Automations.Add(new ExistingAutomation("1", "automation.pantry", "Pantry",
            new HashSet<string>(StringComparer.Ordinal) { "light.pantry", "binary_sensor.pantry_motion" },
            new HashSet<string>(StringComparer.Ordinal) { "state" }));
        Clock.Advance(TimeSpan.FromMinutes(61));
        await scanner.ScanAsync(CancellationToken.None);

        var closed = (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Equal(42, closed.ProposalId);
        Assert.Contains("An automation now does this.", closed.EvidenceJson);
    }

    [Fact]
    public async Task A_scan_that_lists_no_automations_on_a_house_that_had_them_does_not_search()
    {
        await SeedRoutineAsync();
        _ha.Entities.Add(Build.Entity("automation.something", "on", Clock.GetUtcNow(), automationConfigId: "1"));
        _ha.Automations.Add(new ExistingAutomation("1", "automation.something", "Something",
            new HashSet<string>(StringComparer.Ordinal) { "light.pantry", "binary_sensor.pantry_motion" },
            new HashSet<string>(StringComparer.Ordinal) { "state" }));
        var scanner = Scanner(Options("light.pantry", "binary_sensor.pantry_motion"));

        await scanner.ScanAsync(CancellationToken.None);
        Assert.Null(await Store.FindAnomalyAsync(RoutineKey, CancellationToken.None));

        // Home Assistant restarts: the automation integration is not in the state list for a moment.
        _ha.Entities.RemoveAll(entity => entity.Domain == "automation");
        _ha.Automations.Clear();
        Clock.Advance(TimeSpan.FromMinutes(61));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Null(await Store.FindAnomalyAsync(RoutineKey, CancellationToken.None));
        Assert.Contains("no automations", scanner.LastRoutineSearch!.Skipped);
    }

    [Fact]
    public async Task The_sun_is_watched_whatever_the_include_list_says_and_the_cap_keeps_what_routines_are_made_of()
    {
        var now = Clock.GetUtcNow();
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["light.*"];
        options.Scan.MaxTrackedEntities = 3;
        options.Scan.BackfillFromRecorder = false;

        // Two lights, the sun, and a hundred automations that sort before every one of them.
        _ha.Entities.Add(Build.Entity("light.zz_hall", "off", now));
        _ha.Entities.Add(Build.Entity("light.zz_porch", "off", now));
        _ha.Entities.Add(Build.Entity(Habits.Sun, "above_horizon", now));
        for (var i = 0; i < 100; i++) _ha.Entities.Add(Build.Entity($"automation.a{i:000}", "on", now));

        var watched = EntityIndex.Filter(_ha.Entities, options.Scan);

        Assert.Equal([Habits.Sun, "light.zz_hall", "light.zz_porch"], watched.Select(entity => entity.EntityId));

        // With everything included and a cap of three, the same three win over the hundred automations.
        options.Scan.IncludeAll = true;
        Assert.Equal([Habits.Sun, "light.zz_hall", "light.zz_porch"], EntityIndex.Filter(_ha.Entities, options.Scan).Select(entity => entity.EntityId));

        // Excluded by name is excluded.
        options.Scan.Exclude = [Habits.Sun];
        Assert.DoesNotContain(EntityIndex.Filter(_ha.Entities, options.Scan), entity => entity.EntityId == Habits.Sun);
    }
}

public class ExcursionAtScanDensityTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 2, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    private static StateSample Reading(double value, DateTimeOffset at) => new(Ha.Number(value), value, at);

    private static List<StateSample> Baseline(DateTimeOffset until)
    {
        var step = TimeSpan.FromMinutes(20);
        var start = until - step * 40;
        return [.. Enumerable.Range(0, 40).Select(i => Reading(100 + (((i % 4) - 1.5) * 1.0), start + step * i))];
    }

    private static HaEntity Plug(double reading, DateTimeOffset changed) =>
        Build.Entity("sensor.plug_power", Ha.Number(reading), changed, deviceClass: "power", unit: "W");

    [Fact]
    public void Two_kettles_ten_minutes_apart_with_a_normal_reading_between_are_not_one_excursion()
    {
        // One stored reading per five-minute scan, which is all the density numeric history has.
        var baseline = Baseline(Now.AddMinutes(-30));
        var recent = new List<StateSample>(baseline) { Reading(160, Now.AddMinutes(-10)), Reading(100, Now.AddMinutes(-5)), Reading(160, Now) };

        Assert.Null(AnomalyDetection.DetectNumericOutlier(Plug(160, Now), new EntityHistory(recent, baseline), Options, Now));

        // Whereas four scans out is a fifteen-minute excursion.
        var sustained = new List<StateSample>(baseline);
        for (var minute = 15; minute >= 0; minute -= 5) sustained.Add(Reading(160, Now.AddMinutes(-minute)));
        var finding = AnomalyDetection.DetectNumericOutlier(Plug(160, Now), new EntityHistory(sustained, baseline), Options, Now);
        Assert.NotNull(finding);
        Assert.Contains("\"excursion_seconds\":900", finding.EvidenceJson);
    }

    [Fact]
    public void A_chatty_sensors_fresh_spike_is_not_dated_to_an_excursion_hours_ago()
    {
        // The thinned history keeps two readings an hour; six hours ago the dishwasher ran and both of that
        // hour's rows are beyond. A four-minute spike now must not be dated to it.
        var baseline = Baseline(Now.AddHours(-7));
        baseline.Add(Reading(160, Now.AddHours(-6).AddMinutes(-2)));
        baseline.Add(Reading(160, Now.AddHours(-6)));
        for (var hour = 5; hour >= 1; hour--)
        {
            baseline.Add(Reading(100, Now.AddHours(-hour).AddMinutes(-30)));
            baseline.Add(Reading(100, Now.AddHours(-hour)));
        }

        var recent = new List<StateSample>(baseline);
        for (var second = 240; second >= 0; second -= 2) recent.Add(Reading(160, Now.AddSeconds(-second)));

        Assert.Null(AnomalyDetection.DetectNumericOutlier(Plug(160, Now), new EntityHistory(recent, baseline), Options, Now));
    }

    [Fact]
    public void Back_inside_needs_the_same_wait_as_going_out()
    {
        var baseline = Baseline(Now.AddMinutes(-40));
        var recent = new List<StateSample>(baseline);
        for (var minute = 35; minute >= 10; minute -= 5) recent.Add(Reading(160, Now.AddMinutes(-minute)));

        // One reading back at a hundred, five minutes ago, and the current one: not yet.
        recent.Add(Reading(100, Now.AddMinutes(-5)));
        recent.Add(Reading(100, Now));
        Assert.False(AnomalyDetection.BackInside(Plug(100, Now), new EntityHistory(recent, baseline), Options, Now));

        // Ten minutes of them: back.
        var settled = new List<StateSample>(baseline);
        for (var minute = 35; minute >= 15; minute -= 5) settled.Add(Reading(160, Now.AddMinutes(-minute)));
        for (var minute = 10; minute >= 0; minute -= 5) settled.Add(Reading(100, Now.AddMinutes(-minute)));
        Assert.True(AnomalyDetection.BackInside(Plug(100, Now), new EntityHistory(settled, baseline), Options, Now));

        // Still out on the other side is not back.
        Assert.False(AnomalyDetection.BackInside(Plug(40, Now), new EntityHistory(settled, baseline), Options, Now));
    }
}

public class SamplesForBatchingTests : StoreFixture
{
    [Fact]
    public async Task More_ids_than_one_query_names_are_read_in_batches()
    {
        var now = Clock.GetUtcNow();
        List<(string, StateSample)> samples = [];
        var ids = Enumerable.Range(0, 450).Select(i => $"light.l{i:000}").ToList();
        foreach (var id in ids) samples.Add((id, new StateSample("on", null, now.AddMinutes(-1))));
        await Store.AddSamplesAsync(samples, CancellationToken.None);

        var found = await Store.GetSamplesForAsync(ids, now.AddDays(-1), 10, CancellationToken.None);

        Assert.Equal(450, found.Count);
        Assert.All(ids, id => Assert.Single(found[id]));
    }
}

public class TimeZoneReadTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, Task<string>> Body { get; set; } = _ => Task.FromResult("{}");
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return new HttpResponseMessage(Status) { Content = new StringContent(await Body(request), Encoding.UTF8, "application/json") };
        }
    }

    private static (HomeAssistantClient Client, StubHandler Handler, FakeTimeProvider Clock, FakeSettings Settings) Make()
    {
        var handler = new StubHandler();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var settings = new FakeSettings();
        settings.Current.HomeAssistant.BaseUrl = "http://ha.test:8123";
        var secrets = new SecretStore(Path.Combine(Path.GetTempPath(), $"hs-tz-test-{Guid.NewGuid():N}.json"), NullLogger<SecretStore>.Instance);
        return (new HomeAssistantClient(new HttpClient(handler), settings, secrets, clock, NullLogger<HomeAssistantClient>.Instance), handler, clock, settings);
    }

    [Fact]
    public async Task The_time_zone_is_read_from_the_config_and_held_for_hours()
    {
        var (client, handler, clock, _) = Make();
        handler.Body = _ => Task.FromResult("""{"time_zone":"America/Regina","elevation":10}""");

        Assert.Equal("America/Regina", await client.GetTimeZoneAsync(CancellationToken.None));
        Assert.Equal("America/Regina", await client.GetTimeZoneAsync(CancellationToken.None));
        Assert.Equal(1, handler.Requests.Count(request => request == "GET /api/config"));

        clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal("America/Regina", await client.GetTimeZoneAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count(request => request == "GET /api/config"));
    }

    [Fact]
    public async Task A_failed_read_is_not_held_and_a_new_address_is_asked_afresh()
    {
        var (client, handler, _, settings) = Make();
        handler.Body = _ => throw new HttpRequestException("down");
        Assert.Null(await client.GetTimeZoneAsync(CancellationToken.None));

        // Back the next moment: asked again at once, not stood on for six hours.
        handler.Body = _ => Task.FromResult("""{"time_zone":"Europe/London"}""");
        Assert.Equal("Europe/London", await client.GetTimeZoneAsync(CancellationToken.None));

        // Pointed elsewhere: the old answer does not carry over.
        settings.Current.HomeAssistant.BaseUrl = "http://other.test:8123";
        handler.Body = _ => Task.FromResult("""{"time_zone":"Pacific/Auckland"}""");
        Assert.Equal("Pacific/Auckland", await client.GetTimeZoneAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_config_that_answers_with_an_error_yields_null_and_is_not_held()
    {
        var (client, handler, _, _) = Make();
        handler.Status = HttpStatusCode.BadGateway;
        handler.Body = _ => Task.FromResult("""{"time_zone":"America/Regina"}""");

        Assert.Null(await client.GetTimeZoneAsync(CancellationToken.None));

        // Not cached: the very next call asks again and takes the good answer.
        handler.Status = HttpStatusCode.OK;
        Assert.Equal("America/Regina", await client.GetTimeZoneAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count(request => request == "GET /api/config"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"elevation":10}""")]
    public async Task A_config_without_a_time_zone_yields_null(string body)
    {
        var (client, handler, _, _) = Make();
        handler.Body = _ => Task.FromResult(body);
        Assert.Null(await client.GetTimeZoneAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Living Room", "living_room")]
    [InlineData("Kids' Room #2", "kids_room_2")]
    [InlineData("  Hall  ", "hall")]
    public void Area_names_slug_the_way_Home_Assistant_makes_ids(string name, string expected) =>
        Assert.Equal(expected, HomeAssistantClient.Slug(name));
}

[Collection("api")]
public class RoutineApiTests
{
    private readonly TestApp app;
    private readonly HttpClient _client;

    public RoutineApiTests(TestApp app)
    {
        this.app = app;
        _client = app.CreateClient();

        // Seeded here as well as in ApiTests: which class runs first is not promised.
        if (app.HomeAssistant.Entities.Count == 0)
            app.HomeAssistant.Entities.AddRange([
                Build.Entity("light.hall", friendlyName: "Hall Light"),
                Build.Entity("person.sam", "home", friendlyName: "Sam"),
            ]);
    }

    [Fact]
    public async Task The_summary_counts_routines_apart_and_never_as_serious()
    {
        var store = app.Services.GetRequiredService<IStore>();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var before = await _client.GetFromJsonAsync<JsonElement>("/api/anomalies/summary");

        await store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = $"habit:test:{mark}",
            EntityId = $"light.{mark}",
            Kind = AnomalyKind.Habit,
            Summary = "You usually turn on the light.",
            SuggestedRequest = $"Turn on light.{mark} when binary_sensor.{mark} detects movement.",
            EvidenceJson = $$"""{"spoken":"Turn on the {{mark}} light when the {{mark}} sensor detects movement."}""",
            Status = AnomalyStatus.Open,
            Severity = 5,
            DetectedUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = $"stuck:test:{mark}",
            EntityId = $"binary_sensor.{mark}",
            Kind = AnomalyKind.StuckState,
            Summary = "open too long",
            SuggestedRequest = "notify me",
            Status = AnomalyStatus.Open,
            Severity = 1,
            DetectedUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var after = await _client.GetFromJsonAsync<JsonElement>("/api/anomalies/summary");

        Assert.Equal(before.GetProperty("open").GetInt32() + 1, after.GetProperty("open").GetInt32());
        Assert.Equal(before.GetProperty("habits").GetInt32() + 1, after.GetProperty("habits").GetInt32());
        Assert.Equal(before.GetProperty("serious").GetInt32(), after.GetProperty("serious").GetInt32());

        // The composer's examples lead with the routine, in its spoken wording.
        var suggestions = await _client.GetFromJsonAsync<JsonElement>("/api/suggestions");
        Assert.Contains($"Turn on the {mark} light when the {mark} sensor detects movement.",
            suggestions.GetProperty("suggestions").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task Dismissing_counts_and_says_what_it_did()
    {
        var store = app.Services.GetRequiredService<IStore>();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var seeded = await store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = $"outlier:test:{mark}",
            EntityId = $"sensor.{mark}",
            Kind = AnomalyKind.NumericOutlier,
            Summary = "reads high",
            SuggestedRequest = "notify me",
            Status = AnomalyStatus.Open,
            Dismissals = 2,
            DetectedUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var response = await _client.PostAsync($"/api/anomalies/{seeded.Id}/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, body.GetProperty("dismissals").GetInt32());
        Assert.True(body.GetProperty("silenced").GetBoolean());
        Assert.Contains("third time", body.GetProperty("note").GetString());
        Assert.Equal(3, (await store.GetAnomalyAsync(seeded.Id, CancellationToken.None))!.Dismissals);
    }

    [Fact]
    public async Task A_concern_can_be_read_again_and_a_missing_one_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/concerns/999999/reread", null)).StatusCode);

        app.Llm.Clear();
        app.Llm.Response = null;
        var added = await (await _client.PostAsJsonAsync("/api/concerns", new { text = "the hall light left on all night" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(added.GetProperty("provisional").GetBoolean());

        app.Llm.Response = """{"entity_ids":["light.hall"],"kind":"held","state":"on","for_minutes":120,"explanation":"Watching the hall light for staying on two hours."}""";
        var read = await (await _client.PostAsync($"/api/concerns/{added.GetProperty("id").GetInt64()}/reread", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(read.GetProperty("provisional").GetBoolean());
        Assert.True(read.GetProperty("hasRule").GetBoolean());
        Assert.True(read.GetProperty("interpreted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("note").ValueKind);

        await _client.DeleteAsync($"/api/concerns/{added.GetProperty("id").GetInt64()}");
    }
}

/// <summary>
/// The rules that only show themselves at scale or through the client: the cap on how many routines are
/// offered at once, and an automation that names an area or a device rather than entities.
/// </summary>
public class OfferCapTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    /// <summary>Sixteen rooms, each with a light that follows its own motion sensor every evening and goes off at ten.</summary>
    private async Task<HousekeeperOptions> SeedRoomsAsync(int rooms)
    {
        var now = Clock.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-9);

        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.BackfillFromRecorder = false;
        options.Scan.Include = ["light.*", "binary_sensor.*"];

        List<(string EntityId, StateSample Sample)> samples = [];
        void Add(string id, DateTimeOffset at, string state) => samples.Add((id, new StateSample(state, null, at)));

        for (var room = 0; room < rooms; room++)
        {
            var light = $"light.room_{room:00}";
            var motion = $"binary_sensor.room_{room:00}_motion";
            _ha.Entities.Add(Build.Entity(light, "off", now.AddHours(-14), friendlyName: $"Room {room} light", area: $"Room {room}"));
            _ha.Entities.Add(Build.Entity(motion, "off", now.AddHours(-1), friendlyName: $"Room {room} motion", deviceClass: "motion", area: $"Room {room}"));

            Add(light, start, "off");
            Add(motion, start, "off");
            for (var day = 0; day < 9; day++)
            {
                var d = start.AddDays(day);
                Add(motion, d.AddHours(19), "on");
                Add(light, d.AddHours(19).AddSeconds(20 + room), "on");
                Add(motion, d.AddHours(19).AddMinutes(5), "off");
                Add(light, d.AddHours(22).AddMinutes((day % 2 == 0 ? -2 : 2) + room), "off");
            }
        }

        await Store.AddSamplesAsync(samples, CancellationToken.None);
        return options;
    }

    [Fact]
    public async Task More_routines_than_the_cap_are_offered_up_to_it_and_the_rest_are_left_alone()
    {
        var options = await SeedRoomsAsync(16);
        var scanner = Scanner(options);

        await scanner.ScanAsync(CancellationToken.None);

        var search = scanner.LastRoutineSearch!;
        Assert.True(search.Found > Habits.MostOffered, $"only {search.Found} routines held up");
        Assert.Equal(Habits.MostOffered, search.Offered);

        var open = await Store.ListAnomaliesAsync(AnomalyStatus.Open, 200, false, CancellationToken.None);
        Assert.Equal(Habits.MostOffered, open.Count(finding => finding.Kind == AnomalyKind.Habit));

        // Nothing that held up was closed for not holding up: the ones past the cap are simply not shown.
        Assert.Empty((await Store.ListAnomaliesAsync(AnomalyStatus.Resolved, 200, true, CancellationToken.None))
            .Where(finding => finding.Kind == AnomalyKind.Habit));

        // Put one away and the next one takes its place, rather than the slot staying spent for ever.
        var offered = open.First(finding => finding.Kind == AnomalyKind.Habit);
        await Store.UpdateAnomalyAsync(
            offered with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow(), Dismissals = 1 },
            CancellationToken.None);

        Clock.Advance(TimeSpan.FromMinutes(61));
        await scanner.ScanAsync(CancellationToken.None);

        var after = await Store.ListAnomaliesAsync(AnomalyStatus.Open, 200, false, CancellationToken.None);
        Assert.Equal(Habits.MostOffered, after.Count(finding => finding.Kind == AnomalyKind.Habit));
        Assert.DoesNotContain(after, finding => finding.DedupKey == offered.DedupKey);
    }
}

public class AutomationReachTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<string>> Body { get; set; } = _ => Task.FromResult("{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await Body(request), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task An_automation_that_targets_an_area_or_a_device_covers_the_entities_in_them()
    {
        var handler = new StubHandler
        {
            Body = _ => Task.FromResult("""
                {"alias":"Editor built","triggers":[{"trigger":"device","device_id":"dev1","domain":"binary_sensor","type":"motion"}],
                 "actions":[{"action":"light.turn_off","target":{"area_id":"living_room"}}]}
                """),
        };

        var settings = new FakeSettings();
        settings.Current.HomeAssistant.BaseUrl = "http://ha.test:8123";
        var secrets = new SecretStore(Path.Combine(Path.GetTempPath(), $"hs-reach-{Guid.NewGuid():N}.json"), NullLogger<SecretStore>.Instance);
        var client = new HomeAssistantClient(
            new HttpClient(handler), settings, secrets,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<HomeAssistantClient>.Instance);

        List<HaEntity> entities =
        [
            Build.Entity("automation.built", "on", automationConfigId: "1"),
            // The area was renamed after it was created, so only its registry id matches.
            Build.Entity("light.lamp", "off", area: "Master living room", areaId: "living_room"),
            Build.Entity("binary_sensor.hall_motion", "off", deviceId: "dev1", deviceClass: "motion"),
            Build.Entity("light.elsewhere", "off", area: "Study", areaId: "study"),
        ];

        var automations = await client.GetAutomationsAsync(entities, CancellationToken.None);

        var reach = Assert.Single(automations).Entities;
        Assert.Contains("light.lamp", reach);
        Assert.Contains("binary_sensor.hall_motion", reach);
        Assert.DoesNotContain("light.elsewhere", reach);
    }
}
