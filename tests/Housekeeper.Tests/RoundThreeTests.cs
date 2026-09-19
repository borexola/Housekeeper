using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

/// <summary>
/// What review found after the first pass of fixes: a card closed on the wrong evidence, a routine put
/// away by a button that never asked, a dismissal that taught nothing end to end, and a concern the tick
/// kept asking about for ever.
/// </summary>
public class ClosingEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 2, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    private static StateSample Reading(double value, DateTimeOffset at) => new(Ha.Number(value), value, at);

    private static HaEntity Plug(double reading, DateTimeOffset changed) =>
        Build.Entity("sensor.tv_plug_power", Ha.Number(reading), changed, deviceClass: "power", unit: "W");

    /// <summary>
    /// A plug with two normals: off at half a watt nearly always, and the television at 120 W about one
    /// evening reading in twelve. The median sits in the off mode, so 120 W is far outside the bar and
    /// still something this plug does every day.
    /// </summary>
    private static List<StateSample> TwoModes(DateTimeOffset until)
    {
        var step = TimeSpan.FromMinutes(20);
        var start = until - step * 60;
        List<StateSample> readings = [];
        for (var i = 0; i < 60; i++) readings.Add(Reading(i % 12 == 0 ? 120 : 0.5, start + step * i));
        return readings;
    }

    [Fact]
    public void A_reading_that_settles_into_a_mode_the_entity_already_occupies_closes_the_card()
    {
        var baseline = TwoModes(Now.AddMinutes(-40));

        // The console pushes it to 160 W, which is no mode of its own: a finding.
        var out160 = new List<StateSample>(baseline);
        for (var minute = 35; minute >= 0; minute -= 5) out160.Add(Reading(160, Now.AddMinutes(-minute)));
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(Plug(160, Now), new EntityHistory(out160, baseline), Options, Now));

        // The console goes off and the television stays on at 120 W. The detector says nothing about a mode
        // it knows, so the card can only close if "back" means the same thing to both sides.
        var settled = new List<StateSample>(baseline);
        for (var minute = 35; minute >= 15; minute -= 5) settled.Add(Reading(160, Now.AddMinutes(-minute)));
        for (var minute = 10; minute >= 0; minute -= 5) settled.Add(Reading(120, Now.AddMinutes(-minute)));

        var history = new EntityHistory(settled, baseline);
        Assert.Null(AnomalyDetection.DetectNumericOutlier(Plug(120, Now), history, Options, Now));
        Assert.True(AnomalyDetection.BackInside(Plug(120, Now), history, Options, Now));
        Assert.Contains(AnomalyKind.NumericOutlier, AnomalyDetection.Resolvable(Plug(120, Now), history, Options, Now));
    }

    [Fact]
    public void One_reading_in_that_mode_is_not_enough_to_close_it()
    {
        var baseline = TwoModes(Now.AddMinutes(-40));
        var history = new List<StateSample>(baseline);
        for (var minute = 35; minute >= 5; minute -= 5) history.Add(Reading(160, Now.AddMinutes(-minute)));
        history.Add(Reading(120, Now));

        Assert.False(AnomalyDetection.BackInside(Plug(120, Now), new EntityHistory(history, baseline), Options, Now));
    }

    [Fact]
    public void A_bridged_flicker_does_not_cut_the_run_at_the_next_wide_gap()
    {
        // Twenty minutes of one-minute readings at 160 W with one 100 W blip in the middle, and the thinned
        // history reaching back hours at two readings an hour, all of them beyond. The excursion is hours
        // old; the gap between the thinned rows must not be read as the end of it just because a blip
        // happened to sit inside the recent window.
        var baseline = new List<StateSample>();
        for (var i = 0; i < 60; i++) baseline.Add(Reading(100 + (((i % 4) - 1.5) * 1.0), Now.AddHours(-36).AddMinutes(30 * i)));

        // Two thinned rows from when the excursion began, five hours ago. Few, so they are not themselves a
        // mode this sensor is taken to occupy.
        baseline.Add(Reading(160, Now.AddHours(-5).AddMinutes(-30)));
        baseline.Add(Reading(160, Now.AddHours(-5)));

        var recent = new List<StateSample>(baseline);
        for (var minute = 20; minute >= 0; minute--) recent.Add(Reading(minute == 9 ? 100 : 160, Now.AddMinutes(-minute)));

        var finding = AnomalyDetection.DetectNumericOutlier(Plug(160, Now), new EntityHistory(recent, baseline), Options, Now);

        Assert.NotNull(finding);
        var excursion = JsonDocument.Parse(finding.EvidenceJson).RootElement.GetProperty("excursion_seconds").GetDouble();
        Assert.True(excursion >= TimeSpan.FromHours(5).TotalSeconds, $"excursion was {excursion} seconds");
    }
}

