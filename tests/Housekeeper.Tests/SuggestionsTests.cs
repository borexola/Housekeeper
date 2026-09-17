using Housekeeper.Core;

namespace Housekeeper.Tests;

public class SuggestionsTests
{
    [Fact]
    public void Examples_name_the_entities_this_house_actually_has()
    {
        List<HaEntity> house =
        [
            Build.Entity("binary_sensor.hall_motion", friendlyName: "Hall motion", deviceClass: "motion", area: "Hall"),
            Build.Entity("light.kitchen", friendlyName: "Kitchen light", area: "Kitchen"),
            Build.Entity("light.hall", friendlyName: "Hall light", area: "Hall"),
            Build.Entity("binary_sensor.back_door", friendlyName: "Back door", deviceClass: "door"),
            Build.Entity("lock.front_door", friendlyName: "Front door lock", state: "locked"),
            Build.Entity("person.sam", friendlyName: "Sam", state: "home"),
            Build.Entity("sensor.garage_temperature", "21.5", friendlyName: "Garage temperature", deviceClass: "temperature", unit: "°C"),
        ];

        var suggestions = Suggestions.For(house);

        // The motion example pairs the sensor with the light in the same room, not the first light by name.
        Assert.Contains(suggestions, s => s.Contains("Hall light", StringComparison.Ordinal) && s.Contains("Hall motion", StringComparison.Ordinal));
        Assert.DoesNotContain(suggestions, s => s.Contains("Kitchen light", StringComparison.Ordinal) && s.Contains("Hall motion", StringComparison.Ordinal));

        Assert.Contains(suggestions, s => s.Contains("Back door", StringComparison.Ordinal) && s.Contains("left open", StringComparison.Ordinal));
        Assert.Contains(suggestions, s => s.Contains("everyone has left", StringComparison.Ordinal));
        Assert.Contains(suggestions, s => s.Contains("Front door lock", StringComparison.Ordinal));
        Assert.Contains(suggestions, s => s.Contains("Garage temperature", StringComparison.Ordinal) && s.Contains("30 °C", StringComparison.Ordinal));

        // Never an entity id: these are meant to be read as sentences.
        Assert.DoesNotContain(suggestions, s => s.Contains('.'));
    }

    [Fact]
    public void A_house_with_nothing_of_a_kind_gets_no_example_about_it()
    {
        List<HaEntity> house = [Build.Entity("light.lamp", friendlyName: "Lamp")];

        var suggestions = Suggestions.For(house);

        Assert.Single(suggestions);
        Assert.Contains("Lamp", suggestions[0], StringComparison.Ordinal);
        Assert.Contains("sunset", suggestions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_diagnostic_and_unavailable_entities_are_never_suggested()
    {
        List<HaEntity> house =
        [
            Build.Entity("lock.hidden", friendlyName: "Hidden lock", hidden: true),
            Build.Entity("sensor.cpu_temperature", "55", friendlyName: "CPU temperature", deviceClass: "temperature", entityCategory: "diagnostic"),
            Build.Entity("media_player.dead", "unavailable", friendlyName: "Dead TV"),
        ];

        Assert.Empty(Suggestions.For(house));
    }

    [Fact]
    public void The_list_is_capped_and_deterministic()
    {
        List<HaEntity> house =
        [
            Build.Entity("binary_sensor.motion", deviceClass: "motion"),
            Build.Entity("light.a"),
            Build.Entity("light.b"),
            Build.Entity("binary_sensor.door", deviceClass: "door"),
            Build.Entity("lock.door"),
            Build.Entity("person.a"),
            Build.Entity("sensor.temp", "20", deviceClass: "temperature"),
            Build.Entity("climate.house"),
            Build.Entity("cover.garage", deviceClass: "garage"),
            Build.Entity("media_player.tv"),
        ];

        var first = Suggestions.For(house, max: 4);

        Assert.Equal(4, first.Count);
        Assert.Equal(first, Suggestions.For(house, max: 4));
        Assert.Empty(Suggestions.For([], max: 4));
    }
}
