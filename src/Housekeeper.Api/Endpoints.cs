using System.Text.Json.Nodes;
using Housekeeper.Core;

namespace Housekeeper.Api;

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
    /// <summary>The automation in words, or null when there is no draft or it could not be read.</summary>
    Narrative? Story,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Actions,
    IReadOnlyList<DuplicateMatch> Duplicates,
    string? HaAutomationId,
    string? Error,
    long? AnomalyId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DecidedUtc,
    DateTimeOffset? DismissedUtc);

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
    DateTimeOffset? DecidedUtc,
    /// <summary>How far past its own bar: 1 at the bar, one more per doubling, capped; missing-entity findings sit above the cap.</summary>
    double Severity,
    /// <summary>How many times the user has dismissed it. Each raises the bar it must clear to return; three silence it.</summary>
    int Dismissals,
    /// <summary>
    /// For an open finding, the automations the user already has that fire on it, strongest first; empty when
    /// none does. Null when that is not known -- the automations could not be read, or the finding is closed.
    /// </summary>
    IReadOnlyList<CoveringAutomation>? CoveredBy = null);

public sealed record ConcernRequest(string? Text);

/// <param name="Names">The friendly name of each watched entity, in the same order, so the card need not translate ids.</param>
/// <param name="HasRule">False when the rule is only "pay closer attention", which is not worth a label.</param>
/// <param name="Note">Why the model's reading is missing, if it is. Null when the model read it.</param>
/// <param name="Provisional">True while the model has not had its say and the scan's tick will ask again.</param>
/// <param name="CanReread">True when the model has not read this concern, so asking it again could change something.</param>
public sealed record ConcernView(
    long Id,
    string Text,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Names,
    string Rule,
    bool HasRule,
    string? Explanation,
    bool Interpreted,
    string? Note,
    bool Provisional,
    bool CanReread,
    DateTimeOffset CreatedUtc);

