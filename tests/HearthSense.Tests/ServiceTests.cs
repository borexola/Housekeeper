using HearthSense.Api;
using HearthSense.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HearthSense.Tests;

/// <summary>A real SQLite store on a temp file, so the services are exercised against real SQL.</summary>
public abstract class StoreFixture : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hearthsense-test-{Guid.NewGuid():N}.db");

    protected string ConnectionString => SqliteStore.ConnectionStringFor(_path);
    protected SqliteStore Store { get; private set; } = null!;
    protected FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        Store = new SqliteStore(SqliteStore.ConnectionStringFor(_path));
        await Store.InitialiseAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { /* best effort */ }
        }

        return Task.CompletedTask;
    }
}

public class SqliteStoreTests : StoreFixture
{
    [Fact]
    public async Task Proposals_round_trip_with_their_lists_intact()
    {
        var saved = await Store.AddProposalAsync(new Proposal
        {
            Request = "turn off the lights",
            Status = ProposalStatus.Draft,
            Alias = "Lights off",
            ConfigJson = """{"alias":"Lights off"}""",
            Entities = ["light.hall"],
            Actions = ["light.turn_off"],
            Duplicates = [new DuplicateMatch("7", "Existing", 0.9, "shares an entity")],
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        Assert.True(saved.Id > 0);

        var loaded = await Store.GetProposalAsync(saved.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Lights off", loaded.Alias);
        Assert.Equal(["light.hall"], loaded.Entities);
        Assert.Equal(0.9, loaded.Duplicates[0].Score);
        Assert.Equal(Clock.GetUtcNow(), loaded.CreatedUtc);
    }

    [Fact]
    public async Task Listing_filters_by_status_and_returns_newest_first()
    {
        for (var i = 0; i < 3; i++)
            await Store.AddProposalAsync(new Proposal
            {
                Request = $"request {i}",
                Status = i == 1 ? ProposalStatus.Created : ProposalStatus.Draft,
                CreatedUtc = Clock.GetUtcNow(),
            }, CancellationToken.None);

        var drafts = await Store.ListProposalsAsync(ProposalStatus.Draft, 10, CancellationToken.None);
        var all = await Store.ListProposalsAsync(null, 10, CancellationToken.None);

        Assert.Equal(2, drafts.Count);
        Assert.Equal(3, all.Count);
        Assert.Equal("request 2", all[0].Request);
    }

    [Fact]
    public async Task Samples_are_deduplicated_grouped_and_prunable()
    {
        var now = Clock.GetUtcNow();

        var first = await Store.AddSamplesAsync(
        [
            ("sensor.a", new StateSample("1", 1, now.AddHours(-3))),
            ("sensor.a", new StateSample("2", 2, now.AddHours(-2))),
            ("sensor.b", new StateSample("x", null, now.AddHours(-1))),
        ], CancellationToken.None);

        // The same change arriving on a later poll must not create a second row.
        var second = await Store.AddSamplesAsync(
            [("sensor.a", new StateSample("1", 1, now.AddHours(-3)))], CancellationToken.None);

        Assert.Equal(3, first);
        Assert.Equal(0, second);

        var grouped = await Store.GetSamplesAsync(now.AddDays(-1), CancellationToken.None);
        Assert.Equal(2, grouped["sensor.a"].Count);
        Assert.Equal(now.AddHours(-3), grouped["sensor.a"][0].ChangedUtc);

        var latest = await Store.GetLatestSampleTimesAsync(CancellationToken.None);
        Assert.Equal(now.AddHours(-2), latest["sensor.a"]);

        Assert.Equal(2, await Store.PruneSamplesAsync(now.AddHours(-1.5), CancellationToken.None));
    }

    [Fact]
    public async Task Anomalies_upsert_on_their_dedup_key()
    {
        var anomaly = new Anomaly
        {
            DedupKey = "stuck:binary_sensor.freezer_door",
            EntityId = "binary_sensor.freezer_door",
            Kind = AnomalyKind.StuckState,
            Summary = "open for 12 minutes",
            SuggestedRequest = "notify me",
            DetectedUtc = Clock.GetUtcNow(),
        };

        var inserted = await Store.UpsertAnomalyAsync(anomaly, CancellationToken.None);
        var again = await Store.UpsertAnomalyAsync(anomaly with { Summary = "open for 30 minutes" }, CancellationToken.None);

        Assert.Equal(inserted.Id, again.Id);

        var loaded = await Store.FindAnomalyAsync(anomaly.DedupKey, CancellationToken.None);
        Assert.Equal("open for 30 minutes", loaded!.Summary);
        Assert.Single(await Store.ListAnomaliesAsync(null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task A_refinement_keeps_its_feedback_and_parent()
    {
        var saved = await Store.AddProposalAsync(new Proposal
        {
            Request = "turn off the lights",
            Status = ProposalStatus.Draft,
            Feedback = "only the kitchen",
            ParentId = 41,
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var loaded = await Store.GetProposalAsync(saved.Id, CancellationToken.None);

        Assert.Equal("only the kitchen", loaded!.Feedback);
        Assert.Equal(41, loaded.ParentId);
    }

    [Fact]
    public async Task A_version_1_database_is_upgraded_in_place()
    {
        // Rewind the file to the shape version 1 shipped, then start the store again over it.
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE proposals DROP COLUMN feedback;
                ALTER TABLE proposals DROP COLUMN parent_id;
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Store.InitialiseAsync(CancellationToken.None);

        var saved = await Store.AddProposalAsync(new Proposal
        {
            Request = "still works",
            Feedback = "after upgrade",
            ParentId = 5,
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var loaded = await Store.GetProposalAsync(saved.Id, CancellationToken.None);
        Assert.Equal("after upgrade", loaded!.Feedback);
        Assert.Equal(5, loaded.ParentId);
    }
}

public class ProposalServiceTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();
    private readonly FakeLlm _llm = new();

    private const string GoodDraft = """
        {"alias":"Lights off when away","description":"Turns the hall light off.",
         "triggers":[{"trigger":"state","entity_id":"person.sam","to":"not_home"}],
         "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}]}
        """;

    private ProposalService Service()
    {
        _ha.Entities.AddRange([
            Build.Entity("light.hall", friendlyName: "Hall Light"),
            Build.Entity("person.sam", "home", friendlyName: "Sam"),
            Build.Entity("binary_sensor.freezer_door", friendlyName: "Freezer Door", deviceClass: "door"),
        ]);

        return new ProposalService(_ha, _llm, Store, new FakeSettings(), Clock, NullLogger<ProposalService>.Instance);
    }

    [Fact]
    public async Task Drafts_from_a_sentence_without_touching_home_assistant()
    {
        _llm.Response = GoodDraft;

        var proposal = await Service().DraftAsync("Turn off lights when no one is home", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Draft, proposal.Status);
        Assert.Equal("Lights off when away", proposal.Alias);
        Assert.Equal(["light.hall", "person.sam"], proposal.Entities);
        Assert.Empty(_ha.Created);

        // The shortlist, not the whole house, is what reached the model.
        Assert.Contains("light.hall", _llm.LastUserPrompt);
        Assert.Contains("USER_REQUEST", _llm.LastUserPrompt);
    }

    [Fact]
    public async Task Warns_about_an_automation_that_already_exists()
    {
        _llm.Response = GoodDraft;
        _ha.Automations.Add(Build.Existing("1699", "Away lights", ["light.hall", "person.sam"], ["state"]));

        var proposal = await Service().DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Single(proposal.Duplicates);
        Assert.Equal("1699", proposal.Duplicates[0].AutomationId);
    }

    [Fact]
    public async Task Still_drafts_when_existing_automations_cannot_be_read()
    {
        _llm.Response = GoodDraft;
        _ha.AutomationsFailure = new InvalidOperationException("403");

        var proposal = await Service().DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Draft, proposal.Status);
        Assert.Empty(proposal.Duplicates);
    }

    [Fact]
    public async Task Records_a_failure_when_the_model_is_unreachable()
    {
        _llm.Response = null;

        var proposal = await Service().DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("did not answer", proposal.Error);
    }

    [Fact]
    public async Task Records_a_failure_when_the_model_invents_an_entity()
    {
        _llm.Response = GoodDraft.Replace("light.hall", "light.does_not_exist", StringComparison.Ordinal);

        var proposal = await Service().DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("light.does_not_exist", proposal.Error);
        Assert.Empty(_ha.Created);
    }

    [Fact]
    public async Task Reports_when_the_request_cannot_be_met()
    {
        _llm.Response = """
            {"alias":"UNSUPPORTED","description":"No water sensor is exposed to this integration.",
             "triggers":[{"trigger":"state","entity_id":"person.sam"}],
             "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}]}
            """;

        var proposal = await Service().DraftAsync("warn me if the hall floods", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("No water sensor", proposal.Error);
    }

    [Fact]
    public async Task Offers_a_broad_shortlist_when_nothing_obviously_relates_and_lets_the_model_decide()
    {
        _llm.Response = """
            {"alias":"UNSUPPORTED","description":"No coffee machine is exposed to this integration.",
             "triggers":[{"trigger":"state","entity_id":"person.sam"}],
             "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}]}
            """;

        var proposal = await Service().DraftAsync("descale the espresso machine", ProposalSource.User, null, CancellationToken.None);

        // Keyword matching found nothing, so the model was shown the commonly automated entities instead.
        Assert.Equal(1, _llm.Calls);
        Assert.Contains("light.hall", _llm.LastUserPrompt);
        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("No coffee machine", proposal.Error);
    }

    [Fact]
    public async Task Says_so_when_the_house_has_no_entities_at_all()
    {
        _llm.Response = GoodDraft;
        var service = new ProposalService(_ha, _llm, Store, new FakeSettings(), Clock, NullLogger<ProposalService>.Instance);

        var proposal = await service.DraftAsync("turn off the lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("No entities", proposal.Error);
        Assert.Equal(0, _llm.Calls);
    }

    [Fact]
    public async Task Confirming_writes_it_to_home_assistant_once()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        var confirmed = await service.ConfirmAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(OperationStatus.Ok, confirmed.Status);
        Assert.Equal(ProposalStatus.Created, confirmed.Value!.Status);
        Assert.Single(_ha.Created);
        Assert.Contains("Lights off when away", _ha.Created[0].ConfigJson);

        var second = await service.ConfirmAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(OperationStatus.Conflict, second.Status);
        Assert.Single(_ha.Created);
    }

    [Fact]
    public async Task A_refused_write_is_recorded_rather_than_thrown()
    {
        _llm.Response = GoodDraft;
        _ha.CreateFailure = new InvalidOperationException("Home Assistant returned HTTP 400. Invalid trigger.");

        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);
        var result = await service.ConfirmAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("Invalid trigger", result.Error);

        var stored = await Store.GetProposalAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(ProposalStatus.Failed, stored!.Status);
    }

    [Fact]
    public async Task Confirming_something_that_is_not_there_is_a_not_found()
    {
        Assert.Equal(OperationStatus.NotFound, (await Service().ConfirmAsync(999, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Rejecting_a_draft_reopens_the_finding_that_suggested_it()
    {
        _llm.Response = GoodDraft;
        var service = Service();

        var anomaly = await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = "stuck:binary_sensor.freezer_door",
            EntityId = "binary_sensor.freezer_door",
            Kind = AnomalyKind.StuckState,
            Summary = "open too long",
            SuggestedRequest = "Notify me when binary_sensor.freezer_door stays 'on' for more than 10 minutes.",
            Status = AnomalyStatus.Open,
            DetectedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var proposal = await service.DraftAsync(anomaly.SuggestedRequest, ProposalSource.Anomaly, anomaly.Id, CancellationToken.None);
        await Store.UpdateAnomalyAsync(
            anomaly with { Status = AnomalyStatus.Promoted, ProposalId = proposal.Id }, CancellationToken.None);

        await service.RejectAsync(proposal.Id, CancellationToken.None);

        var reopened = await Store.GetAnomalyAsync(anomaly.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Open, reopened!.Status);
        Assert.Null(reopened.ProposalId);
    }

    [Fact]
    public async Task Refining_supersedes_the_old_draft_and_shows_the_model_what_to_change()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var first = await service.DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        _llm.Response = GoodDraft.Replace("Lights off when away", "Hall light off when away", StringComparison.Ordinal);
        var result = await service.RefineAsync(first.Id, "call it hall light off", CancellationToken.None);

        Assert.Equal(OperationStatus.Ok, result.Status);
        var refined = result.Value!;
        Assert.Equal(ProposalStatus.Draft, refined.Status);
        Assert.Equal("Hall light off when away", refined.Alias);
        Assert.Equal(first.Id, refined.ParentId);
        Assert.Equal("call it hall light off", refined.Feedback);

        // The model saw the previous draft and the objection, as data.
        Assert.Contains("PREVIOUS_DRAFT", _llm.LastUserPrompt);
        Assert.Contains("USER_FEEDBACK", _llm.LastUserPrompt);
        Assert.Contains("Lights off when away", _llm.LastUserPrompt);

        var old = await Store.GetProposalAsync(first.Id, CancellationToken.None);
        Assert.Equal(ProposalStatus.Superseded, old!.Status);
        Assert.Equal(OperationStatus.Conflict, (await service.ConfirmAsync(first.Id, CancellationToken.None)).Status);
        Assert.Empty(_ha.Created);
    }

    [Fact]
    public async Task A_refinement_the_validator_rejects_leaves_the_original_draft_standing()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var first = await service.DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        _llm.Response = GoodDraft.Replace("light.hall", "light.invented", StringComparison.Ordinal);
        var result = await service.RefineAsync(first.Id, "use the other light", CancellationToken.None);

        Assert.Equal(OperationStatus.Ok, result.Status);
        Assert.Equal(ProposalStatus.Failed, result.Value!.Status);
        Assert.Contains("light.invented", result.Value.Error);
        Assert.Equal(first.Id, result.Value.ParentId);

        Assert.Equal(ProposalStatus.Draft, (await Store.GetProposalAsync(first.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Only_a_draft_can_be_refined()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var first = await service.DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);
        await service.RejectAsync(first.Id, CancellationToken.None);

        Assert.Equal(OperationStatus.Conflict, (await service.RefineAsync(first.Id, "again", CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.NotFound, (await service.RefineAsync(999, "again", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Refining_a_promoted_finding_moves_the_link_to_the_new_draft()
    {
        _llm.Response = GoodDraft;
        var service = Service();

        var anomaly = await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = "stuck:binary_sensor.freezer_door",
            EntityId = "binary_sensor.freezer_door",
            Kind = AnomalyKind.StuckState,
            Summary = "open too long",
            SuggestedRequest = "Notify me when binary_sensor.freezer_door stays 'on' for more than 10 minutes.",
            Status = AnomalyStatus.Open,
            DetectedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var first = await service.DraftAsync(anomaly.SuggestedRequest, ProposalSource.Anomaly, anomaly.Id, CancellationToken.None);
        await Store.UpdateAnomalyAsync(anomaly with { Status = AnomalyStatus.Promoted, ProposalId = first.Id }, CancellationToken.None);

        var refined = (await service.RefineAsync(first.Id, "make it 20 minutes", CancellationToken.None)).Value!;

        var linked = await Store.GetAnomalyAsync(anomaly.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Promoted, linked!.Status);
        Assert.Equal(refined.Id, linked.ProposalId);
        Assert.Equal(anomaly.Id, refined.AnomalyId);
    }
}

public class AnomalyScannerTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(HearthSenseOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HearthSenseOptions Options()
    {
        var options = new HearthSenseOptions();
        options.Scan.Include = ["binary_sensor.*"];
        return options;
    }

    [Fact]
    public async Task Builds_history_over_repeated_scans_then_reports_the_stuck_door()
    {
        var options = Options();
        var scanner = Scanner(options);

        var door = Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow().AddDays(-2), "Freezer Door");
        _ha.Entities.Add(door);

        // Twelve short open/close cycles teach the scanner what normal looks like.
        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);

            Clock.Advance(TimeSpan.FromSeconds(45));
            _ha.Entities[0] = door with { State = "off", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        Assert.Empty(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, CancellationToken.None));

        // Now it opens and stays open.
        Clock.Advance(TimeSpan.FromHours(2));
        var openedAt = Clock.GetUtcNow();
        _ha.Entities[0] = door with { State = "on", LastChanged = openedAt };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(TimeSpan.FromMinutes(14));
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Raised);
        var open = await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, CancellationToken.None);
        Assert.Single(open);
        Assert.Contains("14 minutes", open[0].Summary);
    }

    [Fact]
    public async Task A_dismissed_finding_stays_dismissed_until_the_window_passes()
    {
        var options = Options();
        options.Scan.RedetectAfter = TimeSpan.FromDays(7);
        var scanner = Scanner(options);

        var anomaly = await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = "stuck:binary_sensor.freezer_door",
            EntityId = "binary_sensor.freezer_door",
            Kind = AnomalyKind.StuckState,
            Summary = "open too long",
            SuggestedRequest = "notify me",
            Status = AnomalyStatus.Dismissed,
            DetectedUtc = Clock.GetUtcNow(),
            DecidedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        _ha.Entities.Add(Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow()));
        await scanner.ScanAsync(CancellationToken.None);

        var stored = await Store.GetAnomalyAsync(anomaly.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Dismissed, stored!.Status);
    }

    [Fact]
    public async Task Observes_only_the_entities_that_were_opted_in()
    {
        var options = Options();
        _ha.Entities.AddRange([
            Build.Entity("binary_sensor.door", "off", Clock.GetUtcNow()),
            Build.Entity("sensor.ignored", "5", Clock.GetUtcNow()),
        ]);

        var report = await Scanner(options).ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Observed);
        var samples = await Store.GetSamplesAsync(Clock.GetUtcNow().AddDays(-1), CancellationToken.None);
        Assert.True(samples.ContainsKey("binary_sensor.door"));
        Assert.False(samples.ContainsKey("sensor.ignored"));
    }

    [Fact]
    public async Task Notices_when_an_automation_it_created_loses_an_entity()
    {
        _ha.Entities.Add(Build.Entity("binary_sensor.door", "off", Clock.GetUtcNow()));
        await Store.AddProposalAsync(new Proposal
        {
            Request = "notify me when the door opens",
            Status = ProposalStatus.Created,
            Alias = "Door alert",
            Entities = ["binary_sensor.door", "light.gone"],
            HaAutomationId = "1699",
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var report = await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Raised);
        var found = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, CancellationToken.None));
        Assert.Equal(AnomalyKind.MissingEntity, found.Kind);
        Assert.Contains("light.gone", found.Summary);
        Assert.Contains("Door alert", found.Summary);
        Assert.Equal("notify me when the door opens", found.SuggestedRequest);

        // Still missing next time round: the same finding is refreshed, not duplicated.
        Assert.Equal(0, (await Scanner(Options()).ScanAsync(CancellationToken.None)).Raised);
        Assert.Single(await Store.ListAnomaliesAsync(null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_entity_list_is_a_hiccup_not_a_house_with_nothing_in_it()
    {
        await Store.AddProposalAsync(new Proposal
        {
            Request = "turn off the hall light at midnight",
            Status = ProposalStatus.Created,
            Entities = ["light.hall"],
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var report = await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.Raised);
        Assert.Empty(await Store.ListAnomaliesAsync(null, 10, CancellationToken.None));
    }
}
