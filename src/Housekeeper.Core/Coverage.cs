using System.Text.Json.Nodes;

namespace Housekeeper.Core;

/// <summary>One existing automation that would already fire on what a finding is about.</summary>
/// <param name="EntityId">The automation's own entity, e.g. <c>automation.high_co2_alert</c>.</param>
/// <param name="Alias">Its name in Home Assistant, which is what the user will recognise it by.</param>
/// <param name="Why">The trigger that fits, in words, for the card: "fires when it goes above 1000 ppm".</param>
/// <param name="Conditional">
/// True when the automation also has conditions. Whether they hold cannot be known from here, so the claim is
/// the weaker one -- it fires, if its conditions allow -- and it is listed after any automation without them.
/// </param>
public sealed record CoveringAutomation(string EntityId, string Alias, string Why, bool Conditional);

/// <summary>
/// The existing automations as last read, and every entity's state as taken alongside them: all that
/// <see cref="Coverage"/> needs to answer for a finding, worked out once rather than once per finding.
/// </summary>
public sealed class KnownAutomations
{
    /// <param name="automations">The automations as last read from Home Assistant.</param>
    /// <param name="entities">Every entity's state as of <paramref name="seenUtc"/>.</param>
    /// <param name="readUtc">When the automations were last read successfully.</param>
    /// <param name="seenUtc">When the entity states were taken, which is the moment every answer is about.</param>
    public KnownAutomations(
        IReadOnlyList<ExistingAutomation> automations,
        IReadOnlyList<HaEntity> entities,
        DateTimeOffset readUtc,
        DateTimeOffset seenUtc)
    {
        Dictionary<string, HaEntity> byId = new(StringComparer.Ordinal);
        Dictionary<string, string> registry = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            byId.TryAdd(entity.EntityId, entity);
            if (entity.RegistryId is { } registryId) registry.TryAdd(registryId, entity.EntityId);
        }

        Automations = automations;
        Entities = byId;
        ReadUtc = readUtc;
        SeenUtc = seenUtc;
        Registry = registry;

        // Only an automation that is switched on fires. That lives on its entity, fresh every scan; the
        // config does not say, and Home Assistant goes on serving the config of one that is switched off.
        Live = [.. automations.Where(automation => byId.TryGetValue(automation.EntityId, out var entity) && entity.State == "on")];
    }

    public IReadOnlyList<ExistingAutomation> Automations { get; }

    public IReadOnlyDictionary<string, HaEntity> Entities { get; }

    public DateTimeOffset ReadUtc { get; }

    public DateTimeOffset SeenUtc { get; }

    /// <summary>The automations that are switched on.</summary>
    internal IReadOnlyList<ExistingAutomation> Live { get; }

    /// <summary>Entity ids by the entity registry's own id, which is how a device trigger built in the editor names its entity.</summary>
    internal IReadOnlyDictionary<string, string> Registry { get; }
}

