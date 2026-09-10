using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HearthSense.Core;

namespace HearthSense.Api;

public enum SettingKind
{
    Text,
    Number,
    Decimal,
    Bool,
    Duration,
    List,
    Select,
    Secret,
}

/// <summary>One editable setting: what it is called, how to render it, and how to read its current value.</summary>
public sealed record SettingField(
    string Key,
    string Section,
    string Label,
    string Help,
    SettingKind Kind,
    Func<HearthSenseOptions, object?> Read,
    bool RestartRequired = false,
    string[]? Choices = null,
    double? Minimum = null,
    double? Maximum = null,
    string? SecretName = null,
    Func<HearthSenseOptions, string?>? SecretVariable = null);

public sealed record SettingSection(string Name, string Title, string Description);

/// <summary>
/// Every setting HearthSense has, in one list. The settings API and the settings page are both generated
/// from it, so adding an option here is all it takes for it to appear in the UI.
/// </summary>
public static class SettingsCatalog
{
    public static readonly SettingSection[] Sections =
    [
        new("HomeAssistant", "Home Assistant", "Where your Home Assistant lives and how HearthSense talks to it."),
        new("Llm", "Model", "The model that drafts automations. Anything that reliably returns one JSON object works."),
        new("Scan", "Watching", "Background scanning. Nothing here notifies anyone; findings wait in a list for you to triage."),
        new("Api", "Service", "How this service is served. The address and port only take effect after a restart."),
        new("Storage", "Storage", "Where the database lives."),
        new("Secrets", "Secrets", "Stored in the data directory, never shown again, and used in preference to environment variables."),
    ];

