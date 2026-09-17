using Microsoft.Extensions.Logging;

namespace Housekeeper.Core;

/// <param name="Observed">Entities the watch list selected.</param>
/// <param name="Visible">Entities Home Assistant reported at all, watched or not.</param>
/// <param name="Resolved">Open findings whose condition had passed by the time this scan looked.</param>
/// <param name="Judged">
/// Watched entities whose stored history is enough for some detector to reach a verdict. Counted here, from
/// the same history the detectors just read, so it can never disagree with what they were able to do.
/// </param>
/// <param name="Backfilled">Samples read from Home Assistant's recorder for entities seen for the first time.</param>
public sealed record ScanReport(
    int Observed,
    int NewSamples,
    int Raised,
    int Pruned,
    int Visible = 0,
    int Resolved = 0,
    int Judged = 0,
    int Backfilled = 0);

/// <summary>The outcome of the most recent scan, kept so the dashboard can say what happened and when.</summary>
public sealed record ScanState(DateTimeOffset FinishedUtc, TimeSpan Took, ScanReport? Report, string? Error);

/// <summary>
/// Polls Home Assistant, keeps a compact history of state changes, and lists what looks off.
/// It never notifies and never writes to Home Assistant — a finding is only ever a suggestion the
/// user can dismiss or promote into a real automation.
/// </summary>
public sealed class AnomalyScanner(
    IHomeAssistant homeAssistant,
    IStore store,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<AnomalyScanner> logger)
{
    /// <summary>
    /// How much contiguous history any one entity contributes to a scan.
    ///
    /// This feeds the detectors that measure stretches between one state and the next, which need adjacency
    /// rather than reach: the bar they compare against is a few dozen completed periods. Numeric baselines
    /// no longer come from here — they were the only reason this was ever large, and taking the newest rows
    /// was exactly the wrong shape for them.
    /// </summary>
    private const int RecentPerEntity = 120;

    /// <summary>
    /// How many readings an hour a numeric baseline keeps, and how many in total.
    ///
    /// Two an hour over four weeks is 1,344, so the cap only binds on a sensor that is busy every hour of
    /// every day, and a busy one still reaches back the three weeks the weekly baseline needs — against four
    /// hours before, which is what a flat count of the newest rows buys you from something that changes
    /// every minute.
    /// </summary>
    private const int NumericPerHour = 2;

    private const int NumericPerEntity = 1400;

    /// <summary>
    /// How long a newly created automation is given to appear in the state list before its absence is
    /// taken to mean it was deleted, rather than that Home Assistant has not registered it yet.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(10);

    /// <summary>How many open findings one scan re-examines for conditions that have since passed.</summary>
    private const int OpenFindingCeiling = 1000;

    /// <summary>
    /// How many first-seen entities one scan asks the recorder about, and how many per request.
    ///
    /// A large house is backfilled over a few scans rather than in one, so the first scan of a 3,000-entity
    /// install is not also its slowest. Fifty ids per request keeps the URL short and the reply a size Home
    /// Assistant answers quickly.
    /// </summary>
    private const int BackfillPerScan = 300;

    private const int BackfillPerRequest = 50;

    /// <summary>
    /// How far back an entity's stored history has to reach before the recorder is not worth asking. Three
    /// weeks is what the weekly baseline needs, and an install that has watched for less than that may well
    /// have a recorder that reaches further.
    /// </summary>
    private static readonly TimeSpan DeepEnough = TimeSpan.FromDays(21);

    /// <summary>
    /// Entities already asked about this process, so one with nothing more in the recorder is not asked
    /// again every scan. Per process on purpose: a restart costs one more question per entity whose history
    /// is still shallow, which is cheap, and a table for it is not.
    /// </summary>
    private readonly HashSet<string> _backfilled = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What the last scan did. Null until one has run.</summary>
    public ScanState? Last { get; private set; }

    /// <summary>
    /// The entities the last scan selected, published so the live feed stores exactly what the scan would.
    ///
    /// Two writers into one sample table have to agree on what is watched, or the feed quietly fills the
    /// database with entities the watch list excludes — including the cap that exists to stop a large
    /// install doing precisely that. Empty until a scan has run, so the feed stores nothing before then
    /// rather than guessing.
    /// </summary>
    public IReadOnlySet<string> Watching { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// When the scheduled scan is next due, published by whatever owns the timer rather than worked out
    /// from the last one.
    ///
    /// "Last finished plus the interval" was a different clock from the real schedule: the timer is anchored
    /// at startup and is not reset by a manual scan, and a changed interval only takes effect on the tick
    /// after. The dashboard renders this as a live countdown, so it has to be the truth. Null when scanning
    /// is off, or before the worker has started waiting.
    /// </summary>
    public DateTimeOffset? NextUtc { get; set; }

    /// <summary>
    /// The last history summary, and the moment it was taken.
    ///
    /// Counting distinct entities and rows across the whole retention window means visiting every sample --
    /// a fortnight of them, for hundreds of entities -- and the dashboard asks for it every thirty seconds,
    /// per open tab, for two decorative counters. The numbers only change when a scan writes samples, so one
    /// is kept and handed out until the next scan has run.
    /// </summary>
    public (HistorySummary Summary, DateTimeOffset TakenUtc, TimeSpan Window)? History { get; private set; }

    /// <summary>How long a held summary is served before it is worked out again, whatever the scanner is doing.</summary>
    private static readonly TimeSpan HistoryFreshFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The history summary, recomputed when the one held is stale: taken before the last scan, measured over
    /// a different retention window from the one now configured, or simply old.
    ///
    /// The age check is what makes it correct with scanning switched off. Inferring freshness from the last
    /// scan alone meant that with no scan ever run there was nothing to compare against, so the first answer
    /// was served for the life of the process — while the window it was measured over kept sliding forward.
    /// </summary>
    public async Task<HistorySummary> HistoryAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (History is { } held &&
            held.Window == window &&
            now - held.TakenUtc < HistoryFreshFor &&
            (Last is null || held.TakenUtc >= Last.FinishedUtc))
            return held.Summary;

        var summary = await store.GetHistorySummaryAsync(now - window, cancellationToken).ConfigureAwait(false);
        History = (summary, now, window);

        return summary;
    }

    /// <summary>Runs one scan. Single-flight, so a manual trigger cannot overlap the scheduled one.</summary>
    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = clock.GetTimestamp();
        try
        {
            var report = await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
            Last = new ScanState(clock.GetUtcNow(), clock.GetElapsedTime(started), report, null);
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed scan is worth showing too; a dashboard that only ever says "nothing found" hides it.
            Last = new ScanState(clock.GetUtcNow(), clock.GetElapsedTime(started), null, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScanReport> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var scan = settings.Current.Scan;
        var now = clock.GetUtcNow();

        var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var raised = 0;

        // Every condition still true this minute. Whatever is open and missing from it has passed.
        HashSet<string> standing = new(StringComparer.Ordinal);

        // Automations Housekeeper created whose entities have since gone. An empty entity list is a Home
        // Assistant hiccup rather than an empty house, so it must not read as "everything is missing".
        var automationsChecked = entities.Count > 0;
        if (automationsChecked)
            raised += await CheckCreatedAutomationsAsync(entities, scan, standing, now, cancellationToken).ConfigureAwait(false);

        var watched = EntityIndex.Filter(entities, scan);
        Watching = new HashSet<string>(watched.Select(entity => entity.EntityId), StringComparer.Ordinal);

        // What the user asked to be watched. An entity a concern names is judged more sharply, ranked
        // ahead, and checked against the concern's own rule when it has one. The first concern to name an
        // entity owns it, which keeps a card from being labelled with three overlapping worries.
        var concerns = await store.ListConcernsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, Concern> concerned = new(StringComparer.Ordinal);
        foreach (var concern in concerns)
            foreach (var id in concern.Entities)
                concerned.TryAdd(id, concern);

        var sharpened = Concerns.Sharpen(scan);

        var inserted = 0;

        // Read once, before anything is raised: what the user can already see. Grouping prefers to fold a
        // newcomer into a card that exists over opening another beside it, and the closing pass needs the
        // same list.
        var open = await store.ListAnomaliesAsync(AnomalyStatus.Open, OpenFindingCeiling, includeClosed: false, cancellationToken)
            .ConfigureAwait(false);
        var openKeys = new HashSet<string>(open.Select(finding => finding.DedupKey), StringComparer.Ordinal);

        // Findings folded into another card this scan, and which card. Their own rows, if open, are closed
        // below rather than left standing beside the card that now speaks for them.
        Dictionary<string, string> absorbed = new(StringComparer.Ordinal);

        // What this scan could actually reach a verdict about, per entity AND per kind of finding. A
        // detector staying silent because it has too little history is not the same as it saying the
        // condition has passed, and closing findings on the first is how a still-dead sensor quietly
        // disappears off the dashboard. The kind matters because the three detectors have genuinely
        // different requirements: being able to judge whether a door is stuck says nothing about whether
        // that entity's readings can be judged.
        HashSet<(string EntityId, AnomalyKind Kind)> resolvable = [];
        var judged = 0;

        var backfilled = 0;

        if (watched.Count > 0)
        {
            if (scan.BackfillFromRecorder)
                backfilled = await BackfillAsync(watched, now, now - scan.History, cancellationToken).ConfigureAwait(false);

            // Read after the backfill, so nothing it stored is counted again as new.
            var latest = await store.GetLatestSampleTimesAsync(cancellationToken).ConfigureAwait(false);

            inserted = await RecordAsync(watched, latest, now - scan.History, cancellationToken).ConfigureAwait(false);

            var recent = await store
                .GetSamplesAsync(now - scan.History, RecentPerEntity, cancellationToken)
                .ConfigureAwait(false);

            var numeric = await store
                .GetNumericHistoryAsync(now - scan.History, NumericPerHour, NumericPerEntity, cancellationToken)
                .ConfigureAwait(false);

            List<(HaEntity Entity, Anomaly Anomaly)> found = [];

            foreach (var entity in watched)
            {
                var history = new EntityHistory(
                    recent.TryGetValue(entity.EntityId, out var contiguous) ? contiguous : [],
                    numeric.TryGetValue(entity.EntityId, out var spread) ? spread : []);

                var concern = concerned.GetValueOrDefault(entity.EntityId);
                var bar = concern is null ? scan : sharpened;

                if (AnomalyDetection.CanJudge(entity, history, bar, now)) judged++;
                foreach (var kind in AnomalyDetection.Resolvable(entity, history, bar, now))
                    resolvable.Add((entity.EntityId, kind));

                foreach (var anomaly in AnomalyDetection.Detect(entity, history, bar, now))
                    found.Add((entity, concern is null ? anomaly : Concerns.Prioritise(anomaly, concern)));

                if (concern is not null && Concerns.Evaluate(concern, entity, history.Recent, now) is { } asked)
                {
                    standing.Add(asked.DedupKey);
                    if (await TryRaiseAsync(asked, scan, now, cancellationToken).ConfigureAwait(false)) raised++;
                }
            }

            var collapsed = Collapse(found, scan, openKeys);
            foreach (var pair in collapsed.Absorbed) absorbed[pair.Key] = pair.Value;

            foreach (var anomaly in collapsed.Kept)
            {
                standing.Add(anomaly.DedupKey);
                if (await TryRaiseAsync(anomaly, scan, now, cancellationToken).ConfigureAwait(false)) raised++;
            }
        }

        var resolved = await ResolveAsync(open, standing, resolvable, absorbed, watched, entities, concerns, now, cancellationToken)
            .ConfigureAwait(false);

        // Pruning happens whatever is being watched. Narrowing the watch list used to leave the samples of
        // everything dropped from it sitting in the database for good.
        var pruned = await store.PruneSamplesAsync(now - scan.History, cancellationToken).ConfigureAwait(false);

        var expired = await store
            .PruneProposalsAsync(now - settings.Current.Storage.KeepDecidedFor, cancellationToken)
            .ConfigureAwait(false);

        // Closed findings are kept past the re-detect window: delete a dismissal any sooner and the thing the
        // user silenced comes back as new.
        var forgotten = await store
            .PruneAnomaliesAsync(now - Longest(settings.Current.Storage.KeepDecidedFor, scan.RedetectAfter), cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Scan observed {Observed} entities, stored {NewSamples} new samples and {Backfilled} from the recorder, raised {Raised} anomalies, " +
            "closed {Resolved} that had passed, pruned {Pruned} rows, expired {Expired} proposals and {Forgotten} findings.",
            watched.Count, inserted, backfilled, raised, resolved, pruned, expired, forgotten);

        return new ScanReport(watched.Count, inserted, raised, pruned, entities.Count, resolved, judged, backfilled);
    }

    private static TimeSpan Longest(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>What grouping made of one scan's findings: the cards to raise, and the findings folded into them.</summary>
    /// <param name="Absorbed">Dedup key of each folded finding, and the entity id of the card that now stands for it.</param>
    public sealed record Collapsed(IReadOnlyList<Anomaly> Kept, IReadOnlyDictionary<string, string> Absorbed);

    /// <summary>
    /// How close together two entities have to go unavailable to be one outage rather than two. Integrations
    /// report their entities gone within the same poll or the same reconnect, so the real gap is seconds; a
    /// few minutes covers a device that drops its entities one at a time.
    /// </summary>
    private static readonly TimeSpan OutageWindow = TimeSpan.FromMinutes(5);

    /// <summary>How many folded siblings are named in a card's summary before the rest are counted.</summary>
    private const int NamedSiblings = 6;

    /// <inheritdoc cref="Collapse(IReadOnlyList{ValueTuple{HaEntity, Anomaly}}, ScanOptions, IReadOnlySet{string})"/>
    internal static IReadOnlyList<Anomaly> Collapse(
        IReadOnlyList<(HaEntity Entity, Anomaly Anomaly)> found,
        ScanOptions options) =>
        Collapse(found, options, new HashSet<string>(StringComparer.Ordinal)).Kept;

    /// <summary>
    /// One card per event, rather than one per entity.
    ///
    /// A smart plug publishes its power, its current and its energy, and a switched light is very often both
    /// a <c>light.</c> entity and a <c>switch.</c> entity, so a single event arrives as three or four
    /// findings that say the same thing in different units. A real house showed its office plug three times
    /// and its backyard light twice out of nineteen cards. The most severe one stands for the group and
    /// names the rest, which is also the more useful card: knowing the current moved with the power is how
    /// you tell a real load from a reporting glitch.
    ///
    /// Going unavailable is grouped a second way, by when it happened. A hub rebooting, a container
    /// restarting or a battery dying takes every entity it carries with it in the same moment, and those
    /// entities do not always share a device Home Assistant knows about: six virtual network interfaces on
    /// one host were six cards, each saying "unavailable for 30 hours". One outage is one card.
    ///
    /// Both groupings prefer a finding the user can already see. Grouping used to look only within one scan,
    /// so two entities that crossed the bar a scan apart each got a card and kept it; with the open findings
    /// in hand, a newcomer joins the existing card and its own row is reported back as absorbed, so the
    /// scanner can close it. Entities with no device and no shared moment are left alone: grouping everything
    /// unattributed together would collapse the entire house into one finding.
    /// </summary>
    internal static Collapsed Collapse(
        IReadOnlyList<(HaEntity Entity, Anomaly Anomaly)> found,
        ScanOptions options,
        IReadOnlySet<string> openKeys)
    {
        Dictionary<string, string> absorbed = new(StringComparer.Ordinal);

        if (!options.GroupByDevice || found.Count < 2)
            return new Collapsed([.. found.Select(pair => pair.Anomaly)], absorbed);

        List<(HaEntity Entity, Anomaly Anomaly)> byDevice = [];

        foreach (var group in found
            .GroupBy(pair => (Device: pair.Entity.DeviceId ?? pair.Entity.DeviceName, pair.Anomaly.Kind)))
        {
            if (group.Key.Device is null || group.Count() == 1)
            {
                byDevice.AddRange(group);
                continue;
            }

            byDevice.Add(Fold([.. group], openKeys, absorbed, Alongside));
        }

        List<Anomaly> kept = [];

        foreach (var group in byDevice.GroupBy(pair => pair.Anomaly.Kind))
        {
            if (group.Key != AnomalyKind.Unavailable)
            {
                kept.AddRange(group.Select(pair => pair.Anomaly));
                continue;
            }

            // Sorted by the moment each went quiet, then cut wherever the next one is more than a window
            // later, so a cluster is a run of entities that vanished together rather than a fixed bucket
            // that a real outage could straddle.
            var ordered = group
                .OrderBy(pair => pair.Entity.LastChanged)
                .ThenBy(pair => pair.Anomaly.EntityId, StringComparer.Ordinal)
                .ToList();
            List<(HaEntity Entity, Anomaly Anomaly)> cluster = [];

            foreach (var pair in ordered)
            {
                if (cluster.Count > 0 && pair.Entity.LastChanged - cluster[^1].Entity.LastChanged > OutageWindow)
                {
                    kept.Add(Fold(cluster, openKeys, absorbed, Together).Anomaly);
                    cluster = [];
                }

                cluster.Add(pair);
            }

            if (cluster.Count > 0) kept.Add(Fold(cluster, openKeys, absorbed, Together).Anomaly);
        }

        return new Collapsed(kept, absorbed);
    }

    /// <summary>
    /// Chooses which of a group stands for the rest, and writes the rest into it.
    ///
    /// A card the user already has wins over one that would be new, severity picks between the rest, and
    /// the entity id breaks ties. The tiebreak is what keeps the choice stable from one scan to the next: a
    /// group whose winner flipped would close one finding and open another every minute, which reads as a
    /// house that cannot make its mind up. A group of one is handed back as it is.
    /// </summary>
    private static (HaEntity Entity, Anomaly Anomaly) Fold(
        List<(HaEntity Entity, Anomaly Anomaly)> group,
        IReadOnlySet<string> openKeys,
        Dictionary<string, string> absorbed,
        Func<IReadOnlyList<string>, string> phrase)
    {
        if (group.Count == 1) return group[0];

        var ordered = group
            .OrderByDescending(pair => openKeys.Contains(pair.Anomaly.DedupKey))
            .ThenByDescending(pair => pair.Anomaly.Severity)
            .ThenBy(pair => pair.Anomaly.EntityId, StringComparer.Ordinal)
            .ToList();

        var lead = ordered[0];

        // A lead that was itself folded earlier (by device, before by moment) already names some siblings;
        // the new ones join that list rather than starting another.
        var already = Siblings(lead.Anomaly.EvidenceJson);
        List<string> others = [.. already];

        foreach (var pair in ordered.Skip(1))
        {
            others.Add(pair.Anomaly.EntityId);
            others.AddRange(Siblings(pair.Anomaly.EvidenceJson));
            absorbed[pair.Anomaly.DedupKey] = lead.Anomaly.EntityId;
        }

        others = [.. others.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // The severity of the group is the worst of it, so folding a bad case into a mild card cannot bury it.
        var severity = ordered.Max(pair => pair.Anomaly.Severity);

        // Only the newcomers are spoken of; the ones the lead already named are still in its sentence.
        var added = others.Except(already, StringComparer.Ordinal).ToList();
        var summary = added.Count == 0 ? lead.Anomaly.Summary : lead.Anomaly.Summary + " " + phrase(added);

        return (lead.Entity, lead.Anomaly with
        {
            Summary = summary,
            Severity = severity,
            EvidenceJson = WithSiblings(lead.Anomaly.EvidenceJson, others),
        });
    }

    private static string Alongside(IReadOnlyList<string> others) =>
        others.Count == 1
            ? $"{others[0]} on the same device moved at the same time."
            : $"{others.Count} other readings on the same device moved at the same time: {Named(others)}.";

    private static string Together(IReadOnlyList<string> others) =>
        others.Count == 1
            ? $"{others[0]} went unavailable at the same moment."
            : $"{others.Count} other entities went unavailable at the same moment: {Named(others)}.";

    private static string Named(IReadOnlyList<string> ids) =>
        ids.Count <= NamedSiblings
            ? string.Join(", ", ids)
            : string.Join(", ", ids.Take(NamedSiblings)) + $" and {ids.Count - NamedSiblings} more";

    /// <summary>The entity ids a finding already speaks for, read back out of its evidence.</summary>
    internal static IReadOnlyList<string> Siblings(string evidenceJson)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(evidenceJson);
            if (node is not System.Text.Json.Nodes.JsonObject json) return [];
            if (json["also_moved"] is not System.Text.Json.Nodes.JsonArray array) return [];

            List<string> ids = [];
            foreach (var item in array)
                if (item is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
                    ids.Add(id);

            return ids;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds the collapsed siblings to a finding's evidence without reparsing it into a model.</summary>
    private static string WithSiblings(string evidenceJson, IReadOnlyList<string> others) =>
        WithValue(evidenceJson, "also_moved", new System.Text.Json.Nodes.JsonArray([.. others.Select(id =>
            (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(id)!)]));

    /// <summary>
    /// Records why the scanner closed a finding, for the card to say. Absent means the plain case: the
    /// condition it described stopped being true.
    /// </summary>
    public const string ClosedBecause = "closed_because";

    private static string WithReason(string evidenceJson, string reason) =>
        WithValue(evidenceJson, ClosedBecause, System.Text.Json.Nodes.JsonValue.Create(reason));

    private static string WithValue(string evidenceJson, string key, System.Text.Json.Nodes.JsonNode? value)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(evidenceJson);
            if (node is not System.Text.Json.Nodes.JsonObject json) return evidenceJson;

            json[key] = value;
            return json.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            // Evidence is written by this process and is always an object, but a finding is not worth
            // losing over a blob that somehow is not.
            return evidenceJson;
        }
    }

    /// <summary>
    /// Closes open findings whose condition has passed: the door was shut, the reading came back, or what
    /// looked unusual has since become this entity's normal.
    ///
    /// A detector's silence is not on its own evidence of anything. It goes quiet when the condition ends,
    /// and equally when it no longer holds the history to have an opinion. An entity stuck in a bad state
    /// stops producing samples by definition, so its history ages out and its detector falls silent while
    /// the problem is still there. Closing on silence alone made a still-dead sensor disappear off the
    /// dashboard, reported to the user as "back to normal". So a finding is closed only on positive evidence.
    ///
    /// Positive evidence is not only "the condition ended". A finding is also over when Housekeeper would
    /// never raise it again as things stand: the entity has left the watch list, or it has come to be
    /// recognised as a setting or an instrument reading, or another card now speaks for it. And a numeric
    /// finding is over once the entity has reported a newer reading: the card quoted a value the sensor no
    /// longer shows, which is not a finding, it is a memory. Each of those is closed with the reason written
    /// into its evidence, because "back to normal" would be the wrong thing to tell someone about a sensor
    /// that was merely reclassified.
    /// </summary>
    private async Task<int> ResolveAsync(
        IReadOnlyList<Anomaly> open,
        IReadOnlySet<string> standing,
        IReadOnlySet<(string EntityId, AnomalyKind Kind)> resolvable,
        IReadOnlyDictionary<string, string> absorbed,
        IReadOnlyList<HaEntity> watched,
        IReadOnlyList<HaEntity> visible,
        IReadOnlyList<Concern> concerns,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (open.Count == 0) return 0;

        var observed = watched.ToDictionary(entity => entity.EntityId, entity => entity, StringComparer.Ordinal);
        var seen = new HashSet<string>(visible.Select(entity => entity.EntityId), StringComparer.Ordinal);
        var concernIds = new HashSet<long>(concerns.Select(concern => concern.Id));
        var resolved = 0;

        foreach (var finding in open)
        {
            if (standing.Contains(finding.DedupKey)) continue;

            var verdict = Settle(finding);
            if (verdict is null) continue;

            var closed = finding with { Status = AnomalyStatus.Resolved, DecidedUtc = now };
            if (verdict.Length > 0) closed = closed with { EvidenceJson = WithReason(closed.EvidenceJson, verdict) };

            await store.UpdateAnomalyAsync(closed, cancellationToken).ConfigureAwait(false);
            resolved++;
        }

        return resolved;

        // Null keeps the finding open. Empty closes it as the plain case; anything else closes it with that
        // sentence as the reason.
        string? Settle(Anomaly finding)
        {
            // A missing-entity finding is about an automation, not an entity, so it turns on whether the
            // automations could be checked at all this scan.
            if (finding.Kind == AnomalyKind.MissingEntity) return seen.Count > 0 ? "" : null;

            if (absorbed.TryGetValue(finding.DedupKey, out var lead))
                return $"It is now covered by the finding for {lead}.";

            // A concern's own finding is a comparison, not a verdict from history: if the entity was looked
            // at this scan and the rule did not fire, it has passed. And if the concern itself is gone, so is
            // the reason for the card.
            if (finding.Kind == AnomalyKind.Concern)
            {
                if (!ConcernOf(finding.DedupKey, out var owner) || !concernIds.Contains(owner))
                    return "The concern it belonged to was removed.";

                return observed.ContainsKey(finding.EntityId) ? "" : null;
            }

            if (resolvable.Contains((finding.EntityId, finding.Kind))) return "";

            if (!observed.TryGetValue(finding.EntityId, out var entity))
            {
                // Home Assistant still reports it, but this scan did not look at it: the watch list no
                // longer selects it, whether by choice or by the cap. Nothing would ever refresh or close
                // this card otherwise.
                if (seen.Contains(finding.EntityId)) return "It is no longer on the watch list.";

                // An entity that is not in this scan at all, gone from Home Assistant, is deliberately NOT
                // closed. It is tempting to, because such a finding is otherwise only ever cleared by the
                // user dismissing it. But "I cannot see it" is absence of evidence, and closing on that is
                // the whole mistake this method exists to avoid: an integration reloading looks identical to
                // an entity that was renamed, and only one of those means the problem stopped. Dismissing it
                // is one click; silently telling someone their still-open freezer is fine is not recoverable.
                return null;
            }

            // The detector has since learned to stay quiet about this entity, whether from the registry or
            // from its own rules. Whatever the reading is doing, no finding of this kind will be raised for
            // it again, and one left standing would be the only card of its kind nothing could ever close.
            if (Excused(finding.Kind, entity) is { } excuse)
                return $"Housekeeper no longer judges it: {excuse}.";

            switch (finding.Kind)
            {
                // A stuck-state finding is about one stretch in one state. If the entity has changed state
                // since the finding was raised, that stretch is over, whatever the history now supports, and
                // even though the state it moved to may be one the detector never speaks about. A closed door
                // is "resting", so without this the finding for the door being open could never be closed.
                case AnomalyKind.StuckState when entity.LastChanged > finding.DetectedUtc:
                    return "";

                // Reporting again is the whole of what "no longer unavailable" means; how much history it had
                // before it went quiet has no bearing on whether it is back.
                case AnomalyKind.Unavailable when !entity.IsUnavailable:
                    return "";

                // The sensor has reported since. Had the detector been able to judge the newer reading and
                // let it pass, Resolvable would have closed this above; reaching here means it could not
                // judge, and the card is quoting a value the sensor no longer shows.
                case AnomalyKind.NumericOutlier when entity.Numeric is not null && entity.LastChanged > finding.DetectedUtc:
                    return "It has reported since, and there is not yet enough history to judge the newer reading.";

                default:
                    return null;
            }
        }

        static bool ConcernOf(string dedupKey, out long id)
        {
            id = 0;
            var parts = dedupKey.Split(':');
            return parts.Length >= 3 && parts[0] == "concern" && long.TryParse(parts[1], out id);
        }

        static string? Excused(AnomalyKind kind, HaEntity entity) => kind switch
        {
            AnomalyKind.StuckState => Baselines.DiagnosticReason(entity),
            AnomalyKind.NumericOutlier => entity.IsCumulative ? "it is a running total" : Baselines.DiagnosticReason(entity),
            AnomalyKind.Unavailable => Baselines.IsInert(entity) ? $"the {entity.Domain} domain has no state that can go wrong" : null,
            _ => null,
        };
    }

    /// <summary>Flags automations we created that now reference entities Home Assistant no longer has.</summary>
    private async Task<int> CheckCreatedAutomationsAsync(
        IReadOnlyList<HaEntity> entities,
        ScanOptions scan,
        HashSet<string> standing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(entities.Select(e => e.EntityId), StringComparer.Ordinal);
        var live = new HashSet<string>(
            entities.Where(e => e.AutomationConfigId is not null).Select(e => e.AutomationConfigId!),
            StringComparer.Ordinal);
        var created = await store
            .ListProposalsAsync(ProposalStatus.Created, 500, includeDismissed: true, cancellationToken)
            .ConfigureAwait(false);

        var raised = 0;
        foreach (var proposal in created)
        {
            // Deleted in Home Assistant's own editor: stop treating it as live and let its finding go. Only
            // concluded when the state list holds automations at all — if the automation integration itself
            // failed to load, every automation would be missing and none of them deleted.
            if (live.Count > 0 &&
                proposal.HaAutomationId is { } id && !live.Contains(id) &&
                now - (proposal.DecidedUtc ?? proposal.CreatedUtc) > SettleTime)
            {
                await RetireAsync(proposal, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var anomaly = AnomalyDetection.DetectMissingEntities(proposal, known, now);
            if (anomaly is null) continue;

            standing.Add(anomaly.DedupKey);
            if (await TryRaiseAsync(anomaly, scan, now, cancellationToken).ConfigureAwait(false)) raised++;
        }

        return raised;
    }

    private async Task RetireAsync(Proposal proposal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Claimed from Created rather than written whole: this row was read at the top of the scan, and a
        // confirm or a dismissal landing since then must not be flattened by a snapshot that predates it.
        await store.TryClaimAsync(proposal.Id, ProposalStatus.Created, ProposalStatus.Removed, now, null, cancellationToken)
            .ConfigureAwait(false);

        var finding = await store.FindAnomalyAsync($"missing:{proposal.Id}", cancellationToken).ConfigureAwait(false);
        if (finding is { Status: AnomalyStatus.Open })
            await store.UpdateAnomalyAsync(
                finding with { Status = AnomalyStatus.Dismissed, DecidedUtc = now }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Automation {AutomationId} from proposal {ProposalId} is no longer in Home Assistant; marked as removed.",
            proposal.HaAutomationId, proposal.Id);
    }

    /// <summary>
    /// Reads the recorder's history for watched entities whose stored history is shallow, a batch per scan.
    ///
    /// A fresh install used to be blind for a day or two while it learned what normal looked like, when Home
    /// Assistant had ten days of that on disk the whole time. An install that has been watching for a week
    /// is in the same position for the fortnight before it started, so the question is not "has this entity
    /// any history" but "does its history reach back far enough" -- and inserting is idempotent, so what is
    /// already held is simply skipped. Best effort in every direction: a recorder that cannot be read costs
    /// nothing but a warning and the batch is asked about again next scan, and an entity asked about once
    /// is remembered so it is not asked about every scan for ever.
    /// </summary>
    private async Task<int> BackfillAsync(
        IReadOnlyList<HaEntity> watched,
        DateTimeOffset now,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var earliest = await store.GetEarliestSampleTimesAsync(cancellationToken).ConfigureAwait(false);

        var wanted = watched
            .Where(entity => !_backfilled.Contains(entity.EntityId) &&
                             (!earliest.TryGetValue(entity.EntityId, out var oldest) || now - oldest < DeepEnough))
            .Select(entity => entity.EntityId)
            .Take(BackfillPerScan)
            .ToList();

        if (wanted.Count == 0) return 0;

        var stored = 0;

        foreach (var batch in wanted.Chunk(BackfillPerRequest))
        {
            IReadOnlyDictionary<string, IReadOnlyList<StateSample>> history;
            try
            {
                history = await homeAssistant.GetHistoryAsync(batch, cutoffUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read history from the recorder for {Count} entities; they will be tried again next scan.", batch.Length);
                return stored;
            }

            foreach (var id in batch) _backfilled.Add(id);

            List<(string EntityId, StateSample Sample)> samples = [];
            foreach (var (entityId, changes) in history)
                foreach (var sample in Backfill.Thin(changes, cutoffUtc, NumericPerHour))
                    samples.Add((entityId, sample));

            if (samples.Count > 0)
                stored += await store.AddSamplesAsync(samples, cancellationToken).ConfigureAwait(false);
        }

        if (stored > 0)
            logger.LogInformation("Backfilled {Samples} samples from the recorder for {Entities} entities with shallow history.", stored, wanted.Count);

        return stored;
    }

    /// <summary>Stores a sample only when the entity's last change is newer than what we already hold.</summary>
    private async Task<int> RecordAsync(
        IReadOnlyList<HaEntity> watched,
        IReadOnlyDictionary<string, DateTimeOffset> latest,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        List<(string EntityId, StateSample Sample)> fresh = [];
        foreach (var entity in watched)
        {
            // Nothing older than the retention window is worth writing: the prune at the end of this very
            // scan would delete it again. Without this, an entity whose last change predates the window was
            // re-inserted and re-deleted on every scan for ever -- counted each time as a new change stored
            // and shown to the user as work done, while the detectors never saw the row at all.
            if (entity.LastChanged < cutoffUtc) continue;

            // Compared at the resolution samples are actually stored at. Home Assistant reports last_changed
            // with microseconds and the store keeps whole milliseconds, so the round-tripped value was always
            // strictly smaller and this check never once fired; only the ON CONFLICT clause saved it.
            if (latest.TryGetValue(entity.EntityId, out var seen) &&
                entity.LastChanged.ToUnixTimeMilliseconds() <= seen.ToUnixTimeMilliseconds())
                continue;

            fresh.Add((entity.EntityId, new StateSample(entity.State, entity.Numeric, entity.LastChanged)));
        }

        return fresh.Count == 0 ? 0 : await store.AddSamplesAsync(fresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One finding failing to persist must not stop the rest of the scan.</summary>
    private async Task<bool> TryRaiseAsync(Anomaly anomaly, ScanOptions scan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await RaiseAsync(anomaly, scan, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record anomaly {DedupKey}.", anomaly.DedupKey);
            return false;
        }
    }

    /// <summary>
    /// Inserts, refreshes or suppresses a finding. A dismissal is respected until
    /// <see cref="ScanOptions.RedetectAfter"/> has passed, so the same nag cannot come straight back.
    /// </summary>
    private async Task<bool> RaiseAsync(Anomaly anomaly, ScanOptions scan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await store.FindAnomalyAsync(anomaly.DedupKey, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            await store.UpsertAnomalyAsync(anomaly, cancellationToken).ConfigureAwait(false);
            return true;
        }

        switch (existing.Status)
        {
            case AnomalyStatus.Open:
                await store.UpdateAnomalyAsync(
                    existing with { Summary = anomaly.Summary, EvidenceJson = anomaly.EvidenceJson, SuggestedRequest = anomaly.SuggestedRequest, Severity = anomaly.Severity },
                    cancellationToken).ConfigureAwait(false);
                return false;

            // A finding that closed itself was never silenced by anyone, so a fresh occurrence of it opens
            // again at once rather than waiting out a window meant for something the user chose to dismiss.
            case AnomalyStatus.Resolved:
            case AnomalyStatus.Dismissed when now - (existing.DecidedUtc ?? existing.DetectedUtc) >= scan.RedetectAfter:

            // Promoting is a decision with a window, like dismissing -- not a permanent silence. It used to
            // be neither: Promoted fell through to the default below, and nothing else moved the row either,
            // so turning one finding into a draft meant that entity could never raise that kind of finding
            // again. The freezer door reported once, ever.
            case AnomalyStatus.Promoted when now - (existing.DecidedUtc ?? existing.DetectedUtc) >= scan.RedetectAfter:
                await store.UpdateAnomalyAsync(
                    existing with
                    {
                        Status = AnomalyStatus.Open,
                        Summary = anomaly.Summary,
                        EvidenceJson = anomaly.EvidenceJson,
                        SuggestedRequest = anomaly.SuggestedRequest,
                        Severity = anomaly.Severity,
                        DetectedUtc = now,
                        DecidedUtc = null,
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;

            // Still inside the window. The sentence is kept current so the card does not freeze on a
            // duration measured the moment it was first noticed, but it is not re-raised.
            case AnomalyStatus.Promoted:
            case AnomalyStatus.Dismissed:
                await store.UpdateAnomalyAsync(
                    existing with { Summary = anomaly.Summary, EvidenceJson = anomaly.EvidenceJson },
                    cancellationToken).ConfigureAwait(false);
                return false;

            default:
                return false;
        }
    }
}
