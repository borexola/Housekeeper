namespace Housekeeper.Core;

/// <summary>Root configuration, bound from the "Housekeeper" section of appsettings / env vars / a config file.</summary>
public sealed class HousekeeperOptions
{
    public const string SectionName = "Housekeeper";

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
        if (HomeAssistant.RequestTimeout > LlmOptions.LongestTimeout)
            errors.Add($"HomeAssistant.RequestTimeout must be {Durations.Format(LlmOptions.LongestTimeout)} or less.");
        if (!string.IsNullOrWhiteSpace(HomeAssistant.CertificateFingerprint) &&
            !Fingerprints.IsWellFormed(HomeAssistant.CertificateFingerprint))
            errors.Add("HomeAssistant.CertificateFingerprint must be a SHA-256 fingerprint: 64 hex digits, however punctuated.");
        if (HomeAssistant.AcceptAnyCertificate)
            warnings.Add(
                "HomeAssistant.AcceptAnyCertificate is on, so the Home Assistant admin token will be sent to "
                + "whatever answers at that address. Pin the certificate's fingerprint instead if you can.");

        if (!IsHttpUrl(Llm.Endpoint))
            errors.Add($"Llm.Endpoint '{Llm.Endpoint}' must be an absolute http(s) URL.");
        if (!LlmOptions.IsSupportedProvider(Llm.Provider))
            errors.Add($"Llm.Provider '{Llm.Provider}' is not supported. Use 'Ollama' or 'OpenAI'.");
        if (Llm.Timeout <= TimeSpan.Zero)
            errors.Add("Llm.Timeout must be greater than zero.");
        if (Llm.Timeout > LlmOptions.LongestTimeout)
            errors.Add($"Llm.Timeout must be {Durations.Format(LlmOptions.LongestTimeout)} or less.");
        if (Llm.Temperature is < 0 or > 2)
            errors.Add("Llm.Temperature must be between 0 and 2.");
        if (Llm.MaxCandidateEntities < 5)
            errors.Add("Llm.MaxCandidateEntities must be at least 5.");
        if (Llm.MaxAttempts is < 1 or > 5)
            errors.Add("Llm.MaxAttempts must be between 1 and 5.");
        if (Llm.ContextTokens < 2048)
            errors.Add("Llm.ContextTokens must be at least 2048; the instructions alone do not fit below that.");

        if (Scan.Interval < TimeSpan.FromSeconds(1))
            errors.Add("Scan.Interval must be at least one second.");
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
        if (Scan.MinimumNumericSamples < 2)
            errors.Add("Scan.MinimumNumericSamples must be at least 2.");
        if (Scan.MinimumBaselineSpan < TimeSpan.Zero)
            errors.Add("Scan.MinimumBaselineSpan must be zero or greater.");
        if (Scan.MinimumBaselineSpan > Scan.History)
            warnings.Add(
                $"Scan.MinimumBaselineSpan ({Durations.Format(Scan.MinimumBaselineSpan)}) is longer than the history "
                + $"kept ({Durations.Format(Scan.History)}), so no numeric reading can ever be judged.");
        if (Scan.MinimumEffect is < 0 or > 10)
            errors.Add("Scan.MinimumEffect must be between 0 and 10, as a fraction — 0.15 is fifteen percent.");
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
        if (Storage.KeepDecidedFor < TimeSpan.FromDays(1))
            errors.Add("Storage.KeepDecidedFor must be at least one day.");

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
    public string TokenEnvironmentVariable { get; set; } = "HOUSEKEEPER_HA_TOKEN";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// SHA-256 fingerprint of the certificate to trust, for a Home Assistant serving HTTPS with one it
    /// signed itself.
    ///
    /// Naming the certificate is the safe way to accept a self-signed one. Every request carries an admin
    /// token, so whatever answers at the configured address receives it; pinning means that something on
    /// the same network presenting its own certificate is still refused. Any punctuation is fine — colons,
    /// spaces, upper or lower case — only the hex digits are compared. Empty means ordinary validation.
    /// </summary>
    public string CertificateFingerprint { get; set; } = "";

    /// <summary>
    /// Turns certificate checking off altogether.
    ///
    /// The escape hatch, and genuinely a bad idea: with this on, Housekeeper sends the Home Assistant admin
    /// token to whatever answers at that address, and cannot tell that it is talking to the wrong machine.
    /// <see cref="CertificateFingerprint"/> solves the same problem without giving that away.
    /// </summary>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>Resolve each entity's area with one extra template call. Improves drafting; costs one request.</summary>
    public bool ResolveAreas { get; set; } = true;

