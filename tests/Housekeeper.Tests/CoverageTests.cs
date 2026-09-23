using System.Net;
using System.Text;
using System.Text.Json;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Housekeeper.Tests;

/// <summary>
/// Whether an automation the user already has fires on what a finding is about. The failure that matters is
/// a false "you already have an automation for this": told they are covered, a user dismisses a real finding.
/// So most of these are everyday automations that name an entity without firing on what was found, and the
/// rest are the few that genuinely do.
/// </summary>
public class CoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private const string Co2 = "sensor.living_room_co2";
    private const string Door = "binary_sensor.freezer_door";

    private static HaEntity Reading(string id, double value, string unit = "ppm", string? registryId = null, string? deviceId = null) =>
        Build.Entity(id, value.ToString(System.Globalization.CultureInfo.InvariantCulture), Now.AddMinutes(-1),
            unit: unit, registryId: registryId, deviceId: deviceId);

    /// <summary>An automation built the way the client builds one: its config through the inspector, its entity switched on.</summary>
    private static (ExistingAutomation Automation, HaEntity Entity) Automation(string id, string config, string state = "on")
    {
        using var document = JsonDocument.Parse(config);
        var inspection = AutomationInspector.Inspect(document.RootElement);
        var alias = document.RootElement.TryGetProperty("alias", out var name) ? name.GetString()! : id;

        return (
            new ExistingAutomation(id, "automation." + id, alias, inspection.Entities, inspection.TriggerKinds, inspection.Triggers, inspection.HasConditions),
            Build.Entity("automation." + id, state, Now.AddDays(-1), alias, automationConfigId: id));
    }

    private static KnownAutomations Known(IEnumerable<HaEntity> entities, params (ExistingAutomation Automation, HaEntity Entity)[] automations) =>
        new([.. automations.Select(pair => pair.Automation)], [.. entities, .. automations.Select(pair => pair.Entity)], Now, Now);

    private static Anomaly Finding(AnomalyKind kind, string entityId, object evidence) => new()
    {
        DedupKey = $"{kind}:{entityId}",
        EntityId = entityId,
        Kind = kind,
        Summary = "",
        SuggestedRequest = "",
        Status = AnomalyStatus.Open,
        EvidenceJson = JsonSerializer.Serialize(evidence),
    };

    private static Anomaly High(string entityId, double current, double median = 500) =>
        Finding(AnomalyKind.NumericOutlier, entityId, new { current, median });

    private static Anomaly Stuck(string entityId, string state = "on") =>
        Finding(AnomalyKind.StuckState, entityId, new { state });

    private static Anomaly Quiet(string entityId, params string[] alsoGone) =>
        Finding(AnomalyKind.Unavailable, entityId, new { state = "unavailable", also_moved = alsoGone });

    /// <summary>The case this exists for: a reading past a line an automation of theirs already watches.</summary>
    [Fact]
    public void A_reading_past_the_line_an_automation_watches_is_covered()
    {
        var alert = Automation("high_co2", """
            {"alias":"High CO2 alert","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000}],
             "actions":[{"action":"notify.notify","data":{"message":"CO2 is high"}}]}
            """);

        var one = Assert.Single(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500)], alert)));

        Assert.Equal("High CO2 alert", one.Alias);
        Assert.Equal("automation.high_co2", one.EntityId);
        Assert.Equal("fires when it goes above 1000 ppm", one.Why);
        Assert.False(one.Conditional);
    }

    /// <summary>
    /// A line the reading has not reached has not fired: an alert set for a far worse reading is no answer to
    /// this one. Nor is a line on the other side of it.
    /// </summary>
    [Fact]
    public void A_line_the_reading_is_not_past_is_not_cover()
    {
        var worse = Automation("worse", """{"alias":"Very high CO2","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":2000}]}""");
        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500)], worse)));

        var low = Finding(AnomalyKind.NumericOutlier, Co2, new { current = 300, median = 600 });
        var above = Automation("above", """{"alias":"High CO2","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000}]}""");
        Assert.Empty(Coverage.Find(low, Known([Reading(Co2, 300)], above)));

        var below = Automation("below", """{"alias":"Too low","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","below":400}]}""");
        Assert.Equal("fires when it drops below 400 ppm", Assert.Single(Coverage.Find(low, Known([Reading(Co2, 300)], below))).Why);
    }

    [Fact]
    public void A_trigger_on_an_attribute_or_on_a_templates_value_is_not_watching_the_reading()
    {
        var battery = Automation("battery", """
            {"alias":"CO2 battery low","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2",
             "value_template":"{{ state.attributes.battery_level }}","below":15}]}
            """);
        var attribute = Automation("attribute", """
            {"alias":"CO2 battery","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","attribute":"battery_level","above":10}]}
            """);

        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500)], battery, attribute)));
    }

    /// <summary>
    /// The most common automation there is. It names the motion sensor in its trigger, and it fires when motion
    /// is seen -- never when the sensor's battery dies, and never because it has been stuck on for hours.
    /// Counting it as cover would bury the very findings that say the hall light has stopped working.
    /// </summary>
    [Fact]
    public void An_everyday_state_trigger_is_not_cover_for_a_sensor_gone_quiet_or_held_too_long()
    {
        var light = Automation("hall_light", """
            {"alias":"Hall light on motion","triggers":[{"trigger":"state","entity_id":"binary_sensor.hall_motion","to":"on"}],
             "actions":[{"action":"light.turn_on","target":{"entity_id":"light.hall"}}]}
            """);

        var dead = Build.Entity("binary_sensor.hall_motion", "unavailable", Now.AddHours(-5), "Hall motion", "motion");
        Assert.Empty(Coverage.Find(Quiet("binary_sensor.hall_motion"), Known([dead], light)));

        var stuck = Build.Entity("binary_sensor.hall_motion", "on", Now.AddHours(-3), "Hall motion", "motion");
        Assert.Empty(Coverage.Find(Stuck("binary_sensor.hall_motion"), Known([stuck], light)));
    }

    [Fact]
    public void A_state_held_past_a_for_that_has_run_out_is_covered()
    {
        var door = Build.Entity(Door, "on", Now.AddHours(-3), "Freezer door", "door");

        var alert = Automation("freezer", """{"alias":"Freezer left open","triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"on","for":"00:10:00"}]}""");
        Assert.Equal("fires when it stays open for 10 minutes", Assert.Single(Coverage.Find(Stuck(Door), Known([door], alert))).Why);

        // Not yet: it fires after four hours, and the door has been open for three.
        var later = Automation("later", """{"alias":"Later","triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"on","for":{"hours":4}}]}""");
        Assert.Empty(Coverage.Find(Stuck(Door), Known([door], later)));

        // A for: on the other state is about the door being shut.
        var shut = Automation("shut", """{"alias":"Shut","triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"off","for":"00:10:00"}]}""");
        Assert.Empty(Coverage.Find(Stuck(Door), Known([door], shut)));

        // And a for: nobody can read is not one anybody can vouch for.
        var templated = Automation("templated", """
            {"alias":"Templated","triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"on","for":"{{ states('input_number.wait') | int }}"}]}
            """);
        Assert.Empty(Coverage.Find(Stuck(Door), Known([door], templated)));
    }

    [Fact]
    public void Going_quiet_is_covered_only_by_a_trigger_told_to_fire_on_it()
    {
        var dead = Build.Entity(Co2, "unavailable", Now.AddHours(-2), "Living room CO2");

        var offline = Automation("offline", """
            {"alias":"CO2 sensor offline","triggers":[{"trigger":"state","entity_id":"sensor.living_room_co2","to":"unavailable","for":"00:30:00"}]}
            """);
        Assert.Equal("fires when it becomes unavailable for 30 minutes", Assert.Single(Coverage.Find(Quiet(Co2), Known([dead], offline))).Why);

        // Written precisely so that it does NOT fire when the sensor drops out.
        var ignoring = Automation("ignoring", """
            {"alias":"CO2 changed","triggers":[{"trigger":"state","entity_id":"sensor.living_room_co2","not_to":["unavailable","unknown"]}]}
            """);

        // Its for: has not run out yet.
        var slow = Automation("slow", """{"alias":"Slow","triggers":[{"trigger":"state","entity_id":"sensor.living_room_co2","to":"unavailable","for":"03:00:00"}]}""");

        // A numeric trigger never fires on a sensor that has stopped reporting a number.
        var numeric = Automation("numeric", """{"alias":"High CO2","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000}]}""");

        Assert.Empty(Coverage.Find(Quiet(Co2), Known([dead], ignoring, slow, numeric)));
    }

    /// <summary>
    /// A template trigger names no entity that can be checked, and an area target names every entity in the
    /// room. Neither makes this an automation watching the fridge.
    /// </summary>
    [Fact]
    public void A_template_trigger_with_an_area_target_is_cover_for_nothing()
    {
        var dusk = Automation("dusk", """
            {"alias":"Kitchen lights at dusk","triggers":[{"trigger":"template","value_template":"{{ state_attr('sun.sun','elevation') < 3 }}"}],
             "actions":[{"action":"light.turn_on","target":{"area_id":"kitchen"}}]}
            """);

        Assert.Empty(Coverage.Find(High("sensor.fridge_temperature", 11, 4), Known([Reading("sensor.fridge_temperature", 11, "°C")], dusk)));
    }

    /// <summary>A blueprint keeps its triggers in the blueprint, and its inputs are mostly what it acts on.</summary>
    [Fact]
    public void A_blueprint_is_cover_for_nothing_it_was_given()
    {
        var blueprint = Automation("kitchen_motion", """
            {"alias":"Kitchen motion light","use_blueprint":{"path":"homeassistant/motion_light.yaml",
             "input":{"motion_entity":"binary_sensor.kitchen_motion","light_target":{"area_id":"kitchen"}}}}
            """);
        var motion = Build.Entity("binary_sensor.kitchen_motion", "unavailable", Now.AddHours(-2), "Kitchen motion", "motion");

        Assert.Empty(Coverage.Find(Quiet("binary_sensor.kitchen_motion"), Known([motion], blueprint)));
    }

    /// <summary>
    /// A device trigger built in the editor names its one entity by the registry's id. It covers that entity,
    /// and not the others on the same multisensor.
    /// </summary>
    [Fact]
    public void A_device_trigger_covers_only_the_entity_it_names()
    {
        var fan = Automation("hot_bedroom", """
            {"alias":"Hot bedroom fan","triggers":[{"trigger":"device","device_id":"dev-ms","domain":"sensor",
             "entity_id":"3b1f0c2d9e8a7b6c5d4e3f2a1b0c9d8e","type":"temperature","above":25}]}
            """);
        var temperature = Reading("sensor.bedroom_temperature", 28, "°C", registryId: "3b1f0c2d9e8a7b6c5d4e3f2a1b0c9d8e", deviceId: "dev-ms");
        var humidity = Reading("sensor.bedroom_humidity", 80, "%", registryId: "0f1e2d3c4b5a69788796a5b4c3d2e1f0", deviceId: "dev-ms");
        var known = Known([temperature, humidity], fan);

        Assert.Equal("fires when it goes above 25 °C", Assert.Single(Coverage.Find(High("sensor.bedroom_temperature", 28, 21), known)).Why);
        Assert.Empty(Coverage.Find(High("sensor.bedroom_humidity", 80, 45), known));

        // Without the registry to say which entity the id is, nothing is claimed.
        Assert.Empty(Coverage.Find(High("sensor.bedroom_temperature", 28, 21), Known([temperature with { RegistryId = null }], fan)));
    }

    [Fact]
    public void An_automation_or_a_trigger_that_is_switched_off_is_cover_for_nothing()
    {
        const string config = """{"alias":"High CO2 alert","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000}]}""";
        var reading = Reading(Co2, 1500);

        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([reading], Automation("off", config, state: "off"))));
        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([reading], Automation("broken", config, state: "unavailable"))));

        // Deleted since the configs were read: its entity is gone.
        var (deleted, _) = Automation("deleted", config);
        Assert.Empty(Coverage.Find(High(Co2, 1500), new KnownAutomations([deleted], [reading], Now, Now)));

        var disabled = Automation("disabled", """
            {"alias":"High CO2 alert","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000,"enabled":false}]}
            """);
        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([reading], disabled)));
    }

    /// <summary>The numeric trigger here watches the outdoor thermometer; the CO2 sensor is watched only for going offline.</summary>
    [Fact]
    public void One_triggers_kind_is_never_paired_with_another_triggers_entity()
    {
        var mixed = Automation("mixed", """
            {"alias":"Frost warning / CO2 offline","triggers":[
              {"trigger":"numeric_state","entity_id":"sensor.outdoor_temperature","below":0},
              {"trigger":"state","entity_id":"sensor.living_room_co2","to":"unavailable"}]}
            """);

        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500), Reading("sensor.outdoor_temperature", 5, "°C")], mixed)));
    }

    /// <summary>Whether its conditions hold cannot be known from here, so it is the weaker claim: said as one, and listed last.</summary>
    [Fact]
    public void An_automation_with_conditions_is_the_weaker_claim_and_comes_last()
    {
        var gated = Automation("a_daytime", """
            {"alias":"A daytime CO2 alert","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000}],
             "conditions":[{"condition":"time","after":"07:00:00","before":"22:00:00"}]}
            """);
        var always = Automation("z_always", """{"alias":"Z always","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1200}]}""");

        var covering = Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500)], gated, always));

        Assert.Equal(["Z always", "A daytime CO2 alert"], covering.Select(one => one.Alias));
        Assert.True(covering[1].Conditional);
        Assert.Equal("fires when it goes above 1000 ppm, if its conditions allow", covering[1].Why);

        // A condition switched off is no condition.
        var off = Automation("off_condition", """
            {"alias":"Off","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":1000}],
             "conditions":[{"condition":"time","after":"07:00:00","enabled":false}]}
            """);
        Assert.False(Assert.Single(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500)], off))).Conditional);
    }

    /// <summary>A concern names the shape of what the user is worried about, and only that shape answers it.</summary>
    [Fact]
    public void A_concern_is_answered_only_by_the_shape_of_its_own_rule()
    {
        const string freezer = "sensor.freezer_temperature";
        var tooWarm = Automation("too_warm", """{"alias":"Freezer too warm","triggers":[{"trigger":"numeric_state","entity_id":"sensor.freezer_temperature","above":-15}]}""");
        var offline = Automation("offline", """{"alias":"Freezer sensor offline","triggers":[{"trigger":"state","entity_id":"sensor.freezer_temperature","to":"unavailable"}]}""");

        // "Tell me if it stops reporting": a threshold alert cannot fire on a sensor that has stopped.
        var stopped = Finding(AnomalyKind.Concern, freezer, new { state = "unavailable", rule_kind = "Unavailable" });
        var dead = Build.Entity(freezer, "unavailable", Now.AddHours(-1), "Freezer");
        Assert.Empty(Coverage.Find(stopped, Known([dead], tooWarm)));
        Assert.Equal("Freezer sensor offline", Assert.Single(Coverage.Find(stopped, Known([dead], tooWarm, offline))).Alias);

        // "Tell me if it gets above -10": an offline alert is no answer to it.
        var warm = Finding(AnomalyKind.Concern, freezer, new { state = "-8", rule_kind = "Above" });
        var reading = Reading(freezer, -8, "°C");
        Assert.Empty(Coverage.Find(warm, Known([reading], offline)));
        Assert.Equal("Freezer too warm", Assert.Single(Coverage.Find(warm, Known([reading], tooWarm, offline))).Alias);

        // A concern raised before its rule's shape was recorded says nothing either way.
        Assert.Empty(Coverage.Find(Finding(AnomalyKind.Concern, freezer, new { state = "-8" }), Known([reading], tooWarm)));
    }

    /// <summary>One card for a whole outage is covered only when everything it speaks for is.</summary>
    [Fact]
    public void A_card_that_speaks_for_several_entities_is_covered_only_when_all_of_them_are()
    {
        var door = Build.Entity("binary_sensor.front_door", "unavailable", Now.AddHours(-2), "Front door", "door");
        var window = Build.Entity("binary_sensor.hall_window", "unavailable", Now.AddHours(-2), "Hall window", "window");
        var outage = Quiet("binary_sensor.front_door", "binary_sensor.hall_window");

        var doorOffline = Automation("door_offline", """{"alias":"Door offline","triggers":[{"trigger":"state","entity_id":"binary_sensor.front_door","to":"unavailable"}]}""");
        Assert.Empty(Coverage.Find(outage, Known([door, window], doorOffline)));

        var windowOffline = Automation("window_offline", """{"alias":"Window offline","triggers":[{"trigger":"state","entity_id":"binary_sensor.hall_window","to":"unavailable"}]}""");
        var covering = Coverage.Find(outage, Known([door, window], doorOffline, windowOffline));

        Assert.Equal(["Door offline", "Window offline"], covering.Select(one => one.Alias));
        Assert.Equal("fires when Hall window becomes unavailable", covering[1].Why);
    }

    [Fact]
    public void A_line_held_by_another_entity_is_read_from_it()
    {
        var alert = Automation("limit", """{"alias":"Over the limit","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","above":"input_number.co2_limit"}]}""");
        var limit = Build.Entity("input_number.co2_limit", "1200", Now.AddDays(-3));

        Assert.Equal("fires when it goes above 1200 ppm", Assert.Single(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500), limit], alert))).Why);
        Assert.Empty(Coverage.Find(High(Co2, 1500), Known([Reading(Co2, 1500), limit with { State = "unavailable" }], alert)));
    }

    /// <summary>A held-state card whose entity has since moved on is about to close; what fires on the new state is no answer to it.</summary>
    [Fact]
    public void A_held_state_finding_whose_entity_has_moved_on_is_not_answered()
    {
        var shut = Automation("shut", """{"alias":"Shut","triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"off","for":"00:10:00"}]}""");
        var door = Build.Entity(Door, "off", Now.AddHours(-1), "Freezer door", "door");

        Assert.Empty(Coverage.Find(Stuck(Door, "on"), Known([door], shut)));
    }

    /// <summary>
    /// Routines are already checked against the existing automations where they are found, and a missing
    /// entity is a repair rather than a suggestion. Neither goes near this.
    /// </summary>
    [Theory]
    [InlineData(AnomalyKind.Habit)]
    [InlineData(AnomalyKind.MissingEntity)]
    public void Routines_and_missing_entities_are_not_answered_here(AnomalyKind kind) =>
        Assert.Null(Coverage.Wanted(Finding(kind, Co2, new { current = 1500, median = 500 })));

    [Fact]
    public void The_inspector_reads_each_trigger_on_its_own()
    {
        using var document = JsonDocument.Parse("""
            {"alias":"x","triggers":[
               {"trigger":"numeric_state","entity_id":"Sensor.CO2, sensor.office_co2","above":1000,"below":"input_number.top","for":{"minutes":5}},
               {"trigger":"state","entity_id":["binary_sensor.door"],"to":["on"],"not_to":"unavailable","for":"00:10","enabled":false},
               {"trigger":"device","device_id":"dev","domain":"sensor","entity_id":"abc123","type":"temperature","above":25},
               {"trigger":"template","value_template":"{{ states('sensor.co2') | float > 1000 }}"},
               {"trigger":"state","entity_id":"sensor.co2","attribute":"battery","for":"{{ 5 }}"}],
             "actions":[{"action":"notify.notify","data":{"message":"hi","entity_id":"sensor.humidity"}}]}
            """);

        var inspection = AutomationInspector.Inspect(document.RootElement);
        var triggers = inspection.Triggers;

        Assert.Equal(["numeric_state", "state", "device", "template", "state"], triggers.Select(trigger => trigger.Kind));

        // Home Assistant splits a comma-separated list and lower-cases what it is given; so does this.
        Assert.Equal(["sensor.co2", "sensor.office_co2"], triggers[0].Entities);
        Assert.Equal("1000", triggers[0].Above);
        Assert.Equal("input_number.top", triggers[0].Below);
        Assert.Equal(TimeSpan.FromMinutes(5), triggers[0].For);

        Assert.Equal(["on"], triggers[1].To);
        Assert.Equal(["unavailable"], triggers[1].NotTo);
        Assert.Equal(TimeSpan.FromMinutes(10), triggers[1].For);
        Assert.False(triggers[1].Enabled);

        Assert.Equal(["abc123"], triggers[2].Entities);
        Assert.Equal(("sensor", "temperature"), (triggers[2].Domain, triggers[2].Type));

        Assert.Empty(triggers[3].Entities);

        Assert.Equal("battery", triggers[4].Attribute);
        Assert.True(triggers[4].ForUnreadable);
        Assert.Null(triggers[4].For);

        // The message's entity is named by the automation, and is nowhere among its triggers.
        Assert.Contains("sensor.humidity", inspection.Entities);
        Assert.DoesNotContain(triggers, trigger => trigger.Entities.Contains("sensor.humidity"));
        Assert.False(inspection.HasConditions);
    }

    [Fact]
    public void A_single_trigger_written_the_older_way_is_read_too()
    {
        using var document = JsonDocument.Parse("""{"alias":"x","trigger":{"platform":"state","entity_id":"binary_sensor.door","to":"on","for":90}}""");

        var trigger = Assert.Single(AutomationInspector.Inspect(document.RootElement).Triggers);

        Assert.Equal("state", trigger.Kind);
        Assert.Equal(["binary_sensor.door"], trigger.Entities);
        Assert.Equal(TimeSpan.FromSeconds(90), trigger.For);
        Assert.True(trigger.Enabled);
    }

    [Theory]
    [InlineData("""{"conditions":[]}""", false)]
    [InlineData("""{"conditions":[{"condition":"time","after":"07:00:00","enabled":false}]}""", false)]
    [InlineData("""{"conditions":[{"condition":"time","after":"07:00:00"}]}""", true)]
    [InlineData("""{"condition":"{{ is_state('input_boolean.guest','off') }}"}""", true)]
    [InlineData("""{"use_blueprint":{"path":"x.yaml","input":{}}}""", false)]
    public void Conditions_count_unless_they_are_switched_off(string config, bool expected)
    {
        using var document = JsonDocument.Parse(config);
        Assert.Equal(expected, AutomationInspector.Inspect(document.RootElement).HasConditions);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("\"00:10\"", 600)]
    [InlineData("\"01:30:00\"", 5400)]
    [InlineData("\"00:00:05.5\"", 5.5)]
    [InlineData("{\"hours\":1,\"minutes\":30}", 5400)]
    [InlineData("{\"milliseconds\":500}", 0.5)]
    public void Durations_are_read_in_the_shapes_home_assistant_accepts(string json, double seconds)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(TimeSpan.FromSeconds(seconds), AutomationInspector.Duration(document.RootElement));
    }

    [Theory]
    [InlineData("\"{{ states('input_number.wait') }}\"")]
    [InlineData("{\"minutes\":\"{{ 5 }}\"}")]
    [InlineData("\"soon\"")]
    [InlineData("-5")]
    public void A_duration_that_cannot_be_read_is_not_guessed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(AutomationInspector.Duration(document.RootElement));
    }
}