/// <summary>
/// Whether the user already has an automation that fires on what a finding is about.
///
/// Housekeeper compares a finished draft against the existing automations, but by then the model has been
/// asked and a minute of the user's own hardware has been spent on something they may not need. This is a
/// check at the point the offer is made instead, and it is a much narrower one: close enough is fine for
/// "this draft may duplicate that", and it is not fine for "you are already covered". A user told they are
/// covered dismisses a real finding on the strength of it, so the only claim made here is that a trigger of
/// theirs fires on exactly this. A trigger counts only when all of these hold:
///
/// - it is switched on, in an automation that is switched on;
/// - it names this entity itself: by entity id, or by the registry id a device trigger uses. Not its
///   device, not its area, not a template that reads it, not an action or condition that mentions it;
/// - it watches the entity's state or reading, not one of its attributes or a template's sum over it;
/// - it fires on what was found. For a reading past its usual range, a line the reading is already past,
///   on that side. For a state held too long, that state, with a <c>for:</c> that has already run out.
///   For an entity gone quiet, a <c>to:</c> naming the very state it went quiet in.
///
/// That leaves out a good deal a person would count -- a template watching the sensor, a blueprint, whose
/// triggers are in the blueprint rather than the config -- and each of those costs nothing more than the
/// offer the card always made. An automation with conditions still counts, said as the weaker claim it is.
/// A card that speaks for several entities at once is covered only when every one of them is.
/// </summary>
public static class Coverage
{
    /// <summary>
    /// What a finding needs a trigger to fire on, or null when an automation could not have answered it: a
    /// routine is already checked against the automations where it is found, and a missing entity is a
    /// repair rather than a suggestion.
    /// </summary>
    public static WatchKind? Wanted(Anomaly finding) => finding.Kind switch
    {
        AnomalyKind.StuckState => WatchKind.Held,
        AnomalyKind.Unavailable => WatchKind.Unavailable,

        // Which side of its usual range the reading went out on decides which line would have caught it.
        AnomalyKind.NumericOutlier =>
            Evidence(finding) is { } evidence && Number(evidence, "current") is { } current &&
            Number(evidence, "median") is { } median && current != median
                ? current > median ? WatchKind.Above : WatchKind.Below
                : null,

        // A concern says in its own rule which side of which line, or which state for how long. An
        // automation that fires on some other shape -- an offline alert for a concern about the reading --
        // is not an answer to it.
        AnomalyKind.Concern =>
            Evidence(finding)?["rule_kind"] is JsonValue value && value.TryGetValue<string>(out var kind) &&
            Enum.TryParse<WatchKind>(kind, out var rule) && rule != WatchKind.Any
                ? rule
                : null,

        _ => null,
    };

