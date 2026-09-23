using Housekeeper.Api;
using Housekeeper.Core;

namespace Housekeeper.Tests;

public sealed class FakeHomeAssistant : IHomeAssistant
{
    public List<HaEntity> Entities { get; } = [];
    public List<ExistingAutomation> Automations { get; } = [];
    public List<(string Id, string ConfigJson)> Created { get; } = [];

    /// <summary>Empty by default, which is how a house whose service list could not be read behaves.</summary>
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ServicesFailure { get; set; }

    public Exception? CreateFailure { get; set; }
    public Exception? AutomationsFailure { get; set; }
    public Exception? EntitiesFailure { get; set; }
    public bool Reachable { get; set; } = true;

    public Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken cancellationToken) =>
        EntitiesFailure is not null
            ? Task.FromException<IReadOnlyList<HaEntity>>(EntitiesFailure)
            : Task.FromResult<IReadOnlyList<HaEntity>>(Entities);

    /// <summary>How many times the automations were asked for, so a test can see who reads them and how often.</summary>
    public int AutomationReads { get; private set; }

    /// <summary>Held open to keep a read of the automations in flight, the way a config endpoint that has stopped answering would.</summary>
    public TaskCompletionSource? AutomationsGate { get; set; }

    public async Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(IReadOnlyList<HaEntity> entities, CancellationToken cancellationToken)
    {
        AutomationReads++;
        if (AutomationsGate is not null) await AutomationsGate.Task.WaitAsync(cancellationToken);
        if (AutomationsFailure is not null) throw AutomationsFailure;

        return [.. Automations];
    }

    public Task<IReadOnlySet<string>> GetServicesAsync(CancellationToken cancellationToken) =>
        ServicesFailure is not null
            ? Task.FromException<IReadOnlySet<string>>(ServicesFailure)
            : Task.FromResult<IReadOnlySet<string>>(Services);

    /// <summary>What the recorder holds per entity, oldest first. Empty by default: a house with no recorder.</summary>
    public Dictionary<string, List<StateSample>> History { get; } = new(StringComparer.Ordinal);

    /// <summary>Every batch of entity ids the recorder was asked about, in order.</summary>
    public List<IReadOnlyList<string>> HistoryRequests { get; } = [];

    public Exception? HistoryFailure { get; set; }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetHistoryAsync(
        IReadOnlyList<string> entityIds, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        HistoryRequests.Add(entityIds);
        if (HistoryFailure is not null) return Task.FromException<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>>(HistoryFailure);

        var found = entityIds
            .Where(History.ContainsKey)
            .ToDictionary(id => id, id => (IReadOnlyList<StateSample>)[.. History[id].Where(s => s.ChangedUtc >= sinceUtc)], StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>>(found);
    }

    /// <summary>Held open to keep a write in flight, so a second confirm can be raced against the first.</summary>
    public TaskCompletionSource? CreateGate { get; set; }

    public async Task<string> CreateAutomationAsync(string id, string configJson, CancellationToken cancellationToken)
    {
        if (CreateGate is not null) await CreateGate.Task.WaitAsync(cancellationToken);
        if (CreateFailure is not null) throw CreateFailure;

        lock (Created) Created.Add((id, configJson));
        return id;
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(Reachable);

    /// <summary>Home Assistant's configured zone. Null is a config that could not be read.</summary>
    public string? TimeZone { get; set; } = "UTC";

    public Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken) => Task.FromResult(TimeZone);
}

public sealed class FakeLlm : ILlmClient
{
    private readonly Queue<string?> _queued = new();

    public string Name => "fake-model";

    /// <summary>Raw text the model will "return" once the queue runs out. Null is an unreachable endpoint.</summary>
    public string? Response { get; set; }

    public string? LastSystemPrompt { get; private set; }
    public string? LastUserPrompt { get; private set; }

    /// <summary>Every prompt this model was sent, in order, so a retry can be inspected.</summary>
    public List<string> Prompts { get; } = [];

    public int Calls { get; private set; }

    /// <summary>Queues one answer per call, for exercising the repair loop.</summary>
    public void Replies(params string?[] responses)
    {
        foreach (var response in responses) _queued.Enqueue(response);
    }

    /// <summary>Drops whatever earlier tests queued, so a shared fake answers only what this test set.</summary>
    public void Clear() => _queued.Clear();

    /// <summary>Held open to keep a draft in flight, so something else can be raced against the model call.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        Calls++;
        LastSystemPrompt = systemPrompt;
        LastUserPrompt = userPrompt;
        Prompts.Add(userPrompt);

        if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);

        return _queued.Count > 0 ? _queued.Dequeue() : Response;
    }

    public Task<CheckResult> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Reachable ? CheckResult.Pass("fake-model is available.") : CheckResult.Fail("fake-model is not reachable."));

    public bool Reachable { get; set; } = true;
}

