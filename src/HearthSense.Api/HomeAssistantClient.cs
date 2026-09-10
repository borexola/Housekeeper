using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HearthSense.Core;

namespace HearthSense.Api;

/// <summary>
/// Home Assistant over REST. Reads use the documented <c>/api/states</c> surface; writes use
/// <c>/api/config/automation/config/{id}</c>, which is the endpoint the automation editor itself calls.
/// That one is undocumented and needs an admin token — it is deliberately the only write in the app.
///
/// The address, timeout and token are read from the current settings on every request, so changing any of
/// them in the settings UI takes effect immediately rather than at the next restart.
/// </summary>
public sealed class HomeAssistantClient(
    HttpClient http,
    ISettingsProvider settings,
    SecretStore secrets,
    ILogger<HomeAssistantClient> logger) : IHomeAssistant
{
    public const string HttpClientName = "home-assistant";

    /// <summary>How many automation configs to read at once when describing what already exists.</summary>
    private const int ConfigReadConcurrency = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Response(HttpStatusCode Status, string Body)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Get, "api/", null, cancellationToken).ConfigureAwait(false);
            return response.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or HomeAssistantException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "api/states", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "read entity states");

        using var document = Parse(response.Body, "the entity list");
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        List<HaEntity> entities = [];
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var entity = ReadEntity(element);
            if (entity is not null) entities.Add(entity);
        }

        if (settings.Current.HomeAssistant.ResolveAreas)
        {
            var areas = await ResolveAreasAsync(cancellationToken).ConfigureAwait(false);
            if (areas.Count > 0)
                entities = [.. entities.Select(e => areas.TryGetValue(e.EntityId, out var area) && !string.IsNullOrWhiteSpace(area)
                    ? e with { Area = area }
                    : e)];
        }

        return entities;
    }

    public async Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(CancellationToken cancellationToken)
    {
        // The config id lives in the automation entity's attributes; without it there is nothing to fetch.
        var identified = await IdentifyAsync(cancellationToken).ConfigureAwait(false);
        List<ExistingAutomation> automations = [];

        using var gate = new SemaphoreSlim(ConfigReadConcurrency);
        var reads = identified.Select(async pair =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadAutomationAsync(pair.ConfigId, pair.EntityId, pair.Alias, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        foreach (var automation in await Task.WhenAll(reads).ConfigureAwait(false))
            if (automation is not null)
                automations.Add(automation);

        return automations;
    }

    public async Task<string> CreateAutomationAsync(string id, string configJson, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            $"api/config/automation/config/{Uri.EscapeDataString(id)}",
            configJson,
            cancellationToken).ConfigureAwait(false);

        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HomeAssistantException(
                "Home Assistant rejected the write. Creating automations needs a long-lived token belonging to an admin user.");

        EnsureSuccess(response, "create the automation");
        return id;
    }

    /// <summary>Lists automation entities together with the config id needed to read their definition.</summary>
    private async Task<IReadOnlyList<(string ConfigId, string EntityId, string Alias)>> IdentifyAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "api/states", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "read automations");

        using var document = Parse(response.Body, "the entity list");

        List<(string, string, string)> found = [];
        if (document.RootElement.ValueKind != JsonValueKind.Array) return found;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("entity_id", out var idElement)) continue;
            var entityId = idElement.GetString();
            if (entityId is null || Ha.DomainOf(entityId) != "automation") continue;

            if (!element.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object) continue;

            var configId = attributes.TryGetProperty("id", out var configIdElement)
                ? configIdElement.ValueKind == JsonValueKind.String ? configIdElement.GetString() : configIdElement.GetRawText().Trim('"')
                : null;

            if (string.IsNullOrWhiteSpace(configId)) continue;

            var alias = attributes.TryGetProperty("friendly_name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? entityId
                : entityId;

            found.Add((configId, entityId, alias));
        }

        return found;
    }

    private async Task<ExistingAutomation?> ReadAutomationAsync(
        string configId,
        string entityId,
        string alias,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(
                HttpMethod.Get,
                $"api/config/automation/config/{Uri.EscapeDataString(configId)}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                logger.LogDebug("Skipping automation {EntityId}: HTTP {Status}.", entityId, (int)response.Status);
                return null;
            }

            using var document = JsonDocument.Parse(response.Body);
            var (entities, triggerKinds) = AutomationInspector.Inspect(document.RootElement);

            var configAlias = document.RootElement.ValueKind == JsonValueKind.Object &&
                              document.RootElement.TryGetProperty("alias", out var aliasElement) &&
                              aliasElement.ValueKind == JsonValueKind.String
                ? aliasElement.GetString() ?? alias
                : alias;

            return new ExistingAutomation(configId, entityId, configAlias, entities, triggerKinds);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or HomeAssistantException)
        {
            // One unreadable automation costs duplicate detection against it, nothing more.
            logger.LogDebug(ex, "Skipping automation {EntityId}: could not read its config.", entityId);
            return null;
        }
    }

    /// <summary>
    /// Resolves entity areas in one templated call. Best effort: areas sharpen drafting but are not required,
    /// so any failure here degrades quality rather than breaking the request.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveAreasAsync(CancellationToken cancellationToken)
    {
        const string template =
            """[{% for s in states %}{"e": {{ s.entity_id | tojson }}, "a": {{ (area_name(s.entity_id) or "") | tojson }}}{{ "," if not loop.last }}{% endfor %}]""";

        Dictionary<string, string> areas = new(StringComparer.Ordinal);

        try
        {
            var response = await SendAsync(
                HttpMethod.Post,
                "api/template",
                JsonSerializer.Serialize(new { template }, Json),
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                logger.LogDebug("Area lookup returned HTTP {Status}; continuing without areas.", (int)response.Status);
                return areas;
            }

            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return areas;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("e", out var entity) || entity.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("a", out var area) || area.ValueKind != JsonValueKind.String) continue;

                var entityId = entity.GetString();
                var areaName = area.GetString();
                if (!string.IsNullOrWhiteSpace(entityId) && !string.IsNullOrWhiteSpace(areaName))
                    areas[entityId] = areaName;
            }

            return areas;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or HomeAssistantException)
        {
            logger.LogDebug(ex, "Area lookup failed; continuing without areas.");
            return areas;
        }
    }

    private async Task<Response> SendAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        var options = settings.Current.HomeAssistant;
        var timeoutAfter = options.RequestTimeout > TimeSpan.Zero ? options.RequestTimeout : TimeSpan.FromSeconds(30);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);

        using var request = new HttpRequestMessage(method, BuildUri(options.BaseUrl, path));

        var token = secrets.Resolve(SecretStore.HomeAssistantToken, options.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new Response(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HomeAssistantException(
                $"Home Assistant at {options.BaseUrl} did not answer within {Durations.Format(timeoutAfter)}.");
        }
    }

    private static Uri BuildUri(string? baseUrl, string path)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
            throw new HomeAssistantException(
                $"The Home Assistant address '{baseUrl}' is not a valid http or https URL. Correct it on the settings page.");

        return new Uri(root, path);
    }

    private static JsonDocument Parse(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new HomeAssistantException($"Home Assistant returned something that is not {what}.", ex);
        }
    }

    private static HaEntity? ReadEntity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("entity_id", out var idElement) || idElement.ValueKind != JsonValueKind.String) return null;

        var entityId = idElement.GetString();
        if (string.IsNullOrWhiteSpace(entityId) || !entityId.Contains('.', StringComparison.Ordinal)) return null;

        var state = element.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
            ? stateElement.GetString() ?? ""
            : "";

        var changed = ReadTime(element, "last_changed");
        var updated = ReadTime(element, "last_updated");

        string? friendlyName = null, deviceClass = null, unit = null;
        if (element.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            friendlyName = ReadAttribute(attributes, "friendly_name");
            deviceClass = ReadAttribute(attributes, "device_class");
            unit = ReadAttribute(attributes, "unit_of_measurement");
        }

        return new HaEntity(entityId, state, changed, updated, friendlyName, deviceClass, unit);
    }

    private static string? ReadAttribute(JsonElement attributes, string name) =>
        attributes.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset ReadTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static void EnsureSuccess(Response response, string what)
    {
        if (response.Ok) return;

        var detail = response.Body.Length > 300 ? response.Body[..300] + "…" : response.Body;

        throw new HomeAssistantException(
            $"Home Assistant returned HTTP {(int)response.Status} when asked to {what}. {detail}".TrimEnd());
    }
}
