using System.Globalization;
using System.Text.Json;

namespace HearthSense.Core;

/// <summary>
/// Three detectors over a compact state history. Each is a pure function and each produces a plain-English
/// <see cref="Anomaly.SuggestedRequest"/>, so promoting a finding into an automation reuses the normal
/// drafting path rather than a second code path.
/// </summary>
public static class AnomalyDetection
{
    /// <summary>Consistency factor making a median absolute deviation comparable to a standard deviation.</summary>
    private const double MadScale = 1.4826;

    private static readonly double[] FriendlyMinutes =
        [1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440];

    public static IReadOnlyList<Anomaly> Detect(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        List<Anomaly> found = [];

        Add(found, DetectUnavailable(entity, history, options, nowUtc));
        Add(found, DetectStuckState(entity, history, options, nowUtc));
        Add(found, DetectNumericOutlier(entity, history, options, nowUtc));

        return found;

        static void Add(List<Anomaly> into, Anomaly? anomaly)
        {
            if (anomaly is not null) into.Add(anomaly);
        }
    }

    /// <summary>
    /// The "freezer door left open" case: the entity is holding a state far longer than it ever has before.
    /// </summary>
    public static Anomaly? DetectStuckState(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        if (entity.IsUnavailable || entity.Numeric is not null) return null;

        var heldFor = nowUtc - entity.LastChanged;
        if (heldFor < options.MinimumStuckDuration) return null;

        // Every *completed* period the entity previously spent in this same state, and when it began.
        List<(DateTimeOffset StartedUtc, TimeSpan Length)> previous = [];
        for (var i = 0; i < history.Count - 1; i++)
        {
            if (!string.Equals(history[i].State, entity.State, StringComparison.Ordinal)) continue;

            var span = history[i + 1].ChangedUtc - history[i].ChangedUtc;
            if (span > TimeSpan.Zero) previous.Add((history[i].ChangedUtc, span));
        }

        var required = Math.Max(4, options.MinimumSamples / 3);
        if (previous.Count < required) return null;

        // A door that is open for an hour at dinner time and for seconds at breakfast has two normals. When
        // enough periods began in the same part of the day, judge against those rather than the whole history.
        var band = BandOf(entity.LastChanged);
        var sameBand = previous.Where(p => BandOf(p.StartedUtc) == band).ToList();
        var timeOfDay = sameBand.Count >= required;
        var baseline = timeOfDay ? sameBand : previous;

        var worst = baseline.Max(p => p.Length);
        if (heldFor <= worst * options.StuckMultiplier) return null;

        var typical = TimeSpan.FromSeconds(Median([.. baseline.Select(p => p.Length.TotalSeconds)]));
        var suggestion = RoundUp(Max(heldFor > worst * 4 ? worst * 2 : heldFor, options.MinimumStuckDuration));
        var when = timeOfDay ? $" at this time of day ({BandLabel(band)} UTC)" : "";

        return new Anomaly
        {
            DedupKey = $"stuck:{entity.EntityId}",
            EntityId = entity.EntityId,
            Kind = AnomalyKind.StuckState,
            DetectedUtc = nowUtc,
            Summary =
                $"{Label(entity)} has been '{entity.State}' for {Ha.Duration(heldFor)}. " +
                $"It is normally '{entity.State}' for about {Ha.Duration(typical)}{when} and never longer than " +
                $"{Ha.Duration(worst)} across {baseline.Count} previous periods.",
            SuggestedRequest =
                $"Notify me when {entity.EntityId} stays '{entity.State}' for more than {Ha.Duration(suggestion)}.",
            EvidenceJson = Evidence(new Dictionary<string, object?>
            {
                ["state"] = entity.State,
                ["held_for_seconds"] = Math.Round(heldFor.TotalSeconds),
                ["typical_seconds"] = Math.Round(typical.TotalSeconds),
                ["longest_previous_seconds"] = Math.Round(worst.TotalSeconds),
                ["previous_periods"] = baseline.Count,
                ["baseline"] = timeOfDay ? "time_of_day" : "all",
                ["band_utc"] = BandLabel(band),
            }),
        };
    }

    /// <summary>An automation HearthSense created whose entities have since been renamed or removed.</summary>
    public static Anomaly? DetectMissingEntities(Proposal proposal, IReadOnlySet<string> knownEntityIds, DateTimeOffset nowUtc)
    {
        if (proposal.Status != ProposalStatus.Created || proposal.Entities.Count == 0) return null;

        var missing = proposal.Entities
            .Where(id => !knownEntityIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0) return null;

        var name = string.IsNullOrWhiteSpace(proposal.Alias) ? $"proposal {proposal.Id}" : $"'{proposal.Alias}'";

        return new Anomaly
        {
            DedupKey = $"missing:{proposal.Id}",
            EntityId = missing[0],
            Kind = AnomalyKind.MissingEntity,
            DetectedUtc = nowUtc,
            Summary =
                $"Automation {name} references {missing.Count} entity id(s) that no longer exist in Home Assistant: " +
                $"{string.Join(", ", missing)}. It will not work as intended until they return or it is recreated.",
            // Promoting re-drafts the original sentence against the entities that exist now.
            SuggestedRequest = proposal.Request,
            EvidenceJson = Evidence(new Dictionary<string, object?>
            {
                ["proposal_id"] = proposal.Id,
                ["ha_automation_id"] = proposal.HaAutomationId,
                ["missing"] = missing,
                ["referenced"] = proposal.Entities.Count,
            }),
        };
    }

