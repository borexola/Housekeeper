using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Housekeeper.Core;

/// <summary>
/// Turns what someone typed into a <see cref="Concern"/> the scanner can act on, and keeps the list.
///
/// The model is asked when the concern is added, and again on the scan's tick only for a concern it has not
/// yet read: a scan runs every minute and a local model takes tens of seconds, so a concern the model has
/// already read is never re-read by the scanner, and pending ones are read one per tick with a backoff that
/// grows while the model keeps failing on the same one. If the model is not configured, does not answer, or
/// names nothing that exists, the concern is still saved, matched by name, so that nothing the user asked
/// for is quietly dropped.
///
/// The tick is the only thing in Housekeeper that asks the model without being asked, so it is the only
/// thing <see cref="LlmOptions.OnlyWhenAsked"/> switches off. Adding a concern and pressing Read again are
/// the user asking, and go on working; what stops is Housekeeper deciding on its own that now is a good
/// moment to spend half a minute of someone's GPU. A concern left unread then says so, and says what to
/// press, rather than promising a retry that is never coming.
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

    /// <summary>
    /// How many unusable answers the tick accepts for one concern before it stops asking on its own. A
    /// low-temperature model answering the identical prompt is unlikely to change its mind; the Read again
    /// button still asks.
    /// </summary>
    public const int MostAutomaticReads = 3;

    /// <summary>One reading at a time, so the tick and a Read again cannot each ask the model about the same concern.</summary>
    private readonly SemaphoreSlim _reading = new(1, 1);

    /// <param name="Failures">Every try that did not end in an answer, which is what the gap between tries grows on.</param>
    /// <param name="Strikes">Tries the model answered unusably. Only these count towards giving up: a model that is down is not wrong, it is absent.</param>
    private readonly record struct Tried(int Failures, int Strikes, DateTimeOffset LastUtc);

    /// <summary>What the tick has tried per concern, so it rotates between them and backs off.</summary>
    private readonly ConcurrentDictionary<long, Tried> _tries = new();

    public Task<IReadOnlyList<Concern>> ListAsync(CancellationToken cancellationToken) => store.ListConcernsAsync(cancellationToken);

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken)
    {
        _tries.TryRemove(id, out _);
        return await store.DeleteConcernAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the concern, with the model where it can be asked, and saves it either way.</summary>
    public async Task<Concern> AddAsync(string text, CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length > MaxLength) text = text[..MaxLength];

        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
            var (concern, _) = await ReadAsync(new Concern { Text = text, CreatedUtc = clock.GetUtcNow() }, entities, cancellationToken).ConfigureAwait(false);

            var saved = await store.AddConcernAsync(concern, cancellationToken).ConfigureAwait(false);
            Log(saved);
            return saved;
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary>
    /// Reads a concern again, on request, whatever state it is in. Null when there is no such concern. The
    /// user asking resets the tick's count of attempts, so the button always gets a fresh try.
    /// </summary>
    public async Task<Concern?> ReadAgainAsync(long id, CancellationToken cancellationToken)
    {
        _tries.TryRemove(id, out _);
        var (concern, _) = await ReadAgainAsync(id, onlyIfProvisional: false, cancellationToken).ConfigureAwait(false);
        return concern;
    }

    /// <summary>
    /// Reads one concern the model has not yet had its say on, if there is one, a model is configured, and
    /// its turn has come. Called on each scan tick, so a concern added while the model was down is read the
    /// moment it is back rather than left matched by name for ever -- one at a time, because a local model
    /// takes tens of seconds and a scan should not wait behind five of them; least recently tried first, so
    /// one the model keeps failing on does not starve the rest; and with a gap that doubles per failed try up
    /// to an hour, so a model that is down is asked ever less often rather than every tick. An answer that
    /// cannot be read counts a strike, and after <see cref="MostAutomaticReads"/> the tick stops asking and
    /// leaves it to the Read again button. Returns how many were read to completion.
    /// </summary>
    public async Task<int> ReadPendingAsync(CancellationToken cancellationToken)
    {
        var llmOptions = settings.Current.Llm;
        if (llmOptions.OnlyWhenAsked) return 0;
        if (llmOptions.IsOllama && string.IsNullOrWhiteSpace(llmOptions.Model)) return 0;

        var now = clock.GetUtcNow();
        var interval = settings.Current.Scan.Interval > TimeSpan.Zero ? settings.Current.Scan.Interval : TimeSpan.FromMinutes(5);

        // Half an interval of slack, because a PeriodicTimer re-arms at period-minus-lateness: two
        // consecutive ticks can be a few milliseconds under one interval apart, and without the slack the
        // concern would be skipped on the tick its backoff lands on and read on the one after.
        var slack = interval / 2;

        var pending = (await store.ListConcernsAsync(cancellationToken).ConfigureAwait(false))
            .Where(concern => concern.Provisional)
            .Select(concern => (Concern: concern, Tried: _tries.GetValueOrDefault(concern.Id)))
            .Where(pair => pair.Tried.Strikes < MostAutomaticReads &&
                           now - pair.Tried.LastUtc >= Backoff(interval, pair.Tried.Failures) - slack)
            .OrderBy(pair => pair.Tried.LastUtc)
            .ThenBy(pair => pair.Concern.Id)
            .Select(pair => pair.Concern)
            .FirstOrDefault();
        if (pending is null) return 0;

        var (read, outcome) = await ReadAgainAsync(pending.Id, onlyIfProvisional: true, cancellationToken).ConfigureAwait(false);
        if (read is null) return 0;

        if (!read.Provisional)
        {
            _tries.TryRemove(pending.Id, out _);
            return 1;
        }

        // Down is not the same as wrong. Either way the gap before the next try doubles, so a model that is
        // down costs one call a tick at first and then less; but only an unusable answer counts a strike,
        // and after three of those the tick leaves it to the button, because a model answering the same
        // prompt at the same temperature is unlikely to change its mind.
        var tried = _tries.GetValueOrDefault(pending.Id);
        var strikes = outcome == Outcome.Unusable ? tried.Strikes + 1 : tried.Strikes;
        _tries[pending.Id] = new Tried(tried.Failures + 1, strikes, now);

        if (strikes >= MostAutomaticReads)
        {
            // Written whole rather than appended: the note it would be appended to says "It will be asked
            // again", which is true until this moment and a contradiction after it.
            var parked = read with
            {
                Note = $"The model's answer could not be read {MostAutomaticReads} times, so this stays matched by name and will not be asked again on its own; press Read again to try once more.",
                Provisional = false,
            };
            await store.UpdateConcernAsync(parked, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Concern {ConcernId} was answered unusably {Strikes} times; leaving it matched by name.", pending.Id, strikes);
        }

        return 0;
    }

    /// <summary>
    /// How long the tick waits before asking again: the scan interval, doubling per failed try, capped at an
    /// hour. An hour rather than a day because the commonest failure is a model that is simply not running
    /// yet, and a concern added during that should be read soon after it comes back, not tomorrow.
    /// </summary>
    internal static TimeSpan Backoff(TimeSpan interval, int failures)
    {
        var scaled = interval.TotalSeconds * Math.Pow(2, Math.Min(failures, 12));
        return TimeSpan.FromSeconds(Math.Min(scaled, TimeSpan.FromHours(1).TotalSeconds));
    }

    private async Task<(Concern? Concern, Outcome Outcome)> ReadAgainAsync(long id, bool onlyIfProvisional, CancellationToken cancellationToken)
    {
        await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Fetched under the gate: the other path may have just finished reading this very concern.
            var concern = await store.GetConcernAsync(id, cancellationToken).ConfigureAwait(false);
            if (concern is null) return (null, Outcome.Read);
            if (onlyIfProvisional && !concern.Provisional) return (concern, Outcome.Read);

            var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
            var (read, outcome) = await ReadAsync(concern, entities, cancellationToken).ConfigureAwait(false);

            await store.UpdateConcernAsync(read, cancellationToken).ConfigureAwait(false);
            Log(read);
            return (read, outcome);
        }
        finally
        {
            _reading.Release();
        }
    }

    private void Log(Concern concern) =>
        logger.LogInformation(
            "Concern {ConcernId} \"{Text}\" watches {Count} entities ({Rule}); {How}{Note}.",
            concern.Id, concern.Text, concern.Entities.Count, concern.Rule.Describe(),
            concern.Interpreted ? "read by the model" : "matched by name",
            concern.Note is null ? "" : "; " + concern.Note);

    /// <summary>How asking the model went, for the tick to decide whether to ask again.</summary>
    internal enum Outcome
    {
        /// <summary>The model read it, or said nothing here relates: an answer either way.</summary>
        Read,
        /// <summary>Not chosen, down, or could not be asked. Worth asking again when it is back.</summary>
        Unavailable,
        /// <summary>It answered, but not with anything that could be read.</summary>
        Unusable,
    }

    /// <summary>
    /// The concern with its entities, rule and wording worked out: from the model where it can be asked,
    /// and by name otherwise. What was matched and why the model is missing are kept as two facts, because
    /// one line that says "watching ten entities; the model was down" paints a working concern as broken.
    ///
    /// A concern the model has already read keeps that reading when the model cannot be asked now: a Read
    /// again pressed while the model is down must not trade a rule for a name match.
    /// </summary>
    private async Task<(Concern Concern, Outcome Outcome)> ReadAsync(Concern concern, IReadOnlyList<HaEntity> entities, CancellationToken cancellationToken)
    {
        var text = concern.Text;
        var byName = Concerns.Match(text, entities);

        var matched = concern with
        {
            Entities = byName,
            Rule = WatchRule.Attention,
            Explanation = byName.Count == 0
                ? "Nothing in Home Assistant matched these words yet."
                : $"Matched by name to {byName.Count} {(byName.Count == 1 ? "entity" : "entities")}.",
            Interpreted = false,
            Note = null,
            Provisional = false,
        };

        var (read, note, outcome) = await InterpretAsync(text, entities, byName, cancellationToken).ConfigureAwait(false);
        if (read is { } r && r.Entities.Count > 0)
        {
            return (matched with
            {
                Entities = r.Entities,
                Rule = r.Rule,
                Explanation = r.Explanation ?? $"Watching {r.Entities.Count} {(r.Entities.Count == 1 ? "entity" : "entities")}: {r.Rule.Describe()}.",
                Interpreted = true,
            }, Outcome.Read);
        }

        if (concern.Interpreted)
            return (concern with { Note = note + " The reading it already had is kept.", Provisional = false }, outcome);

        var refused = read is { Entities.Count: 0 } && !string.IsNullOrWhiteSpace(read.Value.Explanation) ? " " + read.Value.Explanation : "";
        return (matched with { Note = note + refused, Provisional = outcome != Outcome.Read }, outcome);
    }

    /// <summary>The model's reading, or null with the reason it could not be had and what kind of reason that is.</summary>
    private async Task<((IReadOnlyList<string> Entities, WatchRule Rule, string? Explanation)? Read, string Note, Outcome Outcome)> InterpretAsync(
        string text,
        IReadOnlyList<HaEntity> entities,
        IReadOnlyList<string> byName,
        CancellationToken cancellationToken)
    {
        var options = settings.Current;
        if (options.Llm.IsOllama && string.IsNullOrWhiteSpace(options.Llm.Model))
            return (null, "No model is chosen, so this was matched by name only. Pick one under Settings → Model and it will be read properly.", Outcome.Unavailable);

        // What happens to a concern the model could not read, which is not the same thing when nothing is
        // going to pick it up again on its own.
        var next = options.Llm.OnlyWhenAsked
            ? " Housekeeper only asks the model when you do, so press Read again when it is back."
            : " It will be read again when the model is back; check Settings → Model if it stays this way.";
        var retry = options.Llm.OnlyWhenAsked
            ? " Housekeeper only asks the model when you do, so press Read again to try once more."
            : " It will be asked again.";

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
                return (null, $"The model did not answer ({llm.Name}), so this was matched by name only.{next}", Outcome.Unavailable);
            }

            var read = Concerns.Parse(raw, known);
            if (read is null)
            {
                logger.LogWarning("The model's answer to a concern was not usable; matching it by name instead.");
                return (null, "The model's answer could not be read, so this was matched by name only." + retry, Outcome.Unusable);
            }

            if (read.Value.Entities.Count == 0)
                return (read, "The model read this and named nothing that exists here.", Outcome.Read);

            return (read, "", Outcome.Read);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not ask the model to read a concern; matching it by name instead.");
            return (null, $"The model could not be asked ({ex.Message}), so this was matched by name only.{retry}", Outcome.Unavailable);
        }
    }
}