public class DismissalLearningTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options()
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["binary_sensor.*"];
        options.Scan.BackfillFromRecorder = false;
        return options;
    }

    /// <summary>Twelve short openings on record, then one that is held far longer.</summary>
    private async Task<AnomalyScanner> WatchedDoorAsync(HousekeeperOptions options, TimeSpan openFor)
    {
        var scanner = Scanner(options);
        var door = Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow().AddDays(-2), "Freezer Door");
        _ha.Entities.Add(door);

        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);

            Clock.Advance(TimeSpan.FromSeconds(45));
            _ha.Entities[0] = door with { State = "off", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        Clock.Advance(TimeSpan.FromHours(2));
        _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(openFor);
        await scanner.ScanAsync(CancellationToken.None);
        return scanner;
    }

    [Fact]
    public async Task A_dismissed_finding_that_comes_back_further_over_the_line_is_heard_again()
    {
        var options = Options();
        var scanner = await WatchedDoorAsync(options, TimeSpan.FromMinutes(14));
        var raised = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));
        Assert.True(raised.Severity >= 1.5, $"severity was {raised.Severity}");

        // The user dismisses it once; the quiet period passes with the door shut.
        await Store.UpdateAnomalyAsync(
            raised with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow(), Dismissals = 1 },
            CancellationToken.None);

        var door = _ha.Entities[0];
        _ha.Entities[0] = door with { State = "off", LastChanged = Clock.GetUtcNow() };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(options.Scan.RedetectAfter + TimeSpan.FromMinutes(1));
        _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
        await scanner.ScanAsync(CancellationToken.None);

        // Held far longer than the fourteen minutes that raised it the first time, so it is well past both
        // the detector's own bar and the higher one the dismissal set.
        Clock.Advance(TimeSpan.FromHours(3));

        // Over the raised bar, so it is heard again -- and the count of dismissals stays on the row, which
        // is what the card reads to say it has come back over a bar the user raised.
        Assert.Equal(1, (await scanner.ScanAsync(CancellationToken.None)).Raised);

        var back = (await Store.GetAnomalyAsync(raised.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Open, back.Status);
        Assert.Null(back.DecidedUtc);
        Assert.Equal(1, back.Dismissals);
    }

    [Fact]
    public async Task Three_dismissals_silence_a_finding_however_far_over_the_line_it_goes()
    {
        var options = Options();
        var scanner = await WatchedDoorAsync(options, TimeSpan.FromMinutes(14));
        var raised = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));

        await Store.UpdateAnomalyAsync(
            raised with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow(), Dismissals = 3 },
            CancellationToken.None);

        Clock.Advance(options.Scan.RedetectAfter + TimeSpan.FromDays(2));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(AnomalyStatus.Dismissed, (await Store.GetAnomalyAsync(raised.Id, CancellationToken.None))!.Status);

        // And the row outlives the prune, so the silence is not forgotten.
        Assert.Equal(0, await Store.PruneAnomaliesAsync(Clock.GetUtcNow().AddDays(1), CancellationToken.None));
        Assert.NotNull(await Store.FindAnomalyAsync(raised.DedupKey, CancellationToken.None));
    }
}

