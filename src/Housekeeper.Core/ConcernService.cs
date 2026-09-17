using Microsoft.Extensions.Logging;

namespace Housekeeper.Core;

/// <summary>
/// Turns what someone typed into a <see cref="Concern"/> the scanner can act on, and keeps the list.
///
/// The model is asked once, when the concern is added, never during a scan: a scan runs every minute and a
/// local model takes tens of seconds, so the reading is done up front and stored with the concern. If the
/// model is not configured, does not answer, or names nothing that exists, the concern is still saved,
/// matched by name, so that nothing the user asked for is quietly dropped.
/// </summary>
public sealed class ConcernService(
    IHomeAssistant homeAssistant,
    ILlmClient llm,
    IStore store,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<ConcernService> logger)
{
    public const int MaxLength = 300;

    public Task<IReadOnlyList<Concern>> ListAsync(CancellationToken cancellationToken) => store.ListConcernsAsync(cancellationToken);

    public Task<bool> RemoveAsync(long id, CancellationToken cancellationToken) => store.DeleteConcernAsync(id, cancellationToken);

    /// <summary>Reads the concern, with the model where it can be asked, and saves it either way.</summary>
    public async Task<Concern> AddAsync(string text, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length > MaxLength) text = text[..MaxLength];

        var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var byName = Concerns.Match(text, entities);

        var concern = new Concern
        {
            Text = text,
            Entities = byName,
            Rule = WatchRule.Attention,
            Explanation = byName.Count == 0
                ? "Nothing in Home Assistant matched these words yet; add more detail or name the room or device."
                : $"Matched by name to {byName.Count} {(byName.Count == 1 ? "entity" : "entities")}.",
            Interpreted = false,
            CreatedUtc = clock.GetUtcNow(),
        };

        var (read, why) = await InterpretAsync(text, entities, byName, cancellationToken).ConfigureAwait(false);
        if (read is { } r && r.Entities.Count > 0)
        {
            concern = concern with
            {
                Entities = r.Entities,
                Rule = r.Rule,
                Explanation = r.Explanation ?? $"Watching {r.Entities.Count} {(r.Entities.Count == 1 ? "entity" : "entities")}: {r.Rule.Describe()}.",
                Interpreted = true,
            };
        }
        else
        {
            // Name matching stood in. Say why the model did not, because "matched by name" on its own reads
            // as a choice rather than a fallback, and the fix is usually on the settings page.
            var matched = byName.Count == 0
                ? "Nothing in Home Assistant matched these words"
                : $"Matched by name to {byName.Count} {(byName.Count == 1 ? "entity" : "entities")}";
            var refused = read is { Entities.Count: 0 } && !string.IsNullOrWhiteSpace(read.Value.Explanation) ? " " + read.Value.Explanation : "";
            concern = concern with { Explanation = $"{matched}. {why}{refused}" };
        }

        var saved = await store.AddConcernAsync(concern, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Concern {ConcernId} \"{Text}\" watches {Count} entities ({Rule}); {How}.",
            saved.Id, saved.Text, saved.Entities.Count, saved.Rule.Describe(), saved.Interpreted ? "read by the model" : "matched by name");

        return saved;
    }

    /// <summary>The model's reading, or null and the reason it could not be had.</summary>
    private async Task<((IReadOnlyList<string> Entities, WatchRule Rule, string? Explanation)? Read, string Why)> InterpretAsync(
        string text,
        IReadOnlyList<HaEntity> entities,
        IReadOnlyList<string> byName,
        CancellationToken cancellationToken)
    {
        var options = settings.Current;
        if (options.Llm.IsOllama && string.IsNullOrWhiteSpace(options.Llm.Model))
            return (null, "No model is chosen, so it could not be read more carefully; pick one under Settings → Model.");

        // The shortlist is the concern's own words against the house, plus what name matching found, so the
        // model chooses among things that could plausibly be meant rather than among everything.
        var byId = entities.ToDictionary(e => e.EntityId, StringComparer.Ordinal);
        var shortlist = EntityIndex.Shortlist(entities, text, options.Llm.MaxCandidateEntities)
            .Concat(byName.Select(id => byId[id]))
            .DistinctBy(e => e.EntityId, StringComparer.Ordinal)
            .Take(options.Llm.MaxCandidateEntities + Concerns.MostMatched)
            .ToList();
        var known = new HashSet<string>(entities.Select(e => e.EntityId), StringComparer.Ordinal);

        try
        {
            var raw = await llm.CompleteJsonAsync(Concerns.SystemPrompt, Concerns.Prompt(text, shortlist), cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                logger.LogWarning("The model did not answer when asked to read a concern; matching it by name instead.");
                return (null, $"The {llm.Name} endpoint did not answer, so it was not read by the model; check Settings → Model.");
            }

            var read = Concerns.Parse(raw, known);
            if (read is null)
            {
                logger.LogWarning("The model's answer to a concern was not usable; matching it by name instead.");
                return (null, "The model's answer could not be read, so the model's reading was not used.");
            }

            if (read.Value.Entities.Count == 0)
                return (read, "The model named nothing that exists here.");

            return (read, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not ask the model to read a concern; matching it by name instead.");
            return (null, $"The model could not be asked ({ex.Message}), so it was not read by the model.");
        }
    }
}