    /// <summary>
    /// The existing automations that already fire on what this finding is about, the strongest claims
    /// first. Empty when none does, and equally when there is no telling.
    /// </summary>
    public static IReadOnlyList<CoveringAutomation> Find(Anomaly finding, KnownAutomations known)
    {
        if (Wanted(finding) is not { } wanted || known.Live.Count == 0) return [];

        // The state a held-state finding is about, as it was when last raised. If the entity has moved on,
        // the finding is about to close, and whatever fires on the new state is no answer to it.
        var raisedIn = Evidence(finding)?["state"] is JsonValue value && value.TryGetValue<string>(out var state) ? state : null;

        Dictionary<string, CoveringAutomation> found = new(StringComparer.Ordinal);

        // Everything the card speaks for. Covering the front door says nothing about the eleven other
        // things that went quiet with it, and the card must not suggest otherwise.
        foreach (var subject in AnomalyScanner.Siblings(finding.EvidenceJson).Prepend(finding.EntityId).Distinct(StringComparer.Ordinal))
        {
            if (!known.Entities.TryGetValue(subject, out var entity)) return [];

            var lead = subject == finding.EntityId;
            if (lead && wanted == WatchKind.Held && raisedIn is not null &&
                !string.Equals(raisedIn, entity.State, StringComparison.OrdinalIgnoreCase))
                return [];

            var name = lead ? "it" : entity.FriendlyName ?? entity.EntityId;
            var covered = false;

            foreach (var automation in known.Live)
            {
                var why = automation.Triggers
                    .Select(trigger => Fires(trigger, wanted, entity, name, known))
                    .FirstOrDefault(fits => fits is not null);
                if (why is null) continue;

                covered = true;
                found.TryAdd(automation.EntityId, new CoveringAutomation(
                    automation.EntityId,
                    automation.Alias,
                    automation.HasConditions ? why + ", if its conditions allow" : why,
                    automation.HasConditions));
            }

            if (!covered) return [];
        }

        return [.. found.Values
            .OrderBy(covering => covering.Conditional)
            .ThenBy(covering => covering.Alias, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What this trigger fires on that the finding is about, in words; null when it does not fire on it.</summary>
    private static string? Fires(AutomationTrigger trigger, WatchKind wanted, HaEntity entity, string name, KnownAutomations known)
    {
        if (!trigger.Enabled || trigger.ForUnreadable) return null;
        if (!trigger.Entities.Any(id => id == entity.EntityId || (known.Registry.TryGetValue(id, out var named) && named == entity.EntityId)))
            return null;

        var since = known.SeenUtc - entity.LastChanged;
        var wait = trigger.For is { } f ? $" for {Ha.Duration(f)}" : "";

        switch (wanted)
        {
            case WatchKind.Above or WatchKind.Below:
            {
                // The entity's own reading: not an attribute, and not a template's arithmetic on it. A
                // sensor's device trigger is a numeric trigger under another name.
                var reading = trigger.Attribute is null && !trigger.ValueTemplate &&
                              (trigger.Kind == "numeric_state" || (trigger.Kind == "device" && trigger.Domain == "sensor"));
                if (!reading || entity.Numeric is not { } current) return null;

                // A line that cannot be read now -- an input_number that is unavailable, say -- is no line.
                var above = Line(trigger.Above, known);
                var below = Line(trigger.Below, known);
                if ((trigger.Above is not null && above is null) || (trigger.Below is not null && below is null)) return null;

                var unit = string.IsNullOrWhiteSpace(entity.Unit) ? "" : " " + entity.Unit;

                // Home Assistant fires as the reading crosses the line, so one it is already past has fired,
                // or will once its for: is up; given both lines it fires only between them. The for: is said
                // rather than checked: a reading changes by the minute, so how long it has been past the line
                // is not something the entity's own state records.
                if (wanted == WatchKind.Above)
                    return above is { } line && current > line && (below is not { } top || current < top)
                        ? $"fires when {name} goes above {Ha.Number(line)}{unit}{wait}"
                        : null;

                return below is { } floor && current < floor && (above is not { } bottom || current > bottom)
                    ? $"fires when {name} drops below {Ha.Number(floor)}{unit}{wait}"
                    : null;
            }

            case WatchKind.Held:
            {
                // Without a for: it fires the moment the state changes, which says nothing about the state
                // being held too long. With one, it has to have run out already.
                if (trigger.For is not { } held || held > since) return null;

                if (trigger.Kind == "state")
                {
                    if (trigger.Attribute is not null) return null;
                    if (trigger.To is { } to && !to.Contains(entity.State, StringComparer.OrdinalIgnoreCase)) return null;
                    if (trigger.NotTo is { } notTo && notTo.Contains(entity.State, StringComparer.OrdinalIgnoreCase)) return null;

                    return trigger.To is null
                        ? $"fires when {name} has not changed for {Ha.Duration(held)}"
                        : $"fires when {name} stays {entity.StateLabel} for {Ha.Duration(held)}";
                }

                return trigger.Kind == "device" && StateOf(trigger.Type) is { } fires &&
                       string.Equals(fires, entity.State, StringComparison.OrdinalIgnoreCase)
                    ? $"fires when {name} stays {entity.StateLabel} for {Ha.Duration(held)}"
                    : null;
            }

            case WatchKind.Unavailable:
            {
                // Only a state trigger told to fire on that very state. One without a to: fires on every
                // change and was written for something else, and a numeric or device trigger never fires on
                // a sensor that has stopped reporting -- which is the whole of what this finding is about.
                if (trigger.Kind != "state" || trigger.Attribute is not null || !entity.IsUnavailable) return null;
                if (trigger.To is not { } to || !to.Contains(entity.State, StringComparer.OrdinalIgnoreCase)) return null;
                if (trigger.NotTo is { } notTo && notTo.Contains(entity.State, StringComparer.OrdinalIgnoreCase)) return null;
                if (trigger.For is { } held && held > since) return null;

                return $"fires when {name} becomes {entity.StateLabel}{wait}";
            }

            default:
                return null;
        }
    }

    /// <summary>The state a device trigger of this type fires on, for the types whose meaning does not depend on the integration.</summary>
    private static string? StateOf(string? type) => type switch
    {
        "turned_on" or "opened" => "on",
        "turned_off" or "not_opened" => "off",
        _ => null,
    };

    /// <summary>A line as written -- a number, or an entity that holds one, such as an <c>input_number</c> -- as a number now.</summary>
    private static double? Line(string? written, KnownAutomations known) =>
        written is null ? null
        : Ha.TryNumeric(written, out var value) ? value
        : known.Entities.TryGetValue(written.ToLowerInvariant(), out var entity) ? entity.Numeric
        : null;

    private static JsonObject? Evidence(Anomaly finding)
    {
        try
        {
            return JsonNode.Parse(finding.EvidenceJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static double? Number(JsonObject evidence, string name) =>
        evidence[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
