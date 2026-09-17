using System.Globalization;
using System.Text.Json;

namespace Housekeeper.Core;

/// <summary>
/// Three detectors over a compact state history. Each is a pure function and each produces a plain-English
/// <see cref="Anomaly.SuggestedRequest"/>, so promoting a finding into an automation reuses the normal
/// drafting path rather than a second code path.
/// </summary>
public static class AnomalyDetection
{
    /// <summary>Consistency factor making a median absolute deviation comparable to a standard deviation.</summary>
    private const double MadScale = 1.4826;

    /// <summary>
    /// Half the smallest difference a reading is ever shown at, and so the smallest spread worth dividing by.
    /// Readings are written to two decimal places; anything finer is below the resolution of the conversation.
    /// </summary>
    private const double DisplayQuantum = 0.005;

    /// <summary>
    /// How long an entity that barely changes must have been on record before its silence is a finding.
    /// A sample is only stored when an entity changes, so a steady one accumulates almost none.
    /// </summary>
    private static readonly TimeSpan SteadyObservation = TimeSpan.FromDays(1);

    /// <summary>How much of an entity's recorded history must be healthy before its silence means anything.</summary>
    private const double MinimumReliability = 0.9;

    private static readonly double[] FriendlyMinutes =
        [1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440];

    public static IReadOnlyList<Anomaly> Detect(
        HaEntity entity,
        EntityHistory history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        List<Anomaly> found = [];

        Add(found, DetectUnavailable(entity, history.Recent, options, nowUtc));
        Add(found, DetectStuckState(entity, history.Recent, options, nowUtc));
        Add(found, DetectNumericOutlier(entity, history, options, nowUtc));

        return found;

        static void Add(List<Anomaly> into, Anomaly? anomaly)
        {
            if (anomaly is not null) into.Add(anomaly);
        }
    }

    /// <summary>One list of samples used for every detector, for callers with nothing better to hand.</summary>
    public static IReadOnlyList<Anomaly> Detect(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc) =>
        Detect(entity, EntityHistory.Of(history), options, nowUtc);

    /// <summary>
    /// The "freezer door left open" case: the entity is holding a state far longer than it ever has before.
    /// </summary>
    public static Anomaly? DetectStuckState(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        if (entity.IsUnavailable || entity.Numeric is not null || IsResting(entity)) return null;

        // A device setting is not something the house did. "Fridge Plug Auto-off enabled has been off for 50
        // minutes" is a checkbox nobody has ever ticked, reported as though the fridge were misbehaving.
        if (Baselines.DiagnosticReason(entity) is not null) return null;

        var heldFor = nowUtc - entity.LastChanged;
        if (heldFor < options.MinimumStuckDuration) return null;

        var previous = CompletedPeriods(entity, history);
        var required = RequiredPeriods(options);
        if (previous.Count < required) return null;

        // And the stretches have to reach back, not merely add up. A count alone says "four times" without
        // saying over what — four in one evening is an evening, and nothing seen in an evening knows what
        // this entity does at three in the morning. This is the same bar the numeric detector applies to a
        // baseline, for exactly the same reason.
        if (Witnessed(previous) < options.MinimumBaselineSpan) return null;

        // A door that is open for an hour at dinner time and for seconds at breakfast has two normals, and one
        // that is open all Saturday morning has a third. When enough periods began in the same part of the day
        // -- and, once the history reaches back three weeks, the same part of the week -- judge against those
        // rather than the whole history.
        var band = BandOf(entity.LastChanged);
        var weekend = IsWeekend(entity.LastChanged);
        var sameBand = previous.Where(p => BandOf(p.StartedUtc) == band).ToList();
        var sameWeekPart = Witnessed(previous) >= WeeklyBaselineSpan
            ? sameBand.Where(p => IsWeekend(p.StartedUtc) == weekend).ToList()
            : [];

        var (baseline, kind) =
            sameWeekPart.Count >= required ? (sameWeekPart, BaselineKind.TimeOfWeek)
            : sameBand.Count >= required ? (sameBand, BaselineKind.TimeOfDay)
            : (previous, BaselineKind.All);

        var worst = baseline.Max(p => p.Length);
        if (heldFor <= worst * options.StuckMultiplier) return null;

        var typical = TimeSpan.FromSeconds(Median([.. baseline.Select(p => p.Length.TotalSeconds)]));
        var suggestion = RoundUp(Max(heldFor > worst * 4 ? worst * 2 : heldFor, options.MinimumStuckDuration));
        var when = When(kind, band, weekend);

        return new Anomaly
        {
            DedupKey = $"stuck:{entity.EntityId}",
            EntityId = entity.EntityId,
            Kind = AnomalyKind.StuckState,
            DetectedUtc = nowUtc,
            Severity = Times(heldFor, worst * options.StuckMultiplier),
            Summary =
                $"Has been '{entity.State}' for {Ha.Duration(heldFor)}. " +
                $"Usually '{entity.State}' for about {Ha.Duration(typical)}{when}. " +
                $"The longest before now was {Ha.Duration(worst)}, across {baseline.Count} earlier stretches " +
                $"seen over {Ha.Duration(Witnessed(baseline))}.",
            SuggestedRequest =
                $"Notify me when {entity.EntityId} stays '{entity.State}' for more than {Ha.Duration(suggestion)}.",
            EvidenceJson = Evidence(entity, new Dictionary<string, object?>
            {
                ["state"] = entity.State,
                ["held_for_seconds"] = Math.Round(heldFor.TotalSeconds),
                ["typical_seconds"] = Math.Round(typical.TotalSeconds),
                ["longest_previous_seconds"] = Math.Round(worst.TotalSeconds),
                ["previous_periods"] = baseline.Count,
                ["witnessed_seconds"] = Math.Round(Witnessed(baseline).TotalSeconds),
                ["baseline"] = Label(kind),
                ["band_utc"] = BandLabel(band),
                ["week_part"] = weekend ? "weekend" : "weekday",
            }),
        };
    }

