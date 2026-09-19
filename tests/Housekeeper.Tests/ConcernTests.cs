using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

/// <summary>
/// Concerns are the user telling the scanner where to look. Matching by name has to find the right thing
/// without a model; the model's answer has to be checked like a draft; and a concern with a rule has to
/// raise, and close, on its own.
/// </summary>
public class ConcernMatchingTests
{
    private static readonly List<HaEntity> House =
    [
        Build.Entity("sensor.freezer_temperature", "-18", friendlyName: "Freezer temperature", deviceClass: "temperature", unit: "°C", area: "Kitchen"),
        Build.Entity("sensor.garage_temperature", "4", friendlyName: "Garage temperature", deviceClass: "temperature", unit: "°C", area: "Garage"),
        Build.Entity("binary_sensor.freezer_door", "off", friendlyName: "Freezer door", deviceClass: "door", area: "Kitchen"),
        Build.Entity("cover.garage_door", "closed", friendlyName: "Garage door", deviceClass: "garage", area: "Garage"),
        Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"),
        Build.Entity("light.hall", "off", friendlyName: "Hall light"),
        Build.Entity("sensor.hub_linkquality", "200", friendlyName: "Hub link quality", unit: "lqi"),
    ];

    [Fact]
    public void A_named_thing_matches_that_thing_and_not_every_sensor_of_its_kind()
    {
        var matched = Concerns.Match("the freezer warming up", House);

        Assert.Contains("sensor.freezer_temperature", matched);
        Assert.Contains("binary_sensor.freezer_door", matched);
        Assert.DoesNotContain("sensor.garage_temperature", matched);
    }

    [Fact]
    public void A_state_word_that_names_nothing_falls_back_to_the_kind_of_thing()
    {
        List<HaEntity> house =
        [
            .. House,
            Build.Entity("lock.front_door", "locked", friendlyName: "Front door", area: "Hall"),
            Build.Entity("binary_sensor.back_door", "off", friendlyName: "Back door", deviceClass: "door"),
        ];

        // "unlocked" is in no entity's name, and used to sink the whole concern. The locks and the door
        // sensors are what it means.
        var matched = Concerns.Match("a door left unlocked at night", house);

        Assert.Contains("lock.front_door", matched);
        Assert.Contains("binary_sensor.back_door", matched);
        Assert.Contains("binary_sensor.freezer_door", matched);
        Assert.DoesNotContain("sensor.freezer_temperature", matched);
        Assert.DoesNotContain("light.hall", matched);
    }

    [Fact]
    public void A_kind_alone_matches_everything_of_that_kind()
    {
        Assert.Equal(["sensor.freezer_temperature", "sensor.garage_temperature"], Concerns.Match("temperature", House).Order());
        Assert.Equal(["sensor.dryer_power"], Concerns.Match("power", House));
        Assert.Equal(["light.hall"], Concerns.Match("lights", House));
        Assert.Contains("cover.garage_door", Concerns.Match("doors being left open", House));
    }

    [Fact]
    public void Plumbing_is_never_matched()
    {
        Assert.DoesNotContain("sensor.hub_linkquality", Concerns.Match("hub", House));
        Assert.Empty(Concerns.Match("", House));
    }

