namespace Housekeeper.Core;

/// <summary>
/// Example requests written from what is actually in the house.
///
/// An empty text box under "describe an automation" is the hardest part of the product for someone new to
/// it: they do not yet know what it can do, or how to say it. A generic example ("turn off the lights when
/// nobody is home") teaches the shape but not the fit. These name the user's own hall light and their own
/// hall motion sensor, so every one of them is a request that would work here, in wording the drafter is
/// known to handle well. Nothing is sent anywhere until the user chooses one and presses the button.
///
/// Deterministic, and nothing more than pattern matching over the entity list: the same house gives the
/// same examples, and a house with no locks gets no example about locks.
/// </summary>
public static class Suggestions
{
    /// <summary>Device classes that mean a door, a window or a gate rather than any other kind of contact.</summary>
    private static readonly HashSet<string> Openings = new(StringComparer.OrdinalIgnoreCase)
    {
        "door", "window", "opening", "garage_door", "garage",
    };

    public static IReadOnlyList<string> For(IReadOnlyList<HaEntity> entities, int max = 6)
    {
        if (entities.Count == 0 || max <= 0) return [];

        // Only things a person would mean. Hidden entities, settings, diagnostics and the plumbing an
        // integration exposes are not what anyone automates first.
        var usable = entities
            .Where(entity => !entity.Hidden && !entity.IsConfigOrDiagnostic && !EntityIndex.RarelyAutomated(entity) && !entity.IsUnavailable)
            .OrderBy(entity => entity.EntityId, StringComparer.Ordinal)
            .ToList();

        // Each example spends its entities, so six examples name six different things rather than the same
        // light six times.
        HashSet<string> spent = new(StringComparer.Ordinal);
        List<string> suggestions = [];

        foreach (var write in Writers)
        {
            if (suggestions.Count >= max) break;

            var suggestion = write(usable, spent);
            if (suggestion is not null) suggestions.Add(suggestion);
        }

        return suggestions;
    }

    private delegate string? Writer(IReadOnlyList<HaEntity> usable, HashSet<string> spent);

    /// <summary>In the order they are offered, most broadly useful first.</summary>
    private static readonly Writer[] Writers =
    [
        MotionLight,
        OpeningLeftOpen,
        EveryoneLeaves,
        LockAtNight,
        TemperatureThreshold,
        ThermostatSchedule,
        GarageAtNight,
        HumidityFan,
        Leak,
        LightAtSunset,
        MediaOffAtMidnight,
        VacuumWhileOut,
    ];

    private static string? MotionLight(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var motions = usable.Where(e => e.Domain == "binary_sensor" && Class(e) is "motion" or "occupancy" or "presence").ToList();
        var lights = usable.Where(e => e.Domain == "light").ToList();
        if (motions.Count == 0 || lights.Count == 0) return null;

        // The same room first: a hall sensor and a hall light is the example everyone recognises.
        var pair = motions
            .SelectMany(motion => lights.Select(light => (Motion: motion, Light: light)))
            .OrderByDescending(pair => SameArea(pair.Motion, pair.Light))
            .ThenBy(pair => pair.Motion.EntityId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Light.EntityId, StringComparer.Ordinal)
            .First();

        Spend(spent, pair.Motion, pair.Light);
        return $"Turn on the {Name(pair.Light)} when the {Name(pair.Motion)} sees movement after dark, and off again 5 minutes later";
    }

    private static string? OpeningLeftOpen(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var opening = Pick(usable, spent, e => e.Domain == "binary_sensor" && Class(e) is { } c && Openings.Contains(c) && c != "garage_door" && c != "garage");
        if (opening is null) return null;

        return $"Notify me if the {Name(opening)} is left open for more than 10 minutes";
    }

    private static string? EveryoneLeaves(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var people = usable.Where(e => e.Domain is "person" or "device_tracker").ToList();
        if (people.Count == 0 || !usable.Any(e => e.Domain == "light")) return null;

        foreach (var person in people) spent.Add(person.EntityId);
        return "Turn off every light when everyone has left home";
    }

    private static string? LockAtNight(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var lockEntity = Pick(usable, spent, e => e.Domain == "lock");
        return lockEntity is null ? null : $"Remind me at 22:30 if the {Name(lockEntity)} is still unlocked";
    }

    private static string? TemperatureThreshold(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var sensor = Pick(usable, spent, e => e.Domain == "sensor" && Class(e) == "temperature" && e.Numeric is not null);
        if (sensor is null) return null;

        var unit = sensor.Unit?.Trim();
        var fahrenheit = unit is not null && unit.EndsWith('F');
        var limit = fahrenheit ? "85" : "30";
        return $"Notify me when the {Name(sensor)} goes above {limit}{(string.IsNullOrEmpty(unit) ? " degrees" : " " + unit)}";
    }