    /// <summary>Which slice of the history a finding was judged against.</summary>
    private enum BaselineKind { All, TimeOfDay, TimeOfWeek }

    /// <summary>
    /// How far back the history must reach before the same part of the week is a baseline of its own.
    /// Three weeks is three of each weekday, which is the least that can tell a habit from a coincidence.
    /// </summary>
    private static readonly TimeSpan WeeklyBaselineSpan = TimeSpan.FromDays(21);

    /// <summary>
    /// Whether a moment fell on a Saturday or Sunday where the house is, which is the process's own time
    /// zone. Bands are kept in UTC because the dashboard converts them; a day of the week cannot be, since
    /// a Friday night in the Americas is already Saturday in UTC.
    /// </summary>
    internal static bool IsWeekend(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static string Label(BaselineKind kind) => kind switch
    {
        BaselineKind.TimeOfWeek => "time_of_week",
        BaselineKind.TimeOfDay => "time_of_day",
        _ => "all",
    };

    private static string When(BaselineKind kind, int band, bool weekend) => kind switch
    {
        BaselineKind.TimeOfWeek => $" at this time of day on {(weekend ? "weekends" : "weekdays")} ({BandLabel(band)} UTC)",
        BaselineKind.TimeOfDay => $" at this time of day ({BandLabel(band)} UTC)",
        _ => "",
    };

    /// <summary>
    /// The kinds of finding this entity's state and history are enough to declare OVER.
    ///
    /// Deliberately a different question from <see cref="CanJudge"/>. A detector goes quiet for two very
    /// different reasons — the condition ended, or it no longer has the history to have an opinion — and the
    /// scanner closes findings on silence, so it needs to know which. One shared "watched long enough" flag
    /// conflated them, and it erred in the direction that loses information: a sensor that is still dead, or
    /// a reading that is still abnormal, stops producing samples by definition, so its history ages out, its
    /// detector falls silent, and its finding was closed and shown to the user as "back to normal".
    ///
    /// Answered per kind because the three detectors want genuinely different things. Being able to say a
    /// door is no longer stuck tells you nothing about whether that entity's readings can be judged.
    /// </summary>
    public static IReadOnlySet<AnomalyKind> Resolvable(
        HaEntity entity,
        EntityHistory history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        HashSet<AnomalyKind> kinds = [];

        // DetectStuckState: not unavailable, not numeric, not sitting where it always sits, not a setting,
        // and enough completed stretches in the state it is holding now.
        if (!entity.IsUnavailable &&
            entity.Numeric is null &&
            !IsResting(entity) &&
            Baselines.DiagnosticReason(entity) is null &&
            CompletedPeriods(entity, history.Recent).Count >= RequiredPeriods(options))
            kinds.Add(AnomalyKind.StuckState);

        // DetectNumericOutlier: a number, with a baseline of EARLIER readings that is both long enough and
        // wide enough. Counting the reading itself said a verdict was reachable one sample before it really
        // was; counting only rows said it was reachable after four hours of a chatty sensor, which is how
        // "every watched entity has enough history" came to be claimed of a house on its first day.
        if (entity.Numeric is not null &&
            !entity.IsCumulative &&
            Baselines.DiagnosticReason(entity) is null &&
            Judgeable(NumericBaseline(entity, history.Numeric), options))
            kinds.Add(AnomalyKind.NumericOutlier);

        // DetectUnavailable: it is reporting again, and was watched long enough for that to mean something.
        // While the entity is STILL unavailable this stays false whatever the history looks like -- the
        // condition has plainly not passed, so nothing about it may be closed as resolved.
        if (!entity.IsUnavailable && WatchedLongEnough(Earlier(entity, history.Recent), options, nowUtc))
            kinds.Add(AnomalyKind.Unavailable);

        return kinds;
    }

    /// <inheritdoc cref="Resolvable(HaEntity, EntityHistory, ScanOptions, DateTimeOffset)"/>
    public static IReadOnlySet<AnomalyKind> Resolvable(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc) =>
        Resolvable(entity, EntityHistory.Of(history), options, nowUtc);

    /// <summary>
    /// Whether a set of readings can be a baseline: enough of them, spread over enough time.
    ///
    /// The second half is the one that was missing. Samples are stored only when an entity changes and the
    /// scan reads a bounded number of the newest ones, so a count on its own measures how much a sensor
    /// fidgets — 250 readings from a power meter polled every minute is four hours, not a fortnight, and
    /// nothing seen in four hours knows that nights are colder or that the dishwasher runs after dinner.
    /// </summary>
    private static bool Judgeable(IReadOnlyList<StateSample> samples, ScanOptions options) =>
        samples.Count >= options.MinimumNumericSamples &&
        Baselines.Span(samples) >= options.MinimumBaselineSpan;

    /// <summary>
    /// Whether any detector could reach a verdict about this entity at all. This is what "ready to judge"
    /// counts on the dashboard, and it is a wider question than <see cref="Resolvable"/>: an entity that is
    /// unavailable right now cannot have that finding CLOSED, but it is very much something the unavailable
    /// detector has an opinion about — reporting it is the entire point.
    /// </summary>
    public static bool CanJudge(
        HaEntity entity,
        EntityHistory history,
        ScanOptions options,
        DateTimeOffset nowUtc) =>
        Resolvable(entity, history, options, nowUtc).Count > 0 ||
        (entity.IsUnavailable && WatchedLongEnough(Earlier(entity, history.Recent), options, nowUtc));

    /// <inheritdoc cref="CanJudge(HaEntity, EntityHistory, ScanOptions, DateTimeOffset)"/>
    public static bool CanJudge(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc) =>
        CanJudge(entity, EntityHistory.Of(history), options, nowUtc);

    /// <summary>Everything recorded before the reading being judged, which is the only fair baseline.</summary>
    private static List<StateSample> Earlier(HaEntity entity, IReadOnlyList<StateSample> history) =>
        [.. history.Where(sample => sample.ChangedUtc < entity.LastChanged)];

    /// <summary>How much of this history reported normally.</summary>
    private static double Reliability(IReadOnlyList<StateSample> history) =>
        history.Count == 0 ? 0 : (double)history.Count(s => !Ha.IsUnavailable(s.State)) / history.Count;

    /// <summary>
    /// Whether this entity has been on record long enough for what it does next to mean something.
    ///
    /// A sample is only stored when an entity changes, so counting samples measures how much an entity
    /// fidgets rather than how long it has been watched. A thermostat that has read the same number for a
    /// fortnight -- exactly the kind whose going quiet matters most -- records almost none, and a bar made
    /// only of sample counts meant it could never be reported at all. Either enough recorded changes, or a
    /// long enough stretch of history, is evidence that it used to report.
    /// </summary>
    private static bool WatchedLongEnough(IReadOnlyList<StateSample> history, ScanOptions options, DateTimeOffset nowUtc)
    {
        if (history.Count == 0) return false;
        if (history.Count >= options.MinimumSamples) return true;

        return nowUtc - history.Min(sample => sample.ChangedUtc) >= SteadyObservation;
    }

    /// <summary>
    /// The numeric readings this entity is judged against: every earlier one, and not the reading itself.
    ///
    /// The scanner stores the current reading before the detectors run, so the history handed to a detector
    /// already contains the very sample being judged. Left in, it inflates the scale it is measured against:
    /// for a flat baseline plus one jump the score is capped at the square root of the sample count, which at
    /// the default twelve samples is 3.46 and can never clear a threshold of 4. A doubled boiler pressure
    /// scored 3.5 and was never mentioned.
    /// </summary>
    private static StateSample[] NumericBaseline(HaEntity entity, IReadOnlyList<StateSample> history) =>
        [.. history.Where(sample => sample.Numeric is not null && sample.ChangedUtc < entity.LastChanged)];

    /// <summary>Completed stretches the entity previously spent in the state it is holding now.</summary>
    private static List<(DateTimeOffset StartedUtc, TimeSpan Length)> CompletedPeriods(
        HaEntity entity,
        IReadOnlyList<StateSample> history)
    {
        List<(DateTimeOffset StartedUtc, TimeSpan Length)> periods = [];

        for (var i = 0; i < history.Count - 1; i++)
        {
            if (!string.Equals(history[i].State, entity.State, StringComparison.Ordinal)) continue;

            // The next sample has to be a different state. Samples are stored by a poller, so a whole
            // transition can happen between two polls and leave two neighbours carrying the same state --
            // and the gap between those is not one long stretch, it is two stretches with the middle
            // unobserved. Counting it inflated "the longest before now was ...", which is the bar every
            // stuck-state finding is measured against.
            if (string.Equals(history[i + 1].State, history[i].State, StringComparison.Ordinal)) continue;

            var span = history[i + 1].ChangedUtc - history[i].ChangedUtc;
            if (span > TimeSpan.Zero) periods.Add((history[i].ChangedUtc, span));
        }

        return periods;
    }

    private static int RequiredPeriods(ScanOptions options) => Math.Max(4, options.MinimumSamples / 3);

    /// <summary>
    /// How long these stretches were observed over — from the start of the earliest to the end of the
    /// latest. The honest denominator behind "across 4 earlier stretches", which on its own reads as
    /// settled fact whether it was gathered over a fortnight or over one evening.
    /// </summary>
    private static TimeSpan Witnessed(IReadOnlyList<(DateTimeOffset StartedUtc, TimeSpan Length)> periods) =>
        periods.Count == 0
            ? TimeSpan.Zero
            : periods.Max(p => p.StartedUtc + p.Length) - periods.Min(p => p.StartedUtc);

    /// <summary>An automation Housekeeper created whose entities have since been renamed or removed.</summary>
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
            // An automation of the user's own that cannot work is the one finding here that is certainly
            // actionable, so it outranks anything a detector merely thinks is unusual.
            Severity = 10 + missing.Count,
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

    /// <summary>
    /// Binary sensor device classes that SIT at 'on', so it is their off side that reads as active.
    ///
    /// The question is which side the sensor spends nearly all its time at, not which side is healthy -- the
    /// two came apart here more than once. A connection is up and a socket is powered essentially always, so
    /// no length of that is worth a word. 'running' and 'battery_charging' read as healthy but are the
    /// opposite of steady: a dishwasher, a pump, a printer, a charging phone are idle almost all the time, so
    /// listing them swapped which side of an appliance was judged and silenced the one case that matters --
    /// something left running far longer than it ever runs. 'light' is out for the same reason: 'on' there
    /// means light was detected, which is neither healthy nor steady.
    /// </summary>
    private static readonly HashSet<string> RestingWhenOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "connectivity", "plug", "power",
    };

