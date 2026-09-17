using Housekeeper.Core;

namespace Housekeeper.Tests;

public class EntityIndexTests
{
    private static readonly List<HaEntity> Home =
    [
        Build.Entity("light.hall", friendlyName: "Hall Light", area: "Hallway"),
        Build.Entity("light.kitchen", friendlyName: "Kitchen Light", area: "Kitchen"),
        Build.Entity("person.sam", "home", friendlyName: "Sam"),
        Build.Entity("device_tracker.sam_phone", "home", friendlyName: "Sam's Phone"),
        Build.Entity("sensor.freezer_temperature", "-19", friendlyName: "Freezer Temperature", deviceClass: "temperature", unit: "°C"),
        Build.Entity("binary_sensor.freezer_door", friendlyName: "Freezer Door", deviceClass: "door"),
        Build.Entity("media_player.lounge_tv", friendlyName: "Lounge TV"),
        Build.Entity("vacuum.robot", friendlyName: "Robot"),
    ];

    [Fact]
    public void Finds_the_lights_and_the_presence_entities_for_the_canonical_request()
    {
        var shortlist = EntityIndex.Shortlist(Home, "Turn off lights when no one is home", 10);
        var ids = shortlist.Select(e => e.EntityId).ToList();

        Assert.Contains("light.hall", ids);
        Assert.Contains("light.kitchen", ids);
        Assert.Contains("person.sam", ids);
        Assert.DoesNotContain("vacuum.robot", ids);
    }

    [Fact]
    public void Finds_a_device_by_name_even_without_a_domain_word()
    {
        var ids = EntityIndex.Shortlist(Home, "the freezer door has been open too long", 10)
            .Select(e => e.EntityId)
            .ToList();

        Assert.Contains("binary_sensor.freezer_door", ids);
        Assert.Equal("binary_sensor.freezer_door", ids[0]);
    }

    [Fact]
    public void An_entity_id_written_out_in_full_is_always_included()
    {
        // This is what makes promoting an anomaly reliable: the suggestion names the entity verbatim.
        var shortlist = EntityIndex.Shortlist(Home, "Notify me when vacuum.robot stays 'error' for 10 minutes", 5);

        Assert.Equal("vacuum.robot", shortlist[0].EntityId);
    }

    [Fact]
    public void Pads_a_thin_shortlist_with_commonly_automated_entities_so_the_model_can_judge()
    {
        // Nothing here matches a token, yet "cozy at night" is obviously about lights and presence.
        var ids = EntityIndex.Shortlist(Home, "make the place cozy at night", 10).Select(e => e.EntityId).ToList();

        Assert.Contains("light.hall", ids);
        Assert.Contains("light.kitchen", ids);
        Assert.Contains("person.sam", ids);
        Assert.DoesNotContain("vacuum.robot", ids);
    }

