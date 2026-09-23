using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

/// <summary>
/// Routines are the scanner learning how the house is used rather than judging what it does. The bar is
/// three-sided -- how many times, over how many days, how reliably -- and each side is tested on its own,
/// along with the two things that would make a routine wrong to offer: a cue that is really the same
/// entity seen twice, and an automation that already does it.
/// </summary>
public class HabitsTests
{
    /// <summary>A Tuesday, so three weeks from here hold fifteen weekdays and six weekend days.</summary>
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute, int second = 0) =>
        Start.AddDays(day).AddHours(hour).AddMinutes(minute).AddSeconds(second);

    private static ScanOptions Options => new();

    private static readonly HaEntity Light = Build.Entity("light.pantry", "off", Start, friendlyName: "Pantry light", area: "Pantry");
    private static readonly HaEntity Motion = Build.Entity("binary_sensor.pantry_motion", "off", Start, friendlyName: "Pantry motion sensor", deviceClass: "motion", area: "Pantry");
    private static readonly HaEntity Sun = Build.Entity(Habits.Sun, "below_horizon", Start);

    private sealed class Diary
    {
        private readonly Dictionary<string, List<StateSample>> _samples = new(StringComparer.Ordinal);

        public Diary Add(string entityId, DateTimeOffset at, string state)
        {
            if (!_samples.TryGetValue(entityId, out var list)) _samples[entityId] = list = [];
            list.Add(new StateSample(state, null, at));
            return this;
        }

        /// <summary>Sunrise at half past six, sunset at half past six, every day.</summary>
        public Diary WithSun(int days)
        {
            Add(Habits.Sun, Start, "below_horizon");
            for (var day = 0; day < days; day++)
            {
                Add(Habits.Sun, At(day, 6, 30), "above_horizon");
                Add(Habits.Sun, At(day, 18, 30), "below_horizon");
            }

            return this;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<StateSample>> Samples =>
            _samples.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<StateSample>)[.. pair.Value.OrderBy(s => s.ChangedUtc)], StringComparer.Ordinal);
    }

    /// <summary>Eight evenings of the light following the motion sensor, and eight middays of motion alone.</summary>
    private static Diary PantryEvenings(int days = 8, bool daytimeMotion = true)
    {
        var diary = new Diary().WithSun(days);
        diary.Add(Light.EntityId, Start, "off").Add(Motion.EntityId, Start, "off");

        for (var day = 0; day < days; day++)
        {
            if (daytimeMotion)
            {
                diary.Add(Motion.EntityId, At(day, 12, 0), "on");
                diary.Add(Motion.EntityId, At(day, 12, 5), "off");
            }

            diary.Add(Motion.EntityId, At(day, 19, 0), "on");
            diary.Add(Light.EntityId, At(day, 19, 0, 20), "on");
            diary.Add(Motion.EntityId, At(day, 19, 5), "off");
            // A couple of minutes either side of ten: a person, not a timer.
            diary.Add(Light.EntityId, At(day, 22, 0).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        return diary;
    }

    private static Habits.Report Find(Diary diary, IReadOnlyList<HaEntity>? entities = null, IReadOnlyList<ExistingAutomation>? automations = null, int days = 8) =>
        Habits.Find(entities ?? [Light, Motion, Sun], diary.Samples, automations ?? [], TimeZoneInfo.Utc, Options, At(days, 12, 0));

    [Fact]
    public void A_light_that_follows_the_motion_sensor_after_dark_is_offered_with_that_condition()
    {
        var report = Find(PantryEvenings());

        var cue = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");
        Assert.Equal(AnomalyKind.Habit, cue.Kind);
        Assert.Equal("light.pantry", cue.EntityId);
        Assert.Equal("Turn on light.pantry when binary_sensor.pantry_motion detects movement when it is dark.", cue.SuggestedRequest);
        Assert.Contains("You usually turn on the Pantry light when the Pantry motion sensor detects movement after dark.", cue.Summary);
        Assert.Contains("while the Pantry light is off, you turn it on within about 20 seconds 100% of the time. That is 8 of the 8 times you turn it on at all.", cue.Summary);
        Assert.Contains("\"condition\":\"dark\"", cue.EvidenceJson);
        Assert.Contains("\"times\":8", cue.EvidenceJson);
        Assert.Contains("\"days\":8", cue.EvidenceJson);
        Assert.Contains("\"spoken\":\"Turn on the Pantry light when the Pantry motion sensor detects movement when it is dark.\"", cue.EvidenceJson);

        // The light going on at seven every evening is explained by the motion sensor, so no clock routine
        // is offered for it; going off at ten is not, so one is.
        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:clock");
        var clock = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.pantry:off:clock");
        Assert.Equal("Turn off light.pantry at 22:00.", clock.SuggestedRequest);
        Assert.Contains("You usually turn off the Pantry light at about 22:00. On 8 of the last 9 days, between 21:58 and 22:02.", clock.Summary);

        Assert.Empty(report.Automated);
        Assert.Empty(report.MachineMade);
    }

    [Fact]
    public void Motion_while_the_light_is_already_on_is_neither_a_hit_nor_a_miss()
    {
        var diary = PantryEvenings(daytimeMotion: false);
        for (var day = 0; day < 8; day++)
            foreach (var hour in new[] { 19, 20, 21 })
            {
                diary.Add(Motion.EntityId, At(day, hour, 30), "on");
                diary.Add(Motion.EntityId, At(day, hour, 35), "off");
            }

        var cue = Assert.Single(Find(diary).Found, habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");

        // Twenty-four extra firings, all with the light already on: the plainest condition holds perfectly.
        Assert.Contains("\"condition\":\"always\"", cue.EvidenceJson);
        Assert.Contains("\"out_of\":8", cue.EvidenceJson);
        Assert.Contains("\"share\":1", cue.EvidenceJson);
        Assert.Equal("Turn on light.pantry when binary_sensor.pantry_motion detects movement.", cue.SuggestedRequest);
    }

    [Fact]
    public void Two_entities_that_move_as_one_are_the_same_thing_seen_twice()
    {
        var twin = Build.Entity("switch.pantry", "off", Start, friendlyName: "Pantry switch");
        var diary = PantryEvenings(daytimeMotion: false);
        for (var day = 0; day < 8; day++)
        {
            diary.Add(twin.EntityId, At(day, 19, 0, 20), "on");
            diary.Add(twin.EntityId, At(day, 22, 0), "off");
        }

        var report = Find(diary, [Light, Motion, Sun, twin]);

        // The switch is dropped altogether, as an effect and as a cue: the light is the thing.
        Assert.DoesNotContain(report.Found, habit => habit.EntityId == "switch.pantry" || habit.DedupKey.Contains("switch.pantry"));
        Assert.Contains(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");
    }

    [Fact]
    public void A_cue_from_another_room_needs_twice_the_evidence()
    {
        var hallMotion = Motion with { Area = "Hall" };

        // Eight evenings clears the bar for a cue in the same room, not for one elsewhere.
        Assert.DoesNotContain(Find(PantryEvenings(), [Light, hallMotion, Sun]).Found,
            habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");

        // Twelve does, at a hundred percent.
        var cue = Assert.Single(Find(PantryEvenings(days: 12), [Light, hallMotion, Sun], days: 12).Found,
            habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");
        Assert.Contains("\"times\":12", cue.EvidenceJson);
    }

    [Fact]
    public void Only_the_strongest_and_most_immediate_cue_is_offered_for_one_thing()
    {
        var door = Build.Entity("binary_sensor.pantry_door", "off", Start, friendlyName: "Pantry door", deviceClass: "door", area: "Pantry");
        var diary = PantryEvenings();
        for (var day = 0; day < 8; day++)
        {
            // The door opens a minute before the motion sensor fires, every evening: as reliable, less immediate.
            diary.Add(door.EntityId, At(day, 18, 59), "on");
            diary.Add(door.EntityId, At(day, 19, 10), "off");
        }

        var report = Find(diary, [Light, Motion, Sun, door]);

        var offered = report.Found.Where(habit => habit.DedupKey.StartsWith("habit:light.pantry:on:", StringComparison.Ordinal)).ToList();
        var only = Assert.Single(offered);
        Assert.Equal("habit:light.pantry:on:binary_sensor.pantry_motion:on", only.DedupKey);
        Assert.Contains("while the Pantry light is off, you turn it on within about 20 seconds 100% of the time.", only.Summary);
    }

    [Fact]
    public void Something_done_at_the_same_time_on_weekdays_is_a_clock_routine_for_weekdays()
    {
        var kitchen = Build.Entity("light.kitchen", "off", Start, friendlyName: "Kitchen light");
        var diary = new Diary().Add(kitchen.EntityId, Start, "off");

        for (var day = 0; day < 21; day++)
        {
            if (Start.AddDays(day).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            diary.Add(kitchen.EntityId, At(day, 6, 40 + (day % 3) * 5), "on");
            diary.Add(kitchen.EntityId, At(day, 7, 30), "off");
        }

        var report = Find(diary, [kitchen], days: 21);

        var morning = Assert.Single(report.Found, habit => habit.DedupKey == "habit:light.kitchen:on:clock");
        Assert.Equal("Turn on light.kitchen at 06:45 on weekdays.", morning.SuggestedRequest);
        Assert.Contains("\"part\":\"weekdays\"", morning.EvidenceJson);
        // Twenty-two calendar days from a Tuesday to the Tuesday three weeks on hold sixteen weekdays; the
        // last is today, which has not had its morning yet.
        Assert.Contains("\"days\":15", morning.EvidenceJson);
        Assert.Contains("\"out_of\":16", morning.EvidenceJson);
        Assert.Contains("On 15 of the last 16 weekdays, between 06:40 and 06:50.", morning.Summary);
    }

    [Fact]
    public void What_an_automation_already_does_is_left_out_and_reported_as_such()
    {
        var existing = new ExistingAutomation("1", "automation.pantry", "Pantry light on motion",
            new HashSet<string>(StringComparer.Ordinal) { Light.EntityId, Motion.EntityId },
            new HashSet<string>(StringComparer.Ordinal) { "state" },
            []);

        var report = Find(PantryEvenings(), automations: [existing]);

        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");
        Assert.Contains("habit:light.pantry:on:binary_sensor.pantry_motion:on", report.Automated);

        // The automation triggers on state, so the clock routine for switching off is still on offer -- but
        // not one for switching on at seven: the motion sensor explains that, automated or not.
        Assert.Contains(report.Found, habit => habit.DedupKey == "habit:light.pantry:off:clock");
        Assert.DoesNotContain(report.Found, habit => habit.DedupKey == "habit:light.pantry:on:clock");
    }

    [Fact]
    public void Enough_times_on_too_few_days_is_not_a_routine()
    {
        var diary = new Diary().WithSun(3).Add(Light.EntityId, Start, "off").Add(Motion.EntityId, Start, "off");
        for (var day = 0; day < 2; day++)
            foreach (var hour in new[] { 19, 20, 21 })
            {
                diary.Add(Motion.EntityId, At(day, hour, 0), "on");
                diary.Add(Light.EntityId, At(day, hour, 0, 15), "on");
                diary.Add(Motion.EntityId, At(day, hour, 5), "off");
                diary.Add(Light.EntityId, At(day, hour, 30), "off");
            }

        Assert.Empty(Find(diary, days: 3).Found);
    }

    [Fact]
    public void Coming_back_from_unavailable_is_not_a_person_doing_anything()
    {
        var diary = PantryEvenings();
        // A device that drops and returns "on" every evening would otherwise be eight transitions to on.
        var flaky = Build.Entity("switch.heater", "off", Start, friendlyName: "Heater");
        diary.Add(flaky.EntityId, Start, "off");
        for (var day = 0; day < 8; day++)
        {
            diary.Add(flaky.EntityId, At(day, 18, 59), "unavailable");
            diary.Add(flaky.EntityId, At(day, 19, 0, 10), "on");
            diary.Add(flaky.EntityId, At(day, 23, 0), "off");
        }

        var report = Find(diary, [Light, Motion, Sun, flaky]);

        Assert.DoesNotContain(report.Found, habit => habit.DedupKey.StartsWith("habit:switch.heater:on", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_switches_and_device_readings_are_never_routines()
    {
        var led = Build.Entity("switch.plug_led_indicator", "off", Start, friendlyName: "Plug LED", entityCategory: "config");
        var battery = Build.Entity("binary_sensor.door_battery", "off", Start, deviceClass: "battery");

        Assert.False(Habits.IsEffect(led));
        Assert.False(Habits.IsCue(battery));
        Assert.True(Habits.IsCue(Motion));
        Assert.True(Habits.IsEffect(Light));
        Assert.True(Habits.IsCue(Light));
        Assert.Equal([Light.EntityId, Motion.EntityId, Habits.Sun], Habits.Candidates([Light, Motion, Sun, led, battery]));
    }

    [Theory]
    [InlineData(new[] { 1430, 10 }, 0)]
    [InlineData(new[] { 400, 410, 420 }, 410)]
    [InlineData(new[] { 0, 720 }, 180)]
    public void Minutes_of_the_day_average_around_the_clock(int[] minutes, int expected)
    {
        var mean = Habits.CircularMean(minutes);
        // Two opposite points have no mean; whatever comes back must at least be a minute of the day.
        if (minutes.Length == 2 && Math.Abs(minutes[0] - minutes[1]) == 720) Assert.InRange(mean, 0, 1439);
        else Assert.Equal(expected, mean);
    }

    [Theory]
    [InlineData("cover", "opening", "open")]
    [InlineData("cover", "closing", "closed")]
    [InlineData("lock", "unlocking", "unlocked")]
    [InlineData("media_player", "paused", null)]
    [InlineData("person", "work", "away")]
    [InlineData("person", "home", "home")]
    [InlineData("light", "on", "on")]
    [InlineData("climate", "heat", null)]
    public void States_are_read_as_the_thing_that_matters(string domain, string state, string? expected) =>
        Assert.Equal(expected, Habits.Canon(domain, state));
}

/// <summary>The routine search inside a scan: raised as findings, closed when an automation takes over, quiet once put away.</summary>
public class HabitScanTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options()
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["light.pantry", "binary_sensor.pantry_motion", Habits.Sun];
        options.Scan.BackfillFromRecorder = false;
        return options;
    }

    /// <summary>Nine days of the pantry light following its motion sensor every evening, ending yesterday.</summary>
    private async Task SeedAsync()
    {
        var now = Clock.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-9);

        _ha.Entities.Add(Build.Entity("light.pantry", "off", now.AddHours(-14), friendlyName: "Pantry light", area: "Pantry"));
        _ha.Entities.Add(Build.Entity("binary_sensor.pantry_motion", "off", now.AddHours(-1), friendlyName: "Pantry motion sensor", deviceClass: "motion", area: "Pantry"));
        _ha.Entities.Add(Build.Entity(Habits.Sun, "above_horizon", now.AddHours(-5)));

        List<(string EntityId, StateSample Sample)> samples = [];
        void Add(string id, DateTimeOffset at, string state) => samples.Add((id, new StateSample(state, null, at)));

        Add(Habits.Sun, start, "below_horizon");
        Add("light.pantry", start, "off");
        Add("binary_sensor.pantry_motion", start, "off");
        for (var day = 0; day < 9; day++)
        {
            var d = start.AddDays(day);
            Add(Habits.Sun, d.AddHours(6.5), "above_horizon");
            Add(Habits.Sun, d.AddHours(18.5), "below_horizon");
            Add("binary_sensor.pantry_motion", d.AddHours(12), "on");
            Add("binary_sensor.pantry_motion", d.AddHours(12).AddMinutes(5), "off");
            Add("binary_sensor.pantry_motion", d.AddHours(19), "on");
            Add("light.pantry", d.AddHours(19).AddSeconds(20), "on");
            Add("binary_sensor.pantry_motion", d.AddHours(19).AddMinutes(5), "off");
            Add("light.pantry", d.AddHours(22).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        await Store.AddSamplesAsync(samples, CancellationToken.None);
    }

    private async Task<Anomaly> RoutineAsync() =>
        Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None),
            finding => finding.DedupKey == "habit:light.pantry:on:binary_sensor.pantry_motion:on");

    [Fact]
    public async Task A_routine_is_raised_as_a_finding_and_closed_when_an_automation_takes_it_over()
    {
        await SeedAsync();
        var scanner = Scanner(Options());

        var report = await scanner.ScanAsync(CancellationToken.None);

        // Offered, not raised: a routine is an offer, and the dashboard says so apart from findings.
        Assert.True(report.Routines >= 1);
        Assert.Equal(0, report.Raised);
        var routine = await RoutineAsync();
        Assert.Equal(AnomalyKind.Habit, routine.Kind);
        Assert.Contains("after dark", routine.Summary);
        Assert.NotNull(scanner.LastRoutineSearch);
        Assert.Null(scanner.LastRoutineSearch!.Skipped);

        // An hour later the house has an automation for exactly this. The offer is withdrawn and says why.
        _ha.Automations.Add(new ExistingAutomation("1", "automation.pantry", "Pantry",
            new HashSet<string>(StringComparer.Ordinal) { "light.pantry", "binary_sensor.pantry_motion" },
            new HashSet<string>(StringComparer.Ordinal) { "state" },
            []));
        Clock.Advance(TimeSpan.FromMinutes(61));
        await scanner.ScanAsync(CancellationToken.None);

        var closed = (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Contains("An automation now does this.", closed.EvidenceJson);
    }

    [Fact]
    public async Task Between_searches_a_routine_is_left_exactly_as_it_is()
    {
        await SeedAsync();
        var scanner = Scanner(Options());
        await scanner.ScanAsync(CancellationToken.None);
        var routine = await RoutineAsync();

        // Ten minutes later the history was not searched again, and the automations were not consulted.
        _ha.AutomationsFailure = new InvalidOperationException("must not be asked");
        Clock.Advance(TimeSpan.FromMinutes(10));
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.Resolved);
        Assert.Equal(AnomalyStatus.Open, (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_routine_put_away_is_not_offered_again_after_the_quiet_period()
    {
        await SeedAsync();
        var scanner = Scanner(Options());
        await scanner.ScanAsync(CancellationToken.None);
        var routine = await RoutineAsync();

        await Store.UpdateAnomalyAsync(routine with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow() }, CancellationToken.None);

        Clock.Advance(TimeSpan.FromDays(8));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(AnomalyStatus.Dismissed, (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Turning_learning_off_closes_every_open_routine_and_says_so()
    {
        await SeedAsync();
        var options = Options();
        var scanner = Scanner(options);
        await scanner.ScanAsync(CancellationToken.None);
        var routine = await RoutineAsync();

        options.Scan.LearnHabits = false;
        await scanner.ScanAsync(CancellationToken.None);

        var closed = (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Contains("turned off", closed.EvidenceJson);
    }

    [Fact]
    public async Task The_house_time_zone_comes_from_Home_Assistant_and_falls_back_to_the_process()
    {
        // A house zone whose offset differs from the process's own, so a held answer and a fallback to the
        // process's zone can never look alike -- on CI, where both would otherwise be UTC, the old version
        // of this test could not tell the fallback from a stale cache. Taken from the zones this host really
        // has: a Windows host without the IANA data cannot resolve "Pacific/Auckland" by that name.
        var house = TimeZoneInfo.GetSystemTimeZones()
            .First(zone => zone.BaseUtcOffset != TimeZoneInfo.Local.BaseUtcOffset);

        _ha.TimeZone = house.Id;
        var scanner = Scanner(Options());
        Assert.Equal(house.BaseUtcOffset, (await scanner.ZoneAsync(Clock.GetUtcNow(), CancellationToken.None)).BaseUtcOffset);

        // Held for hours, so a Home Assistant asked every scan is not asked every scan.
        _ha.TimeZone = "Not/AZone";
        Assert.Equal(house.BaseUtcOffset, (await scanner.ZoneAsync(Clock.GetUtcNow(), CancellationToken.None)).BaseUtcOffset);

        // Once it is asked again and cannot resolve the answer, the process's own zone stands in.
        Clock.Advance(TimeSpan.FromHours(7));
        var fallback = await scanner.ZoneAsync(Clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(TimeZoneInfo.Local.Id, fallback.Id);
        Assert.NotEqual(house.BaseUtcOffset, fallback.BaseUtcOffset);
    }
}

public class SamplesForTests : StoreFixture
{
    [Fact]
    public async Task Only_the_entities_asked_for_come_back_oldest_first_and_capped()
    {
        var now = Clock.GetUtcNow();
        List<(string, StateSample)> samples = [];
        for (var i = 0; i < 10; i++)
        {
            samples.Add(("light.a", new StateSample(i % 2 == 0 ? "on" : "off", null, now.AddMinutes(-i))));
            samples.Add(("light.b", new StateSample("on", null, now.AddMinutes(-i))));
        }

        samples.Add(("light.a", new StateSample("off", null, now.AddDays(-40))));
        await Store.AddSamplesAsync(samples, CancellationToken.None);

        var found = await Store.GetSamplesForAsync(["light.a", "light.missing"], now.AddDays(-28), 4, CancellationToken.None);

        var a = Assert.Single(found).Value;
        Assert.Equal(4, a.Count);
        Assert.True(a.Zip(a.Skip(1)).All(pair => pair.First.ChangedUtc < pair.Second.ChangedUtc));
        Assert.Equal(now, a[^1].ChangedUtc);

        Assert.Empty(await Store.GetSamplesForAsync([], now.AddDays(-28), 4, CancellationToken.None));
    }
}
