using Microsoft.Extensions.Logging;

namespace HearthSense.Core;

public sealed record ScanReport(int Observed, int NewSamples, int Raised, int Pruned);

/// <summary>
/// Polls Home Assistant, keeps a compact history of state changes, and lists what looks off.
/// It never notifies and never writes to Home Assistant — a finding is only ever a suggestion the
/// user can dismiss or promote into a real automation.
/// </summary>
public sealed class AnomalyScanner(
    IHomeAssistant homeAssistant,
    IStore store,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<AnomalyScanner> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs one scan. Single-flight, so a manual trigger cannot overlap the scheduled one.</summary>
    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScanReport> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var scan = settings.Current.Scan;
        var now = clock.GetUtcNow();

        var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var raised = 0;

        // Automations HearthSense created whose entities have since gone. An empty entity list is a Home
        // Assistant hiccup rather than an empty house, so it must not read as "everything is missing".
        if (entities.Count > 0)
            raised += await CheckCreatedAutomationsAsync(entities, scan, now, cancellationToken).ConfigureAwait(false);

        var watched = EntityIndex.Filter(entities, scan);
        if (watched.Count == 0) return new ScanReport(0, 0, raised, 0);

        var inserted = await RecordAsync(watched, cancellationToken).ConfigureAwait(false);

        var history = await store.GetSamplesAsync(now - scan.History, cancellationToken).ConfigureAwait(false);

        foreach (var entity in watched)
        {
            var samples = history.TryGetValue(entity.EntityId, out var found) ? found : [];

            foreach (var anomaly in AnomalyDetection.Detect(entity, samples, scan, now))
                if (await TryRaiseAsync(anomaly, scan, now, cancellationToken).ConfigureAwait(false)) raised++;
        }

        var pruned = await store.PruneSamplesAsync(now - scan.History, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Scan observed {Observed} entities, stored {NewSamples} new samples, raised {Raised} anomalies, pruned {Pruned} rows.",
            watched.Count, inserted, raised, pruned);

        return new ScanReport(watched.Count, inserted, raised, pruned);
    }

    /// <summary>Flags automations we created that now reference entities Home Assistant no longer has.</summary>
    private async Task<int> CheckCreatedAutomationsAsync(
        IReadOnlyList<HaEntity> entities,
        ScanOptions scan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(entities.Select(e => e.EntityId), StringComparer.Ordinal);
        var created = await store.ListProposalsAsync(ProposalStatus.Created, 500, cancellationToken).ConfigureAwait(false);

        var raised = 0;
        foreach (var proposal in created)
        {
            var anomaly = AnomalyDetection.DetectMissingEntities(proposal, known, now);
            if (anomaly is not null && await TryRaiseAsync(anomaly, scan, now, cancellationToken).ConfigureAwait(false)) raised++;
        }

        return raised;
    }

    /// <summary>Stores a sample only when the entity's last change is newer than what we already hold.</summary>
    private async Task<int> RecordAsync(IReadOnlyList<HaEntity> watched, CancellationToken cancellationToken)
    {
        var latest = await store.GetLatestSampleTimesAsync(cancellationToken).ConfigureAwait(false);

        List<(string EntityId, StateSample Sample)> fresh = [];
        foreach (var entity in watched)
        {
            if (latest.TryGetValue(entity.EntityId, out var seen) && entity.LastChanged <= seen) continue;
            fresh.Add((entity.EntityId, new StateSample(entity.State, entity.Numeric, entity.LastChanged)));
        }

        return fresh.Count == 0 ? 0 : await store.AddSamplesAsync(fresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One finding failing to persist must not stop the rest of the scan.</summary>
    private async Task<bool> TryRaiseAsync(Anomaly anomaly, ScanOptions scan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await RaiseAsync(anomaly, scan, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record anomaly {DedupKey}.", anomaly.DedupKey);
            return false;
        }
    }

    /// <summary>
    /// Inserts, refreshes or suppresses a finding. A dismissal is respected until
    /// <see cref="ScanOptions.RedetectAfter"/> has passed, so the same nag cannot come straight back.
    /// </summary>
    private async Task<bool> RaiseAsync(Anomaly anomaly, ScanOptions scan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await store.FindAnomalyAsync(anomaly.DedupKey, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            await store.UpsertAnomalyAsync(anomaly, cancellationToken).ConfigureAwait(false);
            return true;
        }

        switch (existing.Status)
        {
            case AnomalyStatus.Open:
                await store.UpdateAnomalyAsync(
                    existing with { Summary = anomaly.Summary, EvidenceJson = anomaly.EvidenceJson, SuggestedRequest = anomaly.SuggestedRequest },
                    cancellationToken).ConfigureAwait(false);
                return false;

            case AnomalyStatus.Dismissed when now - (existing.DecidedUtc ?? existing.DetectedUtc) >= scan.RedetectAfter:
                await store.UpdateAnomalyAsync(
                    existing with
                    {
                        Status = AnomalyStatus.Open,
                        Summary = anomaly.Summary,
                        EvidenceJson = anomaly.EvidenceJson,
                        SuggestedRequest = anomaly.SuggestedRequest,
                        DetectedUtc = now,
                        DecidedUtc = null,
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }
}