/// <summary>Hands the shared fakes back whatever settings are asked for, and remembers what was asked.</summary>
public sealed class FakeAdapters(FakeHomeAssistant homeAssistant, FakeLlm llm) : IAdapters
{
    public HousekeeperOptions? LastHomeAssistantOptions { get; private set; }
    public string? LastHomeAssistantToken { get; private set; }
    public HousekeeperOptions? LastLlmOptions { get; private set; }

    public IHomeAssistant HomeAssistant(ISettingsProvider settings, ISecretSource secrets)
    {
        LastHomeAssistantOptions = settings.Current;
        LastHomeAssistantToken = secrets.Resolve(SecretStore.HomeAssistantToken, settings.Current.HomeAssistant.TokenEnvironmentVariable);
        return homeAssistant;
    }

    public ILlmClient Llm(ISettingsProvider settings, ISecretSource secrets)
    {
        LastLlmOptions = settings.Current;
        return llm;
    }

    public IStateFeed StateFeed(ISettingsProvider settings, ISecretSource secrets) => Feed;

    /// <summary>The feed a test sees. Silent by default, so no test opens a socket by accident.</summary>
    public FakeStateFeed Feed { get; } = new();
}

/// <summary>
/// A state feed a test drives by hand. Waits rather than completing, because a feed that ends immediately
/// would have its worker fall out of its loop and stop — which is not what an idle house looks like.
/// </summary>
public sealed class FakeStateFeed : IStateFeed
{
    private readonly System.Threading.Channels.Channel<StateEvent> _changes =
        System.Threading.Channels.Channel.CreateUnbounded<StateEvent>();

    public void Publish(params StateEvent[] changes)
    {
        foreach (var change in changes) _changes.Writer.TryWrite(change);
    }

    public void Finish() => _changes.Writer.TryComplete();

    public IAsyncEnumerable<StateEvent> WatchAsync(CancellationToken cancellationToken) =>
        _changes.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Settings that can be changed mid-test, the way the settings UI changes them at runtime.</summary>
public sealed class FakeSettings(HousekeeperOptions? options = null) : ISettingsProvider
{
    public HousekeeperOptions Current { get; set; } = options ?? new HousekeeperOptions();
}

public static class Build
{
    public static HaEntity Entity(
        string entityId,
        string state = "off",
        DateTimeOffset? lastChanged = null,
        string? friendlyName = null,
        string? deviceClass = null,
        string? unit = null,
        string? area = null,
        string? automationConfigId = null,
        string? deviceId = null,
        string? deviceName = null,
        string? stateClass = null,
        string? entityCategory = null,
        bool hidden = false,
        string? areaId = null,
        string? registryId = null)
    {
        var changed = lastChanged ?? DateTimeOffset.UnixEpoch;
        return new HaEntity(entityId, state, changed, changed, friendlyName, deviceClass, unit, area, automationConfigId, deviceId, deviceName, stateClass, entityCategory, hidden, areaId, registryId);
    }

    public static AutomationDraft Draft(
        string alias,
        IReadOnlyList<string> entities,
        IReadOnlyList<string>? actions = null,
        IReadOnlySet<string>? triggerKinds = null) =>
        new(alias, null, "{}", entities, actions ?? [], triggerKinds ?? new HashSet<string>(StringComparer.Ordinal));

    public static ExistingAutomation Existing(
        string id,
        string alias,
        IEnumerable<string> entities,
        IEnumerable<string>? triggerKinds = null) =>
        new(id, $"automation.{id}", alias,
            new HashSet<string>(entities, StringComparer.Ordinal),
            new HashSet<string>(triggerKinds ?? [], StringComparer.Ordinal),
            []);
}

/// <summary>
/// The real store with one seam: the claim that decides which of two concurrent confirms wins can be held
/// open, so a test can force the interleaving the guard exists for.
///
/// Without this the race cannot be reproduced at all. Microsoft.Data.Sqlite completes its work synchronously,
/// so calling ConfirmAsync twice runs the first all the way to the Home Assistant write before the second
/// even begins — and the second is then turned away by the ordinary status check, not by the atomic claim.
/// A test written that way passes just as happily with the claim deleted, which makes it worth nothing.
/// </summary>
public sealed class GatedStore(IStore inner) : IStore
{
    /// <summary>Held open to suspend a claim mid-flight.</summary>
    public TaskCompletionSource? ClaimGate { get; set; }

    public async Task<bool> TryClaimAsync(
        long id,
        ProposalStatus from,
        ProposalStatus to,
        DateTimeOffset? decidedUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        if (ClaimGate is not null) await ClaimGate.Task.WaitAsync(cancellationToken);
        return await inner.TryClaimAsync(id, from, to, decidedUtc, error, cancellationToken);
    }