public class RoutineLifecycleTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private const string RoutineKey = "habit:light.pantry:on:binary_sensor.pantry_motion:on";

    private static HousekeeperOptions Options(params string[] include)
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = [.. include];
        options.Scan.BackfillFromRecorder = false;
        return options;
    }

    /// <summary>Nine evenings of the pantry light following its motion sensor, and the sun overhead.</summary>
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
            // Motion at midday too, with no light: what makes "after dark" the condition that holds, rather
            // than "always", which is plainer and would otherwise win.
            Add("binary_sensor.pantry_motion", d.AddHours(12), "on");
            Add("binary_sensor.pantry_motion", d.AddHours(12).AddMinutes(5), "off");
            Add("binary_sensor.pantry_motion", d.AddHours(19), "on");
            Add("light.pantry", d.AddHours(19).AddSeconds(20), "on");
            Add("binary_sensor.pantry_motion", d.AddHours(19).AddMinutes(5), "off");
            Add("light.pantry", d.AddHours(22).AddMinutes(day % 2 == 0 ? -2 : 2), "off");
        }

        await Store.AddSamplesAsync(samples, CancellationToken.None);
    }

    [Fact]
    public async Task The_sun_is_watched_and_its_condition_reaches_the_routine_even_when_the_list_does_not_name_it()
    {
        await SeedAsync();
        var scanner = Scanner(Options("light.pantry", "binary_sensor.pantry_motion"));

        await scanner.ScanAsync(CancellationToken.None);

        Assert.Contains(Habits.Sun, scanner.Watching);
        var routine = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None), a => a.DedupKey == RoutineKey);
        Assert.Contains("after dark", routine.Summary);
        Assert.Null(scanner.LastRoutineSearch!.Skipped);
    }

    [Fact]
    public async Task A_promoted_routine_is_refreshed_rather_than_re_offered_once_its_quiet_period_passes()
    {
        await SeedAsync();
        var options = Options("light.pantry", "binary_sensor.pantry_motion");
        var scanner = Scanner(options);
        await scanner.ScanAsync(CancellationToken.None);

        var routine = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None), a => a.DedupKey == RoutineKey);
        await Store.UpdateAnomalyAsync(
            routine with { Status = AnomalyStatus.Promoted, ProposalId = 42, DecidedUtc = Clock.GetUtcNow() },
            CancellationToken.None);

        // Well past the quiet period, with the draft still waiting for the user.
        Clock.Advance(options.Scan.RedetectAfter + TimeSpan.FromDays(1));
        var report = await scanner.ScanAsync(CancellationToken.None);

        var after = (await Store.GetAnomalyAsync(routine.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Promoted, after.Status);
        Assert.Equal(42, after.ProposalId);
        Assert.Equal(0, report.Routines);
    }

    [Fact]
    public async Task Ignoring_an_entity_closes_its_routine_rather_than_putting_it_away_for_good()
    {
        await SeedAsync();
        var options = Options("light.pantry", "binary_sensor.pantry_motion");
        var scanner = Scanner(options);
        await scanner.ScanAsync(CancellationToken.None);
        var routine = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None), a => a.DedupKey == RoutineKey);

        // What the ignore endpoint does to every open finding on the entity.
        var closed = routine with
        {
            Status = AnomalyStatus.Resolved,
            DecidedUtc = Clock.GetUtcNow(),
            EvidenceJson = AnomalyScanner.WithReason(routine.EvidenceJson, "It is no longer on the watch list."),
        };
        await Store.UpdateAnomalyAsync(closed, CancellationToken.None);

        // Resolved, not dismissed, so the prune reaches it and the routine is offered again once the entity
        // is watched once more.
        Assert.Equal(1, await Store.PruneAnomaliesAsync(Clock.GetUtcNow().AddDays(1), CancellationToken.None));

        await Store.AddSamplesAsync([], CancellationToken.None);
        Clock.Advance(TimeSpan.FromMinutes(61));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(AnomalyStatus.Open, (await Store.FindAnomalyAsync(RoutineKey, CancellationToken.None))!.Status);
    }
}

