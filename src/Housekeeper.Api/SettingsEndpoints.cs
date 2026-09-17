using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>The configuration as it stood when the process started, for spotting changes that need a restart.</summary>
public sealed class BootSnapshot(IReadOnlyDictionary<string, string> values, string dataDirectory)
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;
    public string DataDirectory { get; } = dataDirectory;
}

/// <summary>The configuration without the settings-UI layer, so each field can show what it would inherit.</summary>
public sealed record InheritedConfiguration(IConfiguration Value);

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/settings").WithTags("settings");

        api.MapGet("/", (SettingsContext context) => Results.Json(context.Describe()))
            .WithSummary("Every setting, its current value, where that value came from, and what needs a restart.");

        api.MapPut("/", async (
            Dictionary<string, JsonElement> body,
            SettingsContext context,
            CancellationToken cancellationToken) =>
        {
            _ = cancellationToken;
            var result = context.Save(body);

            return result.Errors.Count > 0
                ? Results.BadRequest(new { errors = result.Errors })
                : Results.Json(new
                {
                    changed = result.Changed,
                    restartRequired = result.RestartRequired,
                    settings = context.Describe(),
                });
        })
        .WithSummary("Saves settings. Only values that differ from what would be inherited are stored.");

        api.MapPost("/reset", (ResetRequest? body, SettingsContext context) =>
        {
            var result = context.Reset(body?.Section);

            return result.Errors.Count > 0
                ? Results.BadRequest(new { errors = result.Errors })
                : Results.Json(new { removed = result.Changed.Count, settings = context.Describe() });
        })
        .WithSummary("Drops the stored overrides for one section, or all of them, so values inherit again.");

        api.MapPut("/secrets", (Dictionary<string, string?> body, SettingsContext context) =>
        {
            var result = context.SaveSecrets(body);

            return result.Errors.Count > 0
                ? Results.BadRequest(new { errors = result.Errors })
                : Results.Json(new { changed = result.Changed, settings = context.Describe() });
        })
        .WithSummary("Stores or clears a secret. Values are never returned by any endpoint.");

        api.MapPost("/test", async (TestRequest body, SettingsContext context, CancellationToken cancellationToken) =>
        {
            var result = await context.TestAsync(body, cancellationToken).ConfigureAwait(false);
            return Results.Json(new { ok = result.Ok, detail = result.Detail });
        })
        .WithSummary("Checks Home Assistant or the model with the values given, saved or not, so a setting can be tried before it is kept.");

        return endpoints;
    }

    public sealed record ResetRequest(string? Section);

    /// <summary>What to test, plus any values and secrets typed on the page but not yet saved.</summary>
    public sealed record TestRequest(string? Target, Dictionary<string, JsonElement>? Values, Dictionary<string, string?>? Secrets);
}