    /// <summary>
    /// Read the entity registry, for the two facts that live only there: which entities Home Assistant
    /// classes as settings or diagnostics, and which the user has hidden.
    ///
    /// Both are how Housekeeper knows to stay quiet about a plug's auto-off checkbox or a sensor's link
    /// quality, and neither is in the state API — so this is the one call that uses the WebSocket API. Held
    /// for half an hour at a time. Turning it off falls back to recognising those from device classes,
    /// units and naming, which is right most of the time rather than always.
    /// </summary>
    public bool ReadEntityRegistry { get; set; } = true;
}

public sealed class LlmOptions
{
    /// <summary>"Ollama" or "OpenAI" (any OpenAI-compatible chat-completions endpoint).</summary>
    public string Provider { get; set; } = "Ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";
    /// <summary>
    /// Empty means "whatever the server has loaded", which an OpenAI-style server such as LM Studio or
    /// llama.cpp is happy with. Ollama needs a name; the settings page says so and lists what it has.
    /// </summary>
    public string Model { get; set; } = "";
    public string ApiKeyEnvironmentVariable { get; set; } = "HOUSEKEEPER_LLM_API_KEY";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);
    public double Temperature { get; set; } = 0.1;
    public int MaxOutputTokens { get; set; } = 1200;

    /// <summary>How many entities are shortlisted into the prompt. Keeps token cost bounded on large homes.</summary>
    public int MaxCandidateEntities { get; set; } = 40;

    /// <summary>
    /// How many times to ask before giving up. After a rejected draft the model is told exactly what the
    /// validator objected to and asked again, which is what lets a small model recover from its own mistakes.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// The longest any single request is allowed to take.
    ///
    /// Anything beyond this is not a timeout in any useful sense, and CancellationTokenSource.CancelAfter
    /// throws outright past about 49.7 days — which would fail every outbound call before a byte left the
    /// process, including the one that writes an automation.
    /// </summary>
    public static readonly TimeSpan LongestTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Context window to ask Ollama for, in tokens. Many community builds declare 2048 in their Modelfile,
    /// and Ollama silently discards whatever does not fit rather than complaining -- so the entity catalogue,
    /// the service list, or the rejection being fed back on a retry can vanish without a trace, and the model
    /// looks stupid for reasons that are not its fault. Sent only to Ollama; an OpenAI-compatible server
    /// decides this for itself.
    /// </summary>
    public int ContextTokens { get; set; } = 8192;

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

    /// <summary>
    /// How much state history to keep and analyse.
    ///
    /// Four weeks rather than two, because two weeks holds at most two of each weekday, and a house has a
    /// weekly rhythm as well as a daily one. Past three weeks the detectors judge a Saturday against other
    /// weekend days rather than against the working week.
    /// </summary>
    public TimeSpan History { get; set; } = TimeSpan.FromDays(28);

    /// <summary>
    /// Read what Home Assistant's own recorder already knows about an entity the first time it is watched.
    ///
    /// A fresh install used to start blind and stay quiet for a day or two while it learned what normal
    /// looked like, when Home Assistant had ten days of that answer on disk the whole time. Each scan
    /// backfills a batch of entities that have no stored history yet, so the first scan is already judging.
    /// Numeric readings are thinned on the way in, the same way the detectors thin them on the way out.
    /// </summary>
    public bool BackfillFromRecorder { get; set; } = true;

    /// <summary>
    /// Entity id globs to observe, e.g. <c>binary_sensor.*</c>. Ignored while <see cref="IncludeAll"/> is on,
    /// which it is by default; an empty list with IncludeAll off observes nothing at all.
    /// </summary>
    public List<string> Include { get; set; } = [];

    /// <summary>
    /// Entity id globs never to observe. Applied last, so it narrows <see cref="IncludeAll"/> just as it
    /// narrows <see cref="Include"/> — this is what the dashboard's Ignore buttons write to.
    /// </summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>
    /// Watch every entity rather than a chosen list. On by default: a new install that watches nothing looks
    /// broken, and picking globs before you know what is in your house is a poor first task. Narrow it with
    /// <see cref="Exclude"/>, or turn this off and list what you want in <see cref="Include"/>.
    /// </summary>
    public bool IncludeAll { get; set; } = true;

    /// <summary>Hard cap on observed entities, so a big install cannot blow up the sample table.</summary>
    public int MaxTrackedEntities { get; set; } = 500;

    /// <summary>A stuck state is never reported below this, however unusual it looks.</summary>
    public TimeSpan MinimumStuckDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How much longer than its historical worst case a state must persist before it is reported.</summary>
    public double StuckMultiplier { get; set; } = 3.0;

    public TimeSpan MinimumUnavailableDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Robust z-score above which a numeric reading is reported.</summary>
    public double OutlierThreshold { get; set; } = 4.0;

    /// <summary>Samples required before the stuck-state and unavailable detectors will speak at all.</summary>
    public int MinimumSamples { get; set; } = 12;

    /// <summary>
    /// Readings required before a numeric baseline is a baseline.
    ///
    /// Higher than <see cref="MinimumSamples"/> because a median absolute deviation is unstable on a short
    /// sample in a way a count of door openings is not: on a dozen readings that happen to be close together
    /// the spread collapses, and everything afterwards scores as an excursion. A real house produced findings
    /// judged over thirteen, sixteen and nineteen readings, every one of them noise.
    /// </summary>
    public int MinimumNumericSamples { get; set; } = 30;

    /// <summary>
    /// How long a numeric baseline has to reach back before it is allowed an opinion.
    ///
    /// The bar a sample count cannot express. Samples are kept only when an entity changes, so a busy sensor
    /// reaches any count within hours and is then judged against a fraction of a day — which knows nothing
    /// about nights, or about the dishwasher. Six hours is the shortest window that spans more than one of
    /// the four-hour bands the detectors use; past a day of uptime the time-of-day comparison takes over
    /// anyway, so this mostly governs how quiet the first day is.
    /// </summary>
    public TimeSpan MinimumBaselineSpan { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// The share of its own value a reading has to move before the move is worth mentioning, on top of
    /// clearing <see cref="OutlierThreshold"/>. Fifteen percent; per-device-class floors in real units
    /// (watts, degrees, percent) apply as well, whichever is larger.
    /// </summary>
    public double MinimumEffect { get; set; } = 0.15;

    /// <summary>
    /// Whether one device may occupy more than one card at a time.
    ///
    /// A smart plug reports power, current and energy, and a switched light is often both a
    /// <c>light.</c> and a <c>switch.</c> entity, so one event arrives as three or four identical findings.
    /// With this on, the most severe of them stands for the rest and names them.
    /// </summary>
    public bool GroupByDevice { get; set; } = true;

    /// <summary>
    /// Watch Home Assistant for state changes as they happen, as well as reading everything on a schedule.
    ///
    /// The scan caps at one sample per entity per <see cref="Interval"/>, so a door opened and shut between
    /// two polls is invisible and a motion sensor's real on-durations mostly are too. This catches those.
    /// It is an addition rather than a replacement: the scan keeps running, so a feed that drops or never
    /// connects costs only the transitions it would have seen, and the next poll closes the gap.
    ///
    /// Only non-numeric states are stored from it. Numeric readings are what the outlier detector thins to
    /// a couple an hour anyway, and they are every chatty entity in the house — storing those live would
    /// multiply the sample table for readings nothing would ever look at.
    /// </summary>
    public bool RealtimeUpdates { get; set; } = true;

    /// <summary>A dismissed anomaly stays quiet for this long before it may be raised again.</summary>
    public TimeSpan RedetectAfter { get; set; } = TimeSpan.FromDays(7);
}