    /// <summary>
    /// A real house, matched by name, claimed forty entities per preset: automations about doors for "a door
    /// left open", a Sonos for "a room getting too cold", a light-level sensor for "lights left on".
    /// </summary>
    [Fact]
    public void Filler_words_automations_and_lookalike_names_do_not_match()
    {
        List<HaEntity> house =
        [
            .. House,
            Build.Entity("automation.notify_when_sliding_door_is_left_open", "on", friendlyName: "Notify when sliding door is left open"),
            Build.Entity("script.open_netflix_on_tv", "off", friendlyName: "Open Netflix on TV"),
            Build.Entity("sensor.kitchen_presence_light_level", "120", friendlyName: "Kitchen Presence Sensor Light Level", deviceClass: "illuminance"),
            Build.Entity("media_player.living_room_sonos", "idle", friendlyName: "Living Room Sonos", area: "Living room"),
            Build.Entity("select.living_room_light_preset", "warm", friendlyName: "Living Room Light Switch Light preset"),
            Build.Entity("device_tracker.phone", "home", friendlyName: "Phone"),
        ];

        Assert.Equal(["light.hall"], Concerns.Match("lights left on overnight", house));
        Assert.Equal(["binary_sensor.freezer_door", "cover.garage_door"], Concerns.Match("a door or window left open", house).Order());
        Assert.Empty(Concerns.Match("a device going offline", house));

        var cold = Concerns.Match("a room getting too cold", house);
        Assert.DoesNotContain("media_player.living_room_sonos", cold);
        Assert.Contains("sensor.freezer_temperature", cold);
    }

    [Fact]
    public void The_models_answer_keeps_only_entities_that_exist()
    {
        var known = new HashSet<string>(House.Select(e => e.EntityId), StringComparer.Ordinal);

        var read = Concerns.Parse("""{"entity_ids":["sensor.freezer_temperature","sensor.made_up"],"kind":"above","value":-15,"for_minutes":10,"explanation":"Freezer above -15."}""", known);

        Assert.NotNull(read);
        Assert.Equal(["sensor.freezer_temperature"], read.Value.Entities);
        Assert.Equal(new WatchRule(WatchKind.Above, -15, null, TimeSpan.FromMinutes(10)), read.Value.Rule);
        Assert.Equal("Freezer above -15.", read.Value.Explanation);

        Assert.Null(Concerns.Parse("not json", known));
        Assert.Equal(WatchKind.Any, Concerns.Parse("""{"entity_ids":[],"kind":"above"}""", known)!.Value.Rule.Kind);
    }

