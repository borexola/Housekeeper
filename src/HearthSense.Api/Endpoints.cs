using System.Text.Json.Nodes;
using HearthSense.Core;

namespace HearthSense.Api;

public sealed record DraftRequest(string? Request);

public sealed record RefineRequest(string? Feedback);

public sealed record ProposalView(
    long Id,
    string Request,
    string Source,
    string Status,
    string? Feedback,
    long? ParentId,
    string? Alias,
    string? Description,
    JsonNode? Config,
    string? Yaml,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Actions,
    IReadOnlyList<DuplicateMatch> Duplicates,
    string? HaAutomationId,
    string? Error,
    long? AnomalyId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DecidedUtc);

public sealed record AnomalyView(
    long Id,
    string EntityId,
    string Kind,
    string Status,
    string Summary,
    string SuggestedRequest,
    JsonNode? Evidence,
    long? ProposalId,
    DateTimeOffset DetectedUtc,
    DateTimeOffset? DecidedUtc);

public static class Endpoints
{
    public static IEndpointRouteBuilder MapHearthSense(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("hearthsense");

        api.MapGet("/status", (ISettingsProvider settings, SecretStore secrets, ILlmClient llm) =>
        {
            var options = settings.Current;

            return Results.Json(new
            {
                name = "HearthSense",
                version = Version,
                model = llm.Name,
                homeAssistant = options.HomeAssistant.BaseUrl,
                scanning = options.Scan.Enabled,
                scanInterval = options.Scan.Interval,
                watching = options.Scan.IncludeAll ? ["*"] : options.Scan.Include,
                // Enough for the dashboard to tell someone their install is not finished yet.
                ready = !string.IsNullOrWhiteSpace(
                    secrets.Resolve(SecretStore.HomeAssistantToken, options.HomeAssistant.TokenEnvironmentVariable)),
            });
        })
        .WithSummary("What this instance is pointed at, whether scanning is on, and whether it is configured yet.");

        // ---- proposals ----

        api.MapPost("/proposals", async (
            DraftRequest body,
            ProposalService proposals,
            CancellationToken cancellationToken) =>
        {
            var request = body.Request?.Trim();
            if (string.IsNullOrWhiteSpace(request))
                return Results.BadRequest(new { error = "Describe the automation you want in plain language." });

            if (request.Length > 600)
                return Results.BadRequest(new { error = "That request is too long; keep it under 600 characters." });

            var proposal = await proposals.DraftAsync(request, ProposalSource.User, null, cancellationToken)
                .ConfigureAwait(false);

            return proposal.Status == ProposalStatus.Failed
                ? Results.UnprocessableEntity(View(proposal))
                : Results.Ok(View(proposal));
        })
        .WithSummary("Drafts an automation from a sentence. Writes nothing to Home Assistant.");

        api.MapGet("/proposals", async (
            string? status,
            int? limit,
            IStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<ProposalStatus>(status, out var parsed))
                return Results.BadRequest(new { error = $"Unknown status '{status}'." });

            var found = await store.ListProposalsAsync(parsed, limit ?? 50, cancellationToken).ConfigureAwait(false);
            return Results.Ok(found.Select(View));
        })
        .WithSummary("Lists proposals, newest first.");

        api.MapGet("/proposals/{id:long}", async (long id, IStore store, CancellationToken cancellationToken) =>
        {
            var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
            return proposal is null ? Results.NotFound(new { error = "Proposal not found." }) : Results.Ok(View(proposal));
        })
        .WithSummary("Gets one proposal, including its YAML preview and duplicate warnings.");

        api.MapPost("/proposals/{id:long}/confirm", async (
            long id,
            ProposalService proposals,
            CancellationToken cancellationToken) =>
            Map(await proposals.ConfirmAsync(id, cancellationToken).ConfigureAwait(false)))
        .WithSummary("Writes the proposal to Home Assistant. The only call that changes your home.");

        api.MapPost("/proposals/{id:long}/reject", async (
            long id,
            ProposalService proposals,
            CancellationToken cancellationToken) =>
            Map(await proposals.RejectAsync(id, cancellationToken).ConfigureAwait(false)))
        .WithSummary("Discards a draft.");

        api.MapPost("/proposals/{id:long}/refine", async (
            long id,
            RefineRequest body,
            ProposalService proposals,
            CancellationToken cancellationToken) =>
        {
            var feedback = body.Feedback?.Trim();
            if (string.IsNullOrWhiteSpace(feedback))
                return Results.BadRequest(new { error = "Say what should change about the draft." });

            if (feedback.Length > 600)
                return Results.BadRequest(new { error = "That feedback is too long; keep it under 600 characters." });

            var result = await proposals.RefineAsync(id, feedback, cancellationToken).ConfigureAwait(false);
            if (result.Status != OperationStatus.Ok) return Map(result);

            // A refinement the validator rejected leaves the original draft in place; report why.
            return result.Value!.Status == ProposalStatus.Failed
                ? Results.UnprocessableEntity(View(result.Value))
                : Results.Ok(View(result.Value));
        })
        .WithSummary("Re-drafts with your feedback. The old draft is superseded; nothing is written to Home Assistant.");