public class ConcernPatienceTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();
    private readonly FakeLlm _llm = new();

    private ConcernService Service()
    {
        var settings = new FakeSettings();
        settings.Current.Llm.Model = "fake";
        return new ConcernService(_ha, _llm, Store, settings, Clock, NullLogger<ConcernService>.Instance);
    }

    private static readonly TimeSpan Interval = new HousekeeperOptions().Scan.Interval;

    [Fact]
    public async Task An_unusable_answer_counts_a_strike_and_three_leave_it_to_the_button()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = "I think you mean the dryer, which is in the utility room.";
        var service = Service();

        var concern = await service.AddAsync("dryer power", CancellationToken.None);
        Assert.True(concern.Provisional);

        // Three ticks, each after its own growing gap: three unusable answers, three strikes.
        for (var tries = 0; tries < 3; tries++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        }

        var parked = (await Store.GetConcernAsync(concern.Id, CancellationToken.None))!;
        Assert.False(parked.Provisional);
        Assert.DoesNotContain("It will be asked again.", parked.Note);
        Assert.Contains("will not be asked again on its own", parked.Note);
        Assert.Equal(["sensor.dryer_power"], parked.Entities);

        // The tick has stopped asking, whatever the model does now.
        var calls = _llm.Calls;
        Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        Assert.Equal(calls, _llm.Calls);

        // The button still asks, and it works when the model is finally usable.
        _llm.Response = """{"entity_ids":["sensor.dryer_power"],"kind":"above","value":1500,"explanation":"Watching the dryer's power for going above 1500 W."}""";
        var read = await service.ReadAgainAsync(concern.Id, CancellationToken.None);
        Assert.True(read!.Interpreted);
        Assert.Equal(WatchKind.Above, read.Rule.Kind);
    }

    [Fact]
    public async Task A_model_that_is_merely_down_is_asked_less_often_but_never_given_up_on()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = null;
        var service = Service();

        var concern = await service.AddAsync("dryer power", CancellationToken.None);
        var calls = _llm.Calls;

        // The tick tries once per gap, and the gap doubles: at one interval, then two, then four.
        Clock.Advance(Interval);
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        Assert.Equal(calls + 1, _llm.Calls);

        Clock.Advance(Interval);
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        Assert.Equal(calls + 1, _llm.Calls);

        Clock.Advance(Interval);
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        Assert.Equal(calls + 2, _llm.Calls);

        // Still provisional after all of it: being down is not being wrong.
        Assert.True((await Store.GetConcernAsync(concern.Id, CancellationToken.None))!.Provisional);

        // And when it comes back, the concern is read.
        _llm.Response = """{"entity_ids":["sensor.dryer_power"],"kind":"above","value":1500,"explanation":"Watching the dryer."}""";
        Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await service.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_tick_that_fires_a_hair_early_still_reads_the_concern()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = null;
        var service = Service();
        await service.AddAsync("dryer power", CancellationToken.None);

        _llm.Response = """{"entity_ids":["sensor.dryer_power"],"kind":"above","value":1500,"explanation":"Watching the dryer."}""";
        Clock.Advance(Interval - TimeSpan.FromMilliseconds(20));

        Assert.Equal(1, await service.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reading_again_while_the_model_is_down_keeps_the_reading_it_already_had()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = """{"entity_ids":["sensor.dryer_power"],"kind":"above","value":1500,"explanation":"Watching the dryer's power for going above 1500 W."}""";
        var service = Service();
        var concern = await service.AddAsync("dryer power", CancellationToken.None);
        Assert.True(concern.Interpreted);

        _llm.Response = null;
        var again = await service.ReadAgainAsync(concern.Id, CancellationToken.None);

        Assert.True(again!.Interpreted);
        Assert.Equal(WatchKind.Above, again.Rule.Kind);
        Assert.Equal(1500, again.Rule.Value);
        Assert.Equal(["sensor.dryer_power"], again.Entities);
        Assert.False(again.Provisional);
        Assert.Contains("did not answer", again.Note);
        Assert.Contains("kept", again.Note);
    }
}

