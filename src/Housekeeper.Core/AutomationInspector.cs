using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>
/// Reads an arbitrary Home Assistant automation config and reports what it touches. Used to describe the
/// automations that already exist so a new draft can be compared against them.
/// </summary>
public static class AutomationInspector
{
    private const int MaxDepth = 16;

    /// <param name="Entities">Every entity id named anywhere in the config, including inside blueprint inputs.</param>
    /// <param name="Areas">Area ids the config targets, which the caller can widen to the entities in them.</param>
    /// <param name="Devices">Device ids the config targets or triggers on, likewise.</param>
    public sealed record Inspection(
        IReadOnlySet<string> Entities,
        IReadOnlySet<string> TriggerKinds,
        IReadOnlySet<string> Areas,
        IReadOnlySet<string> Devices);

    public static Inspection Inspect(JsonElement config)
    {
        HashSet<string> entities = new(StringComparer.Ordinal);
        HashSet<string> areas = new(StringComparer.Ordinal);
        HashSet<string> devices = new(StringComparer.Ordinal);
        HashSet<string> triggerKinds = new(StringComparer.Ordinal);

        Collect(config, entities, areas, devices, 0, inBlueprintInput: false);

        if (config.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "triggers", "trigger" })
                if (config.TryGetProperty(name, out var block))
                    CollectTriggerKinds(block, triggerKinds);

        return new Inspection(entities, triggerKinds, areas, devices);
    }

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
