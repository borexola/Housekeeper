using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>The outcome of reading a model's reply: a usable draft, or why it was refused.</summary>
public sealed record DraftResult(AutomationDraft? Draft, string? Error)
{
    public bool Succeeded => Draft is not null;

    public static DraftResult Ok(AutomationDraft draft) => new(draft, null);

    public static DraftResult Fail(string error) => new(null, error);
}

/// <summary>
/// Turns whatever the model said into an automation, or says no and why.
///
/// Everything here treats the reply as hostile input rather than as an answer, because a small local model
/// is wrong in ways that look right: an entity id it invented reads exactly like one that exists, and an
/// automation comparing a temperature to the text "25" is perfectly valid YAML that can never once fire.
/// Every refusal is a sentence written to be handed straight back to the model, since that is what the
/// retry does with it — a validator that says "invalid" teaches nothing, and one that names the mistake
/// gets a correct answer on the next attempt.
/// </summary>
public static class AutomationDrafting
{
    /// <summary>The alias the model is told to use when the request cannot be built from what exists.</summary>
    public const string Unsupported = "UNSUPPORTED";

    private const int MaxRawLength = 16384;
    private const int MaxAliasLength = 120;
    private const int MaxDescriptionLength = 500;

    /// <summary>How deep a draft may nest before it is refused, so a pathological reply cannot exhaust the stack.</summary>
    private const int MaxDepth = 30;

    private const int MaxEntities = 60;

    /// <summary>How many JSON objects in a reply are examined before giving up on finding the draft.</summary>
    private const int MaxCandidates = 8;

    private static readonly string[] Modes = ["single", "restart", "queued", "parallel"];

    /// <summary>
    /// Triggers that depend on the clock rather than on anything in the house. An automation naming no
    /// entity at all is usually a model that invented its way out of a request it could not meet — unless
    /// it really is only about a time of day, which these are.
    /// </summary>
    private static readonly string[] ClockTriggers = ["time", "time_pattern", "sun"];

    public static DraftResult Parse(string? raw, IReadOnlySet<string> knownEntityIds) =>
        Parse(raw, knownEntityIds, null);

    /// <param name="knownServices">
    /// What this Home Assistant actually offers. Empty or null skips the check rather than failing the
    /// draft: an unreadable service list is a problem with the connection, not with what the model wrote.
    /// </param>
    public static DraftResult Parse(string? raw, IReadOnlySet<string> knownEntityIds, IReadOnlySet<string>? knownServices)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DraftResult.Fail("The model returned nothing.");

        if (raw.Length > MaxRawLength)
            return DraftResult.Fail($"The model returned {raw.Length} characters, over the {MaxRawLength} limit.");

