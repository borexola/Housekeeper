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
/// <param name="Routines">Routines newly offered by this scan's search of the history, counted apart from findings because they are offers, not problems.</param>
public sealed record ScanReport(
    int Observed,
    int NewSamples,
    int Raised,
    int Pruned,
    int Visible = 0,
    int Resolved = 0,
    int Judged = 0,
    int Backfilled = 0,
    int Routines = 0);

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

    /// <summary>
    /// How often the stored history is searched for routines. A routine is weeks of transitions, so nothing
    /// about it changes between one minute and the next, and the search reads far more than a scan does.
    /// </summary>
    private static readonly TimeSpan HabitCadence = TimeSpan.FromHours(1);

    /// <summary>The most transitions of one entity the routine search reads: two months of a light switched forty times a day.</summary>
    private const int HabitSamplesPerEntity = 6000;

    private DateTimeOffset? _habitsAt;

    /// <summary>
    /// Whether any scan this process has seen the automation integration in the state list. A later scan
    /// that sees none is Home Assistant mid-restart, not a house that deleted every automation.
    /// </summary>
    private bool _sawAutomations;

    /// <summary>
    /// The existing automations as last read successfully, when, and the last-changed time each automation's
    /// entity had then. Kept across scans: automations change rarely, and one read that fails must not unsay
    /// what an earlier one found.
    /// </summary>
    private (IReadOnlyList<ExistingAutomation> List, IReadOnlyDictionary<string, DateTimeOffset> Changed, DateTimeOffset AtUtc)? _automations;

    /// <summary>The last read of the automations that failed, and why. Nothing asks again until <see cref="AutomationsRetryAfter"/> has passed.</summary>
    private (DateTimeOffset AtUtc, string Reason)? _automationsFailed;

    /// <summary>
    /// How long after a failed read nothing asks again. A read is one request per automation, and when Home
    /// Assistant refuses them -- a token that is not an admin's -- every one is logged there as a failed
    /// login, and can get this address banned. The hour is the routine search's own cadence, so a refused
    /// token costs what it did before anything else read the automations.
    /// </summary>
    private static readonly TimeSpan AutomationsRetryAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// How long one read of every automation config is given, all told. Each request has its own timeout,
    /// but a scan waits on the whole sweep, and a config endpoint that has stopped answering would otherwise
    /// hold the scan for one timeout per six automations.
    /// </summary>
    private static readonly TimeSpan AutomationsDeadline = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long automations read successfully go on being used while every newer read fails. Past this they
    /// are too old to tell anyone what they are covered by, and the cards stop saying.
    /// </summary>
    private static readonly TimeSpan AutomationsTrustedFor = TimeSpan.FromHours(6);

    /// <summary>How long a state list with no automations in it is taken for Home Assistant restarting, as far as the cards are concerned.</summary>
    private static readonly TimeSpan RestartTakesAtMost = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The existing automations and the entity states taken with them, for the Noticed page to work out --
    /// when it is asked, not when a finding was last raised -- whether an automation already fires on each
    /// open finding. Null when no open finding could use them, or they could not be read.
    /// </summary>
    public KnownAutomations? Automations { get; private set; }

    /// <summary>The last search for routines: when, over how much, and what came of it. Null until one has run.</summary>
    public HabitSearch? LastRoutineSearch { get; private set; }

    /// <summary>How many dismissals silence a finding for good.</summary>
    public const int DismissalsToSilence = 3;

    /// <summary>The house's time zone, asked of Home Assistant now and then rather than on every pass.</summary>
    private (TimeZoneInfo Zone, DateTimeOffset At)? _zone;

    private static readonly TimeSpan ZoneFreshFor = TimeSpan.FromHours(6);

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

        // A state list with no automations in it, on a house that had them, is Home Assistant mid-restart
        // rather than a house that deleted every automation. Nothing about the automations is concluded
        // from it: the routine search waits, and what was known about them before stands.
        var listsAutomations = entities.Any(entity => entity.Domain == "automation");
        var restarting = !listsAutomations && _sawAutomations;
        if (listsAutomations) _sawAutomations = true;

        // The existing automations, read at most once this scan, and only by something that needs them.
        Task<IReadOnlyList<ExistingAutomation>?>? automationsRead = null;
        Task<IReadOnlyList<ExistingAutomation>?> ExistingAutomations() =>
            automationsRead ??= ReadAutomationsAsync(entities, now, cancellationToken);

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

        // Entities whose readings could be judged this scan, whatever they read. A numeric finding on one of
        // these is closed only on positive evidence that the reading came back; on the others, a newer
        // reading the detector cannot judge is all the card can be measured against.
        HashSet<string> numericJudgeable = new(StringComparer.Ordinal);

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
                if (AnomalyDetection.NumericJudgeable(entity, history, bar)) numericJudgeable.Add(entity.EntityId);
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

        var habits = await LearnHabitsAsync(entities, watched, scan, standing, restarting, ExistingAutomations, now, cancellationToken).ConfigureAwait(false);

        var resolved = await ResolveAsync(open, standing, resolvable, numericJudgeable, absorbed, watched, entities, concerns, habits, now, cancellationToken)
            .ConfigureAwait(false);

        await KnowAutomationsAsync(entities, restarting, ExistingAutomations, now, cancellationToken).ConfigureAwait(false);

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
            "Scan observed {Observed} entities, stored {NewSamples} new samples and {Backfilled} from the recorder, raised {Raised} anomalies " +
            "and offered {Routines} routines, closed {Resolved} that had passed, pruned {Pruned} rows, expired {Expired} proposals and {Forgotten} findings.",
            watched.Count, inserted, backfilled, raised, habits.Raised, resolved, pruned, expired, forgotten);

        return new ScanReport(watched.Count, inserted, raised, pruned, entities.Count, resolved, judged, backfilled, habits.Raised);
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

    /// <summary>
    /// Keeps <see cref="Automations"/> current for the Noticed page, which uses it to say when an automation
    /// the user already has fires on a finding -- before offering to have the model draft a second one.
    ///
    /// Nothing is read unless an open finding could use it: a house whose findings are all dismissed, or all
    /// routines, pays nothing. Coverage itself is worked out when the page asks rather than stored on each
    /// finding, so a card never goes on naming an automation deleted or switched off since, and a finding
    /// the scan kept open without raising again is answered from the same fresh states as the rest.
    /// </summary>
    private async Task KnowAutomationsAsync(
        IReadOnlyList<HaEntity> entities,
        bool restarting,
        Func<Task<IReadOnlyList<ExistingAutomation>?>> readAutomations,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Mid-restart every automation is missing from the state list, and would read as switched off. What
        // was known before the restart is still the best answer, so it is left as it was -- for as long as a
        // restart takes. One that has gone on longer is a house whose automations have gone.
        if (restarting)
        {
            if (Automations is { } held && now - held.SeenUtc > RestartTakesAtMost) Automations = null;
            return;
        }

        var open = await store.ListAnomaliesAsync(AnomalyStatus.Open, OpenFindingCeiling, includeClosed: false, cancellationToken)
            .ConfigureAwait(false);

        if (!open.Any(finding => Coverage.Wanted(finding) is not null))
        {
            Automations = null;
            return;
        }

        await readAutomations().ConfigureAwait(false);

        // A failed read leaves the last good one in use, for a while. Automations change rarely, and the
        // entity states taken now still say which of them exist and are switched on.
        Automations = _automations is { } known && now - known.AtUtc < AutomationsTrustedFor
            ? new KnownAutomations(known.List, entities, known.AtUtc, now)
            : null;
    }

    /// <summary>
    /// Reads the existing automations, for the routine search and for the Noticed page alike. Null when they
    /// could not be read, or a read has failed within the hour.
    ///
    /// An automation that still exists but could not be read this time, and has not changed since it last
    /// was, keeps its last reading. A config request that timed out is not an automation deleted, and
    /// dropping it would have every card it covers offer to build it again until the next good read.
    /// </summary>
    private async Task<IReadOnlyList<ExistingAutomation>?> ReadAutomationsAsync(
        IReadOnlyList<HaEntity> entities,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_automationsFailed is { } failed && now - failed.AtUtc < AutomationsRetryAfter) return null;

        using var deadline = new CancellationTokenSource(AutomationsDeadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        IReadOnlyList<ExistingAutomation> read;
        try
        {
            read = await homeAssistant.GetAutomationsAsync(entities, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"Home Assistant did not hand over the automation configs within {Ha.Duration(AutomationsDeadline)}.", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex.Message, ex);
        }

        var changed = entities
            .Where(entity => entity.AutomationConfigId is not null)
            .GroupBy(entity => entity.EntityId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().LastChanged, StringComparer.Ordinal);

        List<ExistingAutomation> list = [.. read];
        if (_automations is { } before)
        {
            var fresh = new HashSet<string>(read.Select(automation => automation.EntityId), StringComparer.Ordinal);
            foreach (var automation in before.List)
                if (!fresh.Contains(automation.EntityId) &&
                    changed.TryGetValue(automation.EntityId, out var lastChanged) &&
                    before.Changed.TryGetValue(automation.EntityId, out var then) && lastChanged == then)
                    list.Add(automation);
        }

        _automations = (list, changed, now);
        _automationsFailed = null;
        return list;

        IReadOnlyList<ExistingAutomation>? Failed(string reason, Exception? ex)
        {
            _automationsFailed = (now, reason);
            logger.LogWarning(ex, "Could not read the existing automations; not asking again for {Wait}. {Reason}",
                Ha.Duration(AutomationsRetryAfter), reason);
            return null;
        }
    }

    /// <summary>Writes why a finding was closed into its evidence. Public so every caller that closes one words it the same way.</summary>
    public static string WithReason(string evidenceJson, string reason) =>
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
        IReadOnlySet<string> numericJudgeable,
        IReadOnlyDictionary<string, string> absorbed,
        IReadOnlyList<HaEntity> watched,
        IReadOnlyList<HaEntity> visible,
        IReadOnlyList<Concern> concerns,
        HabitPass habits,
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

            // A routine is derived from weeks of history, not from this minute's state, and only every so
            // often. On a scan that did not look, nothing new is known about it and it stays. When one did
            // look and no longer offers it, it is over: an automation now does it, which is the happy
            // ending, or the pattern did not hold up.
            if (finding.Kind == AnomalyKind.Habit)
            {
                if (!habits.Mined) return null;
                if (habits.Off is { } off) return off;
                if (habits.Automated.Contains(finding.DedupKey)) return "An automation now does this.";

                // The search only looks at watched entities. A routine whose effect or cue has left the
                // watch list was not looked at, and "not held up" would be a false statement about the user.
                if (LeftWatchList(finding.EntityId) || (CueOf(finding.EvidenceJson) is { } cue && LeftWatchList(cue)))
                    return "It is no longer on the watch list.";

                return "It has not held up over the weeks since.";
            }

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

                // The sensor has reported since and its readings cannot be judged any more: the card is
                // quoting a value the sensor no longer shows, and nothing could ever close it. While the
                // readings CAN be judged, a newer reading closes nothing on its own; it has to have come back
                // inside the range and stayed there, which Resolvable decides above.
                case AnomalyKind.NumericOutlier when !numericJudgeable.Contains(finding.EntityId) && entity.Numeric is not null && entity.LastChanged > finding.DetectedUtc:
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

        // Home Assistant still reports it, but this scan did not look at it.
        bool LeftWatchList(string entityId) => !observed.ContainsKey(entityId) && seen.Contains(entityId);

        static string? CueOf(string evidenceJson)
        {
            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(evidenceJson) is System.Text.Json.Nodes.JsonObject json &&
                       json["cue"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var cue)
                    ? cue
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        static string? Excused(AnomalyKind kind, HaEntity entity) => kind switch
        {
            AnomalyKind.StuckState => Baselines.DiagnosticReason(entity),
            AnomalyKind.NumericOutlier => entity.IsCumulative ? "it is a running total" : Baselines.DiagnosticReason(entity),
            AnomalyKind.Unavailable => Baselines.IsInert(entity) ? $"the {entity.Domain} domain has no state that can go wrong" : null,
            _ => null,
        };
    }

    /// <summary>What one scan's search for routines did, for the closing pass.</summary>
    /// <param name="Mined">Whether the history was searched this scan at all.</param>
    /// <param name="Off">When learning is switched off: the reason every open routine is closed with.</param>
    /// <param name="Automated">Routines that held up but an existing automation already performs.</param>
    /// <param name="Raised">Routines newly offered.</param>
    internal sealed record HabitPass(bool Mined, string? Off, IReadOnlySet<string> Automated, int Raised)
    {
        public static readonly HabitPass Skipped = new(false, null, new HashSet<string>(StringComparer.Ordinal), 0);
    }

    /// <summary>The last search for routines, for the dashboard: when, over how many entities, and what came of it.</summary>
    /// <param name="Found">Routines that held up, shown or not.</param>
    /// <param name="Offered">Routines on offer after the cap and the user's own put-aways.</param>
    /// <param name="Skipped">Why the search did not run, when it did not; null when it ran.</param>
    public sealed record HabitSearch(DateTimeOffset AtUtc, int Entities, int Found, int Offered, int Automated, int MachineMade, string? Skipped);

    /// <summary>
    /// Whether a finding is far enough past its bar to come back after being dismissed before. Each
    /// dismissal raises the bar by half a doubling; after <see cref="DismissalsToSilence"/> the answer is final.
    /// </summary>
    internal static bool ClearsTheDismissals(Anomaly anomaly, Anomaly existing) =>
        existing.Dismissals < DismissalsToSilence && anomaly.Severity >= 1 + 0.5 * existing.Dismissals;

    /// <summary>
    /// Searches the stored history for routines, once an hour, and raises each as a finding of its own kind.
    ///
    /// The existing automations are read first, because a routine one of them already performs would
    /// otherwise be offered as new -- and if they cannot be read, or Home Assistant is between restarts and
    /// lists none, the search waits for the next hour rather than guessing. The house's time zone comes
    /// from Home Assistant: "about a quarter to seven" is a local time, and the container this runs in is
    /// pinned to UTC.
    ///
    /// Everything that held up stands, whether or not it is shown, so a routine past the cap is never closed
    /// as "not held up". The cap is applied here rather than in the search, where what the user has already
    /// put away is known: a put-away routine keeps its row current but takes no slot, so the thirty-first
    /// routine is offered once the thirtieth is put away rather than never.
    /// </summary>
    private async Task<HabitPass> LearnHabitsAsync(
        IReadOnlyList<HaEntity> entities,
        IReadOnlyList<HaEntity> watched,
        ScanOptions scan,
        HashSet<string> standing,
        bool restarting,
        Func<Task<IReadOnlyList<ExistingAutomation>?>> readAutomations,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!scan.LearnHabits)
        {
            LastRoutineSearch = new HabitSearch(now, 0, 0, 0, 0, 0, "Learning routines is turned off.");
            return new HabitPass(true, "Learning routines is turned off under Settings → Scan.", new HashSet<string>(StringComparer.Ordinal), 0);
        }

        if (_habitsAt is { } at && now - at < HabitCadence) return HabitPass.Skipped;
        if (watched.Count == 0) return HabitPass.Skipped;

        if (restarting)
        {
            _habitsAt = now;
            LastRoutineSearch = new HabitSearch(now, 0, 0, 0, 0, 0, "Home Assistant listed no automations this scan; waiting for them to come back.");
            logger.LogWarning("The state list holds no automations this scan, so routines were not looked for this hour.");
            return HabitPass.Skipped;
        }

        if (await readAutomations().ConfigureAwait(false) is not { } automations)
        {
            _habitsAt = now;
            LastRoutineSearch = new HabitSearch(now, 0, 0, 0, 0, 0, "The existing automations could not be read: " + _automationsFailed?.Reason);
            logger.LogWarning("Could not read the existing automations, so routines were not looked for this hour.");
            return HabitPass.Skipped;
        }

        var wanted = Habits.Candidates(watched);
        var samples = await store.GetSamplesForAsync(wanted, now - scan.History, HabitSamplesPerEntity, cancellationToken).ConfigureAwait(false);
        var zone = await ZoneAsync(now, cancellationToken).ConfigureAwait(false);

        var report = Habits.Find(watched, samples, automations, zone, scan, now);
        _habitsAt = now;

        foreach (var habit in report.Found) standing.Add(habit.DedupKey);

        int raised = 0, offered = 0;
        foreach (var habit in report.Found)
        {
            var existing = await store.FindAnomalyAsync(habit.DedupKey, cancellationToken).ConfigureAwait(false);

            // Put away: the sentence is kept current, no slot is taken. RaiseAsync never reopens a dismissed
            // routine, so this is only a refresh.
            if (existing is { Status: AnomalyStatus.Dismissed })
            {
                await TryRaiseAsync(habit, scan, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Promoted: a draft exists for it. Refreshed directly rather than through RaiseAsync, which
            // after the quiet period would reopen it as a fresh card -- with its old proposal id still
            // attached and a second "Make an automation" button -- uncounted against the cap. A promoted
            // routine leaves that state when the draft is confirmed (it becomes automated and closes) or
            // when the draft is rejected, which reopens the finding through the proposal path.
            if (existing is { Status: AnomalyStatus.Promoted })
            {
                await store.UpdateAnomalyAsync(
                    existing with
                    {
                        Summary = habit.Summary,
                        EvidenceJson = habit.EvidenceJson,
                        SuggestedRequest = habit.SuggestedRequest,
                        Severity = habit.Severity,
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (existing is { Status: AnomalyStatus.Open })
            {
                offered++;
                await TryRaiseAsync(habit, scan, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (offered >= Habits.MostOffered) continue;

            offered++;
            if (await TryRaiseAsync(habit, scan, now, cancellationToken).ConfigureAwait(false)) raised++;
        }

        // A routine the user turned into an automation is done with, not still on offer.
        foreach (var key in report.Automated)
        {
            var existing = await store.FindAnomalyAsync(key, cancellationToken).ConfigureAwait(false);
            if (existing is not { Kind: AnomalyKind.Habit, Status: AnomalyStatus.Promoted }) continue;

            await store.UpdateAnomalyAsync(
                existing with { Status = AnomalyStatus.Resolved, DecidedUtc = now, EvidenceJson = WithReason(existing.EvidenceJson, "An automation now does this.") },
                cancellationToken).ConfigureAwait(false);
        }

        LastRoutineSearch = new HabitSearch(now, wanted.Count, report.Found.Count, offered, report.Automated.Count, report.MachineMade.Count, null);

        logger.LogInformation(
            "Looked for routines across {Entities} entities: {Found} held up, {Offered} on offer ({Raised} new), {Automated} already automated, {MachineMade} machine-made.",
            wanted.Count, report.Found.Count, offered, raised, report.Automated.Count, report.MachineMade.Count);

        return new HabitPass(true, null, report.Automated, raised);
    }

    /// <summary>The house's time zone, or the process's own when Home Assistant's cannot be read or resolved.</summary>
    internal async Task<TimeZoneInfo> ZoneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_zone is { } held && now - held.At < ZoneFreshFor) return held.Zone;

        var zone = TimeZoneInfo.Local;
        var resolved = false;
        try
        {
            var id = await homeAssistant.GetTimeZoneAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(id))
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
                resolved = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve Home Assistant's time zone; using the process's own.");
        }

        // Only an answer is held. Falling back is tried again next pass, so an outage costs one search in
        // the wrong zone rather than six hours of them.
        if (resolved) _zone = (zone, now);
        else _zone = null;

        return zone;
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
            //
            // A dismissal is the user teaching the detector. Each one raises the bar the same finding has
            // to clear to come back after the quiet period -- half a doubling per dismissal -- and after
            // three the answer is taken as final. A routine the user said no to is final from the first:
            // "not this one" is a decision about the routine, not about that week, and its row is never
            // pruned, so it stays quiet for good.
            case AnomalyStatus.Resolved:
            case AnomalyStatus.Dismissed when existing.Kind != AnomalyKind.Habit && now - (existing.DecidedUtc ?? existing.DetectedUtc) >= scan.RedetectAfter && ClearsTheDismissals(anomaly, existing):

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