        // ---- anomalies ----

        api.MapGet("/anomalies", async (
            string? status,
            int? limit,
            IStore store,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<AnomalyStatus>(status, out var parsed))
                return Results.BadRequest(new { error = $"Unknown status '{status}'." });

            var found = await store.ListAnomaliesAsync(parsed, limit ?? 50, cancellationToken).ConfigureAwait(false);
            return Results.Ok(found.Select(View));
        })
        .WithSummary("Lists what the scanner noticed. Nothing here has notified anyone.");

        api.MapPost("/anomalies/{id:long}/dismiss", async (
            long id,
            IStore store,
            TimeProvider clock,
            ISettingsProvider settings,
            CancellationToken cancellationToken) =>
        {
            var anomaly = await store.GetAnomalyAsync(id, cancellationToken).ConfigureAwait(false);
            if (anomaly is null) return Results.NotFound(new { error = "Anomaly not found." });

            if (anomaly.Status != AnomalyStatus.Open)
                return Results.Conflict(new { error = $"Anomaly {id} is already {anomaly.Status}." });

            var dismissed = anomaly with { Status = AnomalyStatus.Dismissed, DecidedUtc = clock.GetUtcNow() };
            await store.UpdateAnomalyAsync(dismissed, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                anomaly = View(dismissed),
                quietFor = settings.Current.Scan.RedetectAfter,
            });
        })
        .WithSummary("Silences a finding until the re-detect window passes.");

        api.MapPost("/anomalies/{id:long}/automate", async (
            long id,
            IStore store,
            ProposalService proposals,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var anomaly = await store.GetAnomalyAsync(id, cancellationToken).ConfigureAwait(false);
            if (anomaly is null) return Results.NotFound(new { error = "Anomaly not found." });

            if (anomaly.Status == AnomalyStatus.Promoted)
                return Results.Conflict(new { error = $"Anomaly {id} already has proposal {anomaly.ProposalId}." });

            var proposal = await proposals
                .DraftAsync(anomaly.SuggestedRequest, ProposalSource.Anomaly, anomaly.Id, cancellationToken)
                .ConfigureAwait(false);

            if (proposal.Status == ProposalStatus.Failed)
                return Results.UnprocessableEntity(View(proposal));

            await store.UpdateAnomalyAsync(
                anomaly with { Status = AnomalyStatus.Promoted, ProposalId = proposal.Id, DecidedUtc = clock.GetUtcNow() },
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(View(proposal));
        })
        .WithSummary("Turns a finding into a draft automation, still pending confirmation.");

        api.MapPost("/scan", async (AnomalyScanner scanner, CancellationToken cancellationToken) =>
            Results.Ok(await scanner.ScanAsync(cancellationToken).ConfigureAwait(false)))
        .WithSummary("Runs a scan immediately instead of waiting for the next tick.");

        return endpoints;
    }

    private static string Version =>
        typeof(Endpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    private static IResult Map(Operation<Proposal> result) => result.Status switch
    {
        OperationStatus.Ok => Results.Ok(View(result.Value!)),
        OperationStatus.NotFound => Results.NotFound(new { error = result.Error }),
        OperationStatus.Conflict => Results.Conflict(new { error = result.Error }),
        _ => Results.UnprocessableEntity(new { error = result.Error }),
    };

    private static bool TryParse<T>(string? value, out T? parsed) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }

        if (Enum.TryParse<T>(value, ignoreCase: true, out var candidate) && Enum.IsDefined(candidate))
        {
            parsed = candidate;
            return true;
        }

        parsed = null;
        return false;
    }

    internal static ProposalView View(Proposal proposal) => new(
        proposal.Id,
        proposal.Request,
        proposal.Source.ToString(),
        proposal.Status.ToString(),
        proposal.Feedback,
        proposal.ParentId,
        proposal.Alias,
        proposal.Description,
        Node(proposal.ConfigJson),
        proposal.ConfigJson is null ? null : SafeYaml(proposal.ConfigJson),
        proposal.Entities,
        proposal.Actions,
        proposal.Duplicates,
        proposal.HaAutomationId,
        proposal.Error,
        proposal.AnomalyId,
        proposal.CreatedUtc,
        proposal.DecidedUtc);

    internal static AnomalyView View(Anomaly anomaly) => new(
        anomaly.Id,
        anomaly.EntityId,
        anomaly.Kind.ToString(),
        anomaly.Status.ToString(),
        anomaly.Summary,
        anomaly.SuggestedRequest,
        Node(anomaly.EvidenceJson),
        anomaly.ProposalId,
        anomaly.DetectedUtc,
        anomaly.DecidedUtc);

    private static JsonNode? Node(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? SafeYaml(string json)
    {
        try
        {
            return Yaml.FromJson(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
