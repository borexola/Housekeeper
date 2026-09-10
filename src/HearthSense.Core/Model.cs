using System.Globalization;

namespace HearthSense.Core;

/// <summary>A Home Assistant entity as returned by <c>GET /api/states</c>.</summary>
public sealed record HaEntity(
    string EntityId,
    string State,
    DateTimeOffset LastChanged,
    DateTimeOffset LastUpdated,
    string? FriendlyName = null,
    string? DeviceClass = null,
    string? Unit = null,
    string? Area = null)
{
    /// <summary>The part before the first dot, e.g. <c>light</c> for <c>light.kitchen</c>.</summary>
    public string Domain => Ha.DomainOf(EntityId);

    /// <summary>The state parsed as a number, or null when it is not numeric.</summary>
    public double? Numeric => Ha.TryNumeric(State, out var v) ? v : null;

    /// <summary>True when Home Assistant reports the entity as missing or not yet known.</summary>
    public bool IsUnavailable => Ha.IsUnavailable(State);
}

/// <summary>An automation that already exists in Home Assistant, reduced to what duplicate detection needs.</summary>
public sealed record ExistingAutomation(
    string Id,
    string EntityId,
    string Alias,
    IReadOnlySet<string> Entities,
    IReadOnlySet<string> TriggerKinds);

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
}

public enum AnomalyKind
{
    /// <summary>Held a state far longer than it historically does — the "freezer door left open" case.</summary>
    StuckState = 0,
    /// <summary>A numeric reading far outside its own recent distribution.</summary>
    NumericOutlier = 1,
    /// <summary>Reporting unavailable/unknown after a history of being available.</summary>
    Unavailable = 2,
    /// <summary>An automation HearthSense created references an entity that no longer exists.</summary>
    MissingEntity = 3,
}

public enum AnomalyStatus
{
    Open = 0,
    Dismissed = 1,
    /// <summary>The user turned it into an automation proposal.</summary>
    Promoted = 2,
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
}

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

    /// <summary>Formats a duration the way a person would say it.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 90) return $"{Math.Round(span.TotalSeconds)} seconds";
        if (span.TotalMinutes < 90) return $"{Math.Round(span.TotalMinutes)} minutes";
        if (span.TotalHours < 48) return $"{Math.Round(span.TotalHours, 1)} hours";
        return $"{Math.Round(span.TotalDays, 1)} days";
    }

    public static string Number(double value) =>
        Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
