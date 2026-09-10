using HearthSense.Core;

namespace HearthSense.Tests;

public sealed class FakeHomeAssistant : IHomeAssistant
{
    public List<HaEntity> Entities { get; } = [];
    public List<ExistingAutomation> Automations { get; } = [];
    public List<(string Id, string ConfigJson)> Created { get; } = [];

    public Exception? CreateFailure { get; set; }
    public Exception? AutomationsFailure { get; set; }
    public Exception? EntitiesFailure { get; set; }
    public bool Reachable { get; set; } = true;

    public Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken cancellationToken) =>
        EntitiesFailure is not null
            ? Task.FromException<IReadOnlyList<HaEntity>>(EntitiesFailure)
            : Task.FromResult<IReadOnlyList<HaEntity>>(Entities);

    public Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(CancellationToken cancellationToken) =>
        AutomationsFailure is not null
            ? Task.FromException<IReadOnlyList<ExistingAutomation>>(AutomationsFailure)
            : Task.FromResult<IReadOnlyList<ExistingAutomation>>(Automations);

    public Task<string> CreateAutomationAsync(string id, string configJson, CancellationToken cancellationToken)
    {
        if (CreateFailure is not null) return Task.FromException<string>(CreateFailure);

        Created.Add((id, configJson));
        return Task.FromResult(id);
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(Reachable);
}

public sealed class FakeLlm : ILlmClient
{
    public string Name => "fake-model";

    /// <summary>Raw text the model will "return". Null simulates an unreachable endpoint.</summary>
    public string? Response { get; set; }

    public string? LastSystemPrompt { get; private set; }
    public string? LastUserPrompt { get; private set; }
    public int Calls { get; private set; }

    public Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        Calls++;
        LastSystemPrompt = systemPrompt;
        LastUserPrompt = userPrompt;
        return Task.FromResult(Response);
    }

    public Task<CheckResult> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Reachable ? CheckResult.Pass("fake-model is available.") : CheckResult.Fail("fake-model is not reachable."));

    public bool Reachable { get; set; } = true;
}

/// <summary>Settings that can be changed mid-test, the way the settings UI changes them at runtime.</summary>
public sealed class FakeSettings(HearthSenseOptions? options = null) : ISettingsProvider
{
    public HearthSenseOptions Current { get; set; } = options ?? new HearthSenseOptions();
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
        string? area = null)
    {
        var changed = lastChanged ?? DateTimeOffset.UnixEpoch;
        return new HaEntity(entityId, state, changed, changed, friendlyName, deviceClass, unit, area);
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
            new HashSet<string>(triggerKinds ?? [], StringComparer.Ordinal));
}