    public static readonly SettingField[] Fields =
    [
        // ---- Home Assistant ----
        new("HearthSense:HomeAssistant:BaseUrl", "HomeAssistant", "Base URL",
            "Your Home Assistant, e.g. http://homeassistant.local:8123.",
            SettingKind.Text, o => o.HomeAssistant.BaseUrl),

        new("HearthSense:HomeAssistant:RequestTimeout", "HomeAssistant", "Request timeout",
            "How long to wait for Home Assistant to answer. Try 30s.",
            SettingKind.Duration, o => o.HomeAssistant.RequestTimeout),

        new("HearthSense:HomeAssistant:ResolveAreas", "HomeAssistant", "Resolve areas",
            "One extra templated call per entity fetch to learn which area each entity is in. Improves drafting; turn off if you do not use areas.",
            SettingKind.Bool, o => o.HomeAssistant.ResolveAreas),

        new("HearthSense:HomeAssistant:TokenEnvironmentVariable", "HomeAssistant", "Token environment variable",
            "Which environment variable holds the token, used when no token is stored below.",
            SettingKind.Text, o => o.HomeAssistant.TokenEnvironmentVariable),

        // ---- Model ----
        new("HearthSense:Llm:Provider", "Llm", "Provider",
            "Ollama, or OpenAI for any OpenAI-compatible chat-completions endpoint.",
            SettingKind.Select, o => o.Llm.Provider, Choices: ["Ollama", "OpenAI"]),

        new("HearthSense:Llm:Endpoint", "Llm", "Endpoint",
            "Base URL of the model server, e.g. http://localhost:11434.",
            SettingKind.Text, o => o.Llm.Endpoint),

        new("HearthSense:Llm:Model", "Llm", "Model",
            "The model name, e.g. qwen2.5:7b. Pick one that follows JSON instructions well.",
            SettingKind.Text, o => o.Llm.Model),

        new("HearthSense:Llm:Timeout", "Llm", "Timeout",
            "A 7B model on CPU is slow, so this is generous on purpose. Try 90s.",
            SettingKind.Duration, o => o.Llm.Timeout),

        new("HearthSense:Llm:Temperature", "Llm", "Temperature",
            "Lower is more predictable. Drafting wants near-zero.",
            SettingKind.Decimal, o => o.Llm.Temperature, Minimum: 0, Maximum: 2),

        new("HearthSense:Llm:MaxOutputTokens", "Llm", "Max output tokens",
            "Upper bound on the drafted automation. A draft over 16 KB is rejected regardless.",
            SettingKind.Number, o => o.Llm.MaxOutputTokens, Minimum: 1, Maximum: 100_000),

        new("HearthSense:Llm:MaxCandidateEntities", "Llm", "Entities shown to the model",
            "How many entities are shortlisted into the prompt. Raising it costs tokens and dilutes attention.",
            SettingKind.Number, o => o.Llm.MaxCandidateEntities, Minimum: 5, Maximum: 500),

        new("HearthSense:Llm:ApiKeyEnvironmentVariable", "Llm", "API key environment variable",
            "Which environment variable holds the API key, used when none is stored below. Ollama does not need one.",
            SettingKind.Text, o => o.Llm.ApiKeyEnvironmentVariable),

        // ---- Watching ----
        new("HearthSense:Scan:Enabled", "Scan", "Scanning enabled",
            "Turn the background scan on or off. Takes effect on the next tick.",
            SettingKind.Bool, o => o.Scan.Enabled),

        new("HearthSense:Scan:Interval", "Scan", "Scan every",
            "One call to Home Assistant per tick, regardless of house size.",
            SettingKind.Duration, o => o.Scan.Interval),

        new("HearthSense:Scan:History", "Scan", "Keep history for",
            "How much state history to store and analyse. Older samples are pruned each scan.",
            SettingKind.Duration, o => o.Scan.History),

        new("HearthSense:Scan:IncludeAll", "Scan", "Watch everything",
            "Observe every entity instead of the list below. Ignores Watch, still honours Ignore.",
            SettingKind.Bool, o => o.Scan.IncludeAll),

        new("HearthSense:Scan:Include", "Scan", "Watch",
            "Entity id globs to observe, e.g. binary_sensor.* — nothing is watched until you add one.",
            SettingKind.List, o => o.Scan.Include),

        new("HearthSense:Scan:Exclude", "Scan", "Ignore",
            "Entity id globs to skip. Applied after Watch.",
            SettingKind.List, o => o.Scan.Exclude),

        new("HearthSense:Scan:MaxTrackedEntities", "Scan", "Maximum tracked entities",
            "Hard cap, so a large install cannot fill the sample table.",
            SettingKind.Number, o => o.Scan.MaxTrackedEntities, Minimum: 1, Maximum: 100_000),

        new("HearthSense:Scan:MinimumStuckDuration", "Scan", "Never report a stuck state below",
            "A floor under the stuck-state detector, however unusual the reading looks.",
            SettingKind.Duration, o => o.Scan.MinimumStuckDuration),

        new("HearthSense:Scan:StuckMultiplier", "Scan", "Stuck multiplier",
            "How many times its own historical worst case a state must persist before it is reported.",
            SettingKind.Decimal, o => o.Scan.StuckMultiplier, Minimum: 1, Maximum: 100),

        new("HearthSense:Scan:MinimumUnavailableDuration", "Scan", "Never report unavailable below",
            "A floor under the unavailable detector, so a brief blip stays quiet.",
            SettingKind.Duration, o => o.Scan.MinimumUnavailableDuration),

        new("HearthSense:Scan:OutlierThreshold", "Scan", "Outlier threshold",
            "Robust z-score above which a numeric reading is reported. Lower means more findings.",
            SettingKind.Decimal, o => o.Scan.OutlierThreshold, Minimum: 1, Maximum: 20),

        new("HearthSense:Scan:MinimumSamples", "Scan", "Minimum samples",
            "How much history a detector needs before it will say anything.",
            SettingKind.Number, o => o.Scan.MinimumSamples, Minimum: 2, Maximum: 10_000),

        new("HearthSense:Scan:RedetectAfter", "Scan", "Dismissal stays quiet for",
            "How long a dismissed finding is suppressed before it may be raised again.",
            SettingKind.Duration, o => o.Scan.RedetectAfter),

        // ---- Service ----
        new("HearthSense:Api:BindAddress", "Api", "Bind address",
            "127.0.0.1 keeps HearthSense on this machine. Anything else requires an API token.",
            SettingKind.Text, o => o.Api.BindAddress, RestartRequired: true),

        new("HearthSense:Api:Port", "Api", "Port",
            "The port this service listens on.",
            SettingKind.Number, o => o.Api.Port, RestartRequired: true, Minimum: 1, Maximum: 65535),

        new("HearthSense:Api:TokenEnvironmentVariable", "Api", "Token environment variable",
            "Which environment variable holds the API token, used when none is stored below.",
            SettingKind.Text, o => o.Api.TokenEnvironmentVariable),

        new("HearthSense:Api:IngressAddress", "Api", "Trusted ingress address",
            "One IP address allowed in without a token, because something in front already authenticated the user. "
            + "The Home Assistant add-on sets this to the Supervisor, 172.30.32.2. Leave empty otherwise.",
            SettingKind.Text, o => o.Api.IngressAddress),

        // ---- Storage ----
        new("HearthSense:Storage:Path", "Storage", "Database path",
            "Where the SQLite database is written. The settings and secrets files stay in the data directory regardless.",
            SettingKind.Text, o => o.Storage.Path, RestartRequired: true),

        // ---- Secrets ----
        new("Secrets:HomeAssistantToken", "Secrets", "Home Assistant token",
            "A long-lived access token from an admin user. Creating automations needs admin; nothing narrower exists.",
            SettingKind.Secret, _ => null,
            SecretName: SecretStore.HomeAssistantToken,
            SecretVariable: o => o.HomeAssistant.TokenEnvironmentVariable),

        new("Secrets:ApiToken", "Secrets", "API token",
            "Required when the bind address is not loopback. Generate one with: openssl rand -hex 32",
            SettingKind.Secret, _ => null,
            SecretName: SecretStore.ApiToken,
            SecretVariable: o => o.Api.TokenEnvironmentVariable),

        new("Secrets:LlmApiKey", "Secrets", "Model API key",
            "Sent as a bearer token to OpenAI-compatible endpoints. Ollama does not use one.",
            SettingKind.Secret, _ => null,
            SecretName: SecretStore.LlmApiKey,
            SecretVariable: o => o.Llm.ApiKeyEnvironmentVariable),
    ];