public static class Endpoints
{
    public static IEndpointRouteBuilder MapHousekeeper(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("housekeeper");

        api.MapGet("/status", (ISettingsProvider settings, SecretStore secrets, ILlmClient llm) =>
        {
            var options = settings.Current;
            var missing = SettingsContext.MissingFor(options, secrets);

            return Results.Json(new
            {
                name = "Housekeeper",
                version = Version,
                model = llm.Name,
                homeAssistant = options.HomeAssistant.BaseUrl,
                scanning = options.Scan.Enabled,
                scanInterval = options.Scan.Interval,
                watching = options.Scan.IncludeAll ? ["*"] : options.Scan.Include,
                // Enough for the dashboard to tell someone their install is not finished yet, and why.
                ready = missing.Count == 0,
                missing,
            });
        })
        .WithSummary("What this instance is pointed at, whether scanning is on, and whether it is configured yet.");

        api.MapGet("/insight", async (
            ISettingsProvider settings,
            IStore store,
            AnomalyScanner scanner,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var scan = settings.Current.Scan;
            var now = clock.GetUtcNow();

            var history = await scanner.HistoryAsync(scan.History, cancellationToken).ConfigureAwait(false);
            var last = scanner.Last;

            return Results.Json(new
            {
                scanning = scan.Enabled,
                // The interval the worker will really tick at, not the raw setting. The tile prints this
                // beside a countdown derived from the worker's own timer, and the two disagreeing is worse
                // than either being slightly off.
                interval = ScanWorker.IntervalOf(scan),
                keepFor = scan.History,
                minimumSamples = scan.MinimumSamples,
                maxTracked = scan.MaxTrackedEntities,
                watching = scan.IncludeAll ? ["*"] : scan.Include,
                history = new
                {
                    entities = history.Entities,
                    samples = history.Samples,
                    oldestUtc = history.OldestUtc,
                },
                last = last is null ? null : new
                {
                    atUtc = last.FinishedUtc,
                    tookMs = (long)last.Took.TotalMilliseconds,
                    observed = last.Report?.Observed,
                    visible = last.Report?.Visible,
                    newSamples = last.Report?.NewSamples,
                    raised = last.Report?.Raised,
                    resolved = last.Report?.Resolved,
                    judged = last.Report?.Judged,
                    backfilled = last.Report?.Backfilled,
                    routines = last.Report?.Routines,
                    error = last.Error,
                },
                nextUtc = scan.Enabled ? scanner.NextUtc : null,
                // The last search for routines, so the page can say it is happening and why there are none yet.
                routines = scanner.LastRoutineSearch is not { } search ? null : new
                {
                    atUtc = search.AtUtc,
                    entities = search.Entities,
                    found = search.Found,
                    offered = search.Offered,
                    automated = search.Automated,
                    machineMade = search.MachineMade,
                    skipped = search.Skipped,
                    learning = scan.LearnHabits,
                    minimumTimes = scan.HabitMinimumTimes,
                    minimumDays = Habits.MinimumDays,
                },
            });
        })
        .WithSummary("What the scanner is watching, what history it holds, what its last run did, and what its last search for routines found.");

        api.MapGet("/suggestions", async (
            IHomeAssistant homeAssistant,
            IStore store,
            ILogger<NameBook> logger,
            CancellationToken cancellationToken) =>
        {
            // What the house's own history says the user does by hand comes first: it is a request that
            // would work here AND one they have already shown they want. The generic examples fill in.
            var routines = (await store.ListAnomaliesAsync(AnomalyStatus.Open, 200, includeClosed: false, cancellationToken).ConfigureAwait(false))
                .Where(anomaly => anomaly.Kind == AnomalyKind.Habit)
                .OrderByDescending(anomaly => anomaly.Severity)
                .Select(anomaly => Node(anomaly.EvidenceJson)?["spoken"]?.GetValue<string>())
                .Where(spoken => !string.IsNullOrWhiteSpace(spoken))
                .Take(3)
                .ToList();

            // A nicety, never an error: a Home Assistant that is down or not yet configured leaves the box
            // with its placeholder, and the dashboard already says why drafting will not work.
            try
            {
                var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { suggestions = routines.Concat(Suggestions.For(entities)).Distinct(StringComparer.Ordinal).Take(8) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The routines are the house's own and need nothing from Home Assistant to be offered.
                logger.LogDebug(ex, "Could not read entities for suggestions.");
                return Results.Ok(new { suggestions = routines });
            }
        })
        .WithSummary("Example requests written from the entities this house actually has.");

        // ---- logs ----

        api.MapGet("/logs", (string? q, string? level, string? flow, int? limit, LogBuffer logs) =>
        {
            var floor = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information;
            var term = q?.Trim();
            var all = logs.Snapshot();

            var matching = all.Where(entry =>
                entry.Level >= floor &&
                (string.IsNullOrWhiteSpace(flow) || string.Equals(entry.Flow, flow, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(term) ||
                 entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 entry.Source.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 (entry.Exception?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();

            var page = matching.Take(Math.Clamp(limit ?? 300, 1, 2000)).Select(entry => new
            {
                atUtc = entry.AtUtc,
                level = entry.Level.ToString(),
                flow = entry.Flow,
                source = entry.Source,
                message = entry.Message,
                exception = entry.Exception,
            });

            return Results.Ok(new { held = all.Count, total = logs.Total, matched = matching.Count, flows = LogBuffer.Flows, entries = page });
        })
        .WithSummary("The app's recent log lines, newest first, filtered by level, flow and a search term.");

        api.MapGet("/logs/states", async (string? q, int? limit, IStore store, NameBook names, CancellationToken cancellationToken) =>
        {
            var term = q?.Trim();

            // The id filter is done in SQL; a match on the friendly name, the device or the area is done here
            // over a wider page, because those live in memory and a person searches for "freezer" or "Zigbee
            // hub" rather than for the id.
            var byId = await store.ListRecentSamplesAsync(term, Math.Clamp(limit ?? 200, 1, 2000), cancellationToken).ConfigureAwait(false);
            var rows = byId;
            if (!string.IsNullOrWhiteSpace(term) && byId.Count < (limit ?? 200))
            {
                var wide = await store.ListRecentSamplesAsync(null, 2000, cancellationToken).ConfigureAwait(false);
                var seen = new HashSet<(string, DateTimeOffset)>(byId.Select(r => (r.EntityId, r.Sample.ChangedUtc)));
                rows = [.. byId, .. wide.Where(r =>
                    !seen.Contains((r.EntityId, r.Sample.ChangedUtc)) &&
                    (Mentions(names.About(r.EntityId), term) || r.Sample.State.Contains(term, StringComparison.OrdinalIgnoreCase)))];
                rows = [.. rows.OrderByDescending(r => r.Sample.ChangedUtc).Take(Math.Clamp(limit ?? 200, 1, 2000))];
            }

            return Results.Ok(new
            {
                entries = rows.Select(r =>
                {
                    var about = names.About(r.EntityId);
                    return new
                    {
                        atUtc = r.Sample.ChangedUtc,
                        entityId = r.EntityId,
                        name = about?.Name,
                        device = about?.Device,
                        area = about?.Area,
                        state = r.Sample.State,
                        numeric = r.Sample.Numeric,
                    };
                }),
            });

            static bool Mentions(NameBook.Known? about, string term) =>
                about is not null &&
                ((about.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (about.Device?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (about.Area?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        })
        .WithSummary("The newest state changes stored from Home Assistant, by scan, live feed or backfill, newest first.");

        // ---- concerns ----

        api.MapGet("/concerns", async (ConcernService concerns, NameBook names, CancellationToken cancellationToken) =>
            Results.Ok((await concerns.ListAsync(cancellationToken).ConfigureAwait(false)).Select(concern => View(concern, names))))
        .WithSummary("What the user asked to be watched, and what each concern resolved to.");

        api.MapPost("/concerns", async (
            ConcernRequest body,
            ConcernService concerns,
            NameBook names,
            CancellationToken cancellationToken) =>
        {
            var text = body.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "Say what you are concerned about, in a few words." });

            if (text.Length > ConcernService.MaxLength)
                return Results.BadRequest(new { error = $"Keep a concern under {ConcernService.MaxLength} characters." });

            var concern = await concerns.AddAsync(text, cancellationToken).ConfigureAwait(false);
            return Results.Ok(View(concern, names));
        })
        .WithSummary("Adds a concern. The model reads it into entities and a rule where it can; otherwise it is matched by name.");

        api.MapPost("/concerns/{id:long}/reread", async (long id, ConcernService concerns, NameBook names, CancellationToken cancellationToken) =>
        {
            var concern = await concerns.ReadAgainAsync(id, cancellationToken).ConfigureAwait(false);
            return concern is null ? Results.NotFound(new { error = "Concern not found." }) : Results.Ok(View(concern, names));
        })
        .WithSummary("Asks the model to read a concern again, for one it could not read when it was added.");

        api.MapDelete("/concerns/{id:long}", async (long id, ConcernService concerns, CancellationToken cancellationToken) =>
            await concerns.RemoveAsync(id, cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new { error = "Concern not found." }))
        .WithSummary("Removes a concern. Findings it raised close on the next scan.");

        // ---- proposals ----

        api.MapPost("/proposals", async (
            DraftRequest body,
            ProposalService proposals,
            NameBook names,
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
                ? Results.UnprocessableEntity(View(proposal, names))
                : Results.Ok(View(proposal, names));
        })
        .WithSummary("Drafts an automation from a sentence. Writes nothing to Home Assistant.");

        api.MapGet("/proposals", async (
            string? status,
            int? limit,
            bool? dismissed,
            IStore store,
            NameBook names,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<ProposalStatus>(status, out var parsed))
                return Results.BadRequest(new { error = $"Unknown status '{status}'." });

            var found = await store
                .ListProposalsAsync(parsed, limit ?? 50, dismissed ?? false, cancellationToken)
                .ConfigureAwait(false);

            // "dismissed=true" means show them as well, not show only them.
            return Results.Ok(found.Select(proposal => View(proposal, names)));
        })
        .WithSummary("Lists proposals, newest first. Dismissed ones are left out unless asked for.");

        api.MapPost("/proposals/{id:long}/dismiss", async (
            long id,
            ProposalService proposals,
            NameBook names,
            CancellationToken cancellationToken) =>
            Map(await proposals.DismissAsync(id, dismissed: true, cancellationToken).ConfigureAwait(false), names))
        .WithSummary("Hides a finished proposal from the dashboard. Changes nothing in Home Assistant.");

        api.MapPost("/proposals/{id:long}/restore", async (
            long id,
            ProposalService proposals,
            NameBook names,
            CancellationToken cancellationToken) =>
            Map(await proposals.DismissAsync(id, dismissed: false, cancellationToken).ConfigureAwait(false), names))
        .WithSummary("Brings a dismissed proposal back into the list.");

        api.MapGet("/proposals/{id:long}", async (long id, IStore store, NameBook names, CancellationToken cancellationToken) =>
        {
            var proposal = await store.GetProposalAsync(id, cancellationToken).ConfigureAwait(false);
            return proposal is null ? Results.NotFound(new { error = "Proposal not found." }) : Results.Ok(View(proposal, names));
        })
        .WithSummary("Gets one proposal, including its YAML preview and duplicate warnings.");

        // Deliberately not given the request's cancellation token. This is the one call that changes the
        // user's home, and a browser tab closed mid-write would otherwise abandon it between the write to
        // Home Assistant and the record of having made it.
        api.MapPost("/proposals/{id:long}/confirm", async (long id, ProposalService proposals, NameBook names) =>
            Map(await proposals.ConfirmAsync(id, CancellationToken.None).ConfigureAwait(false), names))
        .WithSummary("Writes the proposal to Home Assistant. The only call that changes your home.");

        api.MapPost("/proposals/{id:long}/reject", async (
            long id,
            ProposalService proposals,
            NameBook names,
            CancellationToken cancellationToken) =>
            Map(await proposals.RejectAsync(id, cancellationToken).ConfigureAwait(false), names))
        .WithSummary("Discards a draft.");

        api.MapPost("/proposals/{id:long}/refine", async (
            long id,
            RefineRequest body,
            ProposalService proposals,
            NameBook names,
            CancellationToken cancellationToken) =>
        {
            var feedback = body.Feedback?.Trim();
            if (string.IsNullOrWhiteSpace(feedback))
                return Results.BadRequest(new { error = "Say what should change about the draft." });

            if (feedback.Length > 600)
                return Results.BadRequest(new { error = "That feedback is too long; keep it under 600 characters." });

            var result = await proposals.RefineAsync(id, feedback, cancellationToken).ConfigureAwait(false);
            if (result.Status != OperationStatus.Ok) return Map(result, names);

            // A refinement the validator rejected leaves the original draft in place; report why.
            return result.Value!.Status == ProposalStatus.Failed
                ? Results.UnprocessableEntity(View(result.Value, names))
                : Results.Ok(View(result.Value, names));
        })
        .WithSummary("Re-drafts with your feedback. The old draft is superseded; nothing is written to Home Assistant.");

        // ---- anomalies ----

        api.MapGet("/anomalies", async (
            string? status,
            int? limit,
            bool? closed,
            IStore store,
            AnomalyScanner scanner,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<AnomalyStatus>(status, out var parsed))
                return Results.BadRequest(new { error = $"Unknown status '{status}'." });

            var found = await store
                .ListAnomaliesAsync(parsed, limit ?? 50, closed ?? false, cancellationToken)
                .ConfigureAwait(false);

            // Worked out now, from the automations and states the last scan took, rather than stored on the
            // finding: an automation deleted or switched off since is never named, whichever cards the scan
            // happened to refresh.
            var known = scanner.Automations;
            return Results.Ok(found.Select(anomaly => View(anomaly, CoveredBy(anomaly, known))));
        })
        .WithSummary("Lists what the scanner noticed, and for an open finding any automation that already fires on it. Dismissed and resolved ones are left out unless asked for.");

        api.MapGet("/anomalies/summary", async (IStore store, CancellationToken cancellationToken) =>
        {
            var open = await store.ListAnomaliesAsync(AnomalyStatus.Open, 1000, includeClosed: false, cancellationToken).ConfigureAwait(false);

            // A routine is an offer, not a problem; it is counted apart so the badge means "something is
            // wrong" and nothing else.
            return Results.Ok(new
            {
                open = open.Count(anomaly => anomaly.Kind != AnomalyKind.Habit),
                serious = open.Count(IsSerious),
                habits = open.Count(anomaly => anomaly.Kind == AnomalyKind.Habit),
            });
        })
        .WithSummary("How many findings are open, how many of them are serious or were asked for, and how many routines are on offer. Drives the menu badge.");

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

            // Counted, because a dismissal is the user teaching the detector: the finding has to be further
            // past its bar to come back, and after three it stays quiet for good. A routine put away is
            // quiet for good from the first.
            var dismissed = anomaly with
            {
                Status = AnomalyStatus.Dismissed,
                DecidedUtc = clock.GetUtcNow(),
                Dismissals = anomaly.Dismissals + 1,
            };
            await store.UpdateAnomalyAsync(dismissed, cancellationToken).ConfigureAwait(false);

            var silenced = anomaly.Kind == AnomalyKind.Habit || dismissed.Dismissals >= AnomalyScanner.DismissalsToSilence;
            return Results.Ok(new
            {
                anomaly = View(dismissed),
                quietFor = settings.Current.Scan.RedetectAfter,
                dismissals = dismissed.Dismissals,
                silenced,
                // What the card can say, so the same rule is worded in one place.
                note = silenced
                    ? (anomaly.Kind == AnomalyKind.Habit
                        ? "Put away. This routine will not be suggested again."
                        : "Dismissed for the third time, so it will not be raised again.")
                    : dismissed.Dismissals == 1
                        ? "Dismissed. If it comes back after the quiet period it will have to be further over the line; a third dismissal silences it for good."
                        : "Dismissed again. It now has to be well over the line to come back; one more dismissal silences it for good.",
            });
        })
        .WithSummary("Silences a finding until the re-detect window passes, and remembers the dismissal so the finding has to be further over the line to return; three silence it for good.");

        api.MapPost("/anomalies/{id:long}/ignore", async (
            long id,
            string? scope,
            IStore store,
            IHomeAssistant homeAssistant,
            SettingsContext settings,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var anomaly = await store.GetAnomalyAsync(id, cancellationToken).ConfigureAwait(false);
            if (anomaly is null) return Results.NotFound(new { error = "Anomaly not found." });

            if (anomaly.Kind == AnomalyKind.MissingEntity)
                return Results.UnprocessableEntity(new { error = "This finding is about an automation, not an entity. Fix or delete the automation instead." });

            var byDevice = string.Equals(scope, "device", StringComparison.OrdinalIgnoreCase);
            if (!byDevice && !string.IsNullOrWhiteSpace(scope) && !string.Equals(scope, "entity", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Scope must be 'entity' or 'device'." });

            List<string> ignored = [anomaly.EntityId];
            string? device = null;

            if (byDevice)
            {
                var entities = await homeAssistant.GetEntitiesAsync(cancellationToken).ConfigureAwait(false);
                var subject = entities.FirstOrDefault(e => e.EntityId == anomaly.EntityId);
                if (subject?.DeviceId is null)
                    return Results.UnprocessableEntity(new { error = $"Home Assistant reports no device for {anomaly.EntityId}. Ignore the entity instead." });

                device = subject.DeviceName ?? subject.DeviceId;
                ignored = [.. entities
                    .Where(e => e.DeviceId == subject.DeviceId)
                    .Select(e => e.EntityId)
                    .Order(StringComparer.Ordinal)];
            }

            var saved = settings.Ignore(ignored);
            if (saved.Errors.Count > 0)
                return Results.UnprocessableEntity(new { error = string.Join(" ", saved.Errors) });

            // Nothing about these entities is watched any more, so nothing already raised should linger.
            var now = clock.GetUtcNow();
            var set = new HashSet<string>(ignored, StringComparer.Ordinal);
            var dismissed = 0;
            foreach (var open in await store.ListAnomaliesAsync(AnomalyStatus.Open, 1000, includeClosed: false, cancellationToken).ConfigureAwait(false))
            {
                if (open.Kind == AnomalyKind.MissingEntity || !set.Contains(open.EntityId)) continue;

                // A routine is closed rather than dismissed. "Not this one" is permanent -- the row is never
                // pruned and never re-offered -- and ignoring an entity for an unrelated finding is not the
                // user saying that about a routine they were never asked about. Closed, it prunes normally
                // and is offered again if the entity is watched once more.
                var closed = open.Kind == AnomalyKind.Habit
                    ? open with
                    {
                        Status = AnomalyStatus.Resolved,
                        DecidedUtc = now,
                        EvidenceJson = AnomalyScanner.WithReason(open.EvidenceJson, "It is no longer on the watch list."),
                    }
                    : open with { Status = AnomalyStatus.Dismissed, DecidedUtc = now };

                await store.UpdateAnomalyAsync(closed, cancellationToken).ConfigureAwait(false);
                dismissed++;
            }

            return Results.Ok(new { ignored, device, dismissed });
        })
        .WithSummary("Stops watching the finding's entity, or every entity on its device (scope=device), until it is removed from Scan → Ignore.");

        api.MapPost("/anomalies/{id:long}/automate", async (
            long id,
            IStore store,
            ProposalService proposals,
            NameBook names,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var anomaly = await store.GetAnomalyAsync(id, cancellationToken).ConfigureAwait(false);
            if (anomaly is null) return Results.NotFound(new { error = "Anomaly not found." });

            // Only refused while the draft it points at is still a live decision. Promoted used to be a
            // state with no way out: if the draft failed, was discarded, or the row simply went, the finding
            // stayed on the dashboard with every button on it answering 409 forever.
            if (anomaly.Status == AnomalyStatus.Promoted && anomaly.ProposalId is { } existingId)
            {
                var existing = await store.GetProposalAsync(existingId, cancellationToken).ConfigureAwait(false);
                if (existing is { Status: ProposalStatus.Draft or ProposalStatus.Created })
                    return Results.Conflict(new { error = $"Anomaly {id} already has proposal {existingId}." });
            }

            var proposal = await proposals
                .DraftAsync(anomaly.SuggestedRequest, ProposalSource.Anomaly, anomaly.Id, cancellationToken)
                .ConfigureAwait(false);

            if (proposal.Status == ProposalStatus.Failed)
                return Results.UnprocessableEntity(View(proposal, names));

            // Written with an uncancellable token: the draft exists now, and a browser that gave up while the
            // model was thinking must not leave it orphaned with the finding still unpromoted.
            await store.UpdateAnomalyAsync(
                anomaly with { Status = AnomalyStatus.Promoted, ProposalId = proposal.Id, DecidedUtc = clock.GetUtcNow() },
                CancellationToken.None).ConfigureAwait(false);

            return Results.Ok(View(proposal, names));
        })
        .WithSummary("Turns a finding into a draft automation, still pending confirmation.");

        api.MapPost("/scan", async (AnomalyScanner scanner, CancellationToken cancellationToken) =>
            Results.Ok(await scanner.ScanAsync(cancellationToken).ConfigureAwait(false)))
        .WithSummary("Runs a scan immediately instead of waiting for the next tick.");

        return endpoints;
    }

    /// <summary>
    /// What turns the menu badge red: something the user asked to watch for, an automation of theirs that
    /// no longer works, a finding about a concerned entity, or one far past its own bar — eight times over,
    /// which is where the log scale puts "far".
    /// </summary>
    internal static bool IsSerious(Anomaly anomaly) =>
        anomaly.Kind != AnomalyKind.Habit &&
        (anomaly.Kind is AnomalyKind.Concern or AnomalyKind.MissingEntity ||
         anomaly.Severity >= 4 ||
         anomaly.EvidenceJson.Contains("\"concern\":", StringComparison.Ordinal));

    private static string Version =>
        typeof(Endpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    private static IResult Map(Operation<Proposal> result, NameBook names) => result.Status switch
    {
        OperationStatus.Ok => Results.Ok(View(result.Value!, names)),
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

    internal static ProposalView View(Proposal proposal, NameBook names) => new(
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
        AutomationNarrator.Describe(proposal.ConfigJson, names.NameOf, id => names.About(id)?.DeviceClass),
        proposal.Entities,
        proposal.Actions,
        proposal.Duplicates,
        proposal.HaAutomationId,
        proposal.Error,
        proposal.AnomalyId,
        proposal.CreatedUtc,
        proposal.DecidedUtc,
        proposal.DismissedUtc);

    internal static ConcernView View(Concern concern, NameBook names) => new(
        concern.Id,
        concern.Text,
        concern.Entities,
        [.. concern.Entities.Select(id => names.NameOf(id) ?? id)],
        concern.Rule.Describe(RuleLabel(concern, names)),
        concern.Rule.Kind != WatchKind.Any,
        concern.Explanation,
        concern.Interpreted,
        concern.Note,
        concern.Provisional,
        !concern.Interpreted,
        concern.CreatedUtc);

    /// <summary>
    /// A held-state rule said in the words the entities it watches use, but only where they all use the
    /// same ones: "stays on" covers a door and a lamp, "stays open" is wrong about the lamp, and a concern
    /// matched by name alone routinely spans both. Null leaves the rule in the raw state, which is at
    /// least never wrong about any of them.
    /// </summary>
    private static string? RuleLabel(Concern concern, NameBook names)
    {
        if (concern.Rule.State is not { } state) return null;

        string? agreed = null;
        foreach (var entityId in concern.Entities)
        {
            var label = names.LabelFor(entityId, state);
            if (agreed is null) agreed = label;
            else if (!string.Equals(agreed, label, StringComparison.Ordinal)) return null;
        }

        return agreed;
    }

    internal static AnomalyView View(Anomaly anomaly, IReadOnlyList<CoveringAutomation>? coveredBy = null) => new(
        anomaly.Id,
        anomaly.EntityId,
        anomaly.Kind.ToString(),
        anomaly.Status.ToString(),
        anomaly.Summary,
        anomaly.SuggestedRequest,
        Node(anomaly.EvidenceJson),
        anomaly.ProposalId,
        anomaly.DetectedUtc,
        anomaly.DecidedUtc,
        anomaly.Severity,
        anomaly.Dismissals,
        coveredBy);

    /// <summary>
    /// The automations that already fire on an open finding. Null when there is no telling -- the scanner has
    /// not read the automations, or could not -- which the card treats exactly like "none": it offers to draft
    /// one, and says nothing either way about what already exists.
    /// </summary>
    internal static IReadOnlyList<CoveringAutomation>? CoveredBy(Anomaly anomaly, KnownAutomations? known) =>
        anomaly.Status != AnomalyStatus.Open || known is null ? null : Coverage.Find(anomaly, known);

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