    [Fact]
    public void Sharpened_bars_are_lower_but_never_silly()
    {
        var sharp = Concerns.Sharpen(new ScanOptions());

        Assert.Equal(2.0, sharp.StuckMultiplier, 3);
        Assert.True(sharp.OutlierThreshold < 4 && sharp.OutlierThreshold >= 2.5);
        Assert.True(sharp.MinimumStuckDuration < TimeSpan.FromMinutes(10));
        Assert.True(sharp.MinimumStuckDuration >= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_rule_fires_only_past_its_line_and_its_window()
    {
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var concern = new Concern { Id = 7, Text = "the freezer warming up", Entities = ["sensor.freezer_temperature"], Rule = new WatchRule(WatchKind.Above, -15, null, TimeSpan.FromMinutes(10)) };

        var fine = House[0] with { State = "-18", LastChanged = now.AddMinutes(-30) };
        Assert.Null(Concerns.Evaluate(concern, fine, [new StateSample("-18", -18, now.AddMinutes(-30))], now));

        // Warm for only five minutes: inside the window, so not yet.
        var warming = House[0] with { State = "-12", LastChanged = now.AddMinutes(-5) };
        List<StateSample> recent = [new("-18", -18, now.AddMinutes(-30)), new("-12", -12, now.AddMinutes(-5))];
        Assert.Null(Concerns.Evaluate(concern, warming, recent, now));

        // Warm for a quarter of an hour, every reading in the window above the line.
        var warm = House[0] with { State = "-11", LastChanged = now.AddMinutes(-2) };
        List<StateSample> held = [new("-18", -18, now.AddMinutes(-30)), new("-12", -12, now.AddMinutes(-15)), new("-11", -11, now.AddMinutes(-2))];
        var finding = Concerns.Evaluate(concern, warm, held, now);

        Assert.NotNull(finding);
        Assert.Equal(AnomalyKind.Concern, finding.Kind);
        Assert.Equal("concern:7:sensor.freezer_temperature", finding.DedupKey);
        Assert.True(finding.Severity >= Concerns.RuleSeverity);
        Assert.Contains("above the -15 °C you asked to watch for", finding.Summary);
        Assert.Contains("the freezer warming up", finding.Summary);
        Assert.StartsWith("Notify me when sensor.freezer_temperature goes above -15", finding.SuggestedRequest);
    }

    [Fact]
    public void A_held_state_rule_needs_its_duration()
    {
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var concern = new Concern { Id = 3, Text = "garage door left open", Entities = ["cover.garage_door"], Rule = new WatchRule(WatchKind.Held, null, "open", TimeSpan.FromMinutes(30)) };

        Assert.Null(Concerns.Evaluate(concern, House[3] with { State = "open", LastChanged = now.AddMinutes(-10) }, [], now));
        Assert.NotNull(Concerns.Evaluate(concern, House[3] with { State = "open", LastChanged = now.AddMinutes(-45) }, [], now));
        Assert.Null(Concerns.Evaluate(concern, House[3] with { State = "closed", LastChanged = now.AddHours(-2) }, [], now));
    }
}

public class ConcernServiceTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();
    private readonly FakeLlm _llm = new();

    private ConcernService Service()
    {
        var settings = new FakeSettings();
        settings.Current.Llm.Model = "fake";
        return new ConcernService(_ha, _llm, Store, settings, Clock, NullLogger<ConcernService>.Instance);
    }

    [Fact]
    public async Task The_model_reads_a_concern_into_entities_and_a_rule()
    {
        _ha.Entities.Add(Build.Entity("sensor.kids_room_temperature", "19", friendlyName: "Kids room temperature", deviceClass: "temperature", unit: "°C"));
        _llm.Response = """{"entity_ids":["sensor.kids_room_temperature"],"kind":"below","value":17,"explanation":"Watching the kids' room for dropping below 17 °C."}""";

        var concern = await Service().AddAsync("the kids' room getting too cold at night", CancellationToken.None);

        Assert.True(concern.Interpreted);
        Assert.Equal(["sensor.kids_room_temperature"], concern.Entities);
        Assert.Equal(WatchKind.Below, concern.Rule.Kind);
        Assert.Equal(17, concern.Rule.Value);
        Assert.Contains("CONCERN:", _llm.LastUserPrompt);
        Assert.Single(await Store.ListConcernsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_usable_answer_the_concern_is_kept_and_matched_by_name()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = null;

        var concern = await Service().AddAsync("dryer power", CancellationToken.None);

        Assert.False(concern.Interpreted);
        Assert.Equal(["sensor.dryer_power"], concern.Entities);
        Assert.Equal(WatchKind.Any, concern.Rule.Kind);
        Assert.Equal("Matched by name to 1 entity.", concern.Explanation);
        Assert.Contains("did not answer", concern.Note);
        Assert.True(concern.Provisional);
    }

    [Fact]
    public async Task A_concern_the_model_could_not_read_is_read_again_when_it_answers()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = null;
        var service = Service();
        var concern = await service.AddAsync("dryer power", CancellationToken.None);
        Assert.True(concern.Provisional);

        // Still down: still provisional, and the note stays.
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        Assert.True((await Store.GetConcernAsync(concern.Id, CancellationToken.None))!.Provisional);

        // Not asked again on the very next tick: a model that is down is asked once per interval, not once
        // per tick per concern.
        _llm.Response = """{"entity_ids":["sensor.dryer_power"],"kind":"above","value":1500,"explanation":"Watching the dryer's power for going above 1500 W."}""";
        var calls = _llm.Calls;
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
        Assert.Equal(calls, _llm.Calls);

        // Long enough for the gap, which doubles per failed try.
        Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, await service.ReadPendingAsync(CancellationToken.None));

        var read = (await Store.GetConcernAsync(concern.Id, CancellationToken.None))!;
        Assert.True(read.Interpreted);
        Assert.False(read.Provisional);
        Assert.Null(read.Note);
        Assert.Equal(WatchKind.Above, read.Rule.Kind);
        Assert.Equal("Watching the dryer's power for going above 1500 W.", read.Explanation);
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_model_naming_nothing_is_an_answer_not_a_wait()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        _llm.Response = """{"entity_ids":[],"kind":"any","explanation":"Nothing here measures the dryer."}""";

        var concern = await Service().AddAsync("dryer power", CancellationToken.None);

        Assert.False(concern.Provisional);
        Assert.Equal(["sensor.dryer_power"], concern.Entities);
        Assert.Contains("named nothing that exists here", concern.Note);
        Assert.Contains("Nothing here measures the dryer.", concern.Note);
    }

