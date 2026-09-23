using System.Globalization;

namespace Housekeeper.Core;

/// <summary>
/// A Home Assistant entity as returned by <c>GET /api/states</c>. An automation entity also carries the id
/// of its stored config, which is what its definition is read and written under.
/// </summary>
public sealed record HaEntity(
    string EntityId,
    string State,
    DateTimeOffset LastChanged,
    DateTimeOffset LastUpdated,
    string? FriendlyName = null,
    string? DeviceClass = null,
    string? Unit = null,
    string? Area = null,
    string? AutomationConfigId = null,
    string? DeviceId = null,
    string? DeviceName = null,

    /// <summary>
    /// Home Assistant's <c>state_class</c>: <c>measurement</c>, <c>total</c> or <c>total_increasing</c>.
    /// The last two mark a running total, which no distribution-based detector can say anything about.
    /// </summary>
    string? StateClass = null,

    /// <summary>
    /// Home Assistant's own <c>entity_category</c>: <c>config</c>, <c>diagnostic</c>, or null for an
    /// ordinary entity. Read from the entity registry, so null also means the registry was not available.
    /// </summary>
    string? EntityCategory = null,

    /// <summary>Hidden by the user in Home Assistant, which is them saying they do not want to see it.</summary>
    bool Hidden = false,

    /// <summary>
    /// The area's id in Home Assistant's registry, which is made from the name it was created with and does
    /// not change when it is renamed. What an automation targets, so it is what an automation is matched on;
    /// <see cref="Area"/> is the name, which is what a person reads.
    /// </summary>
    string? AreaId = null,

    /// <summary>
    /// The entity registry's own id for this entity: a 32-character hex string that, unlike the entity id,
    /// survives a rename. A device trigger built in Home Assistant's editor names its entity by this rather
    /// than by entity id. Null when the registry could not be read.
    /// </summary>
    string? RegistryId = null)
{
    /// <summary>The part before the first dot, e.g. <c>light</c> for <c>light.kitchen</c>.</summary>
    public string Domain => Ha.DomainOf(EntityId);

    /// <summary>
    /// Home Assistant classes this as a setting or an instrument reading rather than something the house
    /// does. This is the authoritative version of what <c>Baselines</c> otherwise has to guess from names.
    /// </summary>
    public bool IsConfigOrDiagnostic =>
        EntityCategory is not null &&
        (EntityCategory.Equals("config", StringComparison.OrdinalIgnoreCase) ||
         EntityCategory.Equals("diagnostic", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A running total rather than a reading: an energy meter, a data counter, a rainfall accumulator.
    /// Its newest value is the largest it has ever been by definition, so "unusually high" is meaningless.
    /// </summary>
    public bool IsCumulative =>
        StateClass is not null &&
        (StateClass.Equals("total_increasing", StringComparison.OrdinalIgnoreCase) ||
         StateClass.Equals("total", StringComparison.OrdinalIgnoreCase));

    /// <summary>The state parsed as a number, or null when it is not numeric.</summary>
    public double? Numeric => Ha.TryNumeric(State, out var v) ? v : null;

    /// <summary>True when Home Assistant reports the entity as missing or not yet known.</summary>
    public bool IsUnavailable => Ha.IsUnavailable(State);

    /// <summary>
    /// The state as Home Assistant words it for this entity -- "open" rather than "on" for a door --
    /// ready to sit mid-sentence. For reading to a person only: <see cref="State"/> is what a draft,
    /// a rule and every comparison use.
    /// </summary>
    public string StateLabel => Ha.StateLabel(Domain, DeviceClass, State);
}

/// <summary>
/// An automation that already exists in Home Assistant, reduced to what duplicate detection, the routine
/// search and the coverage check need.
/// </summary>
/// <param name="Entities">Everything the config names, anywhere in it, with areas and devices widened out.</param>
/// <param name="TriggerKinds">The <c>trigger:</c> values of its triggers. Empty for a blueprint, whose triggers are its own.</param>
/// <param name="Triggers">
/// Each of its triggers, with what decides when it fires. Empty for a blueprint, whose triggers are in the
/// blueprint rather than the config, and for a record built without reading them: either way nothing can be
/// said about what it fires on, and nothing is.
/// </param>
/// <param name="HasConditions">True when it has conditions, which can stop it acting even when a trigger fires.</param>
public sealed record ExistingAutomation(
    string Id,
    string EntityId,
    string Alias,
    IReadOnlySet<string> Entities,
    IReadOnlySet<string> TriggerKinds,
    IReadOnlyList<AutomationTrigger> Triggers,
    bool HasConditions = false);

/// <summary>
/// One trigger of an existing automation, reduced to what decides when it fires.
///
/// Kept one per trigger rather than pooled across the automation, because pooling is how one trigger's kind
/// came to be paired with another trigger's entity: a numeric trigger on the outdoor thermometer beside a
/// state trigger on the CO2 sensor read as "triggers on the CO2 reading".
/// </summary>
/// <param name="Kind">The <c>trigger:</c> value, or the older <c>platform:</c>: <c>state</c>, <c>numeric_state</c>, <c>device</c>...</param>
/// <param name="Entities">
/// Its <c>entity_id</c> values as written, lower-cased. Usually entity ids; a device trigger built in the
/// editor names its entity by the entity registry's id instead, which <see cref="HaEntity.RegistryId"/> resolves.
/// </param>
/// <param name="Enabled">False when it was switched off in the editor (<c>enabled: false</c>), and Home Assistant skips it.</param>
/// <param name="To">The states a <c>state</c> trigger fires on; null when it fires on any.</param>
/// <param name="NotTo">States a <c>state</c> trigger is told to ignore.</param>
/// <param name="For">How long the state or the reading has to hold before it fires. Null when it fires at once.</param>
/// <param name="ForUnreadable">A <c>for:</c> given as a template, so when it fires cannot be known from here.</param>
/// <param name="Above">A reading trigger's line, as written: a number, or the entity that holds one.</param>
/// <param name="Below">Its other line, likewise.</param>
/// <param name="Attribute">Set when it watches one of the entity's attributes rather than its state.</param>
/// <param name="ValueTemplate">True when a template, not the state itself, is the value it compares.</param>
/// <param name="Type">A device trigger's type: <c>turned_on</c>, <c>opened</c>, <c>temperature</c>...</param>
/// <param name="Domain">A device trigger's domain: <c>sensor</c>, <c>binary_sensor</c>, <c>switch</c>...</param>
public sealed record AutomationTrigger(
    string Kind,
    IReadOnlyList<string> Entities,
    bool Enabled = true,
    IReadOnlyList<string>? To = null,
    IReadOnlyList<string>? NotTo = null,
    TimeSpan? For = null,
    bool ForUnreadable = false,
    string? Above = null,
    string? Below = null,
    string? Attribute = null,
    bool ValueTemplate = false,
    string? Type = null,
    string? Domain = null);

/// <summary>A validated automation the model drafted, ready to show the user.</summary>
public sealed record AutomationDraft(
    string Alias,
    string? Description,
    string ConfigJson,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Actions,
    IReadOnlySet<string> TriggerKinds);

/// <summary>An existing automation that looks like it already does what a draft proposes.</summary>
public sealed record DuplicateMatch(string AutomationId, string Alias, double Score, string Reason);

public enum ProposalSource { User = 0, Anomaly = 1 }

public enum ProposalStatus
{
    /// <summary>Drafted and waiting for the user to confirm or reject.</summary>
    Draft = 0,
    /// <summary>Confirmed and written to Home Assistant.</summary>
    Created = 1,
    Rejected = 2,
    /// <summary>The model produced nothing usable, or Home Assistant refused the write.</summary>
    Failed = 3,
    /// <summary>Replaced by a refined draft; see the newer proposal whose <see cref="Proposal.ParentId"/> points here.</summary>
    Superseded = 4,
    /// <summary>Created here, then deleted in Home Assistant's own editor. Noticed by the scanner.</summary>
    Removed = 5,
}

/// <summary>A drafted automation and everything the user needs to decide on it.</summary>
public sealed record Proposal
{
    public long Id { get; init; }
    public required string Request { get; init; }
    public ProposalSource Source { get; init; }
    public ProposalStatus Status { get; init; }
    /// <summary>What the user asked to change about the parent draft, when this is a refinement.</summary>
    public string? Feedback { get; init; }
    /// <summary>The draft this one refines, if any.</summary>
    public long? ParentId { get; init; }
    public string? Alias { get; init; }
    public string? Description { get; init; }
    public string? ConfigJson { get; init; }
    public IReadOnlyList<string> Entities { get; init; } = [];
    public IReadOnlyList<string> Actions { get; init; } = [];
    public IReadOnlyList<DuplicateMatch> Duplicates { get; init; } = [];
    public string? HaAutomationId { get; init; }
    public string? Error { get; init; }
    public long? AnomalyId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? DecidedUtc { get; init; }

    /// <summary>
    /// When the user put this out of sight. Only hides it from the dashboard: a dismissed automation is
    /// still live in Home Assistant and is still watched for entities that disappear under it.
    /// </summary>
    public DateTimeOffset? DismissedUtc { get; init; }
}

public enum AnomalyKind
{
    /// <summary>Held a state far longer than it historically does — the "freezer door left open" case.</summary>
    StuckState = 0,
    /// <summary>A numeric reading far outside its own recent distribution.</summary>
    NumericOutlier = 1,
    /// <summary>Reporting unavailable/unknown after a history of being available.</summary>
    Unavailable = 2,
    /// <summary>An automation Housekeeper created references an entity that no longer exists.</summary>
    MissingEntity = 3,
    /// <summary>A rule the user set through a concern fired: a reading past a line, a state held too long.</summary>
    Concern = 4,
    /// <summary>
    /// Something the user does by hand, regularly enough to be a routine, that an automation could do for
    /// them: the pantry light after the pantry motion sensor, the porch light at about ten past nine. Not
    /// a problem, an opportunity; it is listed apart and never counts as serious.
    /// </summary>
    Habit = 5,
}

public enum AnomalyStatus
{
    Open = 0,
    Dismissed = 1,
    /// <summary>The user turned it into an automation proposal.</summary>
    Promoted = 2,
    /// <summary>The condition it described is no longer there, so the scanner closed it.</summary>
    Resolved = 3,
}

/// <summary>Something the scanner noticed. Never notifies on its own — the user decides what it becomes.</summary>
public sealed record Anomaly
{
    public long Id { get; init; }
    /// <summary>Stable identity for one ongoing condition, so repeated scans update rather than duplicate.</summary>
    public required string DedupKey { get; init; }
    public required string EntityId { get; init; }
    public AnomalyKind Kind { get; init; }
    public required string Summary { get; init; }
    /// <summary>The numbers behind <see cref="Summary"/>, as a JSON object.</summary>
    public string EvidenceJson { get; init; } = "{}";
    /// <summary>Plain-English request handed to the drafter when the user promotes this.</summary>
    public required string SuggestedRequest { get; init; }
    public AnomalyStatus Status { get; init; }
    public DateTimeOffset DetectedUtc { get; init; }
    public DateTimeOffset? DecidedUtc { get; init; }
    public long? ProposalId { get; init; }

    /// <summary>
    /// How far past its own bar this finding is, as a multiple: 1.0 is exactly at the threshold that raised
    /// it, 4.0 is four times over. Comparable across the three detectors on purpose, because the list is one
    /// list — an undifferentiated column of nineteen cards buries the freezer door among the smart plugs.
    /// </summary>
    public double Severity { get; init; } = 1;

    /// <summary>
    /// How many times the user has dismissed this finding. Each time raises the bar it has to clear to come
    /// back; after three it stays quiet for good. A dismissal is the user teaching the detector, and a
    /// detector that forgets the lesson after a week is not learning anything.
    /// </summary>
    public int Dismissals { get; init; }
}

/// <summary>
/// The two shapes of history the detectors need, which are not the same shape.
///
/// <see cref="Recent"/> has to be contiguous: the stuck-state detector measures how long the entity spent in
/// each state by subtracting one stored sample from the next, so a gap in it does not read as a gap, it reads
/// as one very long stretch, and the inflated worst case is the bar every finding is then measured against.
///
/// <see cref="Numeric"/> is the opposite. It wants coverage of the whole retention window rather than
/// adjacency, because a distribution taken from the most recent few hundred changes of a chatty sensor is a
/// distribution of the last few hours. That is how an outdoor thermometer came to be judged against an
/// afternoon it had never left: at 8.6 °C against a median of 23.25 °C it scored over five sigma, and the
/// only thing it had actually done was get dark.
/// </summary>
public sealed record EntityHistory(
    IReadOnlyList<StateSample> Recent,
    IReadOnlyList<StateSample> Numeric)
{
    /// <summary>Both views from one list, for callers that have no reason to distinguish them.</summary>
    public static EntityHistory Of(IReadOnlyList<StateSample> samples) => new(samples, samples);

    public static readonly EntityHistory Empty = new([], []);
}

/// <summary>
/// What the stored history amounts to right now. Exists so someone can be told why nothing has been found
/// yet — usually that most entities have not changed often enough for a detector to have an opinion.
/// </summary>
/// <param name="Entities">Entities with any history inside the retention window.</param>
/// <param name="Samples">Recorded state changes across all of them.</param>
/// <param name="OldestUtc">When the oldest kept sample was recorded, or null when there is none.</param>
public sealed record HistorySummary(int Entities, long Samples, DateTimeOffset? OldestUtc);

/// <summary>One observed state for an entity. Recorded only when the state actually changes.</summary>
public sealed record StateSample(string State, double? Numeric, DateTimeOffset ChangedUtc);

/// <summary>Small helpers shared by the model and the detectors.</summary>
public static class Ha
{
    private static readonly string[] UnavailableStates = ["unavailable", "unknown", "none", ""];

    public static string DomainOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot > 0 ? entityId[..dot] : entityId;
    }

    public static bool TryNumeric(string? state, out double value) =>
        double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static bool IsUnavailable(string? state) =>
        state is null || UnavailableStates.Contains(state.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A state written the way Home Assistant's own interface writes it: a door sensor at <c>on</c> is
    /// open, a moisture sensor at <c>on</c> is wet, a problem sensor at <c>off</c> is OK. The raw state is
    /// what an automation is written against and what the detectors compare, and it stays that everywhere
    /// those two things happen -- but it is not what the user sees anywhere in their house, and a card
    /// reading "On for 30 minutes" about a pantry door tells them nothing about a pantry door.
    ///
    /// Returned in the form that belongs in the middle of a sentence -- "usually open for about 17
    /// seconds" -- because the pages that start a sentence with it already capitalise, and doing it this
    /// way round keeps "OK" as OK.
    ///
    /// The wording is Home Assistant's, taken from its <c>binary_sensor</c> and per-domain state strings,
    /// so the same entity reads the same in both places. Anything not listed falls back to the state with
    /// its underscores opened out, which is what Home Assistant does with an unrecognised state too; that
    /// also leaves a device tracker's zone name alone, because "Work" is a place, not a word.
    /// </summary>
    public static string StateLabel(string domain, string? deviceClass, string? state)
    {
        var raw = state?.Trim() ?? "";
        if (raw.Length == 0) return "unavailable";

        var s = raw.ToLowerInvariant();
        var known = domain.ToLowerInvariant() switch
        {
            "binary_sensor" => BinaryLabel(deviceClass?.Trim().ToLowerInvariant(), s),
            "person" or "device_tracker" => s switch { "home" => "home", "not_home" => "away", _ => null },
            "update" => s switch { "on" => "update available", "off" => "up-to-date", _ => null },
            "vacuum" => s == "returning" ? "returning to dock" : null,
            "climate" => s == "heat_cool" ? "heat/cool" : null,
            _ => null,
        };

        return known ?? raw.Replace('_', ' ');
    }

    /// <summary>
    /// What <c>on</c> and <c>off</c> mean for a binary sensor, which is entirely down to its device class.
    /// Note that <c>lock</c> here is the inverse of the lock domain -- a lock binary sensor is a contact,
    /// and its <c>on</c> means unlocked -- and that <c>presence</c> is home and away rather than detected
    /// and clear. Both are Home Assistant's, and both are the kind of thing worth getting from its
    /// strings file rather than from first principles.
    /// </summary>
    private static string? BinaryLabel(string? deviceClass, string state)
    {
        if (state is not ("on" or "off")) return null;
        var on = state == "on";

        return deviceClass switch
        {
            "battery" => on ? "low" : "normal",
            "battery_charging" => on ? "charging" : "not charging",
            "carbon_monoxide" or "gas" or "motion" or "occupancy" or "smoke" or "sound" or "vibration"
                => on ? "detected" : "clear",
            "cold" => on ? "cold" : "normal",
            "connectivity" => on ? "connected" : "disconnected",
            "door" or "garage_door" or "opening" or "window" => on ? "open" : "closed",
            "glass_break" => on ? "glass break detected" : "clear",
            "heat" => on ? "hot" : "normal",
            "light" => on ? "light detected" : "no light",
            "lock" => on ? "unlocked" : "locked",
            "moisture" => on ? "wet" : "dry",
            "moving" => on ? "moving" : "not moving",
            "plug" => on ? "plugged in" : "unplugged",
            "presence" => on ? "home" : "away",
            "problem" => on ? "problem" : "OK",
            "running" => on ? "running" : "not running",
            "safety" => on ? "unsafe" : "safe",
            "tamper" => on ? "tampering detected" : "clear",
            "update" => on ? "update available" : "up-to-date",
            _ => on ? "on" : "off",
        };
    }

    /// <summary>
    /// Services that act on Home Assistant itself rather than on something in the house.
    ///
    /// Every other check in Housekeeper asks "can this be verified?", and for these the answer is yes —
    /// Home Assistant really does offer <c>homeassistant.stop</c>. Verifiable is being used as a proxy for
    /// safe, and here it is not one: an automation that restarts or stops Home Assistant, or moves where it
    /// thinks it is, is not something a language model should be able to reach for while drafting "turn the
    /// hall light off". They are kept off the menu the model is shown, and refused if it names one anyway.
    /// </summary>
    public static bool ActsOnTheInstallation(string service)
    {
        var domain = DomainOf(service);

        // The Supervisor's own domain: updating, restarting and rebuilding add-ons and the host.
        if (domain is "hassio" or "update") return true;
        if (domain is not "homeassistant") return false;

        var name = service[(domain.Length + 1)..];

        return name is "stop" or "restart" or "set_location" or "check_config" or "update_entity"
            || name.StartsWith("reload", StringComparison.Ordinal);
    }

    /// <summary>Formats a duration the way a person would say it.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 90) return $"{Math.Round(span.TotalSeconds)} seconds";
        if (span.TotalMinutes < 90) return $"{Math.Round(span.TotalMinutes)} minutes";
        if (span.TotalHours < 48) return $"{Math.Round(span.TotalHours, 1)} hours";
        return $"{Math.Round(span.TotalDays, 1)} days";
    }

    public static string Number(double value)
    {
        var rounded = Math.Round(value, 2);

        // Rounding a small negative number gives negative zero, which prints as "-0" and compares unequal to
        // "0" as text -- so a reading of -0.004 was shown as "-0" and slipped past the guard that drops a move
        // too small to see. It is zero; it is written as zero.
        if (rounded == 0) rounded = 0;

        return rounded.ToString(CultureInfo.InvariantCulture);
    }
}