public sealed class ApiOptions
{
    /// <summary>Loopback by default. Binding elsewhere requires a bearer token or startup fails.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5080;
    public string TokenEnvironmentVariable { get; set; } = "HOUSEKEEPER_API_TOKEN";

    /// <summary>
    /// One address whose requests skip the bearer token, for running behind Home Assistant ingress where
    /// Home Assistant has already authenticated the user. The Supervisor is <c>172.30.32.2</c>. Empty means
    /// no address is trusted, which is the right answer everywhere except inside an add-on.
    /// </summary>
    public string IngressAddress { get; set; } = "";

    /// <summary>
    /// Extra host names this instance answers to, for a reverse proxy that rewrites the Host header.
    ///
    /// On a loopback binding there is no token and so nothing else identifying a caller, which makes the
    /// Host header load-bearing: DNS rebinding turns a page on someone else's domain into a same-origin
    /// page against 127.0.0.1, and the browser's own cross-site signals then say it is fine. Requests whose
    /// Host is not a loopback name are refused for that reason. Anything listed here is accepted as well;
    /// <c>*</c> switches the check off. Only consulted while no token is required.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];
}

public sealed class StorageOptions
{
    public string Path { get; set; } = "housekeeper.db";

    /// <summary>
    /// How long a finished proposal is kept. Drafts and live automations are never pruned; this only
    /// bounds the history of what was rejected, failed, superseded or deleted.
    /// </summary>
    public TimeSpan KeepDecidedFor { get; set; } = TimeSpan.FromDays(30);
}
