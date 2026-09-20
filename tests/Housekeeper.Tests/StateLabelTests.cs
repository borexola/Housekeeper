using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// The wording here is Home Assistant's, lifted from its own <c>binary_sensor</c> and per-domain state
/// strings, so the same sensor reads the same in both places. These cases are the ones where the raw state
/// and the shown state part company — which is nearly every binary sensor a house has.
/// </summary>
public class StateLabelTests
{
    [Theory]
    // The one from the report: a pantry door that a card called "On".
    [InlineData("door", "on", "open")]
    [InlineData("door", "off", "closed")]
    [InlineData("window", "on", "open")]
    [InlineData("garage_door", "off", "closed")]
    [InlineData("opening", "on", "open")]
    [InlineData("motion", "on", "detected")]
    [InlineData("motion", "off", "clear")]
    [InlineData("occupancy", "on", "detected")]
    [InlineData("smoke", "on", "detected")]
    [InlineData("moisture", "on", "wet")]
    [InlineData("moisture", "off", "dry")]
    [InlineData("battery", "on", "low")]
    [InlineData("battery", "off", "normal")]
    [InlineData("battery_charging", "off", "not charging")]
    [InlineData("connectivity", "off", "disconnected")]
    [InlineData("plug", "on", "plugged in")]
    [InlineData("running", "off", "not running")]
    [InlineData("safety", "on", "unsafe")]
    [InlineData("heat", "on", "hot")]
    [InlineData("cold", "off", "normal")]
    [InlineData("light", "on", "light detected")]
    [InlineData("light", "off", "no light")]
    [InlineData("tamper", "on", "tampering detected")]
    [InlineData("update", "on", "update available")]
    [InlineData("update", "off", "up-to-date")]
    // A binary sensor with no device class, and one whose class Home Assistant words as on and off anyway.
    [InlineData(null, "on", "on")]
    [InlineData("power", "on", "on")]
    public void Words_a_binary_sensor_the_way_home_assistant_does(string? deviceClass, string state, string expected) =>
        Assert.Equal(expected, Ha.StateLabel("binary_sensor", deviceClass, state));

    /// <summary>
    /// Two classes worth pinning because reasoning them out gets them backwards: a lock binary sensor is a
    /// contact, so its <c>on</c> is unlocked, and a presence one is home and away rather than detected.
    /// </summary>
    [Theory]
    [InlineData("lock", "on", "unlocked")]
    [InlineData("lock", "off", "locked")]
    [InlineData("presence", "on", "home")]
    [InlineData("presence", "off", "away")]
    [InlineData("problem", "off", "OK")]
    public void Keeps_the_classes_that_read_backwards_the_way_they_really_are(string deviceClass, string state, string expected) =>
        Assert.Equal(expected, Ha.StateLabel("binary_sensor", deviceClass, state));

    [Theory]
    [InlineData("device_tracker.sam_phone", "not_home", "away")]
    [InlineData("person.sam", "home", "home")]
    [InlineData("vacuum.robot", "returning", "returning to dock")]
    [InlineData("update.hub_firmware", "on", "update available")]
    [InlineData("climate.lounge", "heat_cool", "heat/cool")]
    [InlineData("alarm_control_panel.house", "armed_home", "armed home")]
    [InlineData("sun.sun", "below_horizon", "below horizon")]
    [InlineData("cover.blind", "closed", "closed")]
    [InlineData("lock.front", "locked", "locked")]
    [InlineData("light.hall", "on", "on")]
    public void Words_the_other_domains_the_way_home_assistant_does(string entityId, string state, string expected) =>
        Assert.Equal(expected, Ha.StateLabel(Ha.DomainOf(entityId), null, state));

    /// <summary>
    /// A device tracker's state is the name of a zone, which is a place rather than a word: "Work" is what
    /// Home Assistant shows and what it stays here, underscores and all left to the general rule.
    /// </summary>
    [Fact]
    public void Leaves_a_state_that_is_really_a_name_alone() =>
        Assert.Equal("Work", Ha.StateLabel("device_tracker", null, "Work"));

    [Theory]
    [InlineData("unavailable", "unavailable")]
    [InlineData("unknown", "unknown")]
    [InlineData("", "unavailable")]
    [InlineData("  ", "unavailable")]
    public void Says_something_for_an_entity_that_is_not_reporting(string state, string expected) =>
        Assert.Equal(expected, Ha.StateLabel("binary_sensor", "door", state));

    /// <summary>
    /// The label belongs in the middle of a sentence, because the pages that start one with it capitalise
    /// for themselves — and capitalising "OK" that way leaves it as OK, which lowercasing it would not.
    /// </summary>
    [Fact]
    public void Comes_back_ready_for_the_middle_of_a_sentence()
    {
        Assert.Equal("open", Build.Entity("binary_sensor.pantry_door", "on", deviceClass: "door").StateLabel);
        Assert.Equal("OK", Build.Entity("binary_sensor.pump", "off", deviceClass: "problem").StateLabel);
    }

    /// <summary>
    /// The line that separates the two: what a person reads changes, and what the drafter is handed does
    /// not. A trigger written against "open" waits for a state a binary sensor never reaches.
    /// </summary>
    [Fact]
    public void Leaves_the_state_an_automation_is_written_against_alone()
    {
        var door = Build.Entity("binary_sensor.pantry_door", "on", deviceClass: "door");

        Assert.Equal("on", door.State);
        Assert.Equal("open", door.StateLabel);
    }
}
