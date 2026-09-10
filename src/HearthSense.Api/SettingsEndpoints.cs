using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HearthSense.Core;

namespace HearthSense.Api;

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
            var removed = context.Reset(body?.Section);
            return Results.Json(new { removed, settings = context.Describe() });
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

        api.MapPost("/test", async (
            TestRequest body,
            SettingsContext context,
            IHomeAssistant homeAssistant,
            ILlmClient llm,
            CancellationToken cancellationToken) =>
        {
            var target = body.Target?.Trim().ToLowerInvariant();
            var result = target switch
            {
                "homeassistant" => await context.TestHomeAssistantAsync(homeAssistant, cancellationToken).ConfigureAwait(false),
                "llm" or "model" => await TestLlmAsync(llm, cancellationToken).ConfigureAwait(false),
                _ => CheckResult.Fail("Ask for 'homeAssistant' or 'llm'."),
            };

            return Results.Json(new { ok = result.Ok, detail = result.Detail });
        })
        .WithSummary("Checks the saved Home Assistant or model settings actually work.");

        return endpoints;
    }

    private static async Task<CheckResult> TestLlmAsync(ILlmClient llm, CancellationToken cancellationToken)
    {
        try
        {
            return await llm.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CheckResult.Fail(ex.Message);
        }
    }

    public sealed record ResetRequest(string? Section);

    public sealed record TestRequest(string? Target);
}

/// <summary>Reads and writes settings. Everything the settings API needs, in one place it can be tested through.</summary>
public sealed class SettingsContext(
    IConfiguration configuration,
    InheritedConfiguration inherited,
    SettingsStore store,
    SettingsProvider provider,
    SecretStore secrets,
    BootSnapshot boot,
    ILogger<SettingsContext> logger)
{
    public sealed record SaveResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Changed, IReadOnlyList<string> RestartRequired);

    public object Describe()
    {
        var current = provider.Current;
        var inheritedOptions = SettingsBinder.Bind(inherited.Value, null);
        var defaults = new HearthSenseOptions();
        var validation = current.Validate();

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
            ready = !string.IsNullOrWhiteSpace(
                secrets.Resolve(SecretStore.HomeAssistantToken, current.HomeAssistant.TokenEnvironmentVariable)),
        };
    }

    private object Describe(
        SettingField field,
        HearthSenseOptions current,
        HearthSenseOptions inheritedOptions,
        HearthSenseOptions defaults)
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
            value = SettingsCatalog.Display(field, current),
            inherited = SettingsCatalog.Display(field, inheritedOptions),
            @default = SettingsCatalog.Display(field, defaults),
            overridden,
            source = overridden ? "ui" : inheritedSet ? "environment" : "default",
        };
    }

    private IReadOnlyList<string> RestartPending(HearthSenseOptions current) =>
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

        // Refuse to leave the service reachable from the network with no way to authenticate to it. An ingress
        // address is a way: it is what the Home Assistant add-on runs on, and nothing else gets in.
        if (!Addresses.IsLoopback(final.Api.BindAddress) &&
            string.IsNullOrWhiteSpace(final.Api.IngressAddress) &&
            string.IsNullOrWhiteSpace(secrets.Resolve(SecretStore.ApiToken, final.Api.TokenEnvironmentVariable)))
        {
            return new SaveResult(
                [$"Set an API token before binding to '{final.Api.BindAddress}'. Without one, anything that can reach this port could write automations to your home."],
                [], []);
        }

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

    public int Reset(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section) &&
            !SettingsCatalog.Sections.Any(candidate => string.Equals(candidate.Name, section, StringComparison.OrdinalIgnoreCase)))
            return 0;

        var removed = store.Reset(section);
        if (removed > 0)
        {
            Reload();
            logger.LogInformation("Settings reset: {Count} stored override(s) removed from {Section}.", removed, section ?? "all sections");
        }

        return removed;
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
                !Addresses.IsLoopback(current.Api.BindAddress) &&
                string.IsNullOrWhiteSpace(current.Api.IngressAddress) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(current.Api.TokenEnvironmentVariable ?? "")))
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

    public async Task<CheckResult> TestHomeAssistantAsync(IHomeAssistant homeAssistant, CancellationToken cancellationToken)
    {
        var current = provider.Current;
        if (string.IsNullOrWhiteSpace(secrets.Resolve(SecretStore.HomeAssistantToken, current.HomeAssistant.TokenEnvironmentVariable)))
            return CheckResult.Fail("No Home Assistant token is set. Add one below, save, then test again.");

        try
        {
            var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
            return CheckResult.Pass($"Connected. {entities.Count} entities visible to this token.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CheckResult.Fail(ex.Message);
        }
    }

    /// <summary>Binds the options that would result from applying <paramref name="changes"/> on top of what is stored.</summary>
    private HearthSenseOptions With(IReadOnlyDictionary<string, JsonNode?> changes)
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
