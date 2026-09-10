namespace HearthSense.Core;

/// <summary>Root configuration, bound from the "HearthSense" section of appsettings / env vars / a config file.</summary>
public sealed class HearthSenseOptions
{
    public const string SectionName = "HearthSense";

    public HomeAssistantOptions HomeAssistant { get; set; } = new();
    public LlmOptions Llm { get; set; } = new();
    public ScanOptions Scan { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();

    public ValidationResult Validate()
    {
        List<string> errors = [];
        List<string> warnings = [];

        if (!IsHttpUrl(HomeAssistant.BaseUrl))
            errors.Add($"HomeAssistant.BaseUrl '{HomeAssistant.BaseUrl}' must be an absolute http(s) URL, e.g. http://homeassistant.local:8123.");
        if (string.IsNullOrWhiteSpace(HomeAssistant.TokenEnvironmentVariable))
            errors.Add("HomeAssistant.TokenEnvironmentVariable is required.");
        if (HomeAssistant.RequestTimeout <= TimeSpan.Zero)
            errors.Add("HomeAssistant.RequestTimeout must be greater than zero.");

        if (!IsHttpUrl(Llm.Endpoint))
            errors.Add($"Llm.Endpoint '{Llm.Endpoint}' must be an absolute http(s) URL.");
        if (string.IsNullOrWhiteSpace(Llm.Model))
            errors.Add("Llm.Model is required.");
        if (!LlmOptions.IsSupportedProvider(Llm.Provider))
            errors.Add($"Llm.Provider '{Llm.Provider}' is not supported. Use 'Ollama' or 'OpenAI'.");
        if (Llm.Timeout <= TimeSpan.Zero)
            errors.Add("Llm.Timeout must be greater than zero.");
        if (Llm.Temperature is < 0 or > 2)
            errors.Add("Llm.Temperature must be between 0 and 2.");
        if (Llm.MaxCandidateEntities < 5)
            errors.Add("Llm.MaxCandidateEntities must be at least 5.");

        if (Scan.Interval <= TimeSpan.Zero)
            errors.Add("Scan.Interval must be greater than zero.");
        if (Scan.History < TimeSpan.FromHours(1))
            errors.Add("Scan.History must be at least one hour.");
        if (Scan.MinimumStuckDuration <= TimeSpan.Zero)
            errors.Add("Scan.MinimumStuckDuration must be greater than zero.");
        if (Scan.MinimumUnavailableDuration <= TimeSpan.Zero)
            errors.Add("Scan.MinimumUnavailableDuration must be greater than zero.");
        if (Scan.RedetectAfter < TimeSpan.Zero)
            errors.Add("Scan.RedetectAfter must be zero or greater.");
        if (Scan.MaxTrackedEntities < 1)
            errors.Add("Scan.MaxTrackedEntities must be at least 1.");
        if (Scan.Enabled && Scan.Include.Count == 0 && !Scan.IncludeAll)
            warnings.Add("Scan.Include is empty and Scan.IncludeAll is false, so anomaly scanning will observe nothing.");

        if (Api.Port is < 1 or > 65535)
            errors.Add($"Api.Port '{Api.Port}' must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(Api.BindAddress))
            errors.Add("Api.BindAddress is required.");
        if (!string.IsNullOrWhiteSpace(Api.IngressAddress) && !System.Net.IPAddress.TryParse(Api.IngressAddress, out _))
            errors.Add($"Api.IngressAddress '{Api.IngressAddress}' must be a single IP address, or empty.");

        if (string.IsNullOrWhiteSpace(Storage.Path))
            errors.Add("Storage.Path is required.");

        return new ValidationResult(errors, warnings);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    public string Describe() => IsValid
        ? "Configuration is valid."
        : "Configuration errors:\n - " + string.Join("\n - ", Errors);
}

public sealed class HomeAssistantOptions
{
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    /// <summary>
    /// Environment variable holding a long-lived access token. Writing automations uses Home Assistant's
    /// config API, which requires the token of an <b>admin</b> user.
    /// </summary>
    public string TokenEnvironmentVariable { get; set; } = "HEARTHSENSE_HA_TOKEN";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Resolve each entity's area with one extra template call. Improves drafting; costs one request.</summary>
    public bool ResolveAreas { get; set; } = true;
}

public sealed class LlmOptions
{
    /// <summary>"Ollama" or "OpenAI" (any OpenAI-compatible chat-completions endpoint).</summary>
    public string Provider { get; set; } = "Ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b";
    public string ApiKeyEnvironmentVariable { get; set; } = "HEARTHSENSE_LLM_API_KEY";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);
    public double Temperature { get; set; } = 0.1;
    public int MaxOutputTokens { get; set; } = 1200;

    /// <summary>How many entities are shortlisted into the prompt. Keeps token cost bounded on large homes.</summary>
    public int MaxCandidateEntities { get; set; } = 40;

    public static bool IsSupportedProvider(string? provider) =>
        Normalize(provider) is "ollama" or "openai" or "openaicompatible";

    public bool IsOllama => Normalize(Provider) == "ollama";

    private static string Normalize(string? value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

public sealed class ScanOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How much state history to keep and analyse.</summary>
    public TimeSpan History { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Entity id globs to observe, e.g. <c>binary_sensor.*</c>. Empty plus IncludeAll=false observes nothing.</summary>
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public bool IncludeAll { get; set; }

    /// <summary>Hard cap on observed entities, so a big install cannot blow up the sample table.</summary>
    public int MaxTrackedEntities { get; set; } = 500;

    /// <summary>A stuck state is never reported below this, however unusual it looks.</summary>
    public TimeSpan MinimumStuckDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How much longer than its historical worst case a state must persist before it is reported.</summary>
    public double StuckMultiplier { get; set; } = 3.0;

    public TimeSpan MinimumUnavailableDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Robust z-score above which a numeric reading is reported.</summary>
    public double OutlierThreshold { get; set; } = 4.0;

    /// <summary>Samples required before a detector will speak at all.</summary>
    public int MinimumSamples { get; set; } = 12;

    /// <summary>A dismissed anomaly stays quiet for this long before it may be raised again.</summary>
    public TimeSpan RedetectAfter { get; set; } = TimeSpan.FromDays(7);
}

public sealed class ApiOptions
{
    /// <summary>Loopback by default. Binding elsewhere requires a bearer token or startup fails.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5080;
    public string TokenEnvironmentVariable { get; set; } = "HEARTHSENSE_API_TOKEN";

    /// <summary>
    /// One address whose requests skip the bearer token, for running behind Home Assistant ingress where
    /// Home Assistant has already authenticated the user. The Supervisor is <c>172.30.32.2</c>. Empty means
    /// no address is trusted, which is the right answer everywhere except inside an add-on.
    /// </summary>
    public string IngressAddress { get; set; } = "";
}

public sealed class StorageOptions
{
    public string Path { get; set; } = "hearthsense.db";
}
