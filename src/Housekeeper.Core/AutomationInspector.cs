using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>
/// Reads an arbitrary Home Assistant automation config and reports what it touches and what it fires on.
/// Used to describe the automations that already exist: so a new draft can be compared against them, so a
/// routine one already performs is not offered, and so a finding one already fires on can say so.
/// </summary>
public static class AutomationInspector
{
    private const int MaxDepth = 16;

    /// <param name="Entities">Every entity id named anywhere in the config, including inside blueprint inputs.</param>
    /// <param name="Areas">Area ids the config targets, which the caller can widen to the entities in them.</param>
    /// <param name="Devices">Device ids the config targets or triggers on, likewise.</param>
    /// <param name="Triggers">
    /// Each trigger on its own, with what decides when it fires. The difference from <paramref name="Entities"/>
    /// matters: an automation that says "the CO2 is {{ states(...) }}" in the message it sends names that
    /// sensor without watching it, and telling someone they already have an automation for a reading when
    /// they do not is worse than saying nothing. Empty for a blueprint, whose triggers are in the blueprint.
    /// </param>
    /// <param name="HasConditions">True when any condition is left switched on, which can stop the automation acting.</param>
    public sealed record Inspection(
        IReadOnlySet<string> Entities,
        IReadOnlySet<string> TriggerKinds,
        IReadOnlySet<string> Areas,
        IReadOnlySet<string> Devices,
        IReadOnlyList<AutomationTrigger> Triggers,
        bool HasConditions);

    public static Inspection Inspect(JsonElement config)
    {
        HashSet<string> entities = new(StringComparer.Ordinal);
        HashSet<string> areas = new(StringComparer.Ordinal);
        HashSet<string> devices = new(StringComparer.Ordinal);
        HashSet<string> triggerKinds = new(StringComparer.Ordinal);
        List<AutomationTrigger> triggers = [];

        Collect(config, entities, areas, devices, 0, inBlueprintInput: false);

        var conditions = false;
        if (config.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "triggers", "trigger" })
                if (config.TryGetProperty(name, out var block))
                {
                    CollectTriggerKinds(block, triggerKinds);

                    // A single trigger may be written as a bare object rather than a one-element list.
                    IEnumerable<JsonElement> each = block.ValueKind == JsonValueKind.Array ? block.EnumerateArray() : [block];
                    foreach (var trigger in each)
                        if (ReadTrigger(trigger) is { } read)
                            triggers.Add(read);
                }

