namespace HearthSense.Core;

/// <summary>
/// Home Assistant answered, but not usefully: a non-success status, an unreadable body, or a refused write.
/// Distinct from a transport failure so the API layer can report it as an upstream problem rather than a crash.
/// </summary>
public sealed class HomeAssistantException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything HearthSense needs from Home Assistant. Implemented over the REST API.</summary>
public interface IHomeAssistant
{
    /// <summary>Current state of every entity the token can see.</summary>
    Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken cancellationToken);

    /// <summary>Existing automations with the entities and trigger kinds they reference.</summary>
    Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(CancellationToken cancellationToken);

    /// <summary>Writes a new automation and returns the id it was stored under.</summary>
    Task<string> CreateAutomationAsync(string id, string configJson, CancellationToken cancellationToken);

    /// <summary>True when the base URL answers and the token is accepted.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken);
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
    HearthSenseOptions Current { get; }
}

/// <summary>Local persistence. Three tables: proposals, anomalies, and a compact state history.</summary>
public interface IStore
{
    Task<Proposal> AddProposalAsync(Proposal proposal, CancellationToken cancellationToken);
    Task<Proposal?> GetProposalAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Proposal>> ListProposalsAsync(ProposalStatus? status, int limit, CancellationToken cancellationToken);
    Task UpdateProposalAsync(Proposal proposal, CancellationToken cancellationToken);

    /// <summary>Appends state samples, ignoring ones already recorded. Returns the number inserted.</summary>
    Task<int> AddSamplesAsync(IReadOnlyList<(string EntityId, StateSample Sample)> samples, CancellationToken cancellationToken);

    /// <summary>Newest recorded change per entity, used to skip samples already stored.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestSampleTimesAsync(CancellationToken cancellationToken);

    /// <summary>All samples since a cutoff, grouped by entity and ordered oldest first. One query per scan.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetSamplesAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    Task<int> PruneSamplesAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken);

    Task<Anomaly?> GetAnomalyAsync(long id, CancellationToken cancellationToken);
    Task<Anomaly?> FindAnomalyAsync(string dedupKey, CancellationToken cancellationToken);
    Task<Anomaly> UpsertAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken);
    Task<IReadOnlyList<Anomaly>> ListAnomaliesAsync(AnomalyStatus? status, int limit, CancellationToken cancellationToken);
    Task UpdateAnomalyAsync(Anomaly anomaly, CancellationToken cancellationToken);
}