    /// <summary>Hours per time-of-day band; six bands a day is coarse enough to fill from two weeks of history.</summary>
    private const int BandHours = 4;

    private static int BandOf(DateTimeOffset time) => time.UtcDateTime.Hour / BandHours;

    private static string BandLabel(int band) => $"{band * BandHours:00}:00-{(band + 1) * BandHours:00}:00";

    /// <summary>A numeric reading far outside its own recent distribution, measured robustly.</summary>
    public static Anomaly? DetectNumericOutlier(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        if (entity.Numeric is not { } current) return null;

        var values = history.Where(s => s.Numeric is not null).Select(s => s.Numeric!.Value).ToArray();
        if (values.Length < options.MinimumSamples) return null;

        var median = Median(values);
        var spread = MedianAbsoluteDeviation(values, median) * MadScale;

        // A perfectly flat history gives no scale to judge against; fall back to the standard deviation
        // and stay silent if that is flat too, rather than reporting an infinite score.
        if (spread <= double.Epsilon) spread = StandardDeviation(values);
        if (spread <= double.Epsilon) return null;

        var z = Math.Abs(current - median) / spread;
        if (!double.IsFinite(z) || z < options.OutlierThreshold) return null;

        var above = current > median;
        var limit = Round(above ? median + (3 * spread) : median - (3 * spread));
        var unit = string.IsNullOrWhiteSpace(entity.Unit) ? "" : " " + entity.Unit;

        return new Anomaly
        {
            DedupKey = $"outlier:{entity.EntityId}",
            EntityId = entity.EntityId,
            Kind = AnomalyKind.NumericOutlier,
            DetectedUtc = nowUtc,
            Summary =
                $"{Label(entity)} is {Ha.Number(current)}{unit}, which is well outside its normal range. " +
                $"It usually sits near {Ha.Number(median)}{unit} (robust z-score {Ha.Number(z)} over {values.Length} readings).",
            SuggestedRequest =
                $"Notify me when {entity.EntityId} goes {(above ? "above" : "below")} {Ha.Number(limit)}.",
            EvidenceJson = Evidence(new Dictionary<string, object?>
            {
                ["current"] = Round(current),
                ["median"] = Round(median),
                ["spread"] = Round(spread),
                ["robust_z"] = Round(z),
                ["samples"] = values.Length,
                ["suggested_threshold"] = limit,
            }),
        };
    }

    /// <summary>An entity that used to report reliably and has now gone quiet.</summary>
    public static Anomaly? DetectUnavailable(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        if (!entity.IsUnavailable) return null;

        var quietFor = nowUtc - entity.LastChanged;
        if (quietFor < options.MinimumUnavailableDuration) return null;
        if (history.Count < options.MinimumSamples) return null;

        var healthy = history.Count(s => !Ha.IsUnavailable(s.State));
        var reliability = (double)healthy / history.Count;
        if (reliability < 0.9) return null;

        return new Anomaly
        {
            DedupKey = $"unavailable:{entity.EntityId}",
            EntityId = entity.EntityId,
            Kind = AnomalyKind.Unavailable,
            DetectedUtc = nowUtc,
            Summary =
                $"{Label(entity)} has been '{entity.State}' for {Ha.Duration(quietFor)}. " +
                $"It reported normally in {Math.Round(reliability * 100)}% of its last {history.Count} recorded changes.",
            SuggestedRequest =
                $"Notify me when {entity.EntityId} becomes unavailable for more than " +
                $"{Ha.Duration(RoundUp(options.MinimumUnavailableDuration))}.",
            EvidenceJson = Evidence(new Dictionary<string, object?>
            {
                ["state"] = entity.State,
                ["quiet_for_seconds"] = Math.Round(quietFor.TotalSeconds),
                ["reliability"] = Math.Round(reliability, 4),
                ["samples"] = history.Count,
            }),
        };
    }

    private static string Label(HaEntity entity) =>
        string.IsNullOrWhiteSpace(entity.FriendlyName) ? entity.EntityId : $"{entity.FriendlyName} ({entity.EntityId})";

    private static string Evidence(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>Snaps a duration up to something a person would actually type into an automation.</summary>
    public static TimeSpan RoundUp(TimeSpan span)
    {
        var minutes = Math.Max(1, Math.Ceiling(span.TotalMinutes));
        foreach (var step in FriendlyMinutes)
            if (minutes <= step)
                return TimeSpan.FromMinutes(step);

        return TimeSpan.FromMinutes(FriendlyMinutes[^1]);
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0) return 0;

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    public static double MedianAbsoluteDeviation(double[] values, double median) =>
        values.Length == 0 ? 0 : Median([.. values.Select(v => Math.Abs(v - median))]);

    public static double StandardDeviation(double[] values)
    {
        if (values.Length < 2) return 0;

        var mean = values.Average();
        var sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Length - 1));
    }

    /// <summary>Formats a number the same way everywhere, independent of the host locale.</summary>
    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
