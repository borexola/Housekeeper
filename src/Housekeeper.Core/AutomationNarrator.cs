using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>A validated automation said in plain words: what starts it, what has to hold, and what it does.</summary>
/// <param name="When">One line per trigger.</param>
/// <param name="OnlyIf">One line per top-level condition; empty when the automation has none.</param>
/// <param name="Then">One line per step, with branches and loops folded into the line that owns them.</param>
public sealed record Narrative(IReadOnlyList<string> When, IReadOnlyList<string> OnlyIf, IReadOnlyList<string> Then);

/// <summary>
/// Reads a draft back to the person who has to approve it.
///
/// The YAML is what Home Assistant receives and it stays on the card, but YAML is the wrong thing to make
/// someone read to answer "is this what I meant?". A trigger written as <c>to: "on"</c> with a <c>for:</c>
/// of ten minutes is one line here, "Freezer door has been open for 10 minutes", and a <c>choose</c> over
/// two trigger ids becomes two lines that start with "If". Every entity is named and every state worded the
/// way Home Assistant does it, so the reader checks the light they meant rather than translating an id, and
/// reads about a door opening rather than about it turning on.
///
/// Deterministic on purpose. Asking the model to explain its own draft would cost a second call and would
/// describe what it meant rather than what it wrote; this describes exactly the config that was validated,
/// so the two cannot disagree. Anything it does not understand is said as what it is, "a template
/// condition", rather than guessed at, and nothing here can fail: an unreadable config gives null and the
/// card falls back to the YAML alone.
/// </summary>
public static class AutomationNarrator
{
    private const int MaxDepth = 12;

    /// <summary>How much of a notification message or template is quoted before it is cut.</summary>
    private const int MaxQuote = 140;

    /// <param name="classOf">
    /// An entity's device class, which is what decides how its states are worded: a door sensor's
    /// <c>on</c> is open. Left out, every state is read as Home Assistant's raw value, which is what the
    /// readback did before and is never wrong, only unhelpful.
    /// </param>
    public static Narrative? Describe(string? configJson, Func<string, string?> nameOf, Func<string, string?>? classOf = null)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;