    [Fact]
    public async Task Without_a_model_chosen_the_concern_waits_for_one()
    {
        _ha.Entities.Add(Build.Entity("sensor.dryer_power", "0", friendlyName: "Dryer power", deviceClass: "power", unit: "W"));
        var settings = new FakeSettings();
        settings.Current.Llm.Model = "";
        var service = new ConcernService(_ha, _llm, Store, settings, Clock, NullLogger<ConcernService>.Instance);

        var concern = await service.AddAsync("dryer power", CancellationToken.None);

        Assert.True(concern.Provisional);
        Assert.Contains("No model is chosen", concern.Note);
        Assert.Equal(0, _llm.Calls);
        Assert.Equal(0, await service.ReadPendingAsync(CancellationToken.None));
    }
}

public class ConcernScanTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner()
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["sensor.*", "cover.*"];
        options.Scan.BackfillFromRecorder = false;
        return new AnomalyScanner(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);
    }

    [Fact]
    public async Task A_concern_with_a_rule_raises_its_own_finding_and_closes_it_when_the_rule_stops_firing()
    {
        var now = Clock.GetUtcNow();
        var door = Build.Entity("cover.garage_door", "open", now.AddHours(-1), "Garage door", "garage", area: "Garage");
        _ha.Entities.Add(door);
        var concern = await Store.AddConcernAsync(new Concern
        {
            Text = "the garage door left open",
            Entities = ["cover.garage_door"],
            Rule = new WatchRule(WatchKind.Held, null, "open", TimeSpan.FromMinutes(30)),
            CreatedUtc = now,
        }, CancellationToken.None);

        var scanner = Scanner();
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Raised);
        var finding = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, false, CancellationToken.None));
        Assert.Equal(AnomalyKind.Concern, finding.Kind);
        Assert.Equal($"concern:{concern.Id}:cover.garage_door", finding.DedupKey);
        Assert.Contains("\"concern\":\"the garage door left open\"", finding.EvidenceJson);

        // Shut it: the rule no longer fires, and the entity was looked at, so the card closes.
        Clock.Advance(TimeSpan.FromMinutes(1));
        _ha.Entities[0] = door with { State = "closed", LastChanged = Clock.GetUtcNow() };
        Assert.Equal(1, (await scanner.ScanAsync(CancellationToken.None)).Resolved);
        Assert.Equal(AnomalyStatus.Resolved, (await Store.GetAnomalyAsync(finding.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Removing_a_concern_closes_what_it_raised()
    {
        var now = Clock.GetUtcNow();
        _ha.Entities.Add(Build.Entity("cover.garage_door", "open", now.AddHours(-1), "Garage door", "garage"));
        var concern = await Store.AddConcernAsync(new Concern
        {
            Text = "garage door",
            Entities = ["cover.garage_door"],
            Rule = new WatchRule(WatchKind.Held, null, "open", TimeSpan.FromMinutes(30)),
            CreatedUtc = now,
        }, CancellationToken.None);

        var scanner = Scanner();
        await scanner.ScanAsync(CancellationToken.None);
        var finding = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, false, CancellationToken.None));

        await Store.DeleteConcernAsync(concern.Id, CancellationToken.None);
        await scanner.ScanAsync(CancellationToken.None);

        var closed = (await Store.GetAnomalyAsync(finding.Id, CancellationToken.None))!;
        Assert.Equal(AnomalyStatus.Resolved, closed.Status);
        Assert.Contains("removed", closed.EvidenceJson);
    }
}