    /// <summary>
    /// Where an entity of this kind sits when nothing is happening. A light is off, a lock is locked, a
    /// vacuum is docked, an automation is enabled.
    ///
    /// Only binary sensors were listed here, and every other domain was judged on both sides — so a light
    /// being OFF was measured the same way a freezer door being OPEN is. A real house reported its backyard
    /// light, twice, for the crime of being off for two hours at night: "usually off for about 13 minutes,
    /// the longest before now was 24 minutes". Both true, and both an artefact of having watched a single
    /// evening. No length of a light being off is news; a light left ON all day still is, which is why this
    /// names the resting side rather than switching the entity off altogether.
    /// </summary>
    private static readonly Dictionary<string, string[]> RestingStates = new(StringComparer.Ordinal)
    {
        ["light"] = ["off"],
        ["switch"] = ["off"],
        ["fan"] = ["off"],
        ["siren"] = ["off"],
        ["humidifier"] = ["off"],
        ["climate"] = ["off"],
        ["water_heater"] = ["off"],
        ["input_boolean"] = ["off"],
        ["media_player"] = ["off", "idle", "standby"],
        ["cover"] = ["closed"],
        ["lock"] = ["locked"],
        ["vacuum"] = ["docked"],
        ["alarm_control_panel"] = ["disarmed"],

        // An automation that is enabled is an automation doing its job. The off side is the one worth a
        // word: something turned off "for a minute" three weeks ago and never turned back on.
        ["automation"] = ["on"],
    };