        if (!TryExtractObject(raw, out var json))
            return DraftResult.Fail(raw.Contains('{', StringComparison.Ordinal)
                // The specific, actionable shape of a truncated reply. Told as "malformed JSON" the user
                // blames the model; told like this they raise the token limit and it works.
                ? "The model's reply began a JSON object and never closed it, which is what a reply cut off "
                  + "part-way looks like. Raise Max output tokens, or use a model with more room."
                : "The model returned no JSON object.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return DraftResult.Fail("The model returned malformed JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DraftResult.Fail("The model returned JSON that is not an object.");

            if (!TryString(root, "alias", MaxAliasLength, out var alias) || alias is null)
                return DraftResult.Fail("The draft has no usable 'alias'.");

            TryString(root, "description", MaxDescriptionLength, out var description);

            // The escape hatch. A model that says it cannot do something is far more useful than one that
            // invents a different automation, so this is a success carrying an explanation rather than a
            // failure -- the caller shows it to the user instead of retrying.
            if (alias.StartsWith(Unsupported, StringComparison.OrdinalIgnoreCase))
            {
                var reason = alias.Length > Unsupported.Length
                    ? alias[Unsupported.Length..].TrimStart(' ', ':', '-', '.', ',')
                    : "";

                return DraftResult.Ok(new AutomationDraft(
                    Unsupported,
                    description ?? (reason.Length > 0 ? reason : "The model did not say what was missing."),
                    "{}",
                    [],
                    [],
                    new HashSet<string>(StringComparer.Ordinal)));
            }

            // Home Assistant renamed these to plurals and still accepts the singular, and models trained on
            // both write either. Both spellings are read; only the plural is written back.
            if (!TryRequiredBlock(root, "triggers", "trigger", out var triggers))
                return DraftResult.Fail("The draft has no 'triggers' array.");

            if (!TryRequiredBlock(root, "actions", "action", out var actions))
                return DraftResult.Fail("The draft has no 'actions' array.");

            if (!TryOptionalBlock(root, "conditions", "condition", out var conditions))
                return DraftResult.Fail("The draft's 'conditions' is present but is not an array of condition objects.");

            var mode = "single";
            if (root.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
            {
                var written = modeElement.GetString()?.Trim().ToLowerInvariant();

                if (written is not null && !Modes.Contains(written, StringComparer.Ordinal))
                    return DraftResult.Fail($"The draft's mode '{written}' is not one of: {string.Join(", ", Modes)}.");

                if (written is not null) mode = written;
            }

            Walker walker = new();
            foreach (var block in new[] { triggers, conditions, actions })
            {
                if (block.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;

                if (walker.Visit(block, 0) is { } objection) return DraftResult.Fail(objection);
            }

            var invented = walker.Entities
                .Where(id => !knownEntityIds.Contains(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (invented.Count > 0)
                return DraftResult.Fail(
                    $"The draft references {invented.Count} entity id(s) that do not exist in Home Assistant: "
                    + string.Join(", ", invented.Take(5)) + (invented.Count > 5 ? ", …" : "") + ".");

            if (knownServices is not null && knownServices.Count > 0)
            {
                var unavailable = walker.Actions
                    .Where(call => !knownServices.Contains(call))
                    .OrderBy(call => call, StringComparer.Ordinal)
                    .ToList();

                if (unavailable.Count > 0) return DraftResult.Fail(Suggest(unavailable[0], knownServices));
            }

            if (walker.Entities.Count > MaxEntities)
                return DraftResult.Fail($"The draft references {walker.Entities.Count} entities, over the {MaxEntities} limit.");

            if (walker.Entities.Count == 0 && !OnlyClockTriggers(walker.TriggerKindsOf(triggers)))
                return DraftResult.Fail(
                    "The draft references no entities, so it cannot be verified. Name an entity from the list, "
                    + "or use a time or sun trigger if the automation really is only about a time of day.");

            return DraftResult.Ok(new AutomationDraft(
                alias,
                description,
                Serialize(alias, description, triggers, conditions, actions, mode),
                [.. walker.Entities.OrderBy(id => id, StringComparer.Ordinal)],
                [.. walker.Actions.OrderBy(call => call, StringComparer.Ordinal)],
                walker.TriggerKindsOf(triggers)));
        }
    }

    /// <summary>
    /// Walks a draft collecting what it touches, and stops at the first thing that makes it unusable.
    ///
    /// One pass rather than a schema check, because the mistakes worth catching are not schema violations.
    /// A number in "to:" is valid JSON in a valid place that simply never fires; a device_id is exactly
    /// what Home Assistant's own editor writes and is the one thing here that cannot be verified.
    /// </summary>
    private sealed class Walker
    {
        public HashSet<string> Entities { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Actions { get; } = new(StringComparer.Ordinal);

        /// <summary>Null when nothing is wrong; otherwise the sentence handed back to the model.</summary>
        public string? Visit(JsonElement element, int depth)
        {
            if (depth > MaxDepth) return $"The draft nests deeper than {MaxDepth} levels.";

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (Inspect(property) is { } objection) return objection;

                        if (Visit(property.Value, depth + 1) is { } deeper) return deeper;
                    }

                    return null;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        if (Visit(item, depth + 1) is { } objection)
                            return objection;

                    return null;

                default:
                    return null;
            }
        }

        private string? Inspect(JsonProperty property)
        {
            switch (property.Name)
            {
                case "entity_id":
                    return Collect(property.Value, Entities, "entity_id");

                // "to:" and "from:" compare the state as TEXT. A threshold written there is the one mistake
                // that survives every other check -- valid JSON, valid keys, a real entity -- and produces an
                // automation that silently never fires. It is also the mistake an anomaly finding invites,
                // since those are mostly about numbers.
                case "to" or "from":
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        Ha.TryNumeric(property.Value.GetString(), out _))
                        return $"The draft compares a state to the number '{property.Value.GetString()}' with "
                               + $"\"{property.Name}\", which matches it as text and so can never fire. "
                               + "Use a numeric_state trigger with \"above\" or \"below\" instead.";

                    if (property.Value.ValueKind == JsonValueKind.Number)
                        return $"The draft has a number in \"{property.Name}\", which compares states as text "
                               + "and so can never fire. Use a numeric_state trigger with \"above\" or \"below\" instead.";

                    return null;

                // Every one of these is a real Home Assistant target and none can be checked against the
                // entity list, so a draft using one is unverifiable however correct it may be.
                case "device_id" or "area_id" or "floor_id" or "label_id":
                    return $"The draft uses '{property.Name}', which cannot be verified. It must target entity_id instead.";

                case "action" or "service":
                    return property.Value.ValueKind == JsonValueKind.String
                        ? Service(property.Value.GetString())
                        : null;

                // scene.turn_on names its scene here rather than in a target, so these are entities too.
                case "scene":
                    return property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array
                        ? Collect(property.Value, Entities, "scene")
                        : null;

                default:
                    return null;
            }
        }

        private string? Service(string? name)
        {
            if (IsTemplate(name))
                return $"The draft's service name '{name}' is a template, so it cannot be checked against what "
                       + "this Home Assistant offers. Name the service directly.";

            if (!IsServiceCall(name)) return null;

            if (Ha.ActsOnTheInstallation(name!))
                return $"The draft calls '{name}', which acts on Home Assistant itself rather than on anything "
                       + "in the house. That cannot be automated from here.";

            Actions.Add(name!);
            return null;
        }

        /// <summary>
        /// What kinds of trigger this draft has, reading both the current <c>trigger:</c> spelling and the
        /// older <c>platform:</c> one. Only the first of the two present is counted, so a draft carrying
        /// both does not register as two triggers.
        /// </summary>
        public IReadOnlySet<string> TriggerKindsOf(JsonElement triggers)
        {
            HashSet<string> kinds = new(StringComparer.Ordinal);

            List<JsonElement> each = triggers.ValueKind switch
            {
                JsonValueKind.Array => [.. triggers.EnumerateArray()],
                JsonValueKind.Object => [triggers],
                _ => [],
            };

            foreach (var trigger in each)
            {
                if (trigger.ValueKind != JsonValueKind.Object) continue;

                foreach (var name in new[] { "trigger", "platform" })
                {
                    if (!trigger.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                        continue;

                    var kind = value.GetString();
                    if (!string.IsNullOrWhiteSpace(kind)) kinds.Add(kind);
                    break;
                }
            }

            return kinds;
        }

        private static bool IsTemplate(string? value) =>
            value is not null &&
            (value.Contains("{{", StringComparison.Ordinal) || value.Contains("{%", StringComparison.Ordinal));

        /// <summary>
        /// Whether this reads as <c>domain.service</c> at all. Deliberately strict: anything else in an
        /// "action" is one of the block forms — choose, repeat, delay — and is not a call to check.
        /// </summary>
        private static bool IsServiceCall(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var dot = value.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == value.Length - 1) return false;
            if (value.IndexOf('.', dot + 1) >= 0) return false;

            foreach (var ch in value)
                if (ch != '.' && !char.IsAsciiLetterOrDigit(ch) && ch != '_')
                    return false;

            return true;
        }

        /// <summary>Gathers one id or a list of them, refusing anything that is neither.</summary>
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

                        var id = item.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) into.Add(id.Trim());
                    }

