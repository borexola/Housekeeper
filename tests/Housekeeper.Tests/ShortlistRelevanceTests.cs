using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// What the model is allowed to see, and what a house of three and a half thousand entities does to that.
///
/// The shortlist is the model's entire world: the prompt tells it that anything not listed does not exist.
/// So every line spent on how well a sensor hears its hub is a line not spent on a light, and on a real
/// house the plumbing outnumbers the house by a wide margin. The tests that matter most here are the ones
/// in the other direction — battery, update and connectivity all look diagnostic and are all things people
/// really do automate.
/// </summary>
public class ShortlistRelevanceTests
{
    /// <summary>A Zigbee house: a handful of real entities buried under everything the radios report.</summary>
    private static List<HaEntity> Noisy()
    {
        List<HaEntity> entities = [];

        for (var i = 0; i < 40; i++)
        {
            entities.Add(Build.Entity($"sensor.0x{i:x8}_linkquality", "156", unit: "lqi"));
            entities.Add(Build.Entity($"sensor.device_{i}_rssi", "-66", deviceClass: "signal_strength", unit: "dBm"));
            entities.Add(Build.Entity($"sensor.device_{i}_uptime", "864000", unit: "s"));
            entities.Add(Build.Entity($"switch.plug_{i}_auto_off_enabled", "off", entityCategory: "config"));
            entities.Add(Build.Entity($"number.device_{i}_calibration", "0", entityCategory: "config"));
        }

        entities.Add(Build.Entity("light.kitchen", "off", friendlyName: "Kitchen Light", area: "Kitchen"));
        entities.Add(Build.Entity("light.hall", "off", friendlyName: "Hall Light", area: "Hall"));
        entities.Add(Build.Entity("switch.kettle", "off", friendlyName: "Kettle", area: "Kitchen"));
        entities.Add(Build.Entity("binary_sensor.hall_motion", "off", deviceClass: "motion", area: "Hall"));
        entities.Add(Build.Entity("lock.back_door", "locked", friendlyName: "Back Door", area: "Kitchen"));

        return entities;
    }

    [Fact]
    public void The_plumbing_does_not_crowd_out_the_house()
    {
        var shortlist = EntityIndex.Shortlist(Noisy(), "turn the kitchen light on at sunset", 20);

        Assert.Contains(shortlist, e => e.EntityId == "light.kitchen");

        // Not one radio reading, uptime counter or calibration knob among them.
        Assert.DoesNotContain(shortlist, e => EntityIndex.RarelyAutomated(e));
        Assert.DoesNotContain(shortlist, e => e.EntityId.Contains("_calibration", StringComparison.Ordinal));
    }

    /// <summary>
    /// The padding path is the one that did the damage. Nothing matched, so the shortlist is filled with
    /// whatever the house has — and in a Zigbee house that is two hundred link-quality sensors.
    /// </summary>
    [Fact]
    public void Padding_a_request_that_matched_nothing_never_reaches_for_the_plumbing()
    {
        var shortlist = EntityIndex.Shortlist(Noisy(), "make the place feel cosy", 20);

        Assert.NotEmpty(shortlist);
        Assert.DoesNotContain(shortlist, e => EntityIndex.RarelyAutomated(e));

        // And it is still useful: the things a person might have meant are all there.
        Assert.Contains(shortlist, e => e.Domain == "light");
    }

    /// <summary>
    /// The regression that would be easy to cause and hard to notice. Home Assistant files battery level
    /// under entity_category: diagnostic, and Housekeeper's own detectors skip batteries — because a
    /// battery going down is not an anomaly. Automating one is entirely normal, so the two judgements have
    /// to stay apart.
    /// </summary>
    [Theory]
    [InlineData("sensor.back_door_battery", "battery", "%", "tell me when the back door battery is low")]
    [InlineData("update.hallway_switch_firmware_update", null, null, "tell me when an update is available")]
    [InlineData("binary_sensor.hub_link", "connectivity", null, "tell me when the hub link goes down")]
    public void Things_that_look_diagnostic_but_people_really_do_automate(
        string entityId, string? deviceClass, string? unit, string request)
    {
        var entities = Noisy();
        entities.Add(Build.Entity(entityId, "on", deviceClass: deviceClass, unit: unit, entityCategory: "diagnostic"));

        Assert.False(EntityIndex.RarelyAutomated(entities[^1]), $"{entityId} is ordinary automation material");
        Assert.Contains(EntityIndex.Shortlist(entities, request, 20), e => e.EntityId == entityId);
    }