    /// <summary>
    /// True while an entity sits at rest: motion clear, door closed, link up, light off. Sitting there is
    /// what these entities do nearly all the time, so no length of it is a finding. The active side still is.
    /// </summary>
    private static bool IsResting(HaEntity entity)
    {
        if (entity.Domain == "binary_sensor")
            return string.Equals(
                entity.State,
                entity.DeviceClass is { } deviceClass && RestingWhenOn.Contains(deviceClass) ? "on" : "off",
                StringComparison.OrdinalIgnoreCase);

        return RestingStates.TryGetValue(entity.Domain, out var resting) &&
               resting.Contains(entity.State, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Hours per time-of-day band; six bands a day is coarse enough to fill from two weeks of history.</summary>
    private const int BandHours = 4;

    private static int BandOf(DateTimeOffset time) => time.UtcDateTime.Hour / BandHours;

    private static string BandLabel(int band) => $"{band * BandHours:00}:00-{(band + 1) * BandHours:00}:00";

    /// <summary>
    /// A numeric reading far outside its own distribution, measured robustly — and, before that is even
    /// asked, a reading whose distribution is the kind of thing a distribution can be asked about.
    ///
    /// The z-score is the last gate rather than the only one. On its own it reported a house's energy meters,
    /// its disk usage, its Zigbee link quality, its battery voltages and every plug that had merely been
    /// switched on: nineteen findings in a day, of which two were real. The gates ahead of it, in order, are
    /// what this detector mostly is now.
    /// </summary>
    public static Anomaly? DetectNumericOutlier(
        HaEntity entity,
        EntityHistory history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        if (entity.Numeric is not { } current) return null;

        // A running total's newest reading is its largest by construction, so it clears any threshold
        // eventually and then never stops clearing it.
        if (entity.IsCumulative) return null;

        // Radio strength, link quality, battery level, device settings: real measurements, no verdicts.
        if (Baselines.DiagnosticReason(entity) is not null) return null;

        var samples = NumericBaseline(entity, history.Numeric);
        if (!Judgeable(samples, options)) return null;

        // A sensor with a daily shape has more than one normal, and comparing across them is how an outdoor
        // thermometer gets reported for being cold at night. Used only when the same part of the day is
        // itself a full baseline, which needs more than one day of history and so arrives on its own. The
        // same again for the part of the week, once three weeks of readings exist to draw it from.
        var band = BandOf(entity.LastChanged);
        var weekend = IsWeekend(entity.LastChanged);
        var sameBand = samples.Where(sample => BandOf(sample.ChangedUtc) == band).ToArray();
        var sameWeekPart = Baselines.Span(samples) >= WeeklyBaselineSpan
            ? sameBand.Where(sample => IsWeekend(sample.ChangedUtc) == weekend).ToArray()
            : [];

        var (baseline, kind) =
            Judgeable(sameWeekPart, options) ? (sameWeekPart, BaselineKind.TimeOfWeek)
            : Judgeable(sameBand, options) ? (sameBand, BaselineKind.TimeOfDay)
            : (samples, BaselineKind.All);

        double[] values = [.. baseline.Select(sample => sample.Numeric!.Value)];

        if (Baselines.IsRatchet(values)) return null;

        var median = Median(values);
        var spread = MedianAbsoluteDeviation(values, median) * MadScale;

        // More than half the readings identical leaves the median absolute deviation at zero, which says
        // nothing about the rest; the standard deviation of the same baseline does.
        if (spread <= double.Epsilon) spread = StandardDeviation(values);

        // And a baseline that never moved at all still has to be divisible. Nothing smaller than the
        // precision the number is shown at counts as spread, which both keeps a genuinely constant sensor
        // judgeable and stops a move too small to see from scoring hugely against a near-zero scale.
        spread = Math.Max(spread, DisplayQuantum);

        var move = Math.Abs(current - median);
        var z = move / spread;
        if (!double.IsFinite(z) || z < options.OutlierThreshold) return null;

        // A robust z-score can be large while the move itself is invisible — a disk that idles between
        // 0.00 and 0.02 MB/s scores highly on nothing at all. If the reading and its usual value are the
        // same number once written down, the finding would read "0 MB/s, well outside its normal range,
        // usually near 0 MB/s", which is not something to show anyone.
        if (Ha.Number(current) == Ha.Number(median)) return null;

        // And a move that is real but too small to care about is the same problem one step up: 157.3 to
        // 158.5 GiB of disk, 3019.5 to 3009 mV of battery. Both cleared four sigma on a baseline that had
        // simply been very still.
        //
        // Deliberately stated here even though Threshold below enforces the same bound structurally -- it
        // will not place a line nearer to normal than this, so it can never find room for one when the move
        // itself is smaller. Keeping the rule where a reader looks for it is worth a redundant comparison,
        // and it settles the common case before sorting the baseline for a quantile.
        var smallestWorthMentioning = Baselines.MinimumMove(entity, median, current, options.MinimumEffect);
        if (move < smallestWorthMentioning) return null;

        // Somewhere this entity already goes. A plug that is off most of the time, an HRV on a lower fan
        // speed, a disk that is idle: the median sits in the busiest mode and every other mode reads as an
        // excursion for ever.
        if (Baselines.IsKnownMode(values, current, spread)) return null;

        var above = current > median;
        if (Threshold(values, median, spread, current, above, smallestWorthMentioning) is not { } limit) return null;

        var unit = string.IsNullOrWhiteSpace(entity.Unit) ? "" : " " + entity.Unit;
        var when = When(kind, band, weekend);

        return new Anomaly
        {
            DedupKey = $"outlier:{entity.EntityId}",
            EntityId = entity.EntityId,
            Kind = AnomalyKind.NumericOutlier,
            DetectedUtc = nowUtc,
            Severity = Times(z, options.OutlierThreshold),
            Summary =
                $"Reads {Ha.Number(current)}{unit}, well outside its normal range. " +
                $"Usually near {Ha.Number(median)}{unit}{when}, judged over {values.Length} readings " +
                $"spanning {Ha.Duration(Baselines.Span(baseline))}.",
            SuggestedRequest =
                $"Notify me when {entity.EntityId} goes {(above ? "above" : "below")} {Ha.Number(limit)}.",
            EvidenceJson = Evidence(entity, new Dictionary<string, object?>
            {
                ["current"] = Round(current),
                ["unit"] = entity.Unit,
                ["median"] = Round(median),
                ["spread"] = Round(spread),
                ["robust_z"] = Round(z),
                ["samples"] = values.Length,
                ["baseline_seconds"] = Math.Round(Baselines.Span(baseline).TotalSeconds),
                ["baseline"] = Label(kind),
                ["band_utc"] = BandLabel(band),
                ["week_part"] = weekend ? "weekend" : "weekday",
                ["suggested_threshold"] = limit,
            }),
        };
    }

    /// <summary>
    /// Where to put the line, or null when there is nowhere sensible to put it.
    ///
    /// This is what the suggested automation becomes, so it is the part of a finding the user actually keeps.
    /// It used to be derived from the excursion — three spreads out, but never nearer than halfway to the
    /// reading that had just been seen — which guaranteed the line sat between normal and now, and so was
    /// already crossed at the moment it was proposed. Every numeric suggestion a real house produced had that
    /// shape: "notify me when the voltage goes below 3012.83" from a sensor reading 3009.
    ///
    /// Anchored to the baseline instead, and made to clear three separate things: three spreads, nearly
    /// everything the entity has ever actually done, and the smallest move that would have been worth a
    /// finding in the first place. The last is what keeps a very still sensor sensible — on a baseline that
    /// has read exactly the same number all week the spread collapses to the display precision, and a line
    /// placed three of those out sits a hundredth above normal and fires on the first flicker.
    ///
    /// If all that leaves no room before the current reading, the honest answer is that this excursion is
    /// not clear of ordinary behaviour and there is no automation worth offering.
    /// </summary>
    private static double? Threshold(
        double[] values,
        double median,
        double spread,
        double current,
        bool above,
        double smallestWorthMentioning)
    {
        // A high quantile rather than the outright extreme, so one earlier spike in a fortnight does not
        // veto a threshold, but a level the entity reaches routinely does.
        var seen = Quantile(values, above ? 0.98 : 0.02);
        var edge = above
            ? Math.Max(Math.Max(median + 3 * spread, seen), median + smallestWorthMentioning)
            : Math.Min(Math.Min(median - 3 * spread, seen), median - smallestWorthMentioning);

        // Clear of the baseline by a hair, so a reading equal to the highest ever seen is not already over.
        var limit = Round(above ? edge + DisplayQuantum : edge - DisplayQuantum);

        if (!double.IsFinite(limit)) return null;
        if (above ? limit >= current : limit <= current) return null;

        // Rounding for display must not push the line back inside the range it was placed outside of.
        return Ha.Number(limit) == Ha.Number(current) ? null : limit;
    }

    /// <summary>Linear-interpolated quantile, so a baseline's near-extremes do not depend on one row.</summary>
    public static double Quantile(double[] values, double q)
    {
        if (values.Length == 0) return 0;
        if (values.Length == 1) return values[0];

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var position = Math.Clamp(q, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    /// <summary>
    /// The most severe a finding can be scored by a detector. Missing-entity findings sit above this on
    /// purpose: an automation of the user's own that cannot work is always the first thing to look at.
    /// </summary>
    public const double MostSevere = 8;

    /// <summary>
    /// How far past its own bar something is, on a scale that saturates: 1 at the bar, one more for every
    /// doubling, and never above <see cref="MostSevere"/>.
    ///
    /// A plain ratio grows without limit, and two of the three bars are durations that grow on their own.
    /// A sensor that has been unavailable for a month scored sixty times over, and six virtual network
    /// interfaces that vanished together sat above a boiler at twice its usual pressure for as long as they
    /// stayed gone. Doubling is the unit that matters to a reader — twice as long, twice as far — and after
    /// a few of them the difference between "very" and "extremely" is not information anyone acts on.
    /// </summary>
    private static double Times(double value, double bar)
    {
        if (bar <= 0 || !double.IsFinite(value / bar)) return 1;

        var ratio = Math.Max(1, value / bar);
        return Math.Min(MostSevere, Round(1 + Math.Log2(ratio)));
    }

    private static double Times(TimeSpan value, TimeSpan bar) =>
        bar > TimeSpan.Zero ? Times(value.TotalSeconds, bar.TotalSeconds) : 1;

    /// <inheritdoc cref="DetectNumericOutlier(HaEntity, EntityHistory, ScanOptions, DateTimeOffset)"/>
    public static Anomaly? DetectNumericOutlier(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc) =>
        DetectNumericOutlier(entity, EntityHistory.Of(history), options, nowUtc);

    /// <summary>An entity that used to report reliably and has now gone quiet.</summary>
    public static Anomaly? DetectUnavailable(
        HaEntity entity,
        IReadOnlyList<StateSample> history,
        ScanOptions options,
        DateTimeOffset nowUtc)
    {
        if (!entity.IsUnavailable) return null;

        // A button or an update entity has no state that can be wrong, so its going quiet is not news. The
        // diagnostic entities are left in deliberately: on a device whose only entities are a battery level
        // and a link quality, they are the only way to notice it has died.
        if (Baselines.IsInert(entity)) return null;

        var quietFor = nowUtc - entity.LastChanged;
        if (quietFor < options.MinimumUnavailableDuration) return null;

        // Judged against how it behaved BEFORE it went quiet. The scanner stores the current reading first,
        // so the outage itself was sitting in this history -- counted in the denominator and not the
        // numerator, which capped a spotless record at (n-1)/n and meant the detector could not speak at all
        // below ten recorded changes. That defeated the entire point of watching a steady entity, which by
        // definition has very few.
        var previous = Earlier(entity, history);
        if (previous.Count == 0) return null;
        if (!WatchedLongEnough(previous, options, nowUtc)) return null;

        var reliability = Reliability(previous);
        if (reliability < MinimumReliability) return null;

        var watchedFor = nowUtc - previous.Min(sample => sample.ChangedUtc);
        var changes = previous.Count == 1 ? "1 recorded change" : $"{previous.Count} recorded changes";

        return new Anomaly
        {
            DedupKey = $"unavailable:{entity.EntityId}",
            EntityId = entity.EntityId,
            Kind = AnomalyKind.Unavailable,
            DetectedUtc = nowUtc,
            Severity = Times(quietFor, options.MinimumUnavailableDuration),
            Summary =
                $"Has been '{entity.State}' for {Ha.Duration(quietFor)}. " +
                $"It reported normally in {Math.Round(reliability * 100)}% of {changes} over the previous {Ha.Duration(watchedFor)}.",
            SuggestedRequest =
                $"Notify me when {entity.EntityId} becomes unavailable for more than " +
                $"{Ha.Duration(RoundUp(options.MinimumUnavailableDuration))}.",
            EvidenceJson = Evidence(entity, new Dictionary<string, object?>
            {
                ["state"] = entity.State,
                ["quiet_for_seconds"] = Math.Round(quietFor.TotalSeconds),
                ["reliability"] = Math.Round(reliability, 4),
                ["samples"] = previous.Count,
                ["watched_for_seconds"] = Math.Round(watchedFor.TotalSeconds),
            }),
        };
    }

    /// <summary>The entity's own name and device ride along so the finding can be shown, and ignored, by them.</summary>
    private static string Evidence(HaEntity entity, Dictionary<string, object?> values)
    {
        values["entity_name"] = entity.FriendlyName;
        values["device"] = entity.DeviceName;
        values["area"] = entity.Area;
        return Evidence(values);
    }

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

        // Past the end of the table, round up to the next whole day. Returning the table's last entry rounded
        // anything longer than a day DOWN, which put the suggested threshold below the entity's ordinary
        // behaviour and produced an automation that fires constantly.
        return TimeSpan.FromDays(Math.Ceiling(span.TotalDays));
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