    public static SettingField? Find(string key) =>
        Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal));

    /// <summary>The value as the settings page should show it.</summary>
    public static JsonNode? Display(SettingField field, HearthSenseOptions options)
    {
        var value = field.Read(options);

        return field.Kind switch
        {
            SettingKind.Secret => null,
            SettingKind.Duration => JsonValue.Create(Durations.Format(value is TimeSpan span ? span : TimeSpan.Zero)),
            SettingKind.Bool => JsonValue.Create(value is true),
            SettingKind.Number => JsonValue.Create(Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture)),
            SettingKind.Decimal => JsonValue.Create(Convert.ToDouble(value ?? 0d, CultureInfo.InvariantCulture)),
            SettingKind.List => new JsonArray([.. (value as IEnumerable<string> ?? []).Select(item => (JsonNode?)JsonValue.Create(item))]),
            _ => JsonValue.Create(value?.ToString() ?? ""),
        };
    }

    /// <summary>
    /// Validates one submitted value and returns it in the form configuration expects. Durations are stored
    /// as TimeSpan text because that is what the binder understands, and shown back in the friendly form.
    /// </summary>
    public static bool TryConvert(SettingField field, JsonElement input, out JsonNode? stored, out string error)
    {
        stored = null;
        error = "";

        switch (field.Kind)
        {
            case SettingKind.Duration:
                {
                    if (input.ValueKind != JsonValueKind.String || !Durations.TryParse(input.GetString(), out var span))
                    {
                        error = $"{field.Label} must be a duration such as 45s, 10m, 2h or 14d.";
                        return false;
                    }

                    if (span < TimeSpan.Zero)
                    {
                        error = $"{field.Label} cannot be negative.";
                        return false;
                    }

                    stored = JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture));
                    return true;
                }

            case SettingKind.Bool:
                {
                    if (input.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        stored = JsonValue.Create(input.GetBoolean());
                        return true;
                    }

                    if (input.ValueKind == JsonValueKind.String && bool.TryParse(input.GetString(), out var parsed))
                    {
                        stored = JsonValue.Create(parsed);
                        return true;
                    }

                    error = $"{field.Label} must be true or false.";
                    return false;
                }

            case SettingKind.Number:
                {
                    if (!TryNumber(input, out var number) || number != Math.Floor(number))
                    {
                        error = $"{field.Label} must be a whole number.";
                        return false;
                    }

                    if (!InRange(field, number, out error)) return false;

                    stored = JsonValue.Create((long)number);
                    return true;
                }

            case SettingKind.Decimal:
                {
                    if (!TryNumber(input, out var number))
                    {
                        error = $"{field.Label} must be a number.";
                        return false;
                    }

                    if (!InRange(field, number, out error)) return false;

                    stored = JsonValue.Create(number);
                    return true;
                }

            case SettingKind.Select:
                {
                    var text = input.ValueKind == JsonValueKind.String ? input.GetString()?.Trim() : null;
                    var match = field.Choices?.FirstOrDefault(choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase));

                    if (match is null)
                    {
                        error = $"{field.Label} must be one of: {string.Join(", ", field.Choices ?? [])}.";
                        return false;
                    }

                    stored = JsonValue.Create(match);
                    return true;
                }

            case SettingKind.List:
                {
                    if (input.ValueKind != JsonValueKind.Array)
                    {
                        error = $"{field.Label} must be a list.";
                        return false;
                    }

                    JsonArray values = [];
                    foreach (var item in input.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            error = $"{field.Label} must be a list of text values.";
                            return false;
                        }

                        var text = item.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(text)) values.Add(text);
                    }

                    stored = values;
                    return true;
                }

            case SettingKind.Secret:
                error = "Secrets are set through the secrets endpoint.";
                return false;

            default:
                {
                    if (input.ValueKind != JsonValueKind.String)
                    {
                        error = $"{field.Label} must be text.";
                        return false;
                    }

                    stored = JsonValue.Create(input.GetString()?.Trim() ?? "");
                    return true;
                }
        }
    }

    private static bool TryNumber(JsonElement input, out double value)
    {
        switch (input.ValueKind)
        {
            case JsonValueKind.Number:
                value = input.GetDouble();
                return double.IsFinite(value);

            case JsonValueKind.String:
                return double.TryParse(input.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                       double.IsFinite(value);

            default:
                value = 0;
                return false;
        }
    }

    private static bool InRange(SettingField field, double value, out string error)
    {
        error = "";
        if (field.Minimum is { } min && value < min)
        {
            error = $"{field.Label} must be at least {min.ToString("0.##", CultureInfo.InvariantCulture)}.";
            return false;
        }

        if (field.Maximum is { } max && value > max)
        {
            error = $"{field.Label} must be at most {max.ToString("0.##", CultureInfo.InvariantCulture)}.";
            return false;
        }

        return true;
    }
}

/// <summary>Which callers HearthSense will talk to without a bearer token.</summary>
public static class Addresses
{
    public static bool IsLoopback(string? address) =>
        !string.IsNullOrWhiteSpace(address) &&
        (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip)));

    /// <summary>
    /// True when a request came from the one address configured as already-authenticated. Compared on the
    /// connection's own address, never on a header, so nothing a caller sends can claim to be ingress.
    /// </summary>
    public static bool IsTrustedIngress(IPAddress? remote, string? configured)
    {
        if (remote is null || string.IsNullOrWhiteSpace(configured)) return false;
        if (!IPAddress.TryParse(configured.Trim(), out var expected)) return false;

        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (expected.IsIPv4MappedToIPv6) expected = expected.MapToIPv4();

        return remote.Equals(expected);
    }
}
