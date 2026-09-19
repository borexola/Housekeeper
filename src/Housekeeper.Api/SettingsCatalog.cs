using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Housekeeper.Core;

namespace Housekeeper.Api;

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
    Func<HousekeeperOptions, object?> Read,
    bool RestartRequired = false,
    string[]? Choices = null,

    /// <summary>
    /// Other spellings that mean one of the <see cref="Choices"/>, keyed by their letters-and-digits form.
    ///
    /// Configuration is deliberately looser than the dropdown -- "OpenAI-compatible" is a perfectly valid
    /// provider -- and the two have to agree exactly. Guessing at it with a prefix rule did not: it also
    /// mapped "openai-v1" onto "OpenAI", which validation still rejected, so the page dropped the user's
    /// value as "same as inherited" and then refused every other save over a provider nobody could correct.
    /// </summary>
    IReadOnlyDictionary<string, string>? Aliases = null,
    double? Minimum = null,
    double? Maximum = null,
    string? SecretName = null,
    Func<HousekeeperOptions, string?>? SecretVariable = null);

public sealed record SettingSection(string Name, string Title, string Description);

/// <summary>
/// Every setting Housekeeper has, in one list. The settings API and the settings page are both generated
/// from it, so adding an option here is all it takes for it to appear in the UI.
/// </summary>
public static class SettingsCatalog
{
    public static readonly SettingSection[] Sections =
    [
        new("HomeAssistant", "Home Assistant", "Where your Home Assistant lives and how Housekeeper talks to it."),
        new("Llm", "Model", "The model that drafts automations. Anything that reliably returns one JSON object works."),
        new("Scan", "Watching", "Background scanning. Nothing here notifies anyone; findings wait in a list for you to triage."),
        new("Api", "Service", "How this service is served. The address and port only take effect after a restart."),
        new("Storage", "Storage", "Where the database lives."),
        new("Secrets", "Secrets", "Stored in the data directory, never shown again, and used in preference to environment variables."),
    ];

