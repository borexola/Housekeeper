using Microsoft.Extensions.Logging;

namespace HearthSense.Core;

/// <summary>
/// The main flow: a sentence becomes a shortlist, the shortlist becomes a draft, the draft is verified
/// against real entities and checked for duplicates, and only a human turns it into a live automation.
/// Nothing here writes to Home Assistant until <see cref="ConfirmAsync"/>.
/// </summary>
public sealed class ProposalService(
    IHomeAssistant homeAssistant,
    ILlmClient llm,
    IStore store,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<ProposalService> logger)
{
    private const string Unsupported = "UNSUPPORTED";

    public Task<Proposal> DraftAsync(
        string request,
        ProposalSource source,
        long? anomalyId,
        CancellationToken cancellationToken) =>
        DraftCoreAsync(request, source, anomalyId, parent: null, feedback: null, cancellationToken);

    /// <summary>
    /// Re-drafts with the user's objection in hand. The previous draft is superseded only if the new one is
    /// usable; a refinement that fails leaves the original there to confirm or discard.
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

        if (refined.Status == ProposalStatus.Failed) return Operation<Proposal>.Ok(refined);

        var now = clock.GetUtcNow();
        await store.UpdateProposalAsync(parent with { Status = ProposalStatus.Superseded, DecidedUtc = now }, cancellationToken)
            .ConfigureAwait(false);

        // A finding that was promoted into the old draft now points at the new one.
        if (parent.AnomalyId is { } anomalyId)
        {
            var anomaly = await store.GetAnomalyAsync(anomalyId, cancellationToken).ConfigureAwait(false);
            if (anomaly is { Status: AnomalyStatus.Promoted } && anomaly.ProposalId == parent.Id)
                await store.UpdateAnomalyAsync(anomaly with { ProposalId = refined.Id }, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Proposal {ParentId} superseded by refined proposal {ProposalId}.", parent.Id, refined.Id);
        return Operation<Proposal>.Ok(refined);
    }

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

        var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var known = new HashSet<string>(entities.Select(e => e.EntityId), StringComparer.Ordinal);

        // For a refinement the feedback, and the entities already in play, are part of what to look for.
        var focus = parent is null ? request : $"{request} {feedback} {string.Join(' ', parent.Entities)}";
        var candidates = EntityIndex.Shortlist(entities, focus, options.Llm.MaxCandidateEntities);

        if (candidates.Count == 0)
            return await FailAsync(request, source, anomalyId, parent, feedback, now,
                "No entities in Home Assistant relate to that request. Try naming a device or area.", cancellationToken)
                .ConfigureAwait(false);

        var raw = await llm
            .CompleteJsonAsync(Prompts.System, Prompts.User(request, candidates, parent?.ConfigJson, feedback), cancellationToken)
            .ConfigureAwait(false);

        if (raw is null)
            return await FailAsync(request, source, anomalyId, parent, feedback, now,
                $"The {llm.Name} endpoint did not answer. Check that it is running and the model is pulled.",
                cancellationToken).ConfigureAwait(false);

        var parsed = AutomationDrafting.Parse(raw, known);
        if (parsed.Draft is not { } draft)
            return await FailAsync(request, source, anomalyId, parent, feedback, now,
                parsed.Error ?? "The draft was rejected.", cancellationToken).ConfigureAwait(false);

        if (string.Equals(draft.Alias, Unsupported, StringComparison.OrdinalIgnoreCase))
            return await FailAsync(request, source, anomalyId, parent, feedback, now,
                draft.Description ?? "The model could not build this from the available entities.", cancellationToken)
                .ConfigureAwait(false);

        // A failure to read existing automations should not block drafting; it only costs duplicate detection.
        IReadOnlyList<ExistingAutomation> existing = [];
        try
        {
            existing = await homeAssistant.GetAutomationsAsync(cancellationToken).ConfigureAwait(false);
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
            Duplicates = DuplicateFinder.Find(draft, existing),
            AnomalyId = anomalyId,
            CreatedUtc = now,
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Drafted proposal {ProposalId} '{Alias}' touching {EntityCount} entities with {DuplicateCount} possible duplicate(s).",
            proposal.Id, proposal.Alias, proposal.Entities.Count, proposal.Duplicates.Count);

        return proposal;
    }

    /// <summary>Writes the proposal to Home Assistant. This is the only method that changes the user's home.</summary>
    public async Task<Operation<Proposal>> ConfirmAsync(long id, CancellationToken cancellationToken)
    {
        var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return Operation<Proposal>.NotFound($"Proposal {id} was not found.");

        if (proposal.Status != ProposalStatus.Draft)
            return Operation<Proposal>.Conflict($"Proposal {id} is {proposal.Status} and can no longer be confirmed.");

        if (string.IsNullOrWhiteSpace(proposal.ConfigJson))
            return Operation<Proposal>.Conflict($"Proposal {id} has no automation to write.");

        var now = clock.GetUtcNow();
        var automationId = now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            var created = await homeAssistant.CreateAutomationAsync(automationId, proposal.ConfigJson, cancellationToken)
                .ConfigureAwait(false);

            var confirmed = proposal with
            {
                Status = ProposalStatus.Created,
                HaAutomationId = created,
                DecidedUtc = now,
                Error = null,
            };

            await store.UpdateProposalAsync(confirmed, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Created automation {AutomationId} in Home Assistant from proposal {ProposalId}.", created, id);

            return Operation<Proposal>.Ok(confirmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = proposal with { Status = ProposalStatus.Failed, DecidedUtc = now, Error = ex.Message };
            await store.UpdateProposalAsync(failed, cancellationToken).ConfigureAwait(false);
            logger.LogError(ex, "Home Assistant refused the automation from proposal {ProposalId}.", id);

            return Operation<Proposal>.Failed(ex.Message);
        }
    }

    public async Task<Operation<Proposal>> RejectAsync(long id, CancellationToken cancellationToken)
    {
        var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
        if (proposal is null) return Operation<Proposal>.NotFound($"Proposal {id} was not found.");

        if (proposal.Status != ProposalStatus.Draft)
            return Operation<Proposal>.Conflict($"Proposal {id} is {proposal.Status} and can no longer be rejected.");

        var rejected = proposal with { Status = ProposalStatus.Rejected, DecidedUtc = clock.GetUtcNow() };
        await store.UpdateProposalAsync(rejected, cancellationToken).ConfigureAwait(false);

        // A rejected suggestion should not bury the finding that produced it.
        if (proposal.AnomalyId is { } anomalyId)
        {
            var anomaly = await store.GetAnomalyAsync(anomalyId, cancellationToken).ConfigureAwait(false);
            if (anomaly is { Status: AnomalyStatus.Promoted })
                await store.UpdateAnomalyAsync(
                    anomaly with { Status = AnomalyStatus.Open, ProposalId = null, DecidedUtc = null },
                    cancellationToken).ConfigureAwait(false);
        }

        return Operation<Proposal>.Ok(rejected);
    }

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
