namespace Housekeeper.Core;

/// <summary>
/// Home Assistant answered, but not usefully: a non-success status, an unreadable body, or a refused write.
/// Distinct from a transport failure so the API layer can report it as an upstream problem rather than a crash.
/// </summary>
/// <param name="refused">
/// True when Home Assistant received the request, understood it, and declined — so nothing was written.
///
/// This is the one distinction that matters when a write fails. "It said no" and "I never found out" look
/// identical from here, and only the first is safe to treat as though nothing happened: a rejected token can
/// be corrected and the same draft confirmed again, while a request that timed out may already be live in
/// the user's home. Carried as a flag rather than a status code because nothing in Core knows about HTTP.
/// </param>
public sealed class HomeAssistantException(string message, Exception? inner = null, bool refused = false)
    : Exception(message, inner)
{
    public bool Refused { get; } = refused;
}

/// <summary>Everything Housekeeper needs from Home Assistant. Implemented over the REST API.</summary>
public interface IHomeAssistant
{
    /// <summary>Current state of every entity the token can see.</summary>
    Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Existing automations with the entities and trigger kinds they reference. Takes the entity list the
    /// caller already holds, because the automation entities in it carry the config ids to read.
    /// </summary>
    Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(IReadOnlyList<HaEntity> entities, CancellationToken cancellationToken);

    /// <summary>
    /// Every service this Home Assistant will accept, as <c>domain.service</c>. Used both to tell the model
    /// what it may call and to reject a call it invented. An empty set means the list could not be read, and
    /// service checking is skipped rather than blocking the draft.
    /// </summary>
    Task<IReadOnlySet<string>> GetServicesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What Home Assistant's own recorder holds for these entities since a moment: every state change it
    /// kept, oldest first per entity. Entities the recorder has nothing for are simply absent.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetHistoryAsync(
        IReadOnlyList<string> entityIds,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    /// <summary>Writes a new automation and returns the id it was stored under.</summary>
    Task<string> CreateAutomationAsync(string id, string configJson, CancellationToken cancellationToken);

    /// <summary>True when the base URL answers and the token is accepted.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The IANA time zone Home Assistant is configured for, such as <c>America/Regina</c>, or null when it
    /// could not be read. A routine is a local-time thing -- "about a quarter to seven" -- and the container
    /// Housekeeper runs in is pinned to UTC, so the house's own zone is the only one that can say when.
    /// </summary>
    Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken);
}

/// <summary>One state change, as Home Assistant announced it the moment it happened.</summary>
public sealed record StateEvent(string EntityId, string State, DateTimeOffset ChangedUtc);

/// <summary>
/// A live stream of state changes, for the transitions a poller cannot see.
///
/// Deliberately additive rather than a replacement. The scan keeps running and keeps reading every entity,
/// so a stream that drops, or never connects at all, costs exactly the behaviour Housekeeper had before
/// this existed — and the next poll closes whatever gap the outage left, which is why nothing here has to
/// reconcile one. The stream's whole job is to catch what falls between two polls.
/// </summary>
public interface IStateFeed
{
    /// <summary>
    /// Yields changes until cancelled. Implementations reconnect on their own and simply go quiet while
    /// they cannot connect; a caller is never expected to restart one.
    /// </summary>
    IAsyncEnumerable<StateEvent> WatchAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of a reachability check, phrased for someone looking at a settings screen.</summary>
public sealed record CheckResult(bool Ok, string Detail)
{
    public static CheckResult Pass(string detail) => new(true, detail);
    public static CheckResult Fail(string detail) => new(false, detail);
}

/// <summary>A chat model that returns a single JSON object. Ollama and OpenAI-compatible both fit.</summary>
public interface ILlmClient
{
    string Name { get; }

    /// <summary>Returns raw model output, or null when the endpoint is unreachable or answers badly.</summary>
    Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);