        try
        {
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var teller = new Teller(nameOf, classOf ?? (_ => null));

            var triggers = Items(Block(root, "triggers", "trigger"));
            var when = triggers.Select(teller.Trigger).ToList();

            // Trigger ids are what a "choose" branches on, so a branch can say "if it was the sunset trigger"
            // in the trigger's own words rather than by its id.
            foreach (var trigger in triggers)
                if (Text(trigger, "id") is { } id)
                    teller.TriggerNames[id] = teller.Trigger(trigger);

            var onlyIf = Items(Block(root, "conditions", "condition")).Select(condition => teller.Condition(condition, 0)).ToList();
            var then = Items(Block(root, "actions", "action")).Select(action => teller.Action(action, 0)).ToList();

            return new Narrative(when, onlyIf, then);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- reading the shape ----

    private static JsonElement Block(JsonElement root, string plural, string singular)
    {
        foreach (var name in new[] { plural, singular })
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return value;

        return default;
    }

    private static List<JsonElement> Items(JsonElement block) => block.ValueKind switch
    {
        JsonValueKind.Array => [.. block.EnumerateArray()],
        JsonValueKind.Object => [block],
        _ => [],
    };

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetDouble(out var number) ? Ha.Number(number) : value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static JsonElement? Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    /// <summary>A string, or a list of strings, as one list; anything else is empty.</summary>
    private static List<string> Strings(JsonElement? element)
    {
        if (element is not { } value) return [];

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!.Trim()],
            JsonValueKind.Array => [.. value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(item => item.Length > 0)],
            _ => [],
        };
    }

    // ---- saying things ----

    /// <summary>
    /// The voice, carrying the entity names and the trigger ids so nested blocks can use both.
    /// </summary>
    private sealed class Teller(Func<string, string?> nameOf, Func<string, string?> classOf)
    {
        public Dictionary<string, string> TriggerNames { get; } = new(StringComparer.Ordinal);

        public string Trigger(JsonElement trigger)
        {
            if (trigger.ValueKind == JsonValueKind.Object && Child(trigger, "enabled") is { ValueKind: JsonValueKind.False })
                return "(disabled) " + Trigger(WithoutEnabled(trigger));

            var kind = Text(trigger, "trigger") ?? Text(trigger, "platform") ?? "";
            var who = Names(Child(trigger, "entity_id"), " or ");
            var held = Span(Child(trigger, "for"));
            var attribute = Text(trigger, "attribute");

            switch (kind)
            {
                case "state":
                    {
                        var subject = attribute is null ? who : $"{who}'s {Words(attribute)}";
                        var to = Text(trigger, "to");
                        var from = Text(trigger, "from");

                        string clause;
                        var entities = Child(trigger, "entity_id");
                        if (to is not null && from is not null) clause = $"{subject} goes from {Said(entities, from)} to {Said(entities, to)}";
                        else if (to is not null) clause = held is null ? $"{subject} {Becomes(Said(entities, to))}" : $"{subject} has been {Said(entities, to)}";
                        else if (from is not null) clause = $"{subject} leaves {Said(entities, from)}";
                        else clause = $"{subject} changes";

                        return held is null ? clause : $"{clause} for {held}";
                    }

                case "numeric_state":
                    {
                        var subject = attribute is null ? who : $"{who}'s {Words(attribute)}";
                        var clause = Range(subject, Text(trigger, "above"), Text(trigger, "below"), asMove: true);
                        return held is null ? clause : $"{clause} for {held}";
                    }

                case "time":
                    {
                        var at = Strings(Child(trigger, "at"));
                        if (at.Count == 0) return "at a set time";

                        return "at " + string.Join(" and ", at.Select(item =>
                            item.Contains('.') && !item.Contains(':') ? $"the time set by {Name(item)}" : Clock(item)));
                    }

                case "time_pattern":
                    {
                        if (Every(Text(trigger, "seconds"), "second") is { } seconds) return seconds;
                        if (Every(Text(trigger, "minutes"), "minute") is { } minutes) return minutes;
                        if (Every(Text(trigger, "hours"), "hour") is { } hours) return hours;

                        var hh = Text(trigger, "hours") ?? "*";
                        var mm = Text(trigger, "minutes") ?? "*";
                        return $"whenever the clock matches {hh}:{mm}";
                    }

                case "sun":
                    {
                        var eventName = Text(trigger, "event") ?? "sunset";
                        return Offset(Text(trigger, "offset"), eventName);
                    }

                case "zone":
                    {
                        var zone = Name(Text(trigger, "zone") ?? "a zone");
                        var eventName = Text(trigger, "event") ?? "enter";
                        return eventName == "leave" ? $"{who} leaves {zone}" : $"{who} arrives at {zone}";
                    }

                case "template":
                    return held is null ? "a template becomes true" : $"a template has been true for {held}";

                case "homeassistant":
                    return Text(trigger, "event") == "shutdown" ? "Home Assistant shuts down" : "Home Assistant starts";

                case "mqtt":
                    return Text(trigger, "topic") is { } topic ? $"a message arrives on MQTT topic {topic}" : "an MQTT message arrives";

                case "event":
                    return Strings(Child(trigger, "event_type")) is { Count: > 0 } types
                        ? $"the {string.Join(" or ", types)} event fires"
                        : "an event fires";

                case "calendar":
                    return $"a calendar event {(Text(trigger, "event") == "end" ? "ends" : "starts")} on {who}";

                case "webhook":
                    return "a webhook is called";

                case "tag":
                    return "a tag is scanned";

                case "conversation":
                    return "you say one of the configured sentences";

                case "persistent_notification":
                    return "a notification is posted in Home Assistant";

                case "device":
                    return "a device trigger fires";

                case "":
                    return "a trigger fires";

                default:
                    return $"a {Words(kind)} trigger fires";
            }
        }

        public string Condition(JsonElement condition, int depth)
        {
            if (depth > MaxDepth) return "a nested condition";

            // Home Assistant accepts a bare template string wherever a condition goes.
            if (condition.ValueKind == JsonValueKind.String) return "a template is true";
            if (condition.ValueKind != JsonValueKind.Object) return "a condition holds";

            if (Child(condition, "enabled") is { ValueKind: JsonValueKind.False })
                return "(disabled) " + Condition(WithoutEnabled(condition), depth);

            var kind = Text(condition, "condition") ?? "";
            var who = Names(Child(condition, "entity_id"), " and ");
            var attribute = Text(condition, "attribute");
            var subject = attribute is null ? who : $"{who}'s {Words(attribute)}";

            switch (kind)
            {
                case "state":
                    {
                        var states = Strings(Child(condition, "state"));
                        var entities = Child(condition, "entity_id");
                        var value = states.Count == 0
                            ? "in the expected state"
                            : Join(states.Select(state => Said(entities, state)), " or ");
                        var held = Span(Child(condition, "for"));
                        return held is null ? $"{subject} is {value}" : $"{subject} has been {value} for {held}";
                    }

                case "numeric_state":
                    return Range(subject, Text(condition, "above"), Text(condition, "below"), asMove: false);

                case "time":
                    {
                        var after = Text(condition, "after");
                        var before = Text(condition, "before");
                        var days = Strings(Child(condition, "weekday"));

                        List<string> parts = [];
                        if (after is not null && before is not null) parts.Add($"the time is between {Clock(after)} and {Clock(before)}");
                        else if (after is not null) parts.Add($"the time is after {Clock(after)}");
                        else if (before is not null) parts.Add($"the time is before {Clock(before)}");
                        if (days.Count > 0) parts.Add(Weekdays(days));

                        return parts.Count == 0 ? "the time is right" : string.Join(" and ", parts);
                    }

                case "sun":
                    {
                        var after = Text(condition, "after");
                        var before = Text(condition, "before");
                        var afterOffset = Text(condition, "after_offset");
                        var beforeOffset = Text(condition, "before_offset");

                        if (after == "sunset" && before == "sunrise" && afterOffset is null && beforeOffset is null) return "it is dark";
                        if (after == "sunrise" && before == "sunset" && afterOffset is null && beforeOffset is null) return "it is daylight";

                        List<string> parts = [];
                        if (after is not null) parts.Add("after " + Offset(afterOffset, after).Replace("at ", "", StringComparison.Ordinal));
                        if (before is not null) parts.Add("before " + Offset(beforeOffset, before).Replace("at ", "", StringComparison.Ordinal));
                        return parts.Count == 0 ? "the sun is in the right place" : "it is " + string.Join(" and ", parts);
                    }

                case "zone":
                    return $"{who} is in {Name(Text(condition, "zone") ?? "the zone")}";

                case "template":
                    return "a template is true";

                case "trigger":
                    {
                        var ids = Strings(Child(condition, "id"));
                        var told = ids.Select(id => TriggerNames.TryGetValue(id, out var text) ? "“" + text + "”" : $"'{id}'");
                        return ids.Count == 0 ? "a particular trigger fired" : "the trigger was " + Join(told, " or ");
                    }

                case "and":
                    return Join(Items(Child(condition, "conditions") ?? default).Select(c => Condition(c, depth + 1)), " and ");

                case "or":
                    return "any of: " + Join(Items(Child(condition, "conditions") ?? default).Select(c => Condition(c, depth + 1)), "; ");

                case "not":
                    return "none of: " + Join(Items(Child(condition, "conditions") ?? default).Select(c => Condition(c, depth + 1)), "; ");

                case "device":
                    return "a device condition holds";

                case "":
                    return "a condition holds";

                default:
                    return $"a {Words(kind)} condition holds";
            }
        }

        public string Action(JsonElement action, int depth)
        {
            if (depth > MaxDepth) return "a nested step";
            if (action.ValueKind != JsonValueKind.Object) return "a step runs";

            if (Child(action, "enabled") is { ValueKind: JsonValueKind.False })
                return "(disabled) " + Action(WithoutEnabled(action), depth);

            if ((Text(action, "action") ?? Text(action, "service")) is { } service) return Call(service, action);

            if (Child(action, "delay") is { } delay)
                return $"Wait {Span(delay) ?? "a moment"}";

            if (Child(action, "wait_template") is not null)
                return "Wait until a template is true" + Timeout(action);

            if (Child(action, "wait_for_trigger") is { } waitFor)
                return "Wait until " + Join(Items(waitFor).Select(Trigger), " or ") + Timeout(action);

            if (Child(action, "choose") is { } choose)
            {
                var branches = Items(choose).Select(option =>
                    $"If {Join(Items(Child(option, "conditions") ?? default).Select(c => Condition(c, depth + 1)), " and ")}: " +
                    Sequence(Child(option, "sequence"), depth));

                var text = Join(branches, ". ");
                if (Child(action, "default") is { } fallback) text += $". Otherwise: {Sequence(fallback, depth)}";
                return text;
            }

            if (Child(action, "if") is { } test)
            {
                var text = $"If {Join(Items(test).Select(c => Condition(c, depth + 1)), " and ")}: {Sequence(Child(action, "then"), depth)}";
                if (Child(action, "else") is { } otherwise) text += $". Otherwise: {Sequence(otherwise, depth)}";
                return text;
            }

            if (Child(action, "repeat") is { } repeat)
            {
                var body = Sequence(Child(repeat, "sequence"), depth);

                if (Text(repeat, "count") is { } count) return $"Repeat {count} times: {body}";
                if (Child(repeat, "while") is { } loopWhile) return $"Repeat while {Join(Items(loopWhile).Select(c => Condition(c, depth + 1)), " and ")}: {body}";
                if (Child(repeat, "until") is { } loopUntil) return $"Repeat until {Join(Items(loopUntil).Select(c => Condition(c, depth + 1)), " and ")}: {body}";
                if (Child(repeat, "for_each") is not null) return $"For each item: {body}";
                return $"Repeat: {body}";
            }

            if (Child(action, "parallel") is { } parallel)
                return "At the same time: " + Join(Items(parallel).Select(step => Action(step, depth + 1)), "; ");

            if (Child(action, "sequence") is { } sequence)
                return Sequence(sequence, depth);

            if (Child(action, "condition") is not null)
                return "Continue only if " + Condition(action, depth + 1);

            if (Child(action, "stop") is { } stop)
                return stop.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(stop.GetString())
                    ? $"Stop: {stop.GetString()}"
                    : "Stop";

            if (Text(action, "event") is { } eventName)
                return $"Fire the {eventName} event";

            if (Child(action, "scene") is { } scene)
                return "Activate " + Names(scene, " and ");

            if (Child(action, "variables") is not null)
                return "Set some variables";

            return "A step runs";
        }

        /// <summary>A nested list of steps, said on one line.</summary>
        private string Sequence(JsonElement? block, int depth)
        {
            var steps = Items(block ?? default).Select(step => Lower(Action(step, depth + 1))).ToList();
            return steps.Count == 0 ? "do nothing" : string.Join(", then ", steps);
        }

        private static string Timeout(JsonElement action) =>
            Span(Child(action, "timeout")) is { } limit ? $" (giving up after {limit})" : "";

        // ---- service calls ----

        private string Call(string service, JsonElement action)
        {
            var data = Child(action, "data") ?? Child(action, "service_data");
            var target = Child(action, "target");

            var ids = Strings(Child(target ?? default, "entity_id"));
            if (ids.Count == 0) ids = Strings(Child(action, "entity_id"));
            if (ids.Count == 0 && data is { } d) ids = Strings(Child(d, "entity_id"));

            var who = ids.Count == 0 ? null : Join(ids.Select(Name), " and ");
            var domain = Ha.DomainOf(service);
            var name = service.Length > domain.Length + 1 ? service[(domain.Length + 1)..] : service;

            if (domain == "notify" || service == "persistent_notification.create")
            {
                var message = Text(data ?? default, "message");
                var title = Text(data ?? default, "title");
                var via = service switch
                {
                    "notify.notify" => "Send a notification",
                    "persistent_notification.create" => "Show a notification in Home Assistant",
                    _ => $"Send a notification via {Words(name)}",
                };

                var text = via;
                if (title is not null) text += $" titled {Quote(title)}";
                if (message is not null) text += $": {Quote(message)}";
                return text;
            }

            if (domain == "scene" && name == "turn_on")
                return who is null ? "Activate a scene" : $"Activate {who}";

            if (domain == "script")
                return name == "turn_on"
                    ? (who is null ? "Run a script" : $"Run {who}")
                    : $"Run the {Words(name)} script";

            var verb = name switch
            {
                "turn_on" => "Turn on",
                "turn_off" => "Turn off",
                "toggle" => "Toggle",
                "open_cover" => "Open",
                "close_cover" => "Close",
                "stop_cover" => "Stop",
                "lock" => "Lock",
                "unlock" => "Unlock",
                "open" => "Open",
                "start" => "Start",
                "pause" => "Pause",
                "stop" => "Stop",
                "return_to_base" => "Send home",
                "media_play" => "Play",
                "media_pause" => "Pause",
                "media_stop" => "Stop",
                "media_next_track" => "Skip forward on",
                "volume_mute" => "Mute",
                "alarm_arm_away" => "Arm (away)",
                "alarm_arm_home" => "Arm (home)",
                "alarm_arm_night" => "Arm (night)",
                "alarm_disarm" => "Disarm",
                "press" => "Press",
                "reload" => "Reload",
                "increment" => "Increase",
                "decrement" => "Decrease",
                "select_option" => "Set",
                "set_value" => "Set",
                "set_temperature" => "Set",
                "set_hvac_mode" => "Set",
                "set_preset_mode" => "Set",
                "set_fan_mode" => "Set",
                "set_percentage" => "Set",
                "set_cover_position" => "Set",
                "set_cover_tilt_position" => "Tilt",
                "volume_set" => "Set",
                "set_datetime" => "Set",
                _ => null,
            };

            var details = Details(name, data);

            if (verb is null)
                return who is null
                    ? $"Call {service}{details}"
                    : $"Call {service} on {who}{details}";

            return who is null ? $"{verb}{details}" : $"{verb} {who}{details}";
        }

        /// <summary>The few data keys a reader would want to see, said in words.</summary>
        private static string Details(string service, JsonElement? data)
        {
            if (data is not { ValueKind: JsonValueKind.Object } d) return "";

            List<string> parts = [];

            if (Text(d, "brightness_pct") is { } pct) parts.Add($"at {pct}% brightness");
            else if (Text(d, "brightness") is { } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var level))
                parts.Add($"at {Ha.Number(level / 255 * 100)}% brightness");

            if (Text(d, "color_temp_kelvin") is { } kelvin) parts.Add($"at {kelvin} K");
            else if (Text(d, "color_temp") is { } mired) parts.Add($"at colour temperature {mired}");
            if (Text(d, "color_name") is { } colour) parts.Add($"in {colour}");
            else if (Child(d, "rgb_color") is { ValueKind: JsonValueKind.Array }) parts.Add("in a chosen colour");
            if (Text(d, "kelvin") is { } k) parts.Add($"at {k} K");

            if (Text(d, "temperature") is { } temperature) parts.Add($"to {temperature}°");
            if (Text(d, "target_temp_high") is { } high && Text(d, "target_temp_low") is { } low) parts.Add($"to between {low}° and {high}°");
            if (Text(d, "hvac_mode") is { } hvac) parts.Add($"to {hvac}");
            if (Text(d, "preset_mode") is { } preset) parts.Add($"to the {preset} preset");
            if (Text(d, "fan_mode") is { } fanMode) parts.Add($"to fan mode {fanMode}");
            if (Text(d, "percentage") is { } percentage) parts.Add($"to {percentage}%");
            if (Text(d, "position") is { } position) parts.Add($"to {position}%");
            if (Text(d, "tilt_position") is { } tilt) parts.Add($"to {tilt}%");
            if (Text(d, "volume_level") is { } volume && double.TryParse(volume, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                parts.Add($"volume to {Ha.Number(v * 100)}%");
            if (Text(d, "option") is { } option) parts.Add($"to {Quote(option)}");
            if (Text(d, "value") is { } value && service != "set_datetime") parts.Add($"to {value}");
            if (Text(d, "datetime") is { } dateTime) parts.Add($"to {dateTime}");
            if (Text(d, "time") is { } time) parts.Add($"to {Clock(time)}");
            if (Text(d, "transition") is { } transition) parts.Add($"over {transition} seconds");
            if (Text(d, "code") is not null) parts.Add("with a code");

            return parts.Count == 0 ? "" : " " + string.Join(", ", parts);
        }

        // ---- names and values ----

        private string Name(string entityId)
        {
            var friendly = nameOf(entityId);
            if (!string.IsNullOrWhiteSpace(friendly)) return friendly.Trim();

            // "binary_sensor.hall_motion" reads as "hall motion (binary sensor)": the object part is usually
            // a name already, and the domain settles what kind of thing it is when the name does not.
            var dot = entityId.IndexOf('.');
            if (dot <= 0 || dot == entityId.Length - 1) return entityId;

            var objectPart = Words(entityId[(dot + 1)..]);
            var domain = Words(entityId[..dot]);

            return objectPart.Contains(domain, StringComparison.OrdinalIgnoreCase)
                ? objectPart
                : $"{objectPart} ({domain})";
        }

        /// <summary>
        /// A state in the words Home Assistant uses for the entities being spoken about -- "open" for a
        /// door rather than "on" -- but only where they all use the same ones. A trigger over a door and
        /// a lamp has no single word for <c>on</c>, so it keeps the raw state, which is at least not
        /// wrong about either of them.
        /// </summary>
        private string Said(JsonElement? entityIds, string state)
        {
            string? agreed = null;
            foreach (var entityId in Strings(entityIds))
            {
                var label = Ha.StateLabel(Ha.DomainOf(entityId), classOf(entityId), state);
                if (agreed is null) agreed = label;
                else if (!string.Equals(agreed, label, StringComparison.Ordinal)) return Value(state);
            }

            return agreed ?? Value(state);
        }

        private string Names(JsonElement? element, string joiner)
        {
            var ids = Strings(element);
            return ids.Count == 0 ? "something" : Join(ids.Select(Name), joiner);
        }
    }

    // ---- small words ----

    /// <summary>
    /// A state arriving, as a verb. Given the state in Home Assistant's own words, so the cases below the
    /// first two are the wordings a device class produces -- a door sensor reaches here as "open", never
    /// as "on". The ones listed are those that "becomes X" says badly: "becomes wet" is a sentence and
    /// "becomes update available" is not.
    /// </summary>
    private static string Becomes(string state) => state.ToLowerInvariant() switch
    {
        "on" => "turns on",
        "off" => "turns off",
        "open" => "opens",
        "closed" => "closes",
        "locked" => "locks",
        "unlocked" => "unlocks",
        "home" => "arrives home",
        "away" or "not_home" => "leaves home",
        "unavailable" => "becomes unavailable",
        "playing" => "starts playing",
        "paused" => "pauses",
        "idle" => "goes idle",
        "detected" => "detects something",
        "clear" => "clears",
        "light detected" => "sees light",
        "no light" => "sees dark",
        "glass break detected" => "detects a glass break",
        "tampering detected" => "detects tampering",
        "moving" => "starts moving",
        "not moving" => "stops moving",
        "running" => "starts running",
        "not running" => "stops running",
        "charging" => "starts charging",
        "not charging" => "stops charging",
        "plugged in" => "is plugged in",
        "unplugged" => "is unplugged",
        "problem" => "reports a problem",
        "ok" => "reads OK",
        "update available" => "has an update available",
        "up-to-date" => "becomes up to date",
        "returning to dock" => "heads back to its dock",
        _ => $"becomes {Value(state)}",
    };

    private static string Value(string state) => state switch
    {
        "not_home" => "away",
        _ => state,
    };

    private static string Range(string subject, string? above, string? below, bool asMove)
    {
        if (above is not null && below is not null)
            return asMove ? $"{subject} leaves the range {below} to {above}" : $"{subject} is between {below} and {above}";
        if (above is not null) return asMove ? $"{subject} goes above {above}" : $"{subject} is above {above}";
        if (below is not null) return asMove ? $"{subject} drops below {below}" : $"{subject} is below {below}";
        return asMove ? $"{subject} changes" : $"{subject} is in range";
    }

    private static string? Every(string? pattern, string unit)
    {
        if (pattern is null || !pattern.StartsWith('/')) return null;
        if (!int.TryParse(pattern[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;

        return n == 1 ? $"every {unit}" : $"every {n} {unit}s";
    }

    private static string Offset(string? offset, string eventName)
    {
        var moment = eventName is "sunrise" or "sunset" ? eventName : Words(eventName);
        if (string.IsNullOrWhiteSpace(offset)) return $"at {moment}";

        var negative = offset.StartsWith('-');
        var span = Span(offset.TrimStart('-', '+'));
        if (span is null) return $"at {moment}";

        return negative ? $"{span} before {moment}" : $"{span} after {moment}";
    }

    private static string Weekdays(IReadOnlyList<string> days)
    {
        var set = new HashSet<string>(days.Select(day => day.ToLowerInvariant()), StringComparer.Ordinal);
        string[] week = ["mon", "tue", "wed", "thu", "fri"];
        string[] weekend = ["sat", "sun"];

        if (set.SetEquals(week)) return "on weekdays";
        if (set.SetEquals(weekend)) return "at the weekend";
        if (set.SetEquals(week.Concat(weekend))) return "every day";

        return "on " + Join(days.Select(Day), ", ");
    }

    private static string Day(string day) => day.ToLowerInvariant() switch
    {
        "mon" => "Monday",
        "tue" => "Tuesday",
        "wed" => "Wednesday",
        "thu" => "Thursday",
        "fri" => "Friday",
        "sat" => "Saturday",
        "sun" => "Sunday",
        _ => day,
    };

    /// <summary>"07:00:00" as "07:00"; anything else as written.</summary>
    private static string Clock(string time)
    {
        var parts = time.Split(':');
        if (parts.Length >= 2 && parts[0].Length <= 2 && parts[1].Length == 2 &&
            int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && h is >= 0 and < 24 && m is >= 0 and < 60)
        {
            var clock = $"{h:00}:{m:00}";
            if (parts.Length >= 3 && parts[2] != "00") clock += ":" + parts[2];
            return clock;
        }

        return time;
    }

    /// <summary>A duration however Home Assistant lets it be written, said in words; null when it is not one.</summary>
    internal static string? Span(JsonElement? element)
    {
        if (element is not { } value) return null;

        double seconds;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out seconds)) return null;
                break;

            case JsonValueKind.String:
                return Span(value.GetString());

            case JsonValueKind.Object:
                {
                    seconds = 0;
                    var any = false;
                    foreach (var (key, factor) in new[] { ("days", 86400d), ("hours", 3600d), ("minutes", 60d), ("seconds", 1d), ("milliseconds", 0.001) })
                    {
                        if (!value.TryGetProperty(key, out var part)) continue;
                        var number = part.ValueKind == JsonValueKind.Number && part.TryGetDouble(out var n) ? n
                            : part.ValueKind == JsonValueKind.String && double.TryParse(part.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s
                            : double.NaN;
                        if (double.IsNaN(number)) return null;
                        seconds += number * factor;
                        any = true;
                    }

                    if (!any) return null;
                    break;
                }

            default:
                return null;
        }

        if (!double.IsFinite(seconds) || seconds < 0) return null;
        return Spoken(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>A duration written as text: seconds, "HH:MM:SS", "H:MM", or "D days, HH:MM:SS" the way a timedelta prints.</summary>
    internal static string? Span(string? written)
    {
        var text = written?.Trim() ?? "";
        if (text.Length == 0) return null;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return double.IsFinite(plain) && plain >= 0 ? Spoken(TimeSpan.FromSeconds(plain)) : null;

        // Anything else is a template or a mistake, and either way not something to guess a number for.
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return null;

        double days = 0;
        var hoursPart = parts[0];
        if (hoursPart.Contains("day", StringComparison.OrdinalIgnoreCase))
        {
            var comma = hoursPart.IndexOf(',');
            if (comma < 0) return null;
            if (!double.TryParse(hoursPart[..comma].Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out days)) return null;
            hoursPart = hoursPart[(comma + 1)..].Trim();
        }

        if (!double.TryParse(hoursPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) return null;
        var secs = 0d;
        if (parts.Length == 3 && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) return null;

        var seconds = days * 86400 + hours * 3600 + minutes * 60 + secs;
        return double.IsFinite(seconds) && seconds >= 0 ? Spoken(TimeSpan.FromSeconds(seconds)) : null;
    }

    /// <summary>"2 hours 30 minutes": exact rather than rounded, because this is a value someone is approving.</summary>
    private static string Spoken(TimeSpan span)
    {
        if (span == TimeSpan.Zero) return "no time at all";

        List<string> parts = [];
        if (span.Days > 0) parts.Add(Plural(span.Days, "day"));
        if (span.Hours > 0) parts.Add(Plural(span.Hours, "hour"));
        if (span.Minutes > 0) parts.Add(Plural(span.Minutes, "minute"));
        var seconds = span.Seconds + span.Milliseconds / 1000.0;
        if (seconds > 0) parts.Add(seconds == Math.Floor(seconds) ? Plural((int)seconds, "second") : $"{Ha.Number(seconds)} seconds");

        return string.Join(" ", parts);
    }

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    /// <summary>"hall_motion" as "hall motion"; a service name the same way.</summary>
    private static string Words(string text) => text.Replace('_', ' ').Replace('-', ' ').Trim();

    private static string Quote(string text)
    {
        var flat = new string([.. text.Select(ch => char.IsControl(ch) ? ' ' : ch)]).Trim();
        if (flat.Length > MaxQuote) flat = flat[..MaxQuote].TrimEnd() + "…";
        return "“" + flat + "”";
    }

    private static string Join(IEnumerable<string> parts, string joiner)
    {
        var list = parts.ToList();
        return list.Count == 0 ? "nothing" : string.Join(joiner, list);
    }

    private static string Lower(string sentence) =>
        sentence.Length > 0 && char.IsUpper(sentence[0]) && !(sentence.Length > 1 && char.IsUpper(sentence[1]))
            ? char.ToLowerInvariant(sentence[0]) + sentence[1..]
            : sentence;

    /// <summary>The same object with <c>enabled</c> taken out, so a disabled block is described once.</summary>
    private static JsonElement WithoutEnabled(JsonElement element)
    {
        var buffer = new StringBuilder("{");
        var first = true;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "enabled") continue;
            if (!first) buffer.Append(',');
            first = false;
            buffer.Append(JsonSerializer.Serialize(property.Name)).Append(':').Append(property.Value.GetRawText());
        }

        buffer.Append('}');
        return JsonDocument.Parse(buffer.ToString()).RootElement.Clone();
    }
}
