using System.Text;
using System.Text.Json;

namespace HearthSense.Core;

public sealed record DraftResult(AutomationDraft? Draft, string? Error)
{
    public static DraftResult Ok(AutomationDraft draft) => new(draft, null);
    public static DraftResult Fail(string error) => new(null, error);
    public bool Succeeded => Draft is not null;
}

/// <summary>
/// Turns raw model output into an automation that is safe to write to Home Assistant, or rejects it.
///
/// The rules that matter: only a fixed set of top-level keys survives, every referenced entity must
/// actually exist, and device/area targets are refused because we cannot verify them. A model that
/// invents <c>light.kitchen_ceiling</c> on a house that has no such entity gets caught here rather
/// than silently writing a dead automation.
/// </summary>
public static class AutomationDrafting
{
    private const int MaxRawLength = 16 * 1024;
    private const int MaxAliasLength = 120;
    private const int MaxDescriptionLength = 500;
    private const int MaxDepth = 12;
    private const int MaxEntities = 60;

    private static readonly string[] Modes = ["single", "restart", "queued", "parallel"];

    public static DraftResult Parse(string? raw, IReadOnlySet<string> knownEntityIds)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DraftResult.Fail("The model returned nothing.");

        if (raw.Length > MaxRawLength)
            return DraftResult.Fail($"The model returned {raw.Length} characters, over the {MaxRawLength} limit.");