/// <summary>Reads and writes settings. Everything the settings API needs, in one place it can be tested through.</summary>
public sealed class SettingsContext(
    IConfiguration configuration,
    InheritedConfiguration inherited,
    SettingsStore store,
    SettingsProvider provider,
    SecretStore secrets,
    BootSnapshot boot,
    IAdapters adapters,
    ILogger<SettingsContext> logger)
{
    public sealed record SaveResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Changed, IReadOnlyList<string> RestartRequired);

    /// <summary>
    /// Serialises saving. Every save reads the stored document, works out what to change, and writes it
    /// back; two at once both read the same starting point and the second overwrites the first.
    /// </summary>
    private static readonly Lock Saves = new();

    public object Describe()
    {
        var current = provider.Current;
        var inheritedOptions = SettingsBinder.Bind(inherited.Value, null);
        var defaults = new HousekeeperOptions();
        var validation = current.Validate();
        var missing = MissingFor(current, secrets);

        var sections = SettingsCatalog.Sections.Select(section => new
        {
            name = section.Name,
            title = section.Title,
            description = section.Description,
            fields = SettingsCatalog.Fields
                .Where(field => field.Section == section.Name)
                .Select(field => Describe(field, current, inheritedOptions, defaults))
                .ToArray(),
        });

        return new
        {
            sections,
            errors = validation.Errors,
            warnings = validation.Warnings,
            restartPending = RestartPending(current),
            dataDirectory = boot.DataDirectory,
            settingsFile = store.Path,
            ready = missing.Count == 0,
            missing,
        };
    }

    private object Describe(
        SettingField field,
        HousekeeperOptions current,
        HousekeeperOptions inheritedOptions,
        HousekeeperOptions defaults)
    {
        if (field.Kind == SettingKind.Secret)
        {
            var variable = field.SecretVariable?.Invoke(current);
            var source = secrets.SourceOf(field.SecretName!, variable);

            return new
            {
                key = field.Key,
                label = field.Label,
                help = field.Help,
                kind = "secret",
                restartRequired = false,
                configured = source != SecretSource.None,
                source = source switch
                {
                    SecretSource.Stored => "stored",
                    SecretSource.Environment => "environment",
                    _ => "unset",
                },
                variable,
            };
        }

        var overridden = store.Has(field.Key);
        var inheritedSet = inherited.Value[field.Key] is not null ||
                           inherited.Value.GetSection(field.Key).GetChildren().Any();

        return new
        {
            key = field.Key,
            label = field.Label,
            help = field.Help,
            kind = field.Kind.ToString().ToLowerInvariant(),
            choices = field.Choices,
            minimum = field.Minimum,
            maximum = field.Maximum,
            restartRequired = field.RestartRequired,
            // Set by the add-on and refused by Save. The page needs to know so it can show the field as
            // fixed rather than as an ordinary editable one whose every save is rejected.
            managed = Managed.IsPinned(field.Key),
            value = SettingsCatalog.Display(field, current),
            inherited = SettingsCatalog.Display(field, inheritedOptions),
            @default = SettingsCatalog.Display(field, defaults),
            overridden,
            source = overridden ? "ui" : inheritedSet ? "environment" : "default",
        };
    }

    private IReadOnlyList<string> RestartPending(HousekeeperOptions current) =>
    [
        .. SettingsCatalog.Fields
            .Where(field => field.RestartRequired)
            .Where(field => boot.Values.TryGetValue(field.Key, out var atBoot) &&
                            atBoot != SettingsCatalog.Display(field, current)?.ToJsonString())
            .Select(field => field.Label),
    ];

    public SaveResult Save(Dictionary<string, JsonElement> submitted)
    {
        List<string> errors = [];
        Dictionary<string, JsonNode?> converted = [];

        foreach (var (key, element) in submitted)
        {
            var field = SettingsCatalog.Find(key);
            if (field is null)
            {
                errors.Add($"'{key}' is not a setting.");
                continue;
            }

            if (field.Kind == SettingKind.Secret)
            {
                errors.Add($"{field.Label} is a secret; set it through the secrets endpoint.");
                continue;
            }

            if (!SettingsCatalog.TryConvert(field, element, out var stored, out var error)) errors.Add(error);
            else converted[key] = stored;
        }

        if (errors.Count > 0) return new SaveResult(errors, [], []);

        // Anything that matches what the key would inherit anyway is not worth storing: dropping it lets an
        // environment variable keep control of that key, and keeps the override file to what was really changed.
        var candidate = With(converted);
        var inheritedOptions = SettingsBinder.Bind(inherited.Value, null);

        foreach (var key in converted.Keys.ToList())
        {
            var field = SettingsCatalog.Find(key)!;
            var wanted = SettingsCatalog.Display(field, candidate);
            var wouldInherit = SettingsCatalog.Display(field, inheritedOptions);

            if (JsonNode.DeepEquals(wanted, wouldInherit)) converted[key] = null;
        }

        var final = With(converted);
        var validation = final.Validate();
        if (!validation.IsValid) return new SaveResult(validation.Errors, [], []);

        if (WouldLockUsOut(final) is { } locked) return new SaveResult([locked], [], []);

        // Saving one of these would be stored, ignored at startup, and shown as the value in force forever
        // after. Refusing says what is actually true.
        var managed = converted.Keys.Where(Managed.IsPinned).OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (managed.Count > 0)
            return new SaveResult(
                [.. managed.Select(key =>
                    $"'{SettingsCatalog.Find(key)?.Label ?? key}' is set by the Home Assistant add-on and cannot be changed here.")],
                [], []);

        var changed = store.Apply(converted);
        Reload();

        var restart = SettingsCatalog.Fields
            .Where(field => field.RestartRequired && changed.Contains(field.Key))
            .Select(field => field.Label)
            .ToList();

        if (changed.Count > 0)
            logger.LogInformation("Settings changed through the UI: {Keys}.", string.Join(", ", changed));

        return new SaveResult([], changed, restart);
    }

    /// <summary>Adds entity ids to Scan → Ignore, keeping whatever globs are already there.</summary>
    public SaveResult Ignore(IEnumerable<string> entityIds)
    {
        // Reading the list and writing it back is one operation, not two. Two people ignoring two different
        // entities at the same moment both read the same list and the second write silently dropped the
        // first entry -- and there is nothing on screen to suggest it did not work.
        lock (Saves)
        {
            var merged = provider.Current.Scan.Exclude.ToList();
            foreach (var entityId in entityIds)
                if (!merged.Contains(entityId, StringComparer.OrdinalIgnoreCase)) merged.Add(entityId);

            return Save(new Dictionary<string, JsonElement>
            {
                ["Housekeeper:Scan:Exclude"] = JsonSerializer.SerializeToElement(merged),
            });
        }
    }

    /// <summary>
    /// Refuses to leave the service reachable from the network with no way to authenticate to it. An ingress
    /// address counts as a way: it is what the Home Assistant add-on runs on, and nothing else gets in.
    /// Returns the reason, or null when the configuration is safe.
    /// </summary>
    private string? WouldLockUsOut(HousekeeperOptions candidate) =>
        NetworkBound(candidate) &&
        string.IsNullOrWhiteSpace(candidate.Api.IngressAddress) &&
        string.IsNullOrWhiteSpace(secrets.Resolve(SecretStore.ApiToken, candidate.Api.TokenEnvironmentVariable))
            ? $"Set an API token before binding to '{candidate.Api.BindAddress}'. Without one, anything that can "
              + "reach this port could write automations to your home."
            : null;

    /// <summary>
    /// Drops stored overrides so their values inherit again.
    ///
    /// Removing an override changes what is in force exactly as much as writing one does, so it goes through
    /// the same guards. It did not, and the settings page puts "Reset this section" one click away on every
    /// section — so a single click could drop the bind address back to an inherited network value with no
    /// token, or drop the ingress address the add-on is reached through, with nothing to say it had.
    /// </summary>
    public SaveResult Reset(string? section)
    {
        // Matched case-insensitively here but looked up case-sensitively in the stored document, so "scan"
        // reported a successful reset while removing nothing. The catalog's own spelling is what is used.
        if (!string.IsNullOrWhiteSpace(section))
        {
            section = SettingsCatalog.Sections
                .FirstOrDefault(candidate => string.Equals(candidate.Name, section, StringComparison.OrdinalIgnoreCase))
                ?.Name;

            if (section is null) return new SaveResult([], [], []);
        }

        lock (Saves)
        {
            var candidate = Without(section);

            var validation = candidate.Validate();
            if (!validation.IsValid) return new SaveResult(validation.Errors, [], []);
            if (WouldLockUsOut(candidate) is { } locked) return new SaveResult([locked], [], []);

            var removed = store.Reset(section);
            if (removed > 0)
            {
                Reload();
                logger.LogInformation("Settings reset: {Count} stored override(s) removed from {Section}.", removed, section ?? "all sections");
            }

            return new SaveResult([], [.. Enumerable.Repeat(section ?? "all", removed)], []);
        }
    }

    /// <summary>The options that would be in force with one section's stored overrides — or all of them — gone.</summary>
    private HousekeeperOptions Without(string? section)
    {
        Dictionary<string, JsonNode?> cleared = [];
        foreach (var field in SettingsCatalog.Fields)
            if (field.Kind != SettingKind.Secret &&
                store.Has(field.Key) &&
                (section is null || string.Equals(field.Section, section, StringComparison.OrdinalIgnoreCase)))
                cleared[field.Key] = null;

        return With(cleared);
    }

    public SaveResult SaveSecrets(Dictionary<string, string?> submitted)
    {
        List<string> errors = [];
        List<string> changed = [];
        var current = provider.Current;

        foreach (var (name, value) in submitted)
        {
            var field = SettingsCatalog.Fields.FirstOrDefault(candidate => candidate.SecretName == name);
            if (field is null)
            {
                errors.Add($"'{name}' is not a secret.");
                continue;
            }

            // Clearing the API token while listening off-loopback would lock everyone out, including this UI.
            if (name == SecretStore.ApiToken &&
                string.IsNullOrWhiteSpace(value) &&
                NetworkBound(current) &&
                string.IsNullOrWhiteSpace(current.Api.IngressAddress) &&
                string.IsNullOrWhiteSpace(secrets.WithoutStored(current.Api.TokenEnvironmentVariable)))
            {
                errors.Add("The API token cannot be cleared while the service is bound to a network address.");
            }
        }

        if (errors.Count > 0) return new SaveResult(errors, [], []);

        foreach (var (name, value) in submitted)
            if (secrets.Set(name, value))
                changed.Add(name);

        if (changed.Count > 0) logger.LogInformation("Stored secrets updated: {Names}.", string.Join(", ", changed));

        return new SaveResult([], changed, []);
    }

    /// <summary>What still stands between this install and a first draft, in words for a banner.</summary>
    public static IReadOnlyList<string> MissingFor(HousekeeperOptions options, ISecretSource secrets)
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(secrets.Resolve(SecretStore.HomeAssistantToken, options.HomeAssistant.TokenEnvironmentVariable)))
            missing.Add("a Home Assistant token");

        if (options.Llm.IsOllama && string.IsNullOrWhiteSpace(options.Llm.Model))
            missing.Add("a model name (Ollama needs one)");

        return missing;
    }

    /// <summary>
    /// Tries a connection with the values on the page rather than the values on disk, so a setting can be
    /// checked before it is saved. Anything not supplied falls back to what is stored. Nothing is written.
    /// </summary>
    public async Task<CheckResult> TestAsync(SettingsEndpoints.TestRequest request, CancellationToken cancellationToken)
    {
        Dictionary<string, JsonNode?> converted = [];
        foreach (var (key, element) in request.Values ?? [])
        {
            var field = SettingsCatalog.Find(key);
            if (field is null || field.Kind == SettingKind.Secret) continue;
            if (!SettingsCatalog.TryConvert(field, element, out var stored, out var error)) return CheckResult.Fail(error);
            converted[key] = stored;
        }

        var options = With(converted);
        var candidate = new FixedSettings(options);
        var target = request.Target?.Trim().ToLowerInvariant();

        // A stored credential belongs to the address it was saved against. If this request aims the test at a
        // different host, the stored one is withheld and only a secret typed into the same request is used --
        // otherwise "test" would be a way to make the server post its Home Assistant admin token anywhere.
        var saved = provider.Current;
        var redirected = target switch
        {
            "homeassistant" => !SameHost(options.HomeAssistant.BaseUrl, saved.HomeAssistant.BaseUrl),
            "llm" or "model" => !SameHost(options.Llm.Endpoint, saved.Llm.Endpoint),
            _ => false,
        };

        var typed = new OverlaidSecrets(
            redirected ? null : secrets,
            new Dictionary<string, string?>(request.Secrets ?? [], StringComparer.Ordinal));

        try
        {
            switch (target)
            {
                case "homeassistant":
                    if (string.IsNullOrWhiteSpace(typed.Resolve(SecretStore.HomeAssistantToken, candidate.Current.HomeAssistant.TokenEnvironmentVariable)))
                        return CheckResult.Fail(redirected
                            ? "That is a different Home Assistant to the saved one, so the stored token is not sent to it. "
                              + "Type a token above to test this address."
                            : "No Home Assistant token is set. Add one under Secrets and test again.");

                    var entities = await adapters.HomeAssistant(candidate, typed).GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
                    return CheckResult.Pass($"Connected. {entities.Count} entities visible to this token.");

                case "llm":
                case "model":
                    return await adapters.Llm(candidate, typed).CheckAsync(cancellationToken).ConfigureAwait(false);

                default:
                    return CheckResult.Fail("Ask for 'homeAssistant' or 'llm'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CheckResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Whether this instance is reachable from the network -- judged on the address it is actually listening
    /// on as well as the one that has been typed in.
    ///
    /// The bind address is marked as needing a restart, so a pending change to loopback does not move Kestrel.
    /// Reading only the pending value let a single save switch the setting to 127.0.0.1 and, in the same
    /// breath, clear the token still protecting a socket open to the whole network.
    /// </summary>
    private bool NetworkBound(HousekeeperOptions options) =>
        !Addresses.IsLoopback(options.Api.BindAddress) ||
        !Addresses.IsLoopback(BootValue("Housekeeper:Api:BindAddress") ?? options.Api.BindAddress);

    /// <summary>A value as it was when the process started. Stored as JSON, so it comes back out the same way.</summary>
    private string? BootValue(string key)
    {
        if (!boot.Values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            return JsonNode.Parse(raw) is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether two configured addresses point at the same host, port and scheme.</summary>
    private static bool SameHost(string? left, string? right)
    {
        var a = (left ?? "").Trim();
        var b = (right ?? "").Trim();

        // Text that was not changed names the same place, whether or not it happens to parse.
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        if (!Uri.TryCreate(a, UriKind.Absolute, out var one)) return false;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var two)) return false;

        return string.Equals(one.Host, two.Host, StringComparison.OrdinalIgnoreCase)
            && one.Port == two.Port
            && string.Equals(one.Scheme, two.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Binds the options that would result from applying <paramref name="changes"/> on top of what is stored.</summary>
    private HousekeeperOptions With(IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var document = (JsonObject)store.Overrides.DeepClone();

        foreach (var (key, value) in changes)
        {
            var segments = key.Split(':');
            var branch = document;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (branch[segments[i]] is not JsonObject next)
                {
                    next = [];
                    branch[segments[i]] = next;
                }

                branch = next;
            }

            if (value is null) branch.Remove(segments[^1]);
            else branch[segments[^1]] = value.DeepClone();
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        var candidate = new ConfigurationBuilder()
            .AddConfiguration(inherited.Value)
            .AddJsonStream(stream)
            .Build();

        return SettingsBinder.Bind(candidate, document);
    }

    private void Reload()
    {
        if (configuration is IConfigurationRoot root) root.Reload();
        provider.Reload();
    }
}
