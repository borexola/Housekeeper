using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// The readback is what most people will actually check before pressing confirm, so it has to say what the
/// config says and nothing else. Each case here is a shape the prompt teaches the model to write.
/// </summary>
public class NarratorTests
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["binary_sensor.hall_motion"] = "Hall motion",
        ["light.hall"] = "Hall light",
        ["light.porch"] = "Porch light",
        ["person.sam"] = "Sam",
        ["sensor.freezer_temperature"] = "Freezer temperature",
        ["lock.back_door"] = "Back door lock",
        ["binary_sensor.kitchen_range_running"] = "Kitchen range",
        ["climate.living_room"] = "Living room thermostat",
    };

    private static string? NameOf(string id) => Names.GetValueOrDefault(id);

    private static Narrative Tell(string json) => Assert.IsType<Narrative>(AutomationNarrator.Describe(json, NameOf));

    [Fact]
    public void A_state_trigger_held_for_a_while_reads_as_one_sentence()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"state","entity_id":"person.sam","to":"not_home","for":"00:05:00"}],
             "conditions":[],"actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}],"mode":"single"}
            """);

        Assert.Equal(["Sam has been away for 5 minutes"], story.When);
        Assert.Empty(story.OnlyIf);
        Assert.Equal(["Turn off Hall light"], story.Then);
    }

    [Fact]
    public void A_numeric_threshold_and_a_notification_are_said_with_their_values()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"numeric_state","entity_id":"sensor.freezer_temperature","above":-12,"for":"00:10:00"}],
             "conditions":[],"actions":[{"action":"notify.notify","data":{"message":"The freezer is above -12 degrees."}}],"mode":"single"}
            """);

        Assert.Equal(["Freezer temperature goes above -12 for 10 minutes"], story.When);
        Assert.Equal(["Send a notification: “The freezer is above -12 degrees.”"], story.Then);
    }

    [Fact]
    public void A_time_trigger_with_a_state_condition()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"time","at":"23:00:00"}],
             "conditions":[{"condition":"state","entity_id":"lock.back_door","state":"unlocked"}],
             "actions":[{"action":"notify.mobile_app_pixel","data":{"title":"Door","message":"The back door is still unlocked."}}],"mode":"single"}
            """);

        Assert.Equal(["at 23:00"], story.When);
        Assert.Equal(["Back door lock is unlocked"], story.OnlyIf);
        Assert.Equal(["Send a notification via mobile app pixel titled “Door”: “The back door is still unlocked.”"], story.Then);
    }

    [Fact]
    public void A_motion_light_with_a_delay_and_a_sun_condition()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"state","entity_id":"binary_sensor.hall_motion","to":"on"}],
             "conditions":[{"condition":"sun","after":"sunset"}],
             "actions":[{"action":"light.turn_on","target":{"entity_id":"light.hall"},"data":{"brightness_pct":60}},{"delay":"00:02:00"},{"action":"light.turn_off","target":{"entity_id":"light.hall"}}],
             "mode":"restart"}
            """);

        Assert.Equal(["Hall motion turns on"], story.When);
        Assert.Equal(["it is after sunset"], story.OnlyIf);
        Assert.Equal(["Turn on Hall light at 60% brightness", "Wait 2 minutes", "Turn off Hall light"], story.Then);
    }

    [Fact]
    public void A_repeat_while_the_thing_is_still_on()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"state","entity_id":"binary_sensor.kitchen_range_running","to":"on","for":"02:00:00"}],
             "conditions":[],
             "actions":[{"repeat":{"while":[{"condition":"state","entity_id":"binary_sensor.kitchen_range_running","state":"on"}],
                        "sequence":[{"action":"notify.notify","data":{"message":"Still running."}},{"delay":"00:30:00"}]}}],"mode":"single"}
            """);

        Assert.Equal(["Kitchen range has been on for 2 hours"], story.When);
        Assert.Equal(["Repeat while Kitchen range is on: send a notification: “Still running.”, then wait 30 minutes"], story.Then);
    }

    [Fact]
    public void Two_triggers_that_branch_are_named_by_what_started_them_rather_than_by_id()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"sun","event":"sunset","id":"dusk"},{"trigger":"time","at":"00:00:00","id":"late"}],
             "conditions":[],
             "actions":[{"choose":[
               {"conditions":[{"condition":"trigger","id":"dusk"}],"sequence":[{"action":"light.turn_on","target":{"entity_id":"light.porch"}}]},
               {"conditions":[{"condition":"trigger","id":"late"}],"sequence":[{"action":"light.turn_off","target":{"entity_id":"light.porch"}}]}]}],
             "mode":"single"}
            """);

        Assert.Equal(["at sunset", "at 00:00"], story.When);
        Assert.Equal(["If the trigger was “at sunset”: turn on Porch light. If the trigger was “at 00:00”: turn off Porch light"], story.Then);
    }

    [Fact]
    public void Sun_offsets_time_windows_and_weekdays_are_said_in_words()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"sun","event":"sunset","offset":"-00:30:00"}],
             "conditions":[{"condition":"time","after":"22:00:00","before":"06:00:00","weekday":["mon","tue","wed","thu","fri"]},
                           {"condition":"sun","after":"sunset","before":"sunrise"}],
             "actions":[{"action":"climate.set_temperature","target":{"entity_id":"climate.living_room"},"data":{"temperature":18}}],"mode":"single"}
            """);

        Assert.Equal(["30 minutes before sunset"], story.When);
        Assert.Equal(["the time is between 22:00 and 06:00 and on weekdays", "it is dark"], story.OnlyIf);
        Assert.Equal(["Set Living room thermostat to 18°"], story.Then);
    }

    [Fact]
    public void An_entity_with_no_known_name_is_said_as_words_with_its_domain()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"state","entity_id":"binary_sensor.garage_door","to":"on","for":{"minutes":15}}],
             "actions":[{"action":"switch.turn_on","target":{"entity_id":["switch.fan_1","switch.fan_2"]}}]}
            """);

        Assert.Equal(["garage door (binary sensor) has been on for 15 minutes"], story.When);
        Assert.Equal(["Turn on fan 1 (switch) and fan 2 (switch)"], story.Then);
    }

    [Fact]
    public void Time_patterns_and_unknown_services_still_read_sensibly()
    {
        var story = Tell("""
            {"alias":"x","triggers":[{"trigger":"time_pattern","minutes":"/15"}],
             "actions":[{"action":"vacuum.start","target":{"entity_id":"vacuum.robo"}},{"action":"custom.do_thing","data":{"x":1}},{"stop":"done"}]}
            """);

        Assert.Equal(["every 15 minutes"], story.When);
        Assert.Equal(["Start robo (vacuum)", "Call custom.do_thing", "Stop: done"], story.Then);
    }

    [Fact]
    public void Nothing_readable_gives_nothing_rather_than_throwing()
    {
        Assert.Null(AutomationNarrator.Describe(null, NameOf));
        Assert.Null(AutomationNarrator.Describe("", NameOf));
        Assert.Null(AutomationNarrator.Describe("not json", NameOf));
        Assert.Null(AutomationNarrator.Describe("[1,2]", NameOf));

        var empty = Tell("{}");
        Assert.Empty(empty.When);
        Assert.Empty(empty.Then);
    }

    [Theory]
    [InlineData("00:15:00", "15 minutes")]
    [InlineData("02:00:00", "2 hours")]
    [InlineData("1:30:00", "1 hour 30 minutes")]
    [InlineData("00:00:45", "45 seconds")]
    [InlineData("90", "1 minute 30 seconds")]
    [InlineData("2 days, 01:00:00", "2 days 1 hour")]
    [InlineData("{{ states('input_number.x') }}", null)]
    public void Durations_are_said_exactly(string written, string? expected) =>
        Assert.Equal(expected, AutomationNarrator.Span(written));
}