    /// <summary>Checks the endpoint answers and the configured model is available, without running it.</summary>
    Task<CheckResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The configuration in force right now. Read per operation rather than captured, so a change made in
/// the settings UI takes effect without restarting the process.
/// </summary>
public interface ISettingsProvider
{
    HousekeeperOptions Current { get; }
}

/// <summary>Local persistence. Three tables: proposals, anomalies, and a compact state history.</summary>
public interface IStore
{
    Task<Proposal> AddProposalAsync(Proposal proposal, CancellationToken cancellationToken);
    Task<Proposal?> GetProposalAsync(long id, CancellationToken cancellationToken);
    /// <param name="includeDismissed">
    /// False for anything a person reads; true for supervision, which must keep watching an automation the
    /// user has merely stopped looking at.
    /// </param>
    Task<IReadOnlyList<Proposal>> ListProposalsAsync(
        ProposalStatus? status,
        int limit,
        bool includeDismissed,
        CancellationToken cancellationToken);
    Task UpdateProposalAsync(Proposal proposal, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a proposal from one status to another in a single statement, returning false when it was not in
    /// <paramref name="from"/> any more.
    ///
    /// Every decision about a proposal goes through here rather than through
    /// <see cref="UpdateProposalAsync"/>, for two reasons. It is atomic, so two requests cannot both pass a
    /// status check and both act — confirming is the only call that changes the user's home. And it writes
    /// only the status, the decision time and the error, so a caller holding a row it read some time ago
    /// cannot flatten a decision that was made in the meantime.
    /// </summary>
    Task<bool> TryClaimAsync(
        long id,
        ProposalStatus from,
        ProposalStatus to,
        DateTimeOffset? decidedUtc,
        string? error,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what became of a Home Assistant write, against a proposal this caller has already claimed.
    ///
    /// Narrow for the same reason the claim is. The caller read the row before it spent a second or two
    /// writing to Home Assistant, and putting that snapshot back would erase anything decided in between —
    /// a dismissal, most realistically, which arrives through its own statement and would otherwise be
    /// flattened. Worse in the other order: a dismissal holding an older snapshot would blank the automation
    /// id this is recording, leaving an automation live in the user's home with nothing pointing at it.
    /// </summary>
    Task RecordOutcomeAsync(
        long id,
        ProposalStatus to,
        DateTimeOffset decidedUtc,
        string? haAutomationId,
        string? error,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hides a finished proposal, or brings it back. One column, so it cannot disturb a confirm in flight —
    /// and a draft is refused in the statement itself, because hiding one would lose a pending decision.
    /// </summary>
    Task<bool> SetDismissedAsync(long id, DateTimeOffset? dismissedUtc, CancellationToken cancellationToken);

    /// <summary>Appends state samples, ignoring ones already recorded. Returns the number inserted.</summary>
    Task<int> AddSamplesAsync(IReadOnlyList<(string EntityId, StateSample Sample)> samples, CancellationToken cancellationToken);

    /// <summary>Newest recorded change per entity, used to skip samples already stored.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestSampleTimesAsync(CancellationToken cancellationToken);

    /// <summary>Oldest recorded change per entity, used to see how far back its stored history reaches.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetEarliestSampleTimesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Samples since a cutoff, grouped by entity and ordered oldest first. One query per scan, holding at
    /// most <paramref name="maxPerEntity"/> of the newest samples for each entity so a busy house cannot
    /// pull an unbounded amount of history into memory.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesAsync(
        DateTimeOffset sinceUtc,
        int maxPerEntity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Numeric samples since a cutoff, thinned so they cover the window rather than crowd the end of it.
    ///
    /// A separate read from <see cref="GetSamplesAsync"/> because the two want opposite things.
    /// That one must stay contiguous, since the stuck-state detector measures a stretch by subtracting one
    /// stored sample from the next and a hole in the middle reads as one long stretch rather than as two.
    /// A distribution has no such constraint and the opposite need: taking the newest rows from a sensor
    /// that changes every minute yields a few hours, which is not a baseline for anything with a daily
    /// shape. At most <paramref name="perBucket"/> readings are kept from each hour, newest first.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetNumericHistoryAsync(
        DateTimeOffset sinceUtc,
        int perBucket,
        int maxPerEntity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every stored sample since a cutoff for a chosen set of entities, grouped by entity and ordered
    /// oldest first, at most <paramref name="maxPerEntity"/> of the newest for each. This is what habit
    /// learning reads: weeks of a few hundred entities' transitions, which the per-scan reads deliberately
    /// do not hold.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesForAsync(
        IReadOnlyCollection<string> entityIds,
        DateTimeOffset sinceUtc,
        int maxPerEntity,
        CancellationToken cancellationToken);

    Task<int> PruneSamplesAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The newest stored state changes across every entity, for the Logs page: what has actually arrived
    /// from Home Assistant, whether by scan, live feed or backfill. Optionally only entities whose id
    /// contains <paramref name="entityContains"/>.
    /// </summary>
    Task<IReadOnlyList<(string EntityId, StateSample Sample)>> ListRecentSamplesAsync(
        string? entityContains,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// One pass over the retained history: how much of it there is and how far back it reaches. How much of
    /// it is <em>usable</em> is not asked here, because only the detectors know that; the scan counts it.
    /// </summary>
    Task<HistorySummary> GetHistorySummaryAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes proposals that reached a final state before the cutoff: rejected, failed, superseded or
    /// removed. Drafts wait for a decision and created ones describe live automations, so both are kept.
    /// </summary>
    Task<int> PruneProposalsAsync(DateTimeOffset decidedBefore, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes findings closed before the cutoff, dismissed and resolved alike. Open ones are still true and
    /// promoted ones explain where an automation came from, so both stay. So does a routine the user put
    /// away, and a finding dismissed enough times to be silenced: each is the record of a "no" that must
    /// keep being honoured.
    /// </summary>
    Task<int> PruneAnomaliesAsync(DateTimeOffset decidedBefore, CancellationToken cancellationToken);

    Task<Anomaly?> GetAnomalyAsync(long id, CancellationToken cancellationToken);
    Task<Anomaly?> FindAnomalyAsync(string dedupKey, CancellationToken cancellationToken);
    Task<Anomaly> UpsertAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken);
    /// <param name="includeClosed">
    /// False leaves out the ones nobody has to act on — dismissed, and resolved by the scanner itself.
    /// Ignored when <paramref name="status"/> asks for one of those explicitly.
    /// </param>
    Task<IReadOnlyList<Anomaly>> ListAnomaliesAsync(
        AnomalyStatus? status,
        int limit,
        bool includeClosed,
        CancellationToken cancellationToken);
    Task UpdateAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken);

    Task<Concern> AddConcernAsync(Concern concern, CancellationToken cancellationToken);
    Task<IReadOnlyList<Concern>> ListConcernsAsync(CancellationToken cancellationToken);
    Task<Concern?> GetConcernAsync(long id, CancellationToken cancellationToken);
    Task UpdateConcernAsync(Concern concern, CancellationToken cancellationToken);
    Task<bool> DeleteConcernAsync(long id, CancellationToken cancellationToken);
}
