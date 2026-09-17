using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// Builds the outward-facing clients over any settings and secrets, not only the live ones. The host uses
/// it for the real thing; the settings page uses it to test values that are typed but not yet saved; and a
/// test substitutes all of them, which is the reason nothing constructs one of these directly.
/// </summary>
public interface IAdapters
{
    IHomeAssistant HomeAssistant(ISettingsProvider settings, ISecretSource secrets);

    ILlmClient Llm(ISettingsProvider settings, ISecretSource secrets);

    /// <summary>The live state feed. Long-lived rather than per-request, so the host builds exactly one.</summary>
    IStateFeed StateFeed(ISettingsProvider settings, ISecretSource secrets);
}

public sealed class Adapters(IHttpClientFactory http, TimeProvider clock, ILoggerFactory logs) : IAdapters
{
    public IHomeAssistant HomeAssistant(ISettingsProvider settings, ISecretSource secrets) =>
        new HomeAssistantClient(
            http.CreateClient(HomeAssistantClient.HttpClientName), settings, secrets, clock, logs.CreateLogger<HomeAssistantClient>());

    public ILlmClient Llm(ISettingsProvider settings, ISecretSource secrets) =>
        new LlmClient(http.CreateClient(LlmClient.HttpClientName), settings, secrets, logs.CreateLogger<LlmClient>());

    public IStateFeed StateFeed(ISettingsProvider settings, ISecretSource secrets) =>
        new HomeAssistantEventStream(settings, secrets, logs.CreateLogger<HomeAssistantEventStream>());
}

/// <summary>Settings frozen at one value, for trying a configuration that has not been saved.</summary>
public sealed class FixedSettings(HousekeeperOptions options) : ISettingsProvider
{
    public HousekeeperOptions Current => options;
}

/// <summary>
/// Secrets typed but not yet saved, laid over the stored ones. An empty entry falls through.
///
/// <c>inner</c> is null when the caller has redirected the thing being tested at an address of their own
/// choosing. A stored credential belongs to the address it was saved for; falling through to it would hand
/// the user's Home Assistant admin token to whatever host the request named. In that case only a secret typed
/// into the same request is used, and if none was, the test is told there is no credential.
/// </summary>
public sealed class OverlaidSecrets(ISecretSource? inner, IReadOnlyDictionary<string, string?> typed) : ISecretSource
{
    public string? Resolve(string name, string? environmentVariableName) =>
        typed.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : inner?.Resolve(name, environmentVariableName);
}