        if (!TryExtractObject(raw, out var json))
            return DraftResult.Fail("The model returned no JSON object.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return DraftResult.Fail($"The model returned malformed JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DraftResult.Fail("The model returned JSON that is not an object.");

            if (!TryString(root, "alias", MaxAliasLength, out var alias) || alias is null)
                return DraftResult.Fail("The draft has no usable 'alias'.");

            _ = TryString(root, "description", MaxDescriptionLength, out var description);

            if (!TryRequiredBlock(root, "triggers", "trigger", out var triggers))
                return DraftResult.Fail("The draft has no 'triggers' array.");

            if (!TryRequiredBlock(root, "actions", "action", out var actions))
                return DraftResult.Fail("The draft has no 'actions' array.");

            // Conditions are optional and an empty list is the common case. Only a wrong shape is an error.
            if (!TryOptionalBlock(root, "conditions", "condition", out var conditions))
                return DraftResult.Fail("The draft's 'conditions' is present but is not an array of condition objects.");

            var mode = "single";
            if (root.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
            {
                var candidate = modeElement.GetString()?.Trim().ToLowerInvariant();
                if (candidate is not null && !Modes.Contains(candidate, StringComparer.Ordinal))
                    return DraftResult.Fail($"The draft's mode '{candidate}' is not one of: {string.Join(", ", Modes)}.");
                if (candidate is not null) mode = candidate;
            }

            var walk = new Walker();
            foreach (var block in new[] { triggers, conditions, actions })
            {
                if (block.ValueKind != JsonValueKind.Array) continue;
                var error = walk.Visit(block, 0);
                if (error is not null) return DraftResult.Fail(error);
            }

            var unknown = walk.Entities.Where(id => !knownEntityIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
                return DraftResult.Fail(
                    $"The draft references {unknown.Count} entity id(s) that do not exist in Home Assistant: {string.Join(", ", unknown.Take(5))}" +
                    (unknown.Count > 5 ? ", …" : "") + ".");

            if (walk.Entities.Count > MaxEntities)
                return DraftResult.Fail($"The draft references {walk.Entities.Count} entities, over the {MaxEntities} limit.");

            if (walk.Entities.Count == 0)
                return DraftResult.Fail("The draft references no entities, so it cannot be verified.");

            var configJson = Serialize(alias, description, triggers, conditions, actions, mode);

            return DraftResult.Ok(new AutomationDraft(
                alias,
                description,
                configJson,
                [.. walk.Entities.OrderBy(x => x, StringComparer.Ordinal)],
                [.. walk.Actions.OrderBy(x => x, StringComparer.Ordinal)],
                walk.TriggerKindsOf(triggers)));
        }
    }

    /// <summary>Collects entities, service calls and trigger kinds while enforcing the structural rules.</summary>
    private sealed class Walker
    {
        public HashSet<string> Entities { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Actions { get; } = new(StringComparer.Ordinal);

        public string? Visit(JsonElement element, int depth)
        {
            if (depth > MaxDepth)
                return $"The draft nests deeper than {MaxDepth} levels.";

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        switch (property.Name)
                        {
                            case "entity_id":
                                var entityError = Collect(property.Value, Entities, "entity_id");
                                if (entityError is not null) return entityError;
                                break;

                            // We cannot check these against anything, and a wrong one silently targets
                            // the wrong hardware. Entities are addressable and verifiable; insist on them.
                            case "device_id":
                            case "area_id":
                                return $"The draft uses '{property.Name}', which cannot be verified. It must target entity_id instead.";

                            case "action" when property.Value.ValueKind == JsonValueKind.String:
                            case "service" when property.Value.ValueKind == JsonValueKind.String:
                                var call = property.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(call)) Actions.Add(call);
                                break;
                        }

                        var error = Visit(property.Value, depth + 1);
                        if (error is not null) return error;
                    }

                    return null;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var error = Visit(item, depth + 1);
                        if (error is not null) return error;
                    }

                    return null;

                default:
                    return null;
            }
        }

        public IReadOnlySet<string> TriggerKindsOf(JsonElement triggers)
        {
            HashSet<string> kinds = new(StringComparer.Ordinal);
            if (triggers.ValueKind != JsonValueKind.Array) return kinds;

            foreach (var trigger in triggers.EnumerateArray())
            {
                if (trigger.ValueKind != JsonValueKind.Object) continue;

                // Home Assistant 2024.10 renamed the key from "platform" to "trigger"; both still parse.
                foreach (var key in new[] { "trigger", "platform" })
                {
                    if (!trigger.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
                    var kind = value.GetString();
                    if (!string.IsNullOrWhiteSpace(kind)) kinds.Add(kind);
                    break;
                }
            }

            return kinds;
        }

        private static string? Collect(JsonElement value, HashSet<string> into, string field)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var single = value.GetString();
                    if (!string.IsNullOrWhiteSpace(single)) into.Add(single.Trim());
                    return null;

                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            return $"The draft has a non-string value inside '{field}'.";
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) into.Add(text.Trim());
                    }

                    return null;

                default:
                    return $"The draft's '{field}' is neither a string nor an array of strings.";
            }
        }
    }

    /// <summary>Rebuilds the config from only the keys we validated, dropping anything else the model added.</summary>
    private static string Serialize(
        string alias,
        string? description,
        JsonElement triggers,
        JsonElement conditions,
        JsonElement actions,
        string mode)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("alias", alias);
            if (!string.IsNullOrWhiteSpace(description)) writer.WriteString("description", description);

            writer.WritePropertyName("triggers");
            triggers.WriteTo(writer);

            writer.WritePropertyName("conditions");
            if (conditions.ValueKind == JsonValueKind.Array)
            {
                conditions.WriteTo(writer);
            }
            else
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WritePropertyName("actions");
            actions.WriteTo(writer);

            writer.WriteString("mode", mode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryExtractObject(string raw, out string json)
    {
        json = string.Empty;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        json = raw[start..(end + 1)];
        return true;
    }

    private static bool TryString(JsonElement root, string name, int maxLength, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;

        var text = Sanitize(element.GetString(), maxLength);
        if (string.IsNullOrWhiteSpace(text)) return false;

        value = text;
        return true;
    }

    /// <summary>
    /// Reads a block that must be there, under its modern name or its pre-2024.10 singular alias.
    /// A bare object is a common shorthand, but the array form is required so what gets written to
    /// Home Assistant is always the same shape that was validated.
    /// </summary>
    private static bool TryRequiredBlock(JsonElement root, string plural, string singular, out JsonElement block)
    {
        foreach (var name in new[] { plural, singular })
        {
            if (!root.TryGetProperty(name, out var element)) continue;
            if (!IsArrayOfObjects(element) || element.GetArrayLength() == 0) continue;

            block = element;
            return true;
        }

        block = default;
        return false;
    }

    /// <summary>Reads a block that may be absent, null, or an empty array. False means present but malformed.</summary>
    private static bool TryOptionalBlock(JsonElement root, string plural, string singular, out JsonElement block)
    {
        block = default;

        foreach (var name in new[] { plural, singular })
        {
            if (!root.TryGetProperty(name, out var element)) continue;
            if (element.ValueKind == JsonValueKind.Null) continue;
            if (!IsArrayOfObjects(element)) return false;

            block = element;
            return true;
        }

        return true;
    }

    private static bool IsArrayOfObjects(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array &&
        element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object);

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = new string([.. value.Select(ch => char.IsControl(ch) ? ' ' : ch)]).Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }
}