            foreach (var name in new[] { "conditions", "condition" })
                if (config.TryGetProperty(name, out var block) && AnySwitchedOn(block))
                    conditions = true;
        }

        return new Inspection(entities, triggerKinds, areas, devices, triggers, conditions);
    }

    /// <summary>
    /// One trigger, reduced to what decides when it fires. Null for anything that is not a trigger object.
    ///
    /// Only what can be read for certain is taken. A <c>for:</c> written as a template is marked unreadable
    /// rather than guessed at, and an <c>enabled:</c> that is anything but a plain true or false is taken as
    /// off: a trigger nobody can say is running cannot be said to be watching anything.
    /// </summary>
    private static AutomationTrigger? ReadTrigger(JsonElement trigger)
    {
        if (trigger.ValueKind != JsonValueKind.Object) return null;

        string? kind = null;
        foreach (var key in new[] { "trigger", "platform" })
            if (Text(trigger, key) is { } found)
            {
                kind = found;
                break;
            }

        if (kind is null) return null;

        List<string> entities = [];
        if (trigger.TryGetProperty("entity_id", out var ids))
            foreach (var id in Strings(ids))
                // Home Assistant also accepts a comma-separated list, and lower-cases what it is given.
                foreach (var part in id.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    entities.Add(part.ToLowerInvariant());

        var enabled = !trigger.TryGetProperty("enabled", out var switched) || switched.ValueKind == JsonValueKind.True;

        TimeSpan? wait = null;
        var waitUnreadable = false;
        if (trigger.TryGetProperty("for", out var forElement) && forElement.ValueKind != JsonValueKind.Null)
        {
            wait = Duration(forElement);
            waitUnreadable = wait is null;
        }

        return new AutomationTrigger(
            kind,
            entities,
            enabled,
            To: States(trigger, "to"),
            NotTo: States(trigger, "not_to"),
            For: wait,
            ForUnreadable: waitUnreadable,
            Above: Line(trigger, "above"),
            Below: Line(trigger, "below"),
            Attribute: Text(trigger, "attribute"),
            ValueTemplate: trigger.TryGetProperty("value_template", out var template) && template.ValueKind == JsonValueKind.String,
            Type: Text(trigger, "type"),
            Domain: Text(trigger, "domain"));
    }

    /// <summary>
    /// A <c>to:</c> or <c>not_to:</c>: one state or a list of them. Null when absent, and for <c>to: null</c>,
    /// which means any state. Anything else -- a bare <c>on</c> that YAML read as true, say -- names no state
    /// that can be matched, which is the safe way to read it.
    /// </summary>
    private static IReadOnlyList<string>? States(JsonElement trigger, string name)
    {
        if (!trigger.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;

        return [.. Strings(value).Select(state => state.Trim())];
    }

    /// <summary>An <c>above:</c> or <c>below:</c> as written: a number, or the id of the entity that holds one.</summary>
    private static string? Line(JsonElement trigger, string name)
    {
        if (!trigger.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!.Trim(),
            _ => null,
        };
    }

    /// <summary>
    /// A <c>for:</c> in any of the shapes Home Assistant accepts -- seconds, "HH:MM", "HH:MM:SS", or a
    /// mapping of days, hours, minutes, seconds and milliseconds. Null for anything else, a template included.
    /// </summary>
    internal static TimeSpan? Duration(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

            case JsonValueKind.String:
                var text = value.GetString()?.Trim() ?? "";
                if (Ha.TryNumeric(text, out var plain)) return plain >= 0 ? TimeSpan.FromSeconds(plain) : null;

                var parts = text.Split(':');
                if (parts.Length is not (2 or 3)) return null;

                double total = 0;
                double[] scale = parts.Length == 2 ? [3600, 60] : [3600, 60, 1];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!Ha.TryNumeric(parts[i], out var part) || part < 0) return null;
                    total += part * scale[i];
                }

                return TimeSpan.FromSeconds(total);

            case JsonValueKind.Object:
                double sum = 0;
                foreach (var property in value.EnumerateObject())
                {
                    var unit = property.Name switch
                    {
                        "days" => 86400,
                        "hours" => 3600,
                        "minutes" => 60,
                        "seconds" => 1,
                        "milliseconds" => 0.001,
                        _ => double.NaN,
                    };

                    var amount = property.Value.ValueKind switch
                    {
                        JsonValueKind.Number when property.Value.TryGetDouble(out var number) => number,
                        JsonValueKind.String when Ha.TryNumeric(property.Value.GetString(), out var number) => number,
                        _ => double.NaN,
                    };

                    if (double.IsNaN(unit) || double.IsNaN(amount) || amount < 0) return null;
                    sum += unit * amount;
                }

                return TimeSpan.FromSeconds(sum);

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a condition block holds any condition left switched on. A template written straight in as a
    /// string counts, and so does a condition whose <c>enabled:</c> is anything but a plain false.
    /// </summary>
    private static bool AnySwitchedOn(JsonElement block) => block.ValueKind switch
    {
        JsonValueKind.Array => block.EnumerateArray().Any(AnySwitchedOn),
        JsonValueKind.Object => !(block.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False),
        JsonValueKind.String => !string.IsNullOrWhiteSpace(block.GetString()),
        _ => false,
    };

    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { } single && !string.IsNullOrWhiteSpace(single)) yield return single;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                yield return text;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>What an entity id looks like: a domain, a dot, an object id. Blueprint inputs carry them under any name.</summary>
    private static bool LooksLikeEntityId(string text) =>
        text.Length is > 3 and < 256 &&
        text.IndexOf('.') is > 0 and var dot &&
        dot < text.Length - 1 &&
        text.IndexOf('.', dot + 1) < 0 &&
        text.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '.');

    private static void Collect(JsonElement element, HashSet<string> entities, HashSet<string> areas, HashSet<string> devices, int depth, bool inBlueprintInput)
    {
        if (depth > MaxDepth) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "entity_id": Add(property.Value, entities); break;
                        case "area_id": Add(property.Value, areas); break;
                        case "device_id": Add(property.Value, devices); break;
                    }

                    // A blueprint's inputs are where its entities live, under whatever names the blueprint
                    // chose: {"use_blueprint":{"path":"...","input":{"motion_entity":"binary_sensor.hall"}}}.
                    var inputs = inBlueprintInput || (property.Name == "input" && depth > 0);
                    Collect(property.Value, entities, areas, devices, depth + 1, inputs);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, entities, areas, devices, depth + 1, inBlueprintInput);
                break;

            case JsonValueKind.String when inBlueprintInput:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text) && LooksLikeEntityId(text.Trim())) entities.Add(text.Trim());
                break;
        }
    }

    private static void CollectTriggerKinds(JsonElement block, HashSet<string> into)
    {
        if (block.ValueKind == JsonValueKind.Object)
        {
            AddKind(block, into);
            return;
        }

        if (block.ValueKind != JsonValueKind.Array) return;

        foreach (var trigger in block.EnumerateArray())
            if (trigger.ValueKind == JsonValueKind.Object)
                AddKind(trigger, into);
    }

    // Home Assistant 2024.10 renamed the key inside a trigger from "platform" to "trigger".
    private static void AddKind(JsonElement trigger, HashSet<string> into)
    {
        foreach (var key in new[] { "trigger", "platform" })
        {
            if (!trigger.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;

            var kind = value.GetString();
            if (!string.IsNullOrWhiteSpace(kind)) into.Add(kind);
            return;
        }
    }

    private static void Add(JsonElement value, HashSet<string> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var single = value.GetString();
                if (!string.IsNullOrWhiteSpace(single)) into.Add(single.Trim());
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) into.Add(text.Trim());
                }

                break;
        }
    }
}