    public static readonly SettingField[] Fields =
    [
        // ---- Home Assistant ----
        new("Housekeeper:HomeAssistant:BaseUrl", "HomeAssistant", "Base URL",
            "Your Home Assistant, e.g. http://homeassistant.local:8123.",
            SettingKind.Text, o => o.HomeAssistant.BaseUrl),

        new("Housekeeper:HomeAssistant:RequestTimeout", "HomeAssistant", "Request timeout",
            "How long to wait for Home Assistant to answer. Try 30s.",
            SettingKind.Duration, o => o.HomeAssistant.RequestTimeout),

        new("Housekeeper:HomeAssistant:CertificateFingerprint", "HomeAssistant", "Certificate fingerprint",
            "Only needed for an https Home Assistant using a certificate it signed itself. Paste the "
            + "certificate's SHA-256 fingerprint and that exact certificate is trusted; anything else is still "
            + "refused, so the admin token cannot end up somewhere else. Punctuation does not matter. Get it "
            + "with: openssl s_client -connect your-ha:8123 </dev/null | openssl x509 -noout -fingerprint -sha256",
            SettingKind.Text, o => o.HomeAssistant.CertificateFingerprint),

        new("Housekeeper:HomeAssistant:AcceptAnyCertificate", "HomeAssistant", "Accept any certificate",
            "Turns certificate checking off completely. Housekeeper would then send your Home Assistant admin "
            + "token to whatever answers at that address, with no way to tell it is the wrong machine. Use the "
            + "fingerprint above instead unless you have a reason not to.",
            SettingKind.Bool, o => o.HomeAssistant.AcceptAnyCertificate),

        new("Housekeeper:HomeAssistant:ResolveAreas", "HomeAssistant", "Resolve areas",
            "One extra templated call per entity fetch to learn which area and device each entity belongs to. Improves drafting and lets a finding be ignored by device; turn off if you use neither.",
            SettingKind.Bool, o => o.HomeAssistant.ResolveAreas),

        new("Housekeeper:HomeAssistant:ReadEntityRegistry", "HomeAssistant", "Read the entity registry",
            "One WebSocket call, held for half an hour, for the two things the state API does not carry: which "
            + "entities Home Assistant classes as settings or diagnostics, and which you have hidden. Both are "
            + "how findings about a plug's auto-off checkbox or a sensor's link quality stay out of your way. "
            + "Turn it off and those are recognised from device classes and naming instead, which is right most "
            + "of the time rather than always.",
            SettingKind.Bool, o => o.HomeAssistant.ReadEntityRegistry),

        new("Housekeeper:HomeAssistant:TokenEnvironmentVariable", "HomeAssistant", "Token environment variable",
            "Which environment variable holds the token, used when no token is stored below.",
            SettingKind.Text, o => o.HomeAssistant.TokenEnvironmentVariable),

        // ---- Model ----
        new("Housekeeper:Llm:Provider", "Llm", "Provider",
            "Ollama, or OpenAI for any OpenAI-compatible chat-completions endpoint.",
            SettingKind.Select, o => o.Llm.Provider, Choices: ["Ollama", "OpenAI"],
            Aliases: new Dictionary<string, string>(StringComparer.Ordinal) { ["openaicompatible"] = "OpenAI" }),

        new("Housekeeper:Llm:Endpoint", "Llm", "Endpoint",
            "Where the model server is: http://localhost:11434 for Ollama, or something like http://host:8080/v1 "
            + "for an OpenAI-style server. With or without the /v1 is fine.",
            SettingKind.Text, o => o.Llm.Endpoint),

        new("Housekeeper:Llm:Model", "Llm", "Model",
            "Leave empty to use whatever the server has loaded, which suits LM Studio, llama.cpp and the like. "
            + "Ollama needs a name; Test connection lists what it has.",
            SettingKind.Text, o => o.Llm.Model),

        new("Housekeeper:Llm:Timeout", "Llm", "Timeout",
            "A 7B model on CPU is slow, so this is generous on purpose. Try 90s.",
            SettingKind.Duration, o => o.Llm.Timeout),

        new("Housekeeper:Llm:Temperature", "Llm", "Temperature",
            "Lower is more predictable. Drafting wants near-zero.",
            SettingKind.Decimal, o => o.Llm.Temperature, Minimum: 0, Maximum: 2),

        new("Housekeeper:Llm:MaxOutputTokens", "Llm", "Max output tokens",
            "Upper bound on the drafted automation. A draft over 16 KB is rejected regardless.",
            SettingKind.Number, o => o.Llm.MaxOutputTokens, Minimum: 1, Maximum: 100_000),

        new("Housekeeper:Llm:MaxCandidateEntities", "Llm", "Entities shown to the model",
            "How many entities are shortlisted into the prompt. Raising it costs tokens and dilutes attention.",
            SettingKind.Number, o => o.Llm.MaxCandidateEntities, Minimum: 5, Maximum: 500),

        new("Housekeeper:Llm:MaxAttempts", "Llm", "Attempts per draft",
            "After a draft is rejected the model is told exactly what was wrong and asked again. Small models "
            + "usually get it on the second or third try; raising this trades time for a better hit rate.",
            SettingKind.Number, o => o.Llm.MaxAttempts, Minimum: 1, Maximum: 5),

        new("Housekeeper:Llm:ContextTokens", "Llm", "Context window",
            "How many tokens of context to ask Ollama for. Many small models default to 2048, which is smaller "
            + "than the instructions plus your entity list; Ollama then drops part of it without saying so. "
            + "Ignored by OpenAI-compatible servers, which decide this themselves.",
            SettingKind.Number, o => o.Llm.ContextTokens, Minimum: 2048, Maximum: 131072),

        new("Housekeeper:Llm:ApiKeyEnvironmentVariable", "Llm", "API key environment variable",
            "Which environment variable holds the API key, used when none is stored below. Ollama does not need one.",
            SettingKind.Text, o => o.Llm.ApiKeyEnvironmentVariable),

        // ---- Watching ----
        new("Housekeeper:Scan:Enabled", "Scan", "Scanning enabled",
            "Turn the background scan on or off. Takes effect on the next tick.",
            SettingKind.Bool, o => o.Scan.Enabled),

        new("Housekeeper:Scan:Interval", "Scan", "Scan every",
            "One call to Home Assistant per tick, regardless of house size.",
            SettingKind.Duration, o => o.Scan.Interval),

        new("Housekeeper:Scan:History", "Scan", "Keep history for",
            "How much state history to store and analyse. Older samples are pruned each scan.",
            SettingKind.Duration, o => o.Scan.History),

        new("Housekeeper:Scan:IncludeAll", "Scan", "Watch everything",
            "On by default. Observes every entity, so there is nothing to set up before Housekeeper starts "
            + "noticing things. Ignore below still applies — that is how you stop watching something without "
            + "having to list everything you do want. Turn this off to use the Watch list instead.",
            SettingKind.Bool, o => o.Scan.IncludeAll),

        new("Housekeeper:Scan:Include", "Scan", "Watch",
            "Entity id globs to observe, e.g. binary_sensor.*. Only used when Watch everything is off, and "
            + "then nothing is watched until you add one.",
            SettingKind.List, o => o.Scan.Include),

        new("Housekeeper:Scan:Exclude", "Scan", "Ignore",
            "Entity id globs to skip. Applied after Watch. The Ignore buttons on the dashboard add entries here; remove one to watch it again.",
            SettingKind.List, o => o.Scan.Exclude),

        new("Housekeeper:Scan:MaxTrackedEntities", "Scan", "Maximum tracked entities",
            "Hard cap, so a large install cannot fill the sample table. When it binds, the sun and the things a "
            + "routine is made of -- lights, switches, covers, locks, fans, media players, motion and door "
            + "sensors, people -- are kept first, then readings, then Home Assistant's own machinery.",
            SettingKind.Number, o => o.Scan.MaxTrackedEntities, Minimum: 1, Maximum: 100_000),

        new("Housekeeper:Scan:MinimumStuckDuration", "Scan", "Never report a stuck state below",
            "A floor under the stuck-state detector, however unusual the reading looks.",
            SettingKind.Duration, o => o.Scan.MinimumStuckDuration),

        new("Housekeeper:Scan:StuckMultiplier", "Scan", "Stuck multiplier",
            "How many times its own historical worst case a state must persist before it is reported.",
            SettingKind.Decimal, o => o.Scan.StuckMultiplier, Minimum: 1, Maximum: 100),

        new("Housekeeper:Scan:MinimumUnavailableDuration", "Scan", "Never report unavailable below",
            "A floor under the unavailable detector, so a brief blip stays quiet.",
            SettingKind.Duration, o => o.Scan.MinimumUnavailableDuration),

        new("Housekeeper:Scan:OutlierThreshold", "Scan", "Outlier threshold",
            "Robust z-score above which a numeric reading is reported. Lower means more findings.",
            SettingKind.Decimal, o => o.Scan.OutlierThreshold, Minimum: 1, Maximum: 20),

        new("Housekeeper:Scan:MinimumSamples", "Scan", "Minimum samples",
            "How many recorded changes the stuck-state and unavailable detectors need before they will say anything.",
            SettingKind.Number, o => o.Scan.MinimumSamples, Minimum: 2, Maximum: 10_000),

        new("Housekeeper:Scan:MinimumNumericSamples", "Scan", "Minimum readings for a number",
            "How many earlier readings a numeric baseline needs. Higher than the above on purpose: a spread "
            + "measured over a dozen readings that happen to sit close together collapses, and then everything "
            + "afterwards looks like an outlier.",
            SettingKind.Number, o => o.Scan.MinimumNumericSamples, Minimum: 2, Maximum: 10_000),

        new("Housekeeper:Scan:MinimumBaselineSpan", "Scan", "Minimum baseline span",
            "How far back a numeric baseline has to reach before a reading is judged against it. Readings are "
            + "only kept when something changes, so a busy sensor piles up hundreds within a few hours — and "
            + "a few hours does not know that nights are colder. Raise it to hear less on the first day.",
            SettingKind.Duration, o => o.Scan.MinimumBaselineSpan),

        new("Housekeeper:Scan:MinimumEffect", "Scan", "Minimum move",
            "The share of its own value a reading must move before it is worth mentioning, as a fraction: "
            + "0.15 is fifteen percent. Stops a disk going from 157.3 to 158.5 GiB being reported as unusual "
            + "just because it had been very steady. Per-device-class floors in watts, degrees and percent "
            + "apply too, whichever is larger.",
            SettingKind.Decimal, o => o.Scan.MinimumEffect, Minimum: 0, Maximum: 10),

        new("Housekeeper:Scan:GroupByDevice", "Scan", "One finding per device",
            "A smart plug reports power, current and energy, and a switched light is often both a light and a "
            + "switch entity, so one event becomes three or four identical cards. With this on, the strongest "
            + "one stands for the rest and names them.",
            SettingKind.Bool, o => o.Scan.GroupByDevice),

        new("Housekeeper:Scan:BackfillFromRecorder", "Scan", "Start from Home Assistant's history",
            "While an entity's stored history reaches back less than three weeks, read what Home Assistant's "
            + "recorder already holds for it, usually about ten days, so the detectors can judge from the first "
            + "scan rather than after a day or two of watching. A few hundred entities are read per scan, each "
            + "once per start. Readings are thinned on the way in, so the database stays the size it would have "
            + "grown to anyway.",
            SettingKind.Bool, o => o.Scan.BackfillFromRecorder),

        new("Housekeeper:Scan:RealtimeUpdates", "Scan", "Watch changes as they happen",
            "Keeps a WebSocket open to Home Assistant so changes are recorded the moment they occur, as well "
            + "as on every scan. Without it, a door opened and shut between two scans is never seen at all, "
            + "and how long things stay open or on is guesswork. Only non-numeric states are stored this way; "
            + "readings are still sampled on the scan, because storing every one would fill the database with "
            + "numbers nothing reads. Turning it off loses accuracy, never data.",
            SettingKind.Bool, o => o.Scan.RealtimeUpdates),

        new("Housekeeper:Scan:RedetectAfter", "Scan", "Dismissal stays quiet for",
            "How long a dismissed finding is suppressed before it may be raised again.",
            SettingKind.Duration, o => o.Scan.RedetectAfter),

        new("Housekeeper:Scan:MinimumExcursion", "Scan", "Out of range for at least",
            "How long a reading has to stay outside its usual range before it is reported. A kettle, a "
            + "microwave or a sensor glitch is a spike that is gone by the next scan, and each one used to be "
            + "a card that opened and closed within a minute. A sensor that reports rarely counts from its last "
            + "change, so a thermometer stuck high is not made to wait. Zero reports on the first reading.",
            SettingKind.Duration, o => o.Scan.MinimumExcursion),

        new("Housekeeper:Scan:LearnHabits", "Scan", "Learn routines",
            "Look through the stored history, once an hour, for things you do by hand at about the same time "
            + "or right after the same event, and offer to automate them. Whatever an automation already does "
            + "is left out. Nothing is created until you confirm a draft.",
            SettingKind.Bool, o => o.Scan.LearnHabits),

        new("Housekeeper:Scan:HabitMinimumTimes", "Scan", "Routine seen at least",
            "How many times something has to have happened, on at least three different days, before it is "
            + "offered as a routine.",
            SettingKind.Number, o => o.Scan.HabitMinimumTimes, Minimum: 2, Maximum: 1000),

        new("Housekeeper:Scan:HabitConfidence", "Scan", "Routine reliability",
            "How reliably a routine has to hold before it is offered, as a fraction: 0.6 means that six times "
            + "in ten, when the cue happened and the thing was not already on, you turned it on within a few "
            + "minutes. That is the share of the time an automation built from it would be doing what you "
            + "would have done anyway.",
            SettingKind.Decimal, o => o.Scan.HabitConfidence, Minimum: 0.1, Maximum: 1),

        // ---- Service ----
        new("Housekeeper:Api:BindAddress", "Api", "Bind address",
            "127.0.0.1 keeps Housekeeper on this machine. Anything else requires an API token.",
            SettingKind.Text, o => o.Api.BindAddress, RestartRequired: true),

        new("Housekeeper:Api:Port", "Api", "Port",
            "The port this service listens on.",
            SettingKind.Number, o => o.Api.Port, RestartRequired: true, Minimum: 1, Maximum: 65535),

        new("Housekeeper:Api:TokenEnvironmentVariable", "Api", "Token environment variable",
            "Which environment variable holds the API token, used when none is stored below.",
            SettingKind.Text, o => o.Api.TokenEnvironmentVariable),

        new("Housekeeper:Api:IngressAddress", "Api", "Trusted ingress address",
            "One IP address allowed in without a token, because something in front already authenticated the user. "
            + "The Home Assistant add-on sets this to the Supervisor, 172.30.32.2. Leave empty otherwise.",
            SettingKind.Text, o => o.Api.IngressAddress),

        new("Housekeeper:Api:AllowedHosts", "Api", "Extra host names",
            "Host names this instance answers to, besides localhost. Only needed when no API token is set and "
            + "something in front rewrites the Host header, such as your own reverse proxy. Requests addressed "
            + "to anything else are refused, which is what stops a web page from reaching your loopback install "
            + "through a domain that resolves to 127.0.0.1. One per line; * turns the check off.",
            SettingKind.List, o => o.Api.AllowedHosts),

        // ---- Storage ----
        new("Housekeeper:Storage:Path", "Storage", "Database path",
            "Where the SQLite database is written. The settings and secrets files stay in the data directory regardless.",
            SettingKind.Text, o => o.Storage.Path, RestartRequired: true),

        new("Housekeeper:Storage:KeepDecidedFor", "Storage", "Keep decided proposals and findings for",
            "Rejected, failed, superseded and removed proposals older than this are deleted on each scan, as are "
            + "dismissed and resolved findings older than the longer of this and the dismissal quiet period, so a "
            + "dismissal is never forgotten while it is still keeping something quiet. Drafts, live automations, "
            + "routines you put away and findings you dismissed three times are always kept.",
            SettingKind.Duration, o => o.Storage.KeepDecidedFor),

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
    public static JsonNode? Display(SettingField field, HousekeeperOptions options)
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
            SettingKind.Select => JsonValue.Create(Canonical(field, value?.ToString())),
            _ => JsonValue.Create(value?.ToString() ?? ""),
        };
    }

    /// <summary>
    /// A select's value spelled the way its own list of choices spells it.
    ///
    /// Configuration is looser than the dropdown: <c>openai</c>, <c>OpenAI-compatible</c> and <c>open ai</c>
    /// are all valid settings. Handing one of those to a &lt;select&gt; whose options are "Ollama" and
    /// "OpenAI" left it showing nothing, the page then read it back as empty, declared an unsaved change
    /// nobody made, and refused to save anything else on the page until it was resolved -- which Discard
    /// could not do either, because it wrote the same unmatched value back.
    /// </summary>
    private static string Canonical(SettingField field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || field.Choices is not { Length: > 0 } choices) return value ?? "";

        // Exact, then letters-and-digits-only — the same shape the options themselves are compared in, and
        // no looser. A "starts with" rule was tempting but disagreed with what is actually supported:
        // "openai-v1" canonicalised to "OpenAI" while validation still rejected it, so the page dropped the
        // user's explicit value as "same as inherited" and then refused every other save on the grounds of a
        // provider nobody could now correct.
        var squashed = Squashed(value);

        return choices.FirstOrDefault(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(choice => Squashed(choice) == squashed)
            ?? (field.Aliases is not null && field.Aliases.TryGetValue(squashed, out var alias) ? alias : null)
            ?? value;
    }

    /// <summary>Letters and digits only, lower-cased — the same shape the options themselves are compared in.</summary>
    private static string Squashed(string value) =>
        new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

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

/// <summary>Which callers Housekeeper will talk to without a bearer token.</summary>
public static class Addresses
{
    public static bool IsLoopback(string? address) =>
        !string.IsNullOrWhiteSpace(address) &&
        (address.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip)));

    /// <summary>
    /// Whether the Host header names somewhere this instance is genuinely served.
    ///
    /// This only matters where there is no token, and there it matters a great deal. A page on an
    /// attacker's domain whose DNS is flipped to 127.0.0.1 mid-visit becomes, as far as the browser is
    /// concerned, same-origin with a loopback install: it reports <c>Sec-Fetch-Site: same-origin</c>, the
    /// cross-site guard waves it through, and on loopback there is no token behind it. What gives it away
    /// is the Host header, which still carries the attacker's name — a real user on a loopback install
    /// always arrives as localhost or an IP loopback address.
    /// </summary>
    public static bool IsAllowedHost(string? host, ApiOptions api)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        foreach (var allowed in api.AllowedHosts)
        {
            var candidate = allowed.Trim();
            if (candidate == "*") return true;
            if (string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return IsLoopback(host) || string.Equals(host, api.BindAddress, StringComparison.OrdinalIgnoreCase);
    }

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
