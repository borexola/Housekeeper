using System.Text.Json;
using HearthSense.Core;

namespace HearthSense.Tests;

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
    [InlineData("{ unbalanced", "no JSON object")]
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

    [Fact]
    public void Rejects_a_draft_that_targets_nothing_verifiable()
    {
        var raw = """
            {
              "alias": "Empty",
              "triggers": [{"trigger": "time", "at": "23:00:00"}],
              "actions": [{"action": "notify.persistent_notification", "data": {"message": "hi"}}]
            }
            """;

        var result = AutomationDrafting.Parse(raw, Known);

        Assert.False(result.Succeeded);
        Assert.Contains("no entities", result.Error, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Rejects_output_that_is_too_large()
    {
        var result = AutomationDrafting.Parse(new string('x', 20_000), Known);

        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Error);
    }
}