                    return null;

                default:
                    return $"The draft's '{field}' is neither a string nor an array of strings.";
            }
        }
    }

    /// <summary>
    /// Writes the draft back out from the parts that were checked, rather than passing the model's text
    /// through. Anything not read above — a stray key, a comment, whatever else it decided to include —
    /// does not reach Home Assistant, and the blocks are normalised to the plural array spelling.
    /// </summary>
    private static string Serialize(
        string alias,
        string? description,
        JsonElement triggers,
        JsonElement conditions,
        JsonElement actions,
        string mode)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("alias", alias);
            if (!string.IsNullOrWhiteSpace(description)) writer.WriteString("description", description);

            WriteBlock(writer, "triggers", triggers);
            WriteBlock(writer, "conditions", conditions);
            WriteBlock(writer, "actions", actions);

            writer.WriteString("mode", mode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes a block as an array whether the model wrote one object or a list of them.</summary>
    private static void WriteBlock(Utf8JsonWriter writer, string name, JsonElement block)
    {
        writer.WritePropertyName(name);

        if (block.ValueKind == JsonValueKind.Array)
        {
            block.WriteTo(writer);
            return;
        }

        writer.WriteStartArray();
        if (block.ValueKind == JsonValueKind.Object) block.WriteTo(writer);
        writer.WriteEndArray();
    }

    /// <summary>
    /// Finds the draft in a reply that may have prose around it.
    ///
    /// Models told to return one object still say "Sure! Here's the automation:" first, and still wrap it in
    /// a code fence. Taking the first balanced object is not enough either — a reply that opens with an
    /// example, or with a JSON code block explaining itself, puts something object-shaped ahead of the real
    /// answer. So each candidate is examined and the first that looks like a draft wins; failing that, the
    /// first object found is returned, and the ordinary checks reject it with a reason.
    /// </summary>
    private static bool TryExtractObject(string raw, out string json)
    {
        json = string.Empty;
        string? first = null;

        var start = raw.IndexOf('{');
        for (var examined = 0; start >= 0 && examined < MaxCandidates; examined++)
        {
            var end = MatchingBrace(raw, start);
            if (end >= 0)
            {
                var candidate = raw[start..(end + 1)];
                first ??= candidate;

                if (IsDraftShaped(candidate))
                {
                    json = candidate;
                    return true;
                }
            }

            start = raw.IndexOf('{', start + 1);
        }

        if (first is null) return false;

        json = first;
        return true;
    }

    private static bool IsDraftShaped(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("alias", out _)) return true;

            var hasTriggers = root.TryGetProperty("triggers", out _) || root.TryGetProperty("trigger", out _);
            var hasActions = root.TryGetProperty("actions", out _) || root.TryGetProperty("action", out _);

            return hasTriggers && hasActions;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The brace closing the one at <paramref name="start"/>, ignoring braces inside strings.
    /// A message containing a "{" would otherwise unbalance the count and swallow the rest of the reply.
    /// </summary>
    private static int MatchingBrace(string raw, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;

                continue;
            }

            switch (ch)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return i;
                    break;
            }
        }

        return -1;
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

    /// <summary>A block that must be there and must not be empty, under either spelling.</summary>
    private static bool TryRequiredBlock(JsonElement root, string plural, string singular, out JsonElement block)
    {
        foreach (var name in new[] { plural, singular })
        {
            if (root.TryGetProperty(name, out var value) && IsBlock(value) &&
                (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0))
            {
                block = value;
                return true;
            }
        }

        block = default;
        return false;
    }

    /// <summary>
    /// A block that may be absent. Returns true when there is nothing there at all, because a draft with no
    /// conditions is perfectly ordinary — false means one was written and is not a list of conditions.
    /// </summary>
    private static bool TryOptionalBlock(JsonElement root, string plural, string singular, out JsonElement block)
    {
        block = default;

        foreach (var name in new[] { plural, singular })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;

            if (!IsBlock(value)) return false;

            block = value;
            return true;
        }

        return true;
    }

    private static bool OnlyClockTriggers(IReadOnlySet<string> kinds) =>
        kinds.Count > 0 && kinds.All(kind => ClockTriggers.Contains(kind, StringComparer.OrdinalIgnoreCase));

    private static bool IsBlock(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ||
        (element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object));

    /// <summary>
    /// Names the invented service and lists what the domain really offers.
    ///
    /// The retry hands this straight back to the model, so it is written to be acted on: a model told only
    /// that <c>light.dim</c> does not exist invents <c>light.set_brightness</c> next, while one shown the
    /// six services light actually has picks <c>light.turn_on</c>.
    /// </summary>
    private static string Suggest(string invented, IReadOnlySet<string> knownServices)
    {
        var domain = invented.Split('.')[0];

        var offered = knownServices
            .Where(known => known.StartsWith(domain + ".", StringComparison.Ordinal))
            .OrderBy(known => known, StringComparer.Ordinal)
            .Take(6)
            .ToList();

        return offered.Count > 0
            ? $"The draft calls '{invented}', which this Home Assistant does not have. In that domain it offers: {string.Join(", ", offered)}."
            : $"The draft calls '{invented}', which this Home Assistant does not have, and it has no '{domain}' services at all.";
    }

    /// <summary>
    /// Flattens a name or description into something safe to show and to store.
    ///
    /// Format characters are dropped rather than replaced: a zero-width joiner or a right-to-left override
    /// is invisible in the dashboard and in Home Assistant's own list, so two automations could be told
    /// apart by nothing a person can see. Control characters become spaces, and runs of space collapse.
    /// </summary>
    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = new string([.. value
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format)
            .Select(ch => char.IsControl(ch) ? ' ' : ch)]).Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }
}