    /// <summary>
    /// The padding was a strict sort by domain rank, which drains one domain before starting the next. In a
    /// real house — forty lights and switches is ordinary — a vague request filled the entire shortlist with
    /// lights, and the model never saw a thermostat, a lock, a cover or a sensor. The prompt then tells it
    /// that what it cannot see does not exist, so half the house became unreachable.
    /// </summary>
    [Fact]
    public void Padding_reaches_every_kind_of_thing_in_the_house_not_just_the_commonest()
    {
        List<HaEntity> crowded =
        [
            .. Enumerable.Range(0, 40).Select(i => Build.Entity($"light.bulb_{i:00}")),
            .. Enumerable.Range(0, 40).Select(i => Build.Entity($"switch.socket_{i:00}")),
            Build.Entity("climate.thermostat"),
            Build.Entity("lock.front_door"),
            Build.Entity("cover.garage"),
            Build.Entity("media_player.lounge"),
            Build.Entity("sensor.outside_temperature", "11.5"),
        ];

        var ids = EntityIndex.Shortlist(crowded, "make the place nicer", 40).Select(e => e.EntityId).ToList();

        Assert.Equal(40, ids.Count);
        foreach (var wanted in new[]
                 {
                     "climate.thermostat", "lock.front_door", "cover.garage",
                     "media_player.lounge", "sensor.outside_temperature",
                 })
            Assert.Contains(wanted, ids);

        // And the commonly automated domains still lead, so rank has not been thrown away.
        Assert.StartsWith("light.", ids[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Matched_entities_come_before_any_padding()
    {
        var ids = EntityIndex.Shortlist(Home, "freezer door", 10).Select(e => e.EntityId).ToList();

        Assert.Equal("binary_sensor.freezer_door", ids[0]);
        Assert.True(ids.Count > 2, "the thin match should have been padded");
    }

    [Fact]
    public void Honours_the_size_cap_so_prompts_stay_bounded()
    {
        var many = Enumerable.Range(0, 500)
            .Select(i => Build.Entity($"light.bulb_{i}", friendlyName: $"Bulb {i}"))
            .ToList();

        Assert.Equal(12, EntityIndex.Shortlist(many, "turn off the lights", 12).Count);
    }

    [Fact]
    public void Ranking_is_stable_for_equal_scores()
    {
        var first = EntityIndex.Shortlist(Home, "lights", 5).Select(e => e.EntityId);
        var second = EntityIndex.Shortlist(Home, "lights", 5).Select(e => e.EntityId);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("light.*", "light.hall", true)]
    [InlineData("light.*", "switch.hall", false)]
    [InlineData("*.freezer_*", "binary_sensor.freezer_door", true)]
    [InlineData("sensor.?", "sensor.a", true)]
    [InlineData("sensor.?", "sensor.ab", false)]
    [InlineData("*", "anything.at_all", true)]
    public void Globs_match_the_way_people_expect(string pattern, string value, bool expected) =>
        Assert.Equal(expected, EntityIndex.GlobMatch(pattern, value));

    [Fact]
    public void Filter_applies_includes_excludes_and_the_cap()
    {
        // Include only means anything with watch-everything turned off, which is what this is about.
        var options = new ScanOptions
        {
            IncludeAll = false,
            Include = ["light.*", "binary_sensor.*"],
            Exclude = ["light.kitchen"],
            MaxTrackedEntities = 10,
        };

        var ids = EntityIndex.Filter(Home, options).Select(e => e.EntityId).ToList();

        Assert.Equal(["binary_sensor.freezer_door", "light.hall"], ids);
    }

    /// <summary>
    /// Watch-everything is the default, so the thing that has to keep working is Exclude narrowing it — that
    /// is what the dashboard's Ignore buttons write to, and it is the only way to stop watching something
    /// without going back to naming everything you do want.
    /// </summary>
    [Fact]
    public void Watching_everything_still_honours_what_was_excluded()
    {
        var options = new ScanOptions { Exclude = ["light.kitchen", "vacuum.*"] };

        var ids = EntityIndex.Filter(Home, options).Select(e => e.EntityId).ToList();

        Assert.DoesNotContain("light.kitchen", ids);
        Assert.DoesNotContain("vacuum.robot", ids);
        Assert.Contains("light.hall", ids);
        Assert.Contains("binary_sensor.freezer_door", ids);

        // And an Include list is simply beside the point while everything is watched.
        options.Include = ["nothing.matches_this"];
        Assert.Equal(ids, EntityIndex.Filter(Home, options).Select(e => e.EntityId).ToList());
    }

    [Fact]
    public void Tokenizer_drops_noise_and_folds_plurals()
    {
        Assert.Equal(["light", "home"], EntityIndex.Tokenize("Turn off the lights when no one is home"));
    }
}

public class DuplicateFinderTests
{
    [Fact]
    public void Flags_an_automation_that_already_does_the_job()
    {
        var draft = Build.Draft("Lights off when away", ["light.hall", "person.sam"], ["light.turn_off"],
            new HashSet<string>(["state"], StringComparer.Ordinal));

        var existing = Build.Existing("1699", "Away lights off", ["light.hall", "person.sam"], ["state"]);

        var matches = DuplicateFinder.Find(draft, [existing]);

        Assert.Single(matches);
        Assert.True(matches[0].Score > 0.8);
        Assert.Contains("light.hall", matches[0].Reason);
    }

    [Fact]
    public void Ignores_an_automation_about_completely_different_things()
    {
        var draft = Build.Draft("Lights off when away", ["light.hall", "person.sam"]);
        var existing = Build.Existing("42", "Water the plants", ["switch.pump", "sensor.soil"], ["time"]);

        Assert.Empty(DuplicateFinder.Find(draft, [existing]));
    }

    [Fact]
    public void Partial_entity_overlap_alone_does_not_trip_the_warning()
    {
        var draft = Build.Draft("Hall light off at midnight", ["light.hall"], triggerKinds: new HashSet<string>(["time"], StringComparer.Ordinal));
        var existing = Build.Existing("7", "Morning routine", ["light.hall", "light.kitchen", "media_player.lounge_tv", "switch.kettle"], ["sun"]);

        Assert.Empty(DuplicateFinder.Find(draft, [existing]));
    }

    [Fact]
    public void Orders_the_closest_match_first()
    {
        var draft = Build.Draft("Lights off when away", ["light.hall", "person.sam"], triggerKinds: new HashSet<string>(["state"], StringComparer.Ordinal));

        var matches = DuplicateFinder.Find(draft,
        [
            Build.Existing("a", "Partly related", ["light.hall", "person.sam", "light.kitchen"], ["state"]),
            Build.Existing("b", "Lights off when away", ["light.hall", "person.sam"], ["state"]),
        ]);

        Assert.Equal("b", matches[0].AutomationId);
    }
}

public class YamlTests
{
    [Fact]
    public void Renders_an_automation_the_way_home_assistant_shows_it()
    {
        const string json = """
            {"alias":"Lights off when away","triggers":[{"trigger":"state","entity_id":"person.sam","to":"not_home"}],
             "conditions":[],"actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}],"mode":"single"}
            """;

        var yaml = Yaml.FromJson(json);

        Assert.Equal(
            """
            alias: Lights off when away
            triggers:
              - trigger: state
                entity_id: person.sam
                to: not_home
            conditions: []
            actions:
              - action: light.turn_off
                target:
                  entity_id: light.hall
            mode: single
            """,
            yaml.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Quotes_values_that_would_otherwise_change_meaning()
    {
        var yaml = Yaml.FromJson("""{"a":"on","b":"12","c":"yes","d":"has: colon","e":"plain"}""");

        Assert.Contains("a: 'on'", yaml);
        Assert.Contains("b: '12'", yaml);
        Assert.Contains("c: 'yes'", yaml);
        Assert.Contains("d: 'has: colon'", yaml);
        Assert.Contains("e: plain", yaml);
    }

    [Fact]
    public void Handles_numbers_booleans_nulls_and_empty_collections()
    {
        var yaml = Yaml.FromJson("""{"n":3.5,"b":true,"z":null,"arr":[],"obj":{}}""");

        Assert.Contains("n: 3.5", yaml);
        Assert.Contains("b: true", yaml);
        Assert.Contains("z: null", yaml);
        Assert.Contains("arr: []", yaml);
        Assert.Contains("obj: {}", yaml);
    }
}

public class AutomationInspectorTests
{
    [Fact]
    public void Reports_the_entities_and_trigger_kinds_of_an_existing_automation()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {
              "alias": "Existing",
              "trigger": [{"platform": "state", "entity_id": "binary_sensor.door"}],
              "action": [{"service": "light.turn_on", "target": {"entity_id": ["light.a", "light.b"]}}]
            }
            """);

        var (entities, kinds) = AutomationInspector.Inspect(document.RootElement);

        Assert.Equal(["binary_sensor.door", "light.a", "light.b"], entities.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["state"], kinds);
    }
}

public class OptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(new HousekeeperOptions().Validate().IsValid);
    }

    [Fact]
    public void Reports_every_problem_at_once_with_actionable_text()
    {
        var options = new HousekeeperOptions();
        options.HomeAssistant.BaseUrl = "not-a-url";
        options.Llm.Provider = "SkyNet";
        options.Api.Port = 70000;

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("BaseUrl"));
        Assert.Contains(result.Errors, e => e.Contains("SkyNet"));
        Assert.Contains(result.Errors, e => e.Contains("70000"));
    }

    /// <summary>
    /// A new install watches the whole house. Shipping with an empty watch list meant Housekeeper sat there
    /// noticing nothing until someone worked out that globs were the missing step, which reads as broken.
    /// </summary>
    [Fact]
    public void A_new_install_watches_everything()
    {
        var scan = new HousekeeperOptions().Scan;

        Assert.True(scan.IncludeAll);
        Assert.Empty(scan.Include);
        Assert.Empty(scan.Exclude);
    }

    [Fact]
    public void Warns_when_scanning_is_on_but_nothing_is_selected()
    {
        // Out of the box everything is watched, so there is nothing to warn about.
        Assert.DoesNotContain(new HousekeeperOptions().Validate().Warnings, w => w.Contains("observe nothing"));

        // The warning is for someone who has turned watch-everything off and not said what to watch instead.
        var narrowed = new HousekeeperOptions();
        narrowed.Scan.IncludeAll = false;

        var result = narrowed.Validate();

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("observe nothing"));
    }

    [Theory]
    [InlineData("Ollama", true)]
    [InlineData("ollama", true)]
    [InlineData("OpenAI", false)]
    [InlineData("openai-compatible", false)]
    public void Provider_names_are_matched_loosely(string provider, bool isOllama)
    {
        var options = new LlmOptions { Provider = provider };

        Assert.True(LlmOptions.IsSupportedProvider(provider));
        Assert.Equal(isOllama, options.IsOllama);
    }
}