    public Task<Proposal> AddProposalAsync(Proposal proposal, CancellationToken cancellationToken) => inner.AddProposalAsync(proposal, cancellationToken);
    public Task<Proposal?> GetProposalAsync(long id, CancellationToken cancellationToken) => inner.GetProposalAsync(id, cancellationToken);
    public Task<IReadOnlyList<Proposal>> ListProposalsAsync(ProposalStatus? status, int limit, bool includeDismissed, CancellationToken cancellationToken) => inner.ListProposalsAsync(status, limit, includeDismissed, cancellationToken);
    /// <summary>Held open to suspend a dismissal between reading the row and writing it back.</summary>
    public TaskCompletionSource? DismissGate { get; set; }

    // Both routes a dismissal could take are gated, so a test that forces the interleaving keeps forcing it
    // even if someone puts the full-row write back -- which is exactly the regression it exists to catch.
    public async Task UpdateProposalAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        if (DismissGate is not null && proposal.DismissedUtc is not null) await DismissGate.Task.WaitAsync(cancellationToken);
        await inner.UpdateProposalAsync(proposal, cancellationToken);
    }

    public Task RecordOutcomeAsync(long id, ProposalStatus to, DateTimeOffset decidedUtc, string? haAutomationId, string? error, CancellationToken cancellationToken) =>
        inner.RecordOutcomeAsync(id, to, decidedUtc, haAutomationId, error, cancellationToken);

    public async Task<bool> SetDismissedAsync(long id, DateTimeOffset? dismissedUtc, CancellationToken cancellationToken)
    {
        if (DismissGate is not null) await DismissGate.Task.WaitAsync(cancellationToken);
        return await inner.SetDismissedAsync(id, dismissedUtc, cancellationToken);
    }
    public Task<int> PruneProposalsAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => inner.PruneProposalsAsync(beforeUtc, cancellationToken);
    public Task<int> AddSamplesAsync(IReadOnlyList<(string EntityId, StateSample Sample)> samples, CancellationToken cancellationToken) => inner.AddSamplesAsync(samples, cancellationToken);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestSampleTimesAsync(CancellationToken cancellationToken) => inner.GetLatestSampleTimesAsync(cancellationToken);
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetEarliestSampleTimesAsync(CancellationToken cancellationToken) => inner.GetEarliestSampleTimesAsync(cancellationToken);
    public Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesAsync(DateTimeOffset sinceUtc, int perEntity, CancellationToken cancellationToken) => inner.GetSamplesAsync(sinceUtc, perEntity, cancellationToken);
    public Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetNumericHistoryAsync(DateTimeOffset sinceUtc, int perBucket, int perEntity, CancellationToken cancellationToken) => inner.GetNumericHistoryAsync(sinceUtc, perBucket, perEntity, cancellationToken);
    public Task<int> PruneSamplesAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => inner.PruneSamplesAsync(beforeUtc, cancellationToken);
    public Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesForAsync(IReadOnlyCollection<string> entityIds, DateTimeOffset sinceUtc, int maxPerEntity, CancellationToken cancellationToken) => inner.GetSamplesForAsync(entityIds, sinceUtc, maxPerEntity, cancellationToken);
    public Task<IReadOnlyList<(string EntityId, StateSample Sample)>> ListRecentSamplesAsync(string? entityContains, int limit, CancellationToken cancellationToken) => inner.ListRecentSamplesAsync(entityContains, limit, cancellationToken);
    public Task<HistorySummary> GetHistorySummaryAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) => inner.GetHistorySummaryAsync(sinceUtc, cancellationToken);
    public Task<Anomaly> UpsertAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken) => inner.UpsertAnomalyAsync(anomaly, cancellationToken);
    public Task<Anomaly?> GetAnomalyAsync(long id, CancellationToken cancellationToken) => inner.GetAnomalyAsync(id, cancellationToken);
    public Task<Anomaly?> FindAnomalyAsync(string dedupKey, CancellationToken cancellationToken) => inner.FindAnomalyAsync(dedupKey, cancellationToken);
    public Task<IReadOnlyList<Anomaly>> ListAnomaliesAsync(AnomalyStatus? status, int limit, bool includeClosed, CancellationToken cancellationToken) => inner.ListAnomaliesAsync(status, limit, includeClosed, cancellationToken);
    public Task UpdateAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken) => inner.UpdateAnomalyAsync(anomaly, cancellationToken);
    public Task<int> PruneAnomaliesAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => inner.PruneAnomaliesAsync(beforeUtc, cancellationToken);
    public Task<Concern> AddConcernAsync(Concern concern, CancellationToken cancellationToken) => inner.AddConcernAsync(concern, cancellationToken);
    public Task<IReadOnlyList<Concern>> ListConcernsAsync(CancellationToken cancellationToken) => inner.ListConcernsAsync(cancellationToken);
    public Task<Concern?> GetConcernAsync(long id, CancellationToken cancellationToken) => inner.GetConcernAsync(id, cancellationToken);
    public Task UpdateConcernAsync(Concern concern, CancellationToken cancellationToken) => inner.UpdateConcernAsync(concern, cancellationToken);
    public Task<bool> DeleteConcernAsync(long id, CancellationToken cancellationToken) => inner.DeleteConcernAsync(id, cancellationToken);
}