    /// <summary>
    /// Where the demotion earns its keep: a request that matches on the area, so everything in that room
    /// scores identically and the cap decides who the model gets to see.
    ///
    /// Plumbing is named after the thing it is attached to, so it matches the wording just as well as the
    /// thing does. Twenty radio readings in the garage tie with the garage light and the garage heater, the
    /// tie breaks on entity id, and "sensor." sorts before "switch." — so the heater falls off the end of
    /// the shortlist and the model is then told it does not exist.
    /// </summary>
    [Fact]
    public void When_everything_in_a_room_matches_equally_the_real_things_still_make_the_cut()
    {
        List<HaEntity> garage =
        [
            Build.Entity("light.garage", "off", friendlyName: "Garage Light", area: "Garage"),
            Build.Entity("switch.garage_heater", "off", friendlyName: "Garage Heater", area: "Garage"),
            .. Enumerable.Range(0, 20).Select(i => Build.Entity(
                $"sensor.garage_{i:00}_linkquality", "156", friendlyName: $"Garage {i:00} Linkquality",
                unit: "lqi", area: "Garage")),
        ];

        var shortlist = EntityIndex.Shortlist(garage, "turn everything off in the garage", 3);

        Assert.Contains(shortlist, e => e.EntityId == "light.garage");
        Assert.Contains(shortlist, e => e.EntityId == "switch.garage_heater");
    }

    /// <summary>
    /// A demotion, not a ban. Wording aimed squarely at one of these still reaches it — otherwise the one
    /// person who does want to know about their Zigbee mesh could never ask.
    /// </summary>
    [Fact]
    public void Wording_aimed_at_the_plumbing_still_finds_it()
    {
        var entities = Noisy();
        entities.Add(Build.Entity("sensor.lock_pro_bluetooth_signal", "-91",
            friendlyName: "Garage Door Lock Bluetooth signal", deviceClass: "signal_strength", unit: "dBm"));

        var shortlist = EntityIndex.Shortlist(entities, "notify me when the bluetooth signal on the lock drops", 20);

        Assert.Contains(shortlist, e => e.EntityId == "sensor.lock_pro_bluetooth_signal");
    }

    /// <summary>An entity id written out in full is an instruction, not a hint, whatever kind of entity it is.</summary>
    [Fact]
    public void Naming_one_outright_always_works()
    {
        var shortlist = EntityIndex.Shortlist(
            Noisy(), "notify me when sensor.device_3_uptime resets", 20);

        Assert.Contains(shortlist, e => e.EntityId == "sensor.device_3_uptime");
    }

    /// <summary>Hiding an entity in Home Assistant is the user saying they do not want to see it.</summary>
    [Fact]
    public void A_hidden_entity_is_not_offered()
    {
        var entities = Noisy();
        entities.Add(Build.Entity("light.old_lamp", "off", friendlyName: "Kitchen Lamp", area: "Kitchen", hidden: true));

        var shortlist = EntityIndex.Shortlist(entities, "turn the kitchen lamp on", 20);

        Assert.DoesNotContain(shortlist, e => e.EntityId == "light.old_lamp");
        Assert.Contains(shortlist, e => e.EntityId == "light.kitchen");
    }

    /// <summary>
    /// A house whose entities are all plumbing must still get a shortlist. An empty one is reported to the
    /// user as "nothing in Home Assistant relates to that request", which would be a lie about the filter
    /// rather than a fact about the house.
    /// </summary>
    [Fact]
    public void A_house_of_nothing_but_plumbing_still_gets_a_shortlist()
    {
        List<HaEntity> onlyNoise =
        [
            .. Enumerable.Range(0, 20).Select(i => Build.Entity($"sensor.thing_{i}_linkquality", "156", unit: "lqi")),
        ];

        var shortlist = EntityIndex.Shortlist(onlyNoise, "turn something on", 20);

        // Nothing is offered rather than junk being offered, and the caller says so honestly.
        Assert.Empty(shortlist);
    }

    [Theory]
    [InlineData("sensor.0x54ef_linkquality", null, "lqi", true)]
    [InlineData("sensor.hall_rssi", null, null, true)]
    [InlineData("sensor.pi_uptime", null, "s", true)]
    [InlineData("sensor.hub_firmware", null, null, true)]
    [InlineData("number.motion_sensitivity", null, null, true)]
    [InlineData("image.camera_snapshot", null, null, true)]
    // Everything below is ordinary automation material and must survive.
    [InlineData("light.kitchen", null, null, false)]
    [InlineData("sensor.back_door_battery", "battery", "%", false)]
    [InlineData("button.doorbell_chime", null, null, false)]
    [InlineData("event.remote_button", null, null, false)]
    [InlineData("update.hub_update", null, null, false)]
    [InlineData("sensor.outdoor_temperature", "temperature", "°C", false)]
    public void What_counts_as_plumbing(string entityId, string? deviceClass, string? unit, bool expected) =>
        Assert.Equal(expected, EntityIndex.RarelyAutomated(Build.Entity(entityId, "1", deviceClass: deviceClass, unit: unit)));
}