    private static string? ThermostatSchedule(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var climate = Pick(usable, spent, e => e.Domain == "climate");
        if (climate is null) return null;

        var fahrenheit = climate.Unit?.Trim().EndsWith('F') == true;
        return $"Set the {Name(climate)} to {(fahrenheit ? "65" : "18")} at 23:00 on weekdays";
    }

    private static string? GarageAtNight(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var garage = Pick(usable, spent, e =>
            (e.Domain == "cover" && (Class(e) is "garage" or "gate" || e.EntityId.Contains("garage", StringComparison.OrdinalIgnoreCase))) ||
            (e.Domain == "binary_sensor" && Class(e) is "garage_door" or "garage"));
        return garage is null ? null : $"Notify me if the {Name(garage)} is still open at 22:00";
    }

    private static string? HumidityFan(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var humidity = usable.FirstOrDefault(e => !spent.Contains(e.EntityId) && e.Domain == "sensor" && Class(e) == "humidity");
        var fan = usable.FirstOrDefault(e => !spent.Contains(e.EntityId) && e.Domain == "fan");
        if (humidity is null || fan is null) return null;

        var best = usable.Where(e => e.Domain == "fan" && !spent.Contains(e.EntityId))
            .OrderByDescending(e => SameArea(e, humidity))
            .ThenBy(e => e.EntityId, StringComparer.Ordinal)
            .First();

        Spend(spent, humidity, best);
        return $"Turn on the {Name(best)} when the {Name(humidity)} goes above 70% and off when it drops below 55%";
    }

    private static string? Leak(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var leak = Pick(usable, spent, e => e.Domain == "binary_sensor" && Class(e) is "moisture");
        return leak is null ? null : $"Notify me straight away if the {Name(leak)} detects water";
    }

    private static string? LightAtSunset(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var light = Pick(usable, spent, e => e.Domain == "light" &&
            (e.EntityId.Contains("porch", StringComparison.OrdinalIgnoreCase) ||
             e.EntityId.Contains("outdoor", StringComparison.OrdinalIgnoreCase) ||
             e.EntityId.Contains("outside", StringComparison.OrdinalIgnoreCase) ||
             e.EntityId.Contains("garden", StringComparison.OrdinalIgnoreCase) ||
             e.EntityId.Contains("front", StringComparison.OrdinalIgnoreCase)))
            ?? Pick(usable, spent, e => e.Domain == "light");

        return light is null ? null : $"Turn on the {Name(light)} at sunset and off at midnight";
    }

    private static string? MediaOffAtMidnight(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var player = Pick(usable, spent, e => e.Domain == "media_player");
        return player is null ? null : $"Turn off the {Name(player)} at midnight if it is still on";
    }

    private static string? VacuumWhileOut(IReadOnlyList<HaEntity> usable, HashSet<string> spent)
    {
        var vacuum = Pick(usable, spent, e => e.Domain == "vacuum");
        if (vacuum is null || !usable.Any(e => e.Domain is "person" or "device_tracker")) return null;

        return $"Start the {Name(vacuum)} at 10:00 on weekdays once nobody is home";
    }

    // ---- choosing ----

    private static HaEntity? Pick(IReadOnlyList<HaEntity> usable, HashSet<string> spent, Func<HaEntity, bool> fits)
    {
        var chosen = usable.FirstOrDefault(e => !spent.Contains(e.EntityId) && fits(e));
        if (chosen is not null) spent.Add(chosen.EntityId);
        return chosen;
    }

    private static void Spend(HashSet<string> spent, params HaEntity[] entities)
    {
        foreach (var entity in entities) spent.Add(entity.EntityId);
    }

    private static bool SameArea(HaEntity left, HaEntity right) =>
        !string.IsNullOrWhiteSpace(left.Area) && string.Equals(left.Area, right.Area, StringComparison.OrdinalIgnoreCase);

    private static string? Class(HaEntity entity) => entity.DeviceClass?.Trim().ToLowerInvariant();

    /// <summary>The friendly name, or the id said as words. Never the id itself: the point is to read naturally.</summary>
    private static string Name(HaEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.FriendlyName)) return entity.FriendlyName.Trim();

        var dot = entity.EntityId.IndexOf('.');
        return (dot > 0 ? entity.EntityId[(dot + 1)..] : entity.EntityId).Replace('_', ' ');
    }
}
