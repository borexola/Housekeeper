using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>
/// Reads an arbitrary Home Assistant automation config and reports what it touches. Used to describe the
/// automations that already exist so a new draft can be compared against them.
/// </summary>
public static class AutomationInspector
{
    private const int MaxDepth = 16;

    public static (IReadOnlySet<string> Entities, IReadOnlySet<string> TriggerKinds) Inspect(JsonElement config)
    {
        HashSet<string> entities = new(StringComparer.Ordinal);
        HashSet<string> triggerKinds = new(StringComparer.Ordinal);

        CollectEntities(config, entities, 0);

        if (config.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "triggers", "trigger" })
                if (config.TryGetProperty(name, out var block))
                    CollectTriggerKinds(block, triggerKinds);

        return (entities, triggerKinds);
    }

    private static void CollectEntities(JsonElement element, HashSet<string> into, int depth)
    {
        if (depth > MaxDepth) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "entity_id") Add(property.Value, into);
                    CollectEntities(property.Value, into, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectEntities(item, into, depth + 1);
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