public class ConcernMatchingRankTests
{
    [Fact]
    public void A_concern_about_doors_being_unlocked_keeps_the_locks_when_the_cap_binds()
    {
        List<HaEntity> house = [];
        for (var i = 0; i < 8; i++)
            house.Add(Build.Entity($"binary_sensor.a{i}_door_contact", "off", friendlyName: $"Door {i}", deviceClass: "door"));
        foreach (var room in new[] { "bedroom", "study", "kitchen", "lounge", "hall", "office", "attic", "cellar", "porch", "shed", "garage", "loft" })
            house.Add(Build.Entity($"cover.{room}_blind", "open", friendlyName: $"{room} blind"));
        house.Add(Build.Entity("lock.front", "locked", friendlyName: "Front lock"));
        house.Add(Build.Entity("lock.back", "locked", friendlyName: "Back lock"));

        var matched = Concerns.Match("a door left unlocked at night", house);

        Assert.Contains("lock.front", matched);
        Assert.Contains("lock.back", matched);
        Assert.Contains("binary_sensor.a0_door_contact", matched);
        Assert.True(matched.Count <= Concerns.MostMatched);
    }

    [Fact]
    public void A_particular_name_that_matches_nothing_still_matches_nothing()
    {
        List<HaEntity> house =
        [
            Build.Entity("light.hall", "off", friendlyName: "Hall light", area: "Hall"),
            Build.Entity("light.kitchen", "off", friendlyName: "Kitchen light", area: "Kitchen"),
        ];

        Assert.Empty(Concerns.Match("the attic light", house));
        Assert.Equal(2, Concerns.Match("lights left on", house).Count);
    }
}

[Collection("api")]
public class RoundThreeApiTests
{
    private readonly TestApp _app;
    private readonly HttpClient _client;

    public RoundThreeApiTests(TestApp app)
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
    public async Task Putting_a_routine_away_silences_it_on_the_first_dismissal()
    {
        var store = _app.Services.GetRequiredService<IStore>();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var seeded = await store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = $"habit:test:{mark}",
            EntityId = $"light.{mark}",
            Kind = AnomalyKind.Habit,
            Summary = "You usually turn on the light.",
            SuggestedRequest = $"Turn on light.{mark} when binary_sensor.{mark} detects movement.",
            Status = AnomalyStatus.Open,
            Severity = 2,
            DetectedUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var body = await (await _client.PostAsync($"/api/anomalies/{seeded.Id}/dismiss", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("dismissals").GetInt32());
        Assert.True(body.GetProperty("silenced").GetBoolean());
        Assert.Contains("Put away", body.GetProperty("note").GetString());

        var stored = (await store.GetAnomalyAsync(seeded.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Dismissed, stored.Status);
        Assert.Equal(1, stored.Dismissals);
    }

    [Fact]
    public async Task A_concern_the_model_gave_up_on_can_still_be_read_again_from_the_page()
    {
        _app.Llm.Clear();
        _app.Llm.Response = null;
        var added = await (await _client.PostAsJsonAsync("/api/concerns", new { text = "the porch light left on all night" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = added.GetProperty("id").GetInt64();

        // Provisional, and the page offers the button.
        Assert.True(added.GetProperty("provisional").GetBoolean());
        Assert.True(added.GetProperty("canReread").GetBoolean());

        // Once the model reads it, the button is gone and nothing says otherwise.
        _app.Llm.Response = """{"entity_ids":["light.hall"],"kind":"held","state":"on","for_minutes":120,"explanation":"Watching the hall light for staying on two hours."}""";
        var read = await (await _client.PostAsync($"/api/concerns/{id}/reread", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(read.GetProperty("provisional").GetBoolean());
        Assert.False(read.GetProperty("canReread").GetBoolean());
        Assert.True(read.GetProperty("interpreted").GetBoolean());

        await _client.DeleteAsync($"/api/concerns/{id}");
    }
}
