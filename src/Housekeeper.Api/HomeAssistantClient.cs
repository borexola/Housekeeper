using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Api;

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
    ISecretSource secrets,
    TimeProvider clock,
    ILogger<HomeAssistantClient> logger) : IHomeAssistant
{
    public const string HttpClientName = "home-assistant";

    /// <summary>How many automation configs to read at once when describing what already exists.</summary>
    private const int ConfigReadConcurrency = 6;

    /// <summary>
    /// How long the automation configs and the service list are trusted before being re-read. Both change
    /// rarely, and re-reading a hundred automation configs on every draft was the slowest step after the
    /// model itself. Automations added or removed are noticed at once regardless, because the automations
    /// cache is keyed on the set of ids.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly Cached<IReadOnlyList<ExistingAutomation>> _automations = new(clock, CacheFor);
    private readonly Cached<IReadOnlySet<string>> _services = new(clock, CacheFor);

    /// <summary>
    /// The entity registry, held longer than the rest. It changes when a device is added or renamed, and a
    /// scan asks for it every minute; re-reading that over a fresh WebSocket each time would be the most
    /// expensive thing in a scan, for an answer that is the same all day.
    /// </summary>
    private readonly Cached<IReadOnlyDictionary<string, RegistryEntity>> _categories =
        new(clock, TimeSpan.FromMinutes(30));

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

    /// <summary>The zone last read, from which address, and when. Asked for every hour by the routine search; it changes about never.</summary>
    private (string Zone, string BaseUrl, DateTimeOffset At)? _timeZone;

    private static readonly TimeSpan TimeZoneFreshFor = TimeSpan.FromHours(6);

    public async Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var baseUrl = settings.Current.HomeAssistant.BaseUrl ?? "";
        if (_timeZone is { } held && held.BaseUrl == baseUrl && now - held.At < TimeZoneFreshFor) return held.Zone;

        string? zone = null;
        try
        {
            var response = await SendAsync(HttpMethod.Get, "api/config", null, cancellationToken).ConfigureAwait(false);
            if (response.Ok)
            {
                using var document = Parse(response.Body, "the configuration");
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("time_zone", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    zone = value.GetString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or HomeAssistantException)
        {
            logger.LogDebug(ex, "Could not read Home Assistant's time zone.");
        }

        // A failure is not cached. Standing on a null for six hours meant six hours of routines worked out
        // in UTC, the container's own zone, for a house that is not in it.
        if (!string.IsNullOrWhiteSpace(zone)) _timeZone = (zone, baseUrl, now);
        return zone;
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
            var registry = await ResolveRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (registry.Count > 0)
                entities = [.. entities.Select(e => registry.TryGetValue(e.EntityId, out var found)
                    ? e with { Area = found.Area ?? e.Area, DeviceId = found.DeviceId, DeviceName = found.DeviceName }
                    : e)];
        }

        if (settings.Current.HomeAssistant.ReadEntityRegistry)
        {
            var categories = await EntityCategoriesAsync(cancellationToken).ConfigureAwait(false);
            if (categories.Count > 0)
                entities = [.. entities.Select(e => categories.TryGetValue(e.EntityId, out var found)
                    ? e with { EntityCategory = found.EntityCategory, Hidden = found.Hidden }
                    : e)];
        }

        return entities;
    }

    /// <summary>
    /// The entity registry's view of every entity: which are settings, which are instrument readings, and
    /// which the user has hidden. Cached, because it changes when someone adds a device and a scan asks for
    /// it every minute — and best effort, because everything it improves has a fallback.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, RegistryEntity>> EntityCategoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _categories
                .GetAsync(settings.Current.HomeAssistant.BaseUrl ?? "", () => ReadEntityRegistryAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing here is load-bearing: without it the detectors fall back to recognising a diagnostic
            // entity by its device class, unit and name, which is what they did before this existed.
            logger.LogDebug(ex, "Could not read the entity registry; continuing without entity categories.");
            return ReadOnlyDictionary<string, RegistryEntity>.Empty;
        }
    }

    private async Task<IReadOnlyDictionary<string, RegistryEntity>> ReadEntityRegistryAsync(CancellationToken cancellationToken)
    {
        var options = settings.Current.HomeAssistant;
        var timeoutAfter = options.RequestTimeout > TimeSpan.Zero ? options.RequestTimeout : TimeSpan.FromSeconds(30);
        if (timeoutAfter > LlmOptions.LongestTimeout) timeoutAfter = LlmOptions.LongestTimeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);

        using var socket = new ClientWebSocket();

        // The same certificate rules as every other call. This one carries the admin token too, so a socket
        // that trusted anything would hand it to whatever answered -- exactly what the pin exists to stop.
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            Certificates.Accept(certificate, errors, options.CertificateFingerprint, options.AcceptAnyCertificate);

        var registry = await EntityRegistry.ReadAsync(
            socket,
            EntityRegistry.WebSocketUri(options.BaseUrl),
            secrets.Resolve(SecretStore.HomeAssistantToken, options.TokenEnvironmentVariable),
            timeout.Token).ConfigureAwait(false);

        logger.LogDebug("Entity registry read: {Count} entities.", registry.Count);
        return registry;
    }

    public Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(
        IReadOnlyList<HaEntity> entities,
        CancellationToken cancellationToken)
    {
        // The caller already fetched every state; the automation entities in it carry their config ids.
        var identified = entities
            .Where(entity => entity.AutomationConfigId is not null)
            .Select(entity => (ConfigId: entity.AutomationConfigId!, entity.EntityId, Alias: entity.FriendlyName ?? entity.EntityId))
            .ToList();

        var key = string.Join("\n", identified.Select(pair => pair.ConfigId).Order(StringComparer.Ordinal));
        return _automations.GetAsync(key, () => ReadAllAsync(identified, entities, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<ExistingAutomation>> ReadAllAsync(
        List<(string ConfigId, string EntityId, string Alias)> identified,
        IReadOnlyList<HaEntity> entities,
        CancellationToken cancellationToken)
    {
        List<ExistingAutomation> automations = [];

        using var gate = new SemaphoreSlim(ConfigReadConcurrency);
        var reads = identified.Select(async pair =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadAutomationAsync(pair.ConfigId, pair.EntityId, pair.Alias, entities, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        var refused = 0;
        foreach (var read in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            if (read.Automation is not null) automations.Add(read.Automation);
            if (read.Refused) refused++;
        }

        // One unreadable automation costs duplicate detection against that automation. Every one of them
        // unreadable means something systematic -- most often a token that is not an admin's, since this
        // endpoint requires one -- and returning an empty list for that says "this house has no automations",
        // which is cached as fact for five minutes and quietly turns duplicate detection off. Saying so out
        // loud instead means nothing is cached and the drafting log explains why the check did not run.
        // Only a refusal is worth raising. A 404 here is ordinary and expected: the config endpoint serves
        // the automations Home Assistant's own editor stores, so one written by hand in configuration.yaml
        // answers 404 forever. Blaming the token for that told a house full of YAML automations to fix a
        // problem it did not have, and refusing to cache the result meant every draft retried every one.
        if (identified.Count > 0 && automations.Count == 0 && refused > 0)
            throw new HomeAssistantException(
                $"None of the {identified.Count} automations in Home Assistant could be read. Reading automation "
                + "configs needs a long-lived token belonging to an admin user; without one, Housekeeper cannot "
                + "tell you when a draft duplicates something you already have.");

        return automations;
    }

    public Task<IReadOnlySet<string>> GetServicesAsync(CancellationToken cancellationToken) =>
        // Keyed on the address it came from. Without that, pointing Housekeeper at a different Home
        // Assistant left drafting validating against the previous one's services for five minutes -- and
        // the client is a singleton, so the stale answer outlived any amount of settings editing.
        _services.GetAsync(settings.Current.HomeAssistant.BaseUrl ?? "", () => ReadServicesAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlySet<string>> ReadServicesAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, "api/services", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "read the service list");

        using var document = Parse(response.Body, "the service list");

        // [{ "domain": "light", "services": { "turn_on": {...}, "turn_off": {...} } }]
        HashSet<string> services = new(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return services;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("domain", out var domain) || domain.ValueKind != JsonValueKind.String) continue;
            if (!entry.TryGetProperty("services", out var offered) || offered.ValueKind != JsonValueKind.Object) continue;

            var name = domain.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            foreach (var service in offered.EnumerateObject())
                services.Add(name + "." + service.Name);
        }

        return services;
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
                "Home Assistant rejected the write. Creating automations needs a long-lived token belonging to an admin user.",
                refused: true);

        EnsureSuccess(response, "create the automation");

        // What exists has just changed, whatever the cache thought a moment ago.
        _automations.Invalidate();
        return id;
    }

    /// <summary>One automation's config, and whether Home Assistant refused to hand it over at all.</summary>
    private readonly record struct ConfigRead(ExistingAutomation? Automation, bool Refused);

    /// <summary>An area's id as Home Assistant makes it from the name: lower case, runs of anything else as one underscore.</summary>
    internal static string Slug(string name)
    {
        var chars = new System.Text.StringBuilder(name.Length);
        var underscore = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                chars.Append(ch);
                underscore = false;
            }
            else if (!underscore && chars.Length > 0)
            {
                chars.Append('_');
                underscore = true;
            }
        }

        return chars.ToString().TrimEnd('_');
    }

    private async Task<ConfigRead> ReadAutomationAsync(
        string configId,
        string entityId,
        string alias,
        IReadOnlyList<HaEntity> entities,
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
                // A 404 is ordinary: this endpoint only serves what Home Assistant's own editor stores, so an
                // automation written by hand in configuration.yaml answers 404 for ever and there is nothing
                // wrong. A 401 or 403 is not ordinary -- it is the whole feature off.
                var refused = response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

                if (refused) logger.LogWarning("Home Assistant refused the config for {EntityId}: HTTP {Status}.", entityId, (int)response.Status);
                else logger.LogDebug("No stored config for {EntityId}: HTTP {Status}.", entityId, (int)response.Status);

                return new ConfigRead(null, refused);
            }

            using var document = JsonDocument.Parse(response.Body);
            var inspection = AutomationInspector.Inspect(document.RootElement);

            // An automation built in the editor often targets an area or a device rather than entities.
            // Those are the entities in that area and on that device, as far as this house's own list can
            // say: device ids match exactly, and an area id is the area's name as Home Assistant slugs it.
            HashSet<string> touched = new(inspection.Entities, StringComparer.Ordinal);
            if (inspection.Areas.Count > 0 || inspection.Devices.Count > 0)
                foreach (var entity in entities)
                    if ((entity.DeviceId is { } device && inspection.Devices.Contains(device)) ||
                        (entity.Area is { } area && inspection.Areas.Contains(Slug(area))))
                        touched.Add(entity.EntityId);

            var configAlias = document.RootElement.ValueKind == JsonValueKind.Object &&
                              document.RootElement.TryGetProperty("alias", out var aliasElement) &&
                              aliasElement.ValueKind == JsonValueKind.String
                ? aliasElement.GetString() ?? alias
                : alias;

            return new ConfigRead(new ExistingAutomation(configId, entityId, configAlias, touched, inspection.TriggerKinds), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or HomeAssistantException)
        {
            // One unreadable automation costs duplicate detection against it, nothing more.
            logger.LogWarning(ex, "Skipping automation {EntityId}: could not read its config.", entityId);
            return new ConfigRead(null, false);
        }
    }

    private sealed record RegistryEntry(string? Area, string? DeviceId, string? DeviceName);

    /// <summary>
    /// Resolves each entity's area and device in one templated call. Best effort: these sharpen drafting
    /// and let a finding be ignored by device, but nothing requires them, so any failure here degrades
    /// quality rather than breaking the request.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, RegistryEntry>> ResolveRegistryAsync(CancellationToken cancellationToken)
    {
        const string template =
            """[{% for s in states %}{% set d = device_id(s.entity_id) %}{"e": {{ s.entity_id | tojson }}, "a": {{ (area_name(s.entity_id) or "") | tojson }}, "d": {{ (d or "") | tojson }}, "n": {{ ((device_attr(d, "name_by_user") or device_attr(d, "name") or "") if d else "") | tojson }}}{{ "," if not loop.last }}{% endfor %}]""";

        Dictionary<string, RegistryEntry> registry = new(StringComparer.Ordinal);

        try
        {
            var response = await SendAsync(
                HttpMethod.Post,
                "api/template",
                JsonSerializer.Serialize(new { template }, Json),
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                logger.LogDebug("Registry lookup returned HTTP {Status}; continuing without areas or devices.", (int)response.Status);
                return registry;
            }

            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return registry;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("e", out var entity) || entity.ValueKind != JsonValueKind.String) continue;

                var entityId = entity.GetString();
                if (string.IsNullOrWhiteSpace(entityId)) continue;

                var area = Text(item, "a");
                var deviceId = Text(item, "d");
                if (area is null && deviceId is null) continue;

                registry[entityId] = new RegistryEntry(area, deviceId, deviceId is null ? null : Text(item, "n"));
            }

            return registry;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or HomeAssistantException)
        {
            logger.LogDebug(ex, "Registry lookup failed; continuing without areas or devices.");
            return registry;
        }

        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
    }

    /// <summary>
    /// The recorder's history for a set of entities, via <c>/api/history/period</c>.
    ///
    /// Asked with <c>minimal_response</c> and <c>no_attributes</c>: the reply is then one array per entity,
    /// the first item carrying the entity id and every item carrying a state and when it changed, which is
    /// all a sample is. <c>filter_entity_id</c> is required by current Home Assistant releases, and is what
    /// keeps the reply bounded on a large install.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetHistoryAsync(
        IReadOnlyList<string> entityIds,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<StateSample>> history = new(StringComparer.Ordinal);
        if (entityIds.Count == 0) return history;

        var path = $"api/history/period/{Uri.EscapeDataString(sinceUtc.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture))}"
            + $"?filter_entity_id={Uri.EscapeDataString(string.Join(',', entityIds))}&minimal_response&no_attributes";

        var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "read state history");

        using var document = Parse(response.Body, "a history list");
        if (document.RootElement.ValueKind != JsonValueKind.Array) return history;

        foreach (var series in document.RootElement.EnumerateArray())
        {
            if (series.ValueKind != JsonValueKind.Array) continue;

            string? entityId = null;
            List<StateSample> samples = [];

            foreach (var element in series.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                if (entityId is null && element.TryGetProperty("entity_id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                    entityId = idElement.GetString();

                if (!element.TryGetProperty("state", out var stateElement) || stateElement.ValueKind != JsonValueKind.String) continue;

                var changed = ReadTime(element, "last_changed");
                if (changed == DateTimeOffset.UnixEpoch) changed = ReadTime(element, "last_updated");
                if (changed == DateTimeOffset.UnixEpoch) continue;

                var state = stateElement.GetString() ?? "";
                samples.Add(new StateSample(state, Ha.TryNumeric(state, out var value) ? value : null, changed));
            }

            if (entityId is not null && samples.Count > 0)
                history[entityId] = [.. samples.OrderBy(sample => sample.ChangedUtc)];
        }

        return history;
    }

    private async Task<Response> SendAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        var options = settings.Current.HomeAssistant;
        // Clamped, not trusted. A timeout past what a timer accepts makes CancelAfter throw before the
        // request is even attempted, and this method is on the path that writes to the user's home.
        var timeoutAfter = options.RequestTimeout > TimeSpan.Zero ? options.RequestTimeout : TimeSpan.FromSeconds(30);
        if (timeoutAfter > LlmOptions.LongestTimeout) timeoutAfter = LlmOptions.LongestTimeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);

        using var request = new HttpRequestMessage(method, BuildUri(options.BaseUrl, path));

        var token = secrets.Resolve(SecretStore.HomeAssistantToken, options.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var started = clock.GetTimestamp();
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            logger.LogDebug("{Method} {Path} → HTTP {Status} in {Ms} ms, {Bytes} bytes.",
                method.Method, Shown(path), (int)response.StatusCode, (long)clock.GetElapsedTime(started).TotalMilliseconds, body.Length);
            return new Response(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Method} {Path} timed out after {Timeout}.", method.Method, Shown(path), Durations.Format(timeoutAfter));
            throw new HomeAssistantException(
                $"Home Assistant at {options.BaseUrl} did not answer within {Durations.Format(timeoutAfter)}.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{Method} {Path} failed: {Reason}", method.Method, Shown(path), ex.Message);
            throw;
        }
    }

    /// <summary>The path without its query, which for a history read is a list of every entity asked about.</summary>
    private static string Shown(string path)
    {
        var query = path.IndexOf('?');
        return query < 0 ? "/" + path : "/" + path[..query] + "?…";
    }

    private static Uri BuildUri(string? baseUrl, string path)
    {
        try
        {
            return Urls.Under(baseUrl, path);
        }
        catch (UriFormatException)
        {
            throw new HomeAssistantException(
                $"The Home Assistant address '{baseUrl}' is not a valid http or https URL. Correct it on the settings page.");
        }
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

        string? friendlyName = null, deviceClass = null, unit = null, stateClass = null, configId = null;
        if (element.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            friendlyName = ReadAttribute(attributes, "friendly_name");
            deviceClass = ReadAttribute(attributes, "device_class");
            unit = ReadAttribute(attributes, "unit_of_measurement");

            // Marks a running total — an energy meter, a data counter — which no distribution-based
            // detector can say anything about, since its newest reading is its largest by construction.
            stateClass = ReadAttribute(attributes, "state_class");

            // An automation's config id is how its definition is addressed. It is usually a string of digits,
            // but Home Assistant has emitted it as a number too.
            if (Ha.DomainOf(entityId) == "automation" && attributes.TryGetProperty("id", out var idAttribute))
                configId = idAttribute.ValueKind == JsonValueKind.String ? idAttribute.GetString() : idAttribute.GetRawText().Trim('"');
        }

        return new HaEntity(entityId, state, changed, updated, friendlyName, deviceClass, unit,
            AutomationConfigId: string.IsNullOrWhiteSpace(configId) ? null : configId,
            StateClass: stateClass);
    }

    /// <summary>
    /// A value that is slow to fetch and slow to change, kept until it ages out or its key changes. Loads
    /// are serialised, so two drafts arriving together share one read instead of racing to do the same work.
    /// </summary>
    private sealed class Cached<T>(TimeProvider clock, TimeSpan keepFor) where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private T? _value;
        private string? _key;
        private DateTimeOffset _expires = DateTimeOffset.MinValue;

        public async Task<T> GetAsync(string key, Func<Task<T>> load, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_value is not null && _key == key && clock.GetUtcNow() < _expires) return _value;

                _value = await load().ConfigureAwait(false);
                _key = key;
                _expires = clock.GetUtcNow() + keepFor;
                return _value;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Invalidate() => _expires = DateTimeOffset.MinValue;
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

        // A 4xx is Home Assistant saying no: it read the request, disagreed with it, and changed nothing.
        // A 5xx is not — it can come from a proxy in front, or from Home Assistant after it had already
        // done the work — so it is left as "unknown" and the caller stays cautious about it.
        var refused = (int)response.Status is >= 400 and < 500;

        throw new HomeAssistantException(
            $"Home Assistant returned HTTP {(int)response.Status} when asked to {what}. {detail}".TrimEnd(),
            refused: refused);
    }
}