/// <summary>
/// The same question asked of automations as Home Assistant hands them over, through the real client: the
/// inspection, the widening, the registry ids and the cache all run as they do for real. Each config is one
/// a real house has, and all but the last of them name the entity without firing on what was found.
/// </summary>
public class CoverageThroughTheClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Serves automation configs by id, and counts every config request.</summary>
    private sealed class ConfigServer(Func<string, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        private int _reads;

        public int Reads => _reads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string prefix = "/api/config/automation/config/";
            var path = request.RequestUri!.AbsolutePath;

            var (status, body) = path.StartsWith(prefix, StringComparison.Ordinal)
                ? answer(Uri.UnescapeDataString(path[prefix.Length..]))
                : (HttpStatusCode.NotFound, "{}");

            if (path.StartsWith(prefix, StringComparison.Ordinal)) Interlocked.Increment(ref _reads);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (HomeAssistantClient Client, FakeSettings Settings) Client(HttpMessageHandler handler)
    {
        var settings = new FakeSettings();
        settings.Current.HomeAssistant.BaseUrl = "http://ha.test:8123";

        var secrets = new SecretStore(
            Path.Combine(Path.GetTempPath(), $"hs-coverage-test-{Guid.NewGuid():N}.json"),
            NullLogger<SecretStore>.Instance);

        return (new HomeAssistantClient(new HttpClient(handler), settings, secrets, new FakeTimeProvider(Now), NullLogger<HomeAssistantClient>.Instance), settings);
    }

    private static readonly Dictionary<string, string> Configs = new(StringComparer.Ordinal)
    {
        ["a1"] = """{"alias":"Hall light on motion","triggers":[{"trigger":"state","entity_id":"binary_sensor.hall_motion","to":"on"}],"actions":[{"action":"light.turn_on","target":{"entity_id":"light.hall"}}]}""",
        ["a2"] = """{"alias":"Kitchen lights at dusk","triggers":[{"trigger":"template","value_template":"{{ state_attr('sun.sun','elevation') < 3 }}"}],"actions":[{"action":"light.turn_on","target":{"area_id":"kitchen"}}]}""",
        ["a3"] = """{"alias":"Hot bedroom fan","triggers":[{"trigger":"device","device_id":"dev-ms","domain":"sensor","entity_id":"3b1f0c2d9e8a7b6c5d4e3f2a1b0c9d8e","type":"temperature","above":25}],"actions":[{"action":"fan.turn_on","target":{"entity_id":"fan.bedroom"}}]}""",
        ["a4"] = """{"alias":"Kitchen motion light","use_blueprint":{"path":"homeassistant/motion_light.yaml","input":{"motion_entity":"binary_sensor.kitchen_motion","light_target":{"area_id":"kitchen"}}}}""",
        ["a5"] = """{"alias":"CO2 changes","triggers":[{"trigger":"state","entity_id":"sensor.co2","not_to":["unavailable","unknown"]}],"actions":[{"action":"notify.notify","data":{"message":"CO2 changed"}}]}""",
        ["a6"] = """{"alias":"Frost warning / CO2 offline","triggers":[{"trigger":"numeric_state","entity_id":"sensor.outdoor_temperature","below":0},{"trigger":"state","entity_id":"sensor.living_room_co2","to":"unavailable"}],"actions":[]}""",
        ["a7"] = """{"alias":"CO2 sensor battery low","triggers":[{"trigger":"numeric_state","entity_id":"sensor.living_room_co2","value_template":"{{ state.attributes.battery_level }}","below":15}],"actions":[]}""",
        ["a8"] = """{"alias":"Nightly summary","triggers":[{"trigger":"time","at":"22:00:00"}],"conditions":[{"condition":"numeric_state","entity_id":"sensor.fridge_temperature","above":8}],"actions":[]}""",
        ["a9"] = """{"alias":"Freezer left open","triggers":[{"trigger":"state","entity_id":"binary_sensor.freezer_door","to":"on","for":"00:10:00"}],"actions":[]}""",
    };

    private static List<HaEntity> House() =>
    [
        Build.Entity("sensor.fridge_temperature", "11", Now.AddMinutes(-1), "Fridge", unit: "°C", areaId: "kitchen"),
        Build.Entity("binary_sensor.freezer_door", "on", Now.AddHours(-3), "Freezer door", "door", areaId: "kitchen"),
        Build.Entity("binary_sensor.kitchen_motion", "unavailable", Now.AddHours(-2), "Kitchen motion", "motion", areaId: "kitchen"),
        Build.Entity("light.kitchen", "off", Now.AddHours(-5), "Kitchen", areaId: "kitchen"),
        Build.Entity("binary_sensor.hall_motion", "unavailable", Now.AddHours(-4), "Hall motion", "motion"),
        Build.Entity("sensor.bedroom_temperature", "28", Now.AddMinutes(-1), "Bedroom temperature", unit: "°C", deviceId: "dev-ms", registryId: "3b1f0c2d9e8a7b6c5d4e3f2a1b0c9d8e"),
        Build.Entity("sensor.bedroom_humidity", "80", Now.AddMinutes(-1), "Bedroom humidity", unit: "%", deviceId: "dev-ms", registryId: "0f1e2d3c4b5a69788796a5b4c3d2e1f0"),
        Build.Entity("sensor.bedroom_battery", "unavailable", Now.AddHours(-2), "Bedroom battery", deviceId: "dev-ms"),
        Build.Entity("sensor.co2", "unavailable", Now.AddHours(-2), "CO2"),
        Build.Entity("sensor.living_room_co2", "1500", Now.AddMinutes(-1), "Living room CO2", unit: "ppm"),
        Build.Entity("sensor.outdoor_temperature", "5", Now.AddMinutes(-1), "Outdoor", unit: "°C"),
        .. Configs.Keys.Select(id => Build.Entity("automation." + id, "on", Now.AddDays(-7), automationConfigId: id)),
    ];

    private static Anomaly Finding(AnomalyKind kind, string entityId, object evidence) => new()
    {
        DedupKey = $"{kind}:{entityId}",
        EntityId = entityId,
        Kind = kind,
        Summary = "",
        SuggestedRequest = "",
        Status = AnomalyStatus.Open,
        EvidenceJson = JsonSerializer.Serialize(evidence),
    };

    [Fact]
    public async Task Only_a_trigger_that_fires_on_what_was_found_is_claimed()
    {
        var server = new ConfigServer(id => Configs.TryGetValue(id, out var body) ? (HttpStatusCode.OK, body) : (HttpStatusCode.NotFound, "{}"));
        var (client, _) = Client(server);
        var house = House();

        var known = new KnownAutomations(await client.GetAutomationsAsync(house, CancellationToken.None), house, Now, Now);

        string[] Claimed(Anomaly finding) => [.. Coverage.Find(finding, known).Select(one => one.Alias)];

        // A motion light, whose sensor has died.
        Assert.Empty(Claimed(Finding(AnomalyKind.Unavailable, "binary_sensor.hall_motion", new { state = "unavailable" })));

        // A dusk template lighting the kitchen, and the stock motion blueprint lighting it too: neither watches the fridge.
        Assert.Empty(Claimed(Finding(AnomalyKind.NumericOutlier, "sensor.fridge_temperature", new { current = 11, median = 4 })));
        Assert.Empty(Claimed(Finding(AnomalyKind.Unavailable, "binary_sensor.kitchen_motion", new { state = "unavailable" })));

        // A device trigger on the multisensor's temperature: not its humidity, and not its battery.
        Assert.Empty(Claimed(Finding(AnomalyKind.NumericOutlier, "sensor.bedroom_humidity", new { current = 80, median = 45 })));
        Assert.Empty(Claimed(Finding(AnomalyKind.Unavailable, "sensor.bedroom_battery", new { state = "unavailable" })));
        Assert.Equal(["Hot bedroom fan"], Claimed(Finding(AnomalyKind.NumericOutlier, "sensor.bedroom_temperature", new { current = 28, median = 21 })));

        // A trigger written to ignore the sensor going quiet; a numeric trigger on another sensor; a battery template.
        Assert.Empty(Claimed(Finding(AnomalyKind.Unavailable, "sensor.co2", new { state = "unavailable" })));
        Assert.Empty(Claimed(Finding(AnomalyKind.NumericOutlier, "sensor.living_room_co2", new { current = 1500, median = 600 })));

        // And the one that really does: open for three hours, where it fires after ten minutes. Only it.
        Assert.Equal(["Freezer left open"], Claimed(Finding(AnomalyKind.StuckState, "binary_sensor.freezer_door", new { state = "on" })));
    }

    /// <summary>
    /// An automation edited in Home Assistant is reloaded there, and a reloaded automation is a new state for
    /// its entity. That is what the automations are kept against, so an edit is read again at once, and the
    /// rest of the time nothing is -- not every five minutes for a scan that has seen it all before.
    /// </summary>
    [Fact]
    public async Task An_edited_automation_is_read_again_at_once_and_an_unchanged_house_not_at_all()
    {
        var server = new ConfigServer(id => Configs.TryGetValue(id, out var body) ? (HttpStatusCode.OK, body) : (HttpStatusCode.NotFound, "{}"));
        var (client, _) = Client(server);
        var house = House();

        await client.GetAutomationsAsync(house, CancellationToken.None);
        await client.GetAutomationsAsync(house, CancellationToken.None);
        Assert.Equal(Configs.Count, server.Reads);

        var index = house.FindIndex(entity => entity.EntityId == "automation.a9");
        house[index] = house[index] with { LastChanged = Now };
        await client.GetAutomationsAsync(house, CancellationToken.None);

        Assert.Equal(Configs.Count * 2, server.Reads);
    }

    [Fact]
    public async Task Another_home_assistant_is_not_described_by_the_last_ones_configs()
    {
        var server = new ConfigServer(id => Configs.TryGetValue(id, out var body) ? (HttpStatusCode.OK, body) : (HttpStatusCode.NotFound, "{}"));
        var (client, settings) = Client(server);
        var house = House();

        await client.GetAutomationsAsync(house, CancellationToken.None);
        settings.Current.HomeAssistant.BaseUrl = "http://other-ha.test:8123";
        await client.GetAutomationsAsync(house, CancellationToken.None);

        Assert.Equal(Configs.Count * 2, server.Reads);
    }

    /// <summary>A config that timed out is a gap in this answer, not a fact about the house, so the answer is used but not kept.</summary>
    [Fact]
    public async Task A_sweep_with_gaps_in_it_is_used_but_not_kept()
    {
        var server = new ConfigServer(id =>
            id == "a9" ? (HttpStatusCode.BadGateway, "{}")
            : Configs.TryGetValue(id, out var body) ? (HttpStatusCode.OK, body) : (HttpStatusCode.NotFound, "{}"));
        var (client, _) = Client(server);
        var house = House();

        var first = await client.GetAutomationsAsync(house, CancellationToken.None);
        Assert.DoesNotContain(first, automation => automation.EntityId == "automation.a9");
        Assert.Contains(first, automation => automation.EntityId == "automation.a1");

        await client.GetAutomationsAsync(house, CancellationToken.None);
        Assert.Equal(Configs.Count * 2, server.Reads);
    }
}
