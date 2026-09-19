using System.Collections.Concurrent;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// The friendly name of every entity this process has ever seen, so a proposal can be read back in words
/// without a trip to Home Assistant for each page load.
///
/// A proposal stores entity ids, which is right: the id is what was validated and what Home Assistant will
/// run. But "light.hall turns on" is not how anyone thinks about their house, and the dashboard reloads
/// the proposal list every thirty seconds, per tab. Every read of the entity list already passes through
/// here, from the scanner once a minute and from every draft, so the names are simply remembered on the
/// way past. Empty until something has read the list, in which case the narrator falls back to the id
/// said as words.
/// </summary>
public sealed class NameBook
{
    /// <summary>What is worth knowing about an entity by id when the entity itself is not to hand.</summary>
    public sealed record Known(string? Name, string? Device, string? Area);

    private readonly ConcurrentDictionary<string, Known> _known = new(StringComparer.Ordinal);

    public int Count => _known.Count;

    public string? NameOf(string entityId) => _known.TryGetValue(entityId, out var known) ? known.Name : null;

    public Known? About(string entityId) => _known.TryGetValue(entityId, out var known) ? known : null;

    public void Remember(IReadOnlyList<HaEntity> entities)
    {
        foreach (var entity in entities)
        {
            var known = new Known(Trim(entity.FriendlyName), Trim(entity.DeviceName), Trim(entity.Area));
            if (known.Name is null && known.Device is null && known.Area is null) continue;
            _known[entity.EntityId] = known;
        }
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Passes every call through, and remembers the names on the one that lists entities.</summary>
public sealed class NamingHomeAssistant(IHomeAssistant inner, NameBook names) : IHomeAssistant
{
    public async Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken cancellationToken)
    {
        var entities = await inner.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
        names.Remember(entities);
        return entities;
    }

    public Task<IReadOnlyList<ExistingAutomation>> GetAutomationsAsync(IReadOnlyList<HaEntity> entities, CancellationToken cancellationToken) =>
        inner.GetAutomationsAsync(entities, cancellationToken);

    public Task<IReadOnlySet<string>> GetServicesAsync(CancellationToken cancellationToken) =>
        inner.GetServicesAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<StateSample>>> GetHistoryAsync(
        IReadOnlyList<string> entityIds, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        inner.GetHistoryAsync(entityIds, sinceUtc, cancellationToken);

    public Task<string> CreateAutomationAsync(string id, string configJson, CancellationToken cancellationToken) =>
        inner.CreateAutomationAsync(id, configJson, cancellationToken);

    public Task<bool> PingAsync(CancellationToken cancellationToken) => inner.PingAsync(cancellationToken);

    public Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken) => inner.GetTimeZoneAsync(cancellationToken);
}
