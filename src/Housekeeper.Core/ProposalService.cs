using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Housekeeper.Core;

/// <summary>
/// The whole life of a proposal: drafting one, refining it, and the single call that changes the user's
/// home. Nothing here writes to Home Assistant except <see cref="ConfirmAsync"/>, and that only ever runs
/// because someone pressed a button.
/// </summary>
public sealed class ProposalService(
    IHomeAssistant homeAssistant,
    ILlmClient llm,
    IStore store,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<ProposalService> logger)
{
    public Task<Proposal> DraftAsync(string request, ProposalSource source, long? anomalyId, CancellationToken cancellationToken) =>
        DraftCoreAsync(request, source, anomalyId, null, null, cancellationToken);

    /// <summary>
    /// Redrafts a proposal against what the user objected to, and retires the one it replaces.
    ///
    /// The refinement is stored first and the parent superseded afterwards, so a failure in between leaves
    /// two drafts rather than none. If the parent was decided while the model was thinking -- confirmed, or
    /// rejected -- the claim fails and it is left exactly as it was: the user acted on what they could see,
    /// and a refinement arriving late must not undo that.
    /// </summary>
    public async Task<Operation<Proposal>> RefineAsync(long id, string feedback, CancellationToken cancellationToken)
    {
        var parent = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
        if (parent is null) return Operation<Proposal>.NotFound($"Proposal {id} was not found.");

        if (parent.Status != ProposalStatus.Draft)
            return Operation<Proposal>.Conflict($"Proposal {id} is {parent.Status} and can no longer be refined.");

        if (string.IsNullOrWhiteSpace(parent.ConfigJson))
            return Operation<Proposal>.Conflict($"Proposal {id} has no draft to refine.");

        var refined = await DraftCoreAsync(parent.Request, parent.Source, parent.AnomalyId, parent, feedback, cancellationToken)
            .ConfigureAwait(false);

        // A refinement that failed leaves the parent alone: the user still has the draft they had.
        if (refined.Status == ProposalStatus.Failed) return Operation<Proposal>.Ok(refined);

        var now = clock.GetUtcNow();

        // Not cancellable from here on. The refinement exists; leaving the parent live as well would show
        // the user two drafts of the same thing with no way to tell which is which.
        if (!await store.TryClaimAsync(parent.Id, ProposalStatus.Draft, ProposalStatus.Superseded, now, null, CancellationToken.None)
            .ConfigureAwait(false))
        {
            logger.LogInformation(
                "Proposal {ParentId} was decided while it was being refined, so it was left as it is; the refinement is proposal {ProposalId}.",
                parent.Id, refined.Id);
            return Operation<Proposal>.Ok(refined);
        }

        // The finding that started this points at the parent. Moved across, or dismissing the refinement
        // would leave the finding pointing at a superseded draft nobody can act on.
        if (parent.AnomalyId is { } anomalyId)
        {
            var anomaly = await store.GetAnomalyAsync(anomalyId, CancellationToken.None).ConfigureAwait(false);

            if (anomaly is { Status: AnomalyStatus.Promoted } && anomaly.ProposalId == parent.Id)
                await store.UpdateAnomalyAsync(anomaly with { ProposalId = refined.Id }, CancellationToken.None)
                    .ConfigureAwait(false);
        }

        logger.LogInformation("Proposal {ParentId} superseded by refined proposal {ProposalId}.", parent.Id, refined.Id);
        return Operation<Proposal>.Ok(refined);
    }

    /// <summary>
    /// Asks the model, checks what comes back, and asks again with the objection when it is refused.
    ///
    /// The retry loop is what makes a small local model usable. A 7B model gets the shape wrong often, and
    /// gets it right when told exactly what was wrong -- so the validator's sentence goes straight back in
    /// as REJECTED_BECAUSE rather than being logged and swallowed.
    /// </summary>
    private async Task<Proposal> DraftCoreAsync(
        string request,
        ProposalSource source,
        long? anomalyId,
        Proposal? parent,
        string? feedback,
        CancellationToken cancellationToken)
    {
        var options = settings.Current;
        var now = clock.GetUtcNow();

        // Caught here rather than at the endpoint so the reason is stored on the proposal and the user sees
        // it where the failure is, with the steps to fix it.
        if (options.Llm.IsOllama && string.IsNullOrWhiteSpace(options.Llm.Model))
            return await FailAsync(request, source, anomalyId, parent, feedback, now,
                "No model is chosen, and Ollama needs one. Open Settings, use Test connection under Model to see "
                + "what it offers, and pick one.", cancellationToken).ConfigureAwait(false);

        var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var known = new HashSet<string>(entities.Select(e => e.EntityId), StringComparer.Ordinal);

        // On a refinement the shortlist is built from the original request, the feedback AND what the
        // previous draft touched -- otherwise "make it slower" matches nothing and the model is shown a
        // shortlist with none of the entities it is being asked to revise.
        var focus = parent is null ? request : $"{request} {feedback} {string.Join(' ', parent.Entities)}";
        var candidates = EntityIndex.Shortlist(entities, focus, options.Llm.MaxCandidateEntities);

        if (candidates.Count == 0)
            return await FailAsync(request, source, anomalyId, parent, feedback, now,
                "No entities in Home Assistant relate to that request. Try naming a device or area.",
                cancellationToken).ConfigureAwait(false);

        var services = await ServicesAsync(cancellationToken).ConfigureAwait(false);

        var attempts = Math.Clamp(options.Llm.MaxAttempts, 1, 5);
        string? rejected = null;
        string? reason = null;
        var asked = 0;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            asked = attempt;

            var prompt = Prompts.User(request, candidates, services, rejected ?? parent?.ConfigJson, feedback, reason);
            var raw = await llm.CompleteJsonAsync(Prompts.System, prompt, cancellationToken).ConfigureAwait(false);

            if (raw is null)
                return await FailAsync(request, source, anomalyId, parent, feedback, now,
                    $"The model did not answer ({llm.Name}). Check that it is running and the model is pulled.",
                    cancellationToken).ConfigureAwait(false);

            var parsed = AutomationDrafting.Parse(raw, known, services);

            if (parsed.Draft is { } draft)
            {
                // The model saying it cannot be done is an answer, not a failure to try again at. Retrying
                // would only pressure it into inventing something.
                if (string.Equals(draft.Alias, AutomationDrafting.Unsupported, StringComparison.OrdinalIgnoreCase))
                    return await FailAsync(request, source, anomalyId, parent, feedback, now,
                        draft.Description ?? "The model could not build this from the available entities.",
                        cancellationToken).ConfigureAwait(false);

                if (attempt > 1) logger.LogInformation("Draft accepted on attempt {Attempt} of {Attempts}.", attempt, attempts);

                return await StoreAsync(request, source, anomalyId, parent, feedback, now, draft, entities, cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation("Attempt {Attempt} of {Attempts} was rejected: {Reason}", attempt, attempts, parsed.Error);

            // A model that repeats itself verbatim has nothing else to offer, and asking again just spends
            // another thirty seconds of someone's evening to be told the same thing.
            if (string.Equals(raw, rejected, StringComparison.Ordinal))
            {
                logger.LogInformation("The model repeated its previous answer; not asking again.");
                break;
            }

            rejected = raw;
            reason = parsed.Error;
        }

        var times = asked == 1 ? "once" : $"{asked} times";

        return await FailAsync(request, source, anomalyId, parent, feedback, now,
            $"{reason} The model was asked {times} and could not produce a usable automation.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The service list, or an empty set. Drafting without it costs a check; refusing to draft costs the
    /// whole feature, and an unreadable service list is a connection problem rather than a bad request.
    /// </summary>
    private async Task<IReadOnlySet<string>> ServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await homeAssistant.GetServicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the service list; drafting without it.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task<Proposal> StoreAsync(
        string request,
        ProposalSource source,
        long? anomalyId,
        Proposal? parent,
        string? feedback,
        DateTimeOffset now,
        AutomationDraft draft,
        IReadOnlyList<HaEntity> entities,
        CancellationToken cancellationToken)
    {
        // Null means the list could not be read, which is a different thing from a house with no
        // automations: the first must not be reported to the user as "nothing similar exists".
        IReadOnlyList<ExistingAutomation>? existing = null;
        try
        {
            existing = await homeAssistant.GetAutomationsAsync(entities, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read existing automations; skipping duplicate detection.");
        }

        var proposal = await store.AddProposalAsync(new Proposal
        {
            Request = request,
            Source = source,
            Status = ProposalStatus.Draft,
            Feedback = feedback,
            ParentId = parent?.Id,
            Alias = draft.Alias,
            Description = draft.Description,
            ConfigJson = draft.ConfigJson,
            Entities = draft.Entities,
            Actions = draft.Actions,
            Duplicates = existing is null ? [] : DuplicateFinder.Find(draft, existing),
            AnomalyId = anomalyId,
            CreatedUtc = now,
        }, cancellationToken).ConfigureAwait(false);

        if (existing is null)
            logger.LogInformation(
                "Drafted proposal {ProposalId} '{Alias}' touching {EntityCount} entities. Existing automations could not be read, so it was not checked against them.",
                proposal.Id, proposal.Alias, proposal.Entities.Count);
        else
            logger.LogInformation(
                "Drafted proposal {ProposalId} '{Alias}' touching {EntityCount} entities with {DuplicateCount} possible duplicate(s).",
                proposal.Id, proposal.Alias, proposal.Entities.Count, proposal.Duplicates.Count);

        return proposal;
    }

    /// <summary>
    /// Writes the automation to Home Assistant. The only call in Housekeeper that changes the user's home.
    ///
    /// The status is claimed BEFORE the write, so two clicks on the same button cannot both pass a check and
    /// both create an automation. What happens to a failure depends entirely on whether Home Assistant said
    /// no or never answered -- see the two catch blocks, which are the difference between a draft that can
    /// be confirmed again and one that may already be live.
    /// </summary>
    public async Task<Operation<Proposal>> ConfirmAsync(long id, CancellationToken cancellationToken)
    {
        var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return Operation<Proposal>.NotFound($"Proposal {id} was not found.");

        // A write whose outcome was never learned is left Failed carrying the id it would have used.
        // Confirming again reuses that id, so if the first attempt did land this edits the same automation
        // rather than creating a second one beside it.
        var retry = proposal is { Status: ProposalStatus.Failed, HaAutomationId: not (null or "") };

        if (proposal.Status != ProposalStatus.Draft && !retry)
            return Operation<Proposal>.Conflict($"Proposal {id} is {proposal.Status} and can no longer be confirmed.");

        if (string.IsNullOrWhiteSpace(proposal.ConfigJson))
            return Operation<Proposal>.Conflict($"Proposal {id} has no automation to write.");

        var now = clock.GetUtcNow();
        var from = retry ? ProposalStatus.Failed : ProposalStatus.Draft;

        if (!await store.TryClaimAsync(id, from, ProposalStatus.Created, now, null, cancellationToken).ConfigureAwait(false))
            return Operation<Proposal>.Conflict($"Proposal {id} is already being confirmed or has since been decided.");

        var automationId = retry
            ? proposal.HaAutomationId!
            : string.Create(CultureInfo.InvariantCulture, $"{now.ToUnixTimeMilliseconds()}{id}");

        try
        {
            var created = await homeAssistant.CreateAutomationAsync(automationId, proposal.ConfigJson, cancellationToken)
                .ConfigureAwait(false);

            // Recorded with CancellationToken.None: the automation is live in the user's home, and losing
            // the record of that because the browser hung up is the one outcome with no way back.
            await store.RecordOutcomeAsync(id, ProposalStatus.Created, now, created, null, CancellationToken.None)
                .ConfigureAwait(false);

            logger.LogInformation("Created automation {AutomationId} in Home Assistant from proposal {ProposalId}.", created, id);

            return Operation<Proposal>.Ok(proposal with
            {
                Status = ProposalStatus.Created,
                HaAutomationId = created,
                DecidedUtc = now,
                Error = null,
            });
        }
        catch (HomeAssistantException ex) when (ex.Refused)
        {
            // Home Assistant received it, understood it, and said no -- so nothing was written and this is
            // safely still a draft. A rejected token can be corrected and the same draft confirmed again.
            await store.TryClaimAsync(id, ProposalStatus.Created, from, null, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);

            logger.LogWarning(ex, "Home Assistant refused the automation from proposal {ProposalId}; it is still a draft.", id);
            return Operation<Proposal>.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            // We never found out. It may be live. Saying so plainly is the only honest option, and keeping
            // the id is what makes trying again safe rather than a way to end up with two.
            var failed = proposal with
            {
                Status = ProposalStatus.Failed,
                DecidedUtc = now,
                HaAutomationId = automationId,
                Error = $"{ex.Message} It is not certain whether Home Assistant saved it; if it did, it is "
                        + $"automation id {automationId}. Trying again is safe: it would edit that same automation.",
            };

            await store.RecordOutcomeAsync(id, ProposalStatus.Failed, now, automationId, failed.Error, CancellationToken.None)
                .ConfigureAwait(false);
            await ReopenFindingAsync(proposal, CancellationToken.None).ConfigureAwait(false);

            logger.LogError(ex, "The automation from proposal {ProposalId} may not have been written.", id);
            return Operation<Proposal>.Failed(failed.Error);
        }
    }

    /// <summary>
    /// Puts a finding back on the dashboard when the draft it became did not survive.
    ///
    /// Promoting a finding closes it. If the automation was then rejected or never written, the thing the
    /// finding described is still happening — and without this it would sit silently promoted, its draft
    /// gone, with nothing to tell the user the freezer is still open.
    /// </summary>
    private async Task ReopenFindingAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        if (proposal.AnomalyId is not { } anomalyId) return;

        var anomaly = await store.GetAnomalyAsync(anomalyId, cancellationToken).ConfigureAwait(false);
        if (anomaly is not { Status: AnomalyStatus.Promoted }) return;

        await store.UpdateAnomalyAsync(
            anomaly with { Status = AnomalyStatus.Open, ProposalId = null, DecidedUtc = null },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hides a finished proposal from the dashboard, or brings it back. Changes nothing in Home Assistant:
    /// a dismissed automation is still live and is still watched for entities that disappear under it.
    /// </summary>
    public async Task<Operation<Proposal>> DismissAsync(long id, bool dismissed, CancellationToken cancellationToken)
    {
        var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return Operation<Proposal>.NotFound($"Proposal {id} was not found.");

        // A draft is a decision waiting to be made, not a record to be filed away. Hiding one would lose it.
        if (proposal.Status == ProposalStatus.Draft)
            return Operation<Proposal>.Conflict($"Proposal {id} is still a draft. Create it or discard it rather than dismissing it.");

        var now = clock.GetUtcNow();

        if (!await store.SetDismissedAsync(id, dismissed ? now : null, cancellationToken).ConfigureAwait(false))
            return Operation<Proposal>.Conflict($"Proposal {id} cannot be hidden.");

        var updated = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);

        return updated is null
            ? Operation<Proposal>.NotFound($"Proposal {id} was not found.")
            : Operation<Proposal>.Ok(updated);
    }

    public async Task<Operation<Proposal>> RejectAsync(long id, CancellationToken cancellationToken)
    {
        var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return Operation<Proposal>.NotFound($"Proposal {id} was not found.");

        if (proposal.Status != ProposalStatus.Draft)
            return Operation<Proposal>.Conflict($"Proposal {id} is {proposal.Status} and can no longer be rejected.");

        var now = clock.GetUtcNow();

        if (!await store.TryClaimAsync(id, ProposalStatus.Draft, ProposalStatus.Rejected, now, null, cancellationToken)
            .ConfigureAwait(false))
            return Operation<Proposal>.Conflict($"Proposal {id} is no longer a draft and can no longer be rejected.");

        await ReopenFindingAsync(proposal, cancellationToken).ConfigureAwait(false);

        return Operation<Proposal>.Ok(proposal with { Status = ProposalStatus.Rejected, DecidedUtc = now });
    }

    /// <summary>
    /// Records a failure as a proposal of its own rather than throwing.
    ///
    /// A failed draft is something the user asked for and did not get, so it belongs in the list with the
    /// reason attached — not lost in a log with an error toast that disappears.
    /// </summary>
    private async Task<Proposal> FailAsync(
        string request,
        ProposalSource source,
        long? anomalyId,
        Proposal? parent,
        string? feedback,
        DateTimeOffset now,
        string error,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Could not draft an automation for {Request}: {Error}", request, error);

        return await store.AddProposalAsync(new Proposal
        {
            Request = request,
            Source = source,
            Status = ProposalStatus.Failed,
            Feedback = feedback,
            ParentId = parent?.Id,
            AnomalyId = anomalyId,
            CreatedUtc = now,
            DecidedUtc = now,
            Error = error,
        }, cancellationToken).ConfigureAwait(false);
    }
}
