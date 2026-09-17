using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// The gate between a language model and someone's house. These are the tests that matter most.
/// </summary>
public class AutomationDraftingTests
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "light.hall", "light.kitchen", "person.sam", "binary_sensor.freezer_door",
    };

    private const string Valid = """
        {
          "alias": "Lights off when away",
          "description": "Turns the hall light off once nobody is home.",
          "triggers": [{"trigger": "state", "entity_id": "person.sam", "to": "not_home"}],
          "conditions": [],
          "actions": [{"action": "light.turn_off", "target": {"entity_id": "light.hall"}}],
          "mode": "single"
        }
        """;

    [Fact]
    public void Accepts_a_well_formed_draft()
    {
        var result = AutomationDrafting.Parse(Valid, Known);

        Assert.True(result.Succeeded);
        var draft = result.Draft!;
        Assert.Equal("Lights off when away", draft.Alias);
        Assert.Equal(["light.hall", "person.sam"], draft.Entities);
        Assert.Equal(["light.turn_off"], draft.Actions);
        Assert.Contains("state", draft.TriggerKinds);
    }

    [Fact]
    public void Rejects_entities_that_do_not_exist()
    {
        var raw = Valid.Replace("light.hall", "light.imaginary_ceiling", StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("light.imaginary_ceiling", result.Error);
        Assert.Contains("do not exist", result.Error);
    }

    [Fact]
    public void Rejects_device_and_area_targets_because_they_cannot_be_checked()
    {
        var raw = """
            {
              "alias": "Nope",
              "triggers": [{"trigger": "state", "entity_id": "person.sam"}],
              "actions": [{"action": "light.turn_off", "target": {"device_id": "abc123"}}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("device_id", result.Error);
    }

    [Fact]
    public void Strips_keys_it_did_not_validate()
    {
        var raw = """
            {
              "alias": "Sneaky",
              "id": "9999",
              "initial_state": true,
              "triggers": [{"trigger": "state", "entity_id": "person.sam"}],
              "actions": [{"action": "light.turn_off", "target": {"entity_id": "light.hall"}}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded);
        using var document = JsonDocument.Parse(result.Draft!.ConfigJson);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain("id", keys);
        Assert.DoesNotContain("initial_state", keys);
        Assert.Equal(["alias", "triggers", "conditions", "actions", "mode"], keys);
    }

    [Fact]
    public void Accepts_the_pre_2024_key_names()
    {
        var raw = """
            {
              "alias": "Legacy shape",
              "trigger": [{"platform": "state", "entity_id": "binary_sensor.freezer_door", "to": "on"}],
              "action": [{"service": "light.turn_on", "entity_id": "light.kitchen"}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded);
        Assert.Contains("state", result.Draft!.TriggerKinds);
        Assert.Equal(["light.turn_on"], result.Draft.Actions);
    }

    [Fact]
    public void Tolerates_prose_wrapped_around_the_json()
    {
        var result = AutomationDrafting.Parse($"Sure! Here you go:\n```json\n{Valid}\n```\nHope that helps.", Known);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("", "returned nothing")]
    [InlineData("no json at all", "no JSON object")]
    [InlineData("{ not json }", "malformed")]
    // A reply that opens an object and never closes it is what hitting the output-token cap looks like, so
    // it is worth saying that rather than "no JSON object", which sends the user looking at the wrong thing.
    [InlineData("{ unbalanced", "cut off")]
    [InlineData("""{"triggers":[{"trigger":"state","entity_id":"person.sam"}],"actions":[{"action":"a.b","entity_id":"light.hall"}]}""", "alias")]
    [InlineData("""{"alias":"x","actions":[{"action":"a.b","entity_id":"light.hall"}]}""", "triggers")]
    [InlineData("""{"alias":"x","triggers":[{"trigger":"state","entity_id":"person.sam"}]}""", "actions")]
    public void Rejects_unusable_output(string raw, string expected)
    {
        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_unknown_mode()
    {
        var raw = Valid.Replace("\"single\"", "\"chaotic\"", StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("chaotic", result.Error);
    }

    [Theory]
    [InlineData("floor_id", "ground_floor")]
    [InlineData("label_id", "everything_downstairs")]
    public void Rejects_floor_and_label_targets_because_one_wrong_value_actuates_the_whole_house(string key, string value)
    {
        var raw = """
            {
              "alias": "Nope",
              "triggers": [{"trigger": "state", "entity_id": "person.sam"}],
              "actions": [{"action": "light.turn_off", "target": {"KEY": "VALUE"}}]
            }
            """.Replace("KEY", key, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains(key, result.Error);
    }

    [Fact]
    public void Rejects_a_draft_that_targets_nothing_verifiable()
    {
        // A state trigger is about something. If the model could not name what, it has invented the whole
        // automation and there is nothing to check it against.
        var raw = """
            {
              "alias": "Empty",
              "triggers": [{"trigger": "state", "to": "on"}],
              "actions": [{"action": "notify.persistent_notification", "data": {"message": "hi"}}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("no entities", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_a_clock_only_automation_that_names_no_entity()
    {
        // "At eleven, remind me to take the bins out" is a real automation with nothing to target. Rejecting
        // it for naming no entity turned a whole category of reasonable request into a failure.
        var raw = """
            {
              "alias": "Bins",
              "triggers": [{"trigger": "time", "at": "23:00:00"}],
              "actions": [{"action": "notify.persistent_notification", "data": {"message": "Bins out."}}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Draft!.Entities);
        Assert.Contains("time", result.Draft.TriggerKinds);
    }

    /// <summary>
    /// A small model decorates its refusal at least as often as it writes the bare word. An exact match sent
    /// every decorated form down the automation path, where it failed for having no triggers — and THAT
    /// sentence went back to the model under a rule telling it to fix the one thing that was wrong. Which is
    /// how "turn off everything downstairs" stopped being a refusal and became an invented automation.
    /// </summary>
    [Theory]
    [InlineData("UNSUPPORTED: no area or floor targeting is available")]
    [InlineData("UNSUPPORTED - cannot target areas")]
    [InlineData("UNSUPPORTED.")]
    [InlineData("unsupported request")]
    [InlineData("  UNSUPPORTED  ")]
    public void A_refusal_is_taken_as_one_however_the_model_dressed_it_up(string alias)
    {
        var raw = $"{{\"alias\": {System.Text.Json.JsonSerializer.Serialize(alias)}}}";

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AutomationDrafting.Unsupported, result.Draft!.Alias);
    }

    [Fact]
    public void What_the_model_wrote_after_the_marker_is_kept_as_the_explanation()
    {
        // It is the only account the user gets of why their request could not be built.
        var result = AutomationDrafting.Parse(
            """{"alias":"UNSUPPORTED: no water leak sensor appears in this house"}""", Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("no water leak sensor appears in this house", result.Draft!.Description);
    }

    /// <summary>
    /// Every automation is full of objects that parse — the trigger, the target, the data payload. Accepting
    /// the first one that merely parsed meant a malformed outer object (the commonest slip a small model
    /// makes) fell through to a nested one, and the draft was refused for "no usable 'alias'". The alias was
    /// fine; the JSON was not. The repair loop then spent every remaining attempt fixing the wrong thing.
    /// </summary>
    [Fact]
    public void A_malformed_reply_is_reported_as_malformed_rather_than_as_a_missing_alias()
    {
        // A trailing comma after "mode" — valid-looking to a model, fatal to a parser. Inside it sit two
        // perfectly parseable objects: the trigger and the target.
        var raw = """
            {"alias":"Lights off","triggers":[{"trigger":"state","entity_id":"person.sam","to":"not_home"}],
             "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}],"mode":"single",}
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("malformed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alias", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one mistake the prompt singles out as always wrong, and nothing was enforcing it. A state trigger
    /// compares text, so this is a legal automation that can never fire: Home Assistant accepts it, it is
    /// written into the user's home, and nothing ever notices it doing nothing.
    /// </summary>
    [Theory]
    [InlineData("\"2.4\"")]
    [InlineData("\"25\"")]
    [InlineData("2.4")]
    public void Refuses_a_state_trigger_that_compares_against_a_number(string value)
    {
        var raw = """
            {
              "alias": "Never fires",
              "triggers": [{"trigger": "state", "entity_id": "person.sam", "to": VALUE}],
              "actions": [{"action": "light.turn_off", "target": {"entity_id": "light.hall"}}]
            }
            """.Replace("VALUE", value, StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("never fire", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("numeric_state", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_word_in_to_is_still_perfectly_fine()
    {
        var raw = """
            {
              "alias": "Fine",
              "triggers": [{"trigger": "state", "entity_id": "person.sam", "to": "not_home"}],
              "actions": [{"action": "light.turn_off", "target": {"entity_id": "light.hall"}}]
            }
            """;

        Assert.True(AutomationDrafting.Parse(raw, Known).Succeeded);
    }

    /// <summary>
    /// Verifiable is not the same as safe. Home Assistant really does offer these, so the existence check
    /// waves them through — but an automation that restarts Home Assistant is not something a language model
    /// should be able to reach for while drafting "turn the hall light off".
    /// </summary>
    [Theory]
    [InlineData("homeassistant.restart")]
    [InlineData("homeassistant.stop")]
    [InlineData("homeassistant.reload_all")]
    [InlineData("homeassistant.set_location")]
    [InlineData("hassio.host_reboot")]
    public void Refuses_a_service_that_acts_on_home_assistant_itself(string service)
    {
        var raw = Valid.Replace("light.turn_off", service, StringComparison.Ordinal);
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { service };

        var result = AutomationDrafting.Parse(raw, Known, services);

        Assert.False(result.Succeeded);
        Assert.Contains("Home Assistant itself", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ordinary_homeassistant_services_are_still_allowed()
    {
        // homeassistant.turn_off works on any entity and is genuinely useful; it is the installation-wide
        // ones that are refused, not the whole domain.
        var raw = Valid.Replace("light.turn_off", "homeassistant.turn_off", StringComparison.Ordinal);
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "homeassistant.turn_off" };

        Assert.True(AutomationDrafting.Parse(raw, Known, services).Succeeded);
    }

    [Fact]
    public void The_scene_shorthand_carries_an_entity_id_that_is_checked_like_any_other()
    {
        // Home Assistant's script syntax allows "- scene: scene.morning", where the id sits under its own
        // key. It was never visited, so an invented scene reached the written config unverified.
        var raw = """
            {
              "alias": "Morning",
              "triggers": [{"trigger": "time", "at": "07:00:00"}],
              "actions": [{"scene": "scene.morning"}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("scene.morning", result.Error, StringComparison.Ordinal);

        // And when it does exist it is reported as something the automation touches.
        var known = new HashSet<string>(Known, StringComparer.Ordinal) { "scene.morning" };
        var accepted = AutomationDrafting.Parse(raw, known);

        Assert.True(accepted.Succeeded, accepted.Error);
        Assert.Contains("scene.morning", accepted.Draft!.Entities);
    }

    [Fact]
    public void Takes_the_models_refusal_at_its_word_without_demanding_a_trigger()
    {
        // The escape hatch only works if the shape the prompt asks for is the shape that is accepted. An
        // UNSUPPORTED reply has no triggers and no actions by definition; validating it like an automation
        // rejected it, the repair loop asked again, and the model eventually invented something instead.
        var raw = """
            {"alias": "UNSUPPORTED", "description": "No water leak sensor appears in AVAILABLE_ENTITIES."}
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AutomationDrafting.Unsupported, result.Draft!.Alias);
        Assert.Contains("water leak", result.Draft.Description);
        Assert.Empty(result.Draft.Entities);
    }

    [Fact]
    public void Reads_entity_lists_as_well_as_single_values()
    {
        var raw = """
            {
              "alias": "Both lights",
              "triggers": [{"trigger": "state", "entity_id": "person.sam", "to": "not_home"}],
              "actions": [{"action": "light.turn_off", "target": {"entity_id": ["light.hall", "light.kitchen"]}}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded);
        Assert.Equal(["light.hall", "light.kitchen", "person.sam"], result.Draft!.Entities);
    }

    /// <summary>
    /// A small model very often writes one trigger as an object rather than a one-element array. The meaning
    /// is unambiguous, so it is accepted and normalised rather than costing an attempt.
    /// </summary>
    [Fact]
    public void Accepts_a_single_trigger_written_as_a_bare_object_and_normalises_it()
    {
        var raw = """
            {
              "alias": "One of each",
              "triggers": {"trigger": "state", "entity_id": "person.sam", "to": "not_home"},
              "actions": {"action": "light.turn_off", "target": {"entity_id": "light.hall"}}
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded);
        Assert.Contains("state", result.Draft!.TriggerKinds);
        Assert.Equal(["light.hall", "person.sam"], result.Draft.Entities);

        // What reaches Home Assistant is the array form regardless of how it arrived.
        using var document = JsonDocument.Parse(result.Draft.ConfigJson);
        foreach (var block in new[] { "triggers", "conditions", "actions" })
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty(block).ValueKind);

        Assert.Equal(1, document.RootElement.GetProperty("triggers").GetArrayLength());
    }

    [Fact]
    public void Rejects_a_service_this_home_assistant_does_not_have_and_names_the_ones_it_does()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "light.turn_on", "light.turn_off", "light.toggle", "notify.persistent_notification",
        };

        var raw = Valid.Replace("light.turn_off", "light.dim_slowly", StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known, services);

        Assert.False(result.Succeeded);
        Assert.Contains("light.dim_slowly", result.Error);
        Assert.Contains("light.turn_off", result.Error);
    }

    [Fact]
    public void Accepts_a_service_that_exists()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "light.turn_off" };

        Assert.True(AutomationDrafting.Parse(Valid, Known, services).Succeeded);
    }

    [Fact]
    public void Skips_the_service_check_when_the_list_could_not_be_read()
    {
        var raw = Valid.Replace("light.turn_off", "light.dim_slowly", StringComparison.Ordinal);

        Assert.True(AutomationDrafting.Parse(raw, Known, new HashSet<string>()).Succeeded);
        Assert.True(AutomationDrafting.Parse(raw, Known, null).Succeeded);
    }

    /// <summary>
    /// A templated service name is only resolved when the automation runs, so nothing here can say what it
    /// will call. That is the same problem as device_id and area_id, and it gets the same answer: every other
    /// target in a draft is checked, so the one thing that actually acts cannot be the exception.
    /// </summary>
    [Fact]
    public void Refuses_a_templated_service_name_because_nothing_can_check_what_it_will_call()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "light.turn_off" };
        var raw = Valid.Replace("light.turn_off", "{{ states('input_text.service') }}", StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known, services);

        Assert.False(result.Succeeded);
        Assert.Contains("template", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_template_anywhere_but_the_service_name_is_left_alone()
    {
        // Templates are how Home Assistant automations say anything interesting. Only the name of the thing
        // being called has to be a fixed, checkable value.
        var raw = """
            {
              "alias": "Tell me which door",
              "triggers": [{"trigger": "state", "entity_id": "binary_sensor.freezer_door", "to": "on"}],
              "actions": [{"action": "notify.notify", "data": {"message": "{{ trigger.entity_id }} opened"}}]
            }
            """;

        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notify.notify" };

        Assert.True(AutomationDrafting.Parse(raw, Known, services).Succeeded);
    }

    /// <summary>
    /// Prose around the JSON is common from a small model. Slicing from the first brace to the last spanned
    /// any brace the model used in its own sentence, and turned a perfectly good answer into "malformed JSON".
    /// </summary>
    [Theory]
    [InlineData("Here is the config {as you asked for}: RAW")]
    [InlineData("RAW Let me know if you want it changed :-}")]
    [InlineData("```json\nRAW\n```")]
    [InlineData("{ thinking out loud } RAW trailing words {")]
    public void The_object_is_found_among_whatever_the_model_said_around_it(string wrapper)
    {
        var raw = wrapper.Replace("RAW", Valid.ReplaceLineEndings(" "), StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Lights off when away", result.Draft!.Alias);
    }

    [Fact]
    public void A_brace_inside_a_string_does_not_end_the_object()
    {
        var raw = Valid.Replace("Lights off when away", "Lights off when away {home}", StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Lights off when away {home}", result.Draft!.Alias);
    }

    /// <summary>
    /// The alias is the sentence someone reads before letting an automation into their house. A right-to-left
    /// override inside it reverses how the rest is drawn, so it can be made to read as something it is not.
    /// </summary>
    [Fact]
    public void An_alias_cannot_hide_what_it_says_behind_invisible_characters()
    {
        var raw = Valid.Replace("Lights off when away", "Turn ‮on sthgil lla‬", StringComparison.Ordinal);

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.True(result.Succeeded, result.Error);
        Assert.DoesNotContain('‮', result.Draft!.Alias);
        Assert.DoesNotContain('‬', result.Draft.Alias);
    }

    /// <summary>
    /// The depth limit counts JSON nodes, not automation constructs, and a choose holding a sequence holding
    /// a mobile notification payload is a dozen levels on its own. At 12 the validator refused automations
    /// Home Assistant is perfectly happy with.
    /// </summary>
    [Fact]
    public void A_realistic_nested_automation_is_not_rejected_for_its_depth()
    {
        var raw = """
            {
              "alias": "Freezer door escalation",
              "triggers": [{"trigger": "state", "entity_id": "binary_sensor.freezer_door", "to": "on", "for": "00:05:00"}],
              "conditions": [],
              "actions": [
                {
                  "choose": [
                    {
                      "conditions": [{"condition": "state", "entity_id": "person.sam", "state": "home"}],
                      "sequence": [
                        {
                          "action": "notify.notify",
                          "data": {
                            "message": "The freezer door is open.",
                            "data": {
                              "actions": [
                                {"action": "CLOSE_IT", "title": "I closed it"},
                                {"action": "SNOOZE", "title": "Remind me later"}
                              ]
                            }
                          }
                        }
                      ]
                    }
                  ]
                }
              ],
              "mode": "single"
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notify.notify" });

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["binary_sensor.freezer_door", "person.sam"], result.Draft!.Entities);
    }

    /// <summary>
    /// Home Assistant reuses the key "action" inside a notification payload for a button the reader can tap.
    /// Those are not service calls, and reading them as such rejected a perfectly good automation.
    /// </summary>
    [Fact]
    public void A_tappable_notification_button_is_not_mistaken_for_a_service_call()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notify.mobile_app_phone" };

        var raw = """
            {
              "alias": "Freezer door",
              "triggers": [{"trigger": "state", "entity_id": "binary_sensor.freezer_door", "to": "on"}],
              "actions": [{
                "action": "notify.mobile_app_phone",
                "data": {
                  "message": "The freezer door is open.",
                  "data": {"actions": [{"action": "CLOSE_IT", "title": "Remind me later"}]}
                }
              }]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known, services);

        Assert.True(result.Succeeded, result.Error);

        // And it is not reported to the user as something the automation calls.
        Assert.Equal(["notify.mobile_app_phone"], result.Draft!.Actions);
    }

    /// <summary>
    /// "Warn me after two hours, then every thirty minutes" needs a repeat loop, which nests a condition and
    /// a service call several levels down inside an action. The validator has to see through that: the
    /// entity in the while-condition counts, the delay is not a service, and the shape survives rebuilding.
    /// </summary>
    [Fact]
    public void Accepts_a_repeating_reminder_and_reads_what_is_buried_in_it()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notify.notify" };

        var raw = """
            {
              "alias": "Range left running",
              "description": "Warns after two hours, then every half hour until it is off.",
              "triggers": [{"trigger": "state", "entity_id": "binary_sensor.freezer_door", "to": "on", "for": "02:00:00"}],
              "conditions": [],
              "actions": [{
                "repeat": {
                  "while": [{"condition": "state", "entity_id": "binary_sensor.freezer_door", "state": "on"}],
                  "sequence": [
                    {"action": "notify.notify", "data": {"message": "Still running."}},
                    {"delay": "00:30:00"}
                  ]
                }
              }],
              "mode": "single"
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known, services);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["binary_sensor.freezer_door"], result.Draft!.Entities);

        // The delay is not a service call, and the one real call was found inside the repeat.
        Assert.Equal(["notify.notify"], result.Draft.Actions);

        // The loop survives being rebuilt from only the validated keys.
        using var document = JsonDocument.Parse(result.Draft.ConfigJson);
        var repeat = document.RootElement.GetProperty("actions")[0].GetProperty("repeat");
        Assert.Equal("00:30:00", repeat.GetProperty("sequence")[1].GetProperty("delay").GetString());
        Assert.Equal(1, repeat.GetProperty("while").GetArrayLength());
    }

    [Fact]
    public void The_prompt_shows_the_model_how_to_repeat_a_reminder()
    {
        // The pattern is not obvious to a small model, and a request that asks for it is common.
        Assert.Contains("\"repeat\"", Prompts.System, StringComparison.Ordinal);
        Assert.Contains("every N after that", Prompts.System, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_output_that_is_too_large()
    {
        var result = AutomationDrafting.Parse(new string('x', 20_000), Known);

        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Error);
    }
}
