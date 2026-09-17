using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Housekeeper.Tests;

/// <summary>A real SQLite store on a temp file, so the services are exercised against real SQL.</summary>
public abstract class StoreFixture : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"housekeeper-test-{Guid.NewGuid():N}.db");

    /// <summary>
    /// The real connection string with pooling switched off.
    ///
    /// The temp file has to be deletable when the test finishes, which with pooling on needs
    /// <c>SqliteConnection.ClearAllPools()</c> -- and that is process-wide. xUnit runs test classes in
    /// parallel, so one fixture tearing down was closing connections another class was in the middle of
    /// using, and the suite failed at random with "Cannot access a disposed object". Unpooled connections
    /// close when they are disposed, which is all these tests need.
    /// </summary>
    protected string ConnectionString =>
        new SqliteConnectionStringBuilder(SqliteStore.ConnectionStringFor(_path)) { Pooling = false }.ToString();

    protected SqliteStore Store { get; private set; } = null!;
    protected FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        Store = new SqliteStore(ConnectionString);
        await Store.InitialiseAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
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

        var drafts = await Store.ListProposalsAsync(ProposalStatus.Draft, 10, false, CancellationToken.None);
        var all = await Store.ListProposalsAsync(null, 10, false, CancellationToken.None);

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

        var grouped = await Store.GetSamplesAsync(now.AddDays(-1), 250, CancellationToken.None);
        Assert.Equal(2, grouped["sensor.a"].Count);
        Assert.Equal(now.AddHours(-3), grouped["sensor.a"][0].ChangedUtc);

        var latest = await Store.GetLatestSampleTimesAsync(CancellationToken.None);
        Assert.Equal(now.AddHours(-2), latest["sensor.a"]);

        Assert.Equal(2, await Store.PruneSamplesAsync(now.AddHours(-1.5), CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_newest_samples_of_each_entity_are_loaded()
    {
        var now = Clock.GetUtcNow();

        await Store.AddSamplesAsync(
            [.. Enumerable.Range(0, 40).Select(i => ("sensor.chatty", new StateSample($"{i}", i, now.AddMinutes(-40 + i))))],
            CancellationToken.None);

        var capped = await Store.GetSamplesAsync(now.AddDays(-1), 10, CancellationToken.None);
        var samples = capped["sensor.chatty"];

        // The ten most recent, still oldest first so the detectors read them in order.
        Assert.Equal(10, samples.Count);
        Assert.Equal(now.AddMinutes(-1), samples[^1].ChangedUtc);
        Assert.Equal(now.AddMinutes(-10), samples[0].ChangedUtc);
    }

    [Fact]
    public async Task Finished_proposals_expire_but_drafts_and_live_automations_do_not()
    {
        var now = Clock.GetUtcNow();
        var old = now.AddDays(-40);

        foreach (var (status, decided) in new (ProposalStatus, DateTimeOffset?)[]
                 {
                     (ProposalStatus.Rejected, old),
                     (ProposalStatus.Failed, old),
                     (ProposalStatus.Superseded, old),
                     (ProposalStatus.Removed, old),
                     (ProposalStatus.Rejected, now.AddDays(-2)),   // recent: kept
                     (ProposalStatus.Created, old),                // live automation: kept
                     (ProposalStatus.Draft, null),                 // undecided: kept
                 })
        {
            await Store.AddProposalAsync(new Proposal
            {
                Request = status.ToString(),
                Status = status,
                CreatedUtc = old,
                DecidedUtc = decided,
            }, CancellationToken.None);
        }

        var expired = await Store.PruneProposalsAsync(now.AddDays(-30), CancellationToken.None);

        Assert.Equal(4, expired);
        var left = (await Store.ListProposalsAsync(null, 50, false, CancellationToken.None)).Select(p => p.Status).ToList();
        Assert.Equal(3, left.Count);
        Assert.Contains(ProposalStatus.Created, left);
        Assert.Contains(ProposalStatus.Draft, left);
        Assert.Contains(ProposalStatus.Rejected, left);
    }

    [Fact]
    public async Task The_history_summary_says_how_much_there_is_and_how_much_is_enough()
    {
        var now = Clock.GetUtcNow();

        // One entity with plenty, one with too little, one that fell out of the window entirely.
        await Store.AddSamplesAsync(
        [
            .. Enumerable.Range(0, 20).Select(i => ("sensor.busy", new StateSample($"{i}", i, now.AddMinutes(-60 + i)))),
            ("sensor.quiet", new StateSample("1", 1, now.AddMinutes(-30))),
            ("sensor.ancient", new StateSample("1", 1, now.AddDays(-90))),
        ], CancellationToken.None);

        var summary = await Store.GetHistorySummaryAsync(now.AddDays(-14), CancellationToken.None);

        Assert.Equal(2, summary.Entities);
        Assert.Equal(21, summary.Samples);
        Assert.Equal(now.AddMinutes(-60), summary.OldestUtc);
    }

    [Fact]
    public async Task An_empty_history_summarises_to_nothing_rather_than_failing()
    {
        var summary = await Store.GetHistorySummaryAsync(Clock.GetUtcNow().AddDays(-14), CancellationToken.None);

        Assert.Equal(0, summary.Entities);
        Assert.Equal(0, summary.Samples);
        Assert.Null(summary.OldestUtc);
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
        Assert.Single(await Store.ListAnomaliesAsync(null, 10, true, CancellationToken.None));
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

    /// <summary>
    /// Every column added since version 1 is added by a migration step, so a database from the first release
    /// climbs the whole ladder rather than only the most recent rung.
    /// </summary>
    [Fact]
    public async Task A_version_1_database_is_upgraded_all_the_way_in_place()
    {
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE proposals DROP COLUMN feedback;
                ALTER TABLE proposals DROP COLUMN parent_id;
                ALTER TABLE proposals DROP COLUMN dismissed_utc;
                DROP INDEX IF EXISTS ix_anomalies_severity;
                ALTER TABLE anomalies DROP COLUMN severity;
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Store.InitialiseAsync(CancellationToken.None);

        var saved = await Store.AddProposalAsync(new Proposal
        {
            Request = "still works",
            Status = ProposalStatus.Created,
            Feedback = "after upgrade",
            ParentId = 5,
            DismissedUtc = Clock.GetUtcNow(),
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        var loaded = await Store.GetProposalAsync(saved.Id, CancellationToken.None);
        Assert.Equal("after upgrade", loaded!.Feedback);
        Assert.Equal(5, loaded.ParentId);
        Assert.Equal(Clock.GetUtcNow(), loaded.DismissedUtc);

        // And the column the newest step added is really filtered on, not merely stored.
        Assert.Empty(await Store.ListProposalsAsync(null, 50, false, CancellationToken.None));
    }

    /// <summary>
    /// An upgrade that dies partway has to leave nothing behind. Before this was one transaction, a process
    /// killed between a step and the version bump left the old version recorded with the new column already
    /// added, and every later start died on "duplicate column name" -- before the host could serve the
    /// settings page needed to fix it. The only way out was deleting the database.
    /// </summary>
    [Fact]
    public async Task A_migration_that_fails_partway_leaves_the_database_exactly_as_it_was()
    {
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();

            // Back to version 1, but with the column the LAST step adds already present. Step two will
            // succeed and step three will collide, which is precisely the half-done upgrade being guarded.
            command.CommandText = """
                ALTER TABLE proposals DROP COLUMN feedback;
                ALTER TABLE proposals DROP COLUMN parent_id;
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => Store.InitialiseAsync(CancellationToken.None));

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, Convert.ToInt64(await version.ExecuteScalarAsync()));

            // And the step that did succeed was undone with it, so the next attempt starts from the top.
            await using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('proposals') WHERE name = 'feedback';";
            Assert.Equal(0L, Convert.ToInt64(await columns.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task Upgrading_a_database_that_is_already_current_does_nothing()
    {
        await Store.InitialiseAsync(CancellationToken.None);
        await Store.InitialiseAsync(CancellationToken.None);

        var saved = await Store.AddProposalAsync(new Proposal
        {
            Request = "unharmed",
            CreatedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        Assert.NotNull(await Store.GetProposalAsync(saved.Id, CancellationToken.None));
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

    private ProposalService Service(IStore? over = null)
    {
        _ha.Entities.AddRange([
            Build.Entity("light.hall", friendlyName: "Hall Light"),
            Build.Entity("person.sam", "home", friendlyName: "Sam"),
            Build.Entity("binary_sensor.freezer_door", friendlyName: "Freezer Door", deviceClass: "door"),
        ]);

        var settings = new FakeSettings();
        settings.Current.Llm.Model = "test-model";
        return new ProposalService(_ha, _llm, over ?? Store, settings, Clock, NullLogger<ProposalService>.Instance);
    }

    private static FakeSettings SettingsWithModel()
    {
        var settings = new FakeSettings();
        settings.Current.Llm.Model = "test-model";
        return settings;
    }

    [Fact]
    public async Task Ollama_without_a_model_name_is_refused_before_anything_is_fetched()
    {
        _llm.Response = GoodDraft;
        var service = new ProposalService(_ha, _llm, Store, new FakeSettings(), Clock, NullLogger<ProposalService>.Instance);

        var proposal = await service.DraftAsync("turn off the lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("Ollama needs one", proposal.Error);
        Assert.Equal(0, _llm.Calls);
    }

    [Fact]
    public async Task An_openai_style_server_drafts_without_a_model_name()
    {
        _llm.Response = GoodDraft;
        var settings = new FakeSettings();
        settings.Current.Llm.Provider = "OpenAI";
        var service = new ProposalService(_ha, _llm, Store, settings, Clock, NullLogger<ProposalService>.Instance);
        _ha.Entities.AddRange([Build.Entity("light.hall", friendlyName: "Hall Light"), Build.Entity("person.sam", "home")]);

        var proposal = await service.DraftAsync("turn off the lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Draft, proposal.Status);
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
        var service = new ProposalService(_ha, _llm, Store, SettingsWithModel(), Clock, NullLogger<ProposalService>.Instance);

        var proposal = await service.DraftAsync("turn off the lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("No entities", proposal.Error);
        Assert.Equal(0, _llm.Calls);
    }

    [Fact]
    public async Task Tells_the_model_what_was_wrong_and_takes_the_corrected_answer()
    {
        // First answer invents an entity. Second one, having been told, uses a real one.
        _llm.Replies(
            GoodDraft.Replace("light.hall", "light.imaginary", StringComparison.Ordinal),
            GoodDraft);

        var proposal = await Service().DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Draft, proposal.Status);
        Assert.Equal(2, _llm.Calls);

        // The second prompt carried the validator's own sentence, and the answer it was complaining about.
        Assert.DoesNotContain("REJECTED_BECAUSE", _llm.Prompts[0]);
        Assert.Contains("REJECTED_BECAUSE", _llm.Prompts[1]);
        Assert.Contains("light.imaginary", _llm.Prompts[1]);
        Assert.Contains("PREVIOUS_DRAFT", _llm.Prompts[1]);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_number_of_attempts_and_says_why()
    {
        _llm.Response = GoodDraft.Replace("light.hall", "light.imaginary", StringComparison.Ordinal);

        var settings = SettingsWithModel();
        settings.Current.Llm.MaxAttempts = 2;
        var service = new ProposalService(_ha, _llm, Store, settings, Clock, NullLogger<ProposalService>.Instance);
        _ha.Entities.Add(Build.Entity("light.hall", friendlyName: "Hall Light"));

        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Equal(2, _llm.Calls);
        Assert.Contains("light.imaginary", proposal.Error);
        Assert.Contains("asked 2 times", proposal.Error);
    }

    [Fact]
    public async Task Does_not_retry_when_the_model_says_it_cannot_be_done()
    {
        _llm.Response = """
            {"alias":"UNSUPPORTED","description":"No water sensor is exposed to this integration.",
             "triggers":[{"trigger":"state","entity_id":"person.sam"}],
             "actions":[{"action":"light.turn_off","target":{"entity_id":"light.hall"}}]}
            """;

        var proposal = await Service().DraftAsync("warn me if the hall floods", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Contains("No water sensor", proposal.Error);

        // A refusal is an answer. Asking again would only waste a minute of a slow local model.
        Assert.Equal(1, _llm.Calls);
    }

    [Fact]
    public async Task Does_not_retry_when_the_endpoint_is_not_answering()
    {
        _llm.Response = null;

        var proposal = await Service().DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Equal(1, _llm.Calls);
    }

    [Fact]
    public async Task Shows_the_model_the_services_this_house_actually_has()
    {
        _llm.Response = GoodDraft;
        _ha.Services.Add("light.turn_off");
        _ha.Services.Add("light.turn_on");
        _ha.Services.Add("vacuum.start");

        await Service().DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Contains("AVAILABLE_ACTIONS", _llm.LastUserPrompt);
        Assert.Contains("light.turn_off", _llm.LastUserPrompt);
    }

    [Fact]
    public async Task An_invented_service_is_caught_and_corrected_on_the_next_attempt()
    {
        _ha.Services.Add("light.turn_off");
        _llm.Replies(GoodDraft.Replace("light.turn_off", "light.extinguish", StringComparison.Ordinal), GoodDraft);

        var proposal = await Service().DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Draft, proposal.Status);
        Assert.Equal(2, _llm.Calls);
        Assert.Contains("light.extinguish", _llm.Prompts[1]);
    }

    [Fact]
    public async Task Still_drafts_when_the_service_list_cannot_be_read()
    {
        _llm.Response = GoodDraft;
        _ha.ServicesFailure = new InvalidOperationException("503");

        var proposal = await Service().DraftAsync("turn off lights when out", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Draft, proposal.Status);
        Assert.DoesNotContain("AVAILABLE_ACTIONS", _llm.LastUserPrompt);
    }

    [Fact]
    public async Task Stops_asking_when_the_model_just_repeats_itself()
    {
        // A low temperature makes an identical second answer likely. Another attempt would only cost time.
        _llm.Response = GoodDraft.Replace("light.hall", "light.imaginary", StringComparison.Ordinal);

        var settings = SettingsWithModel();
        settings.Current.Llm.MaxAttempts = 5;
        var service = new ProposalService(_ha, _llm, Store, settings, Clock, NullLogger<ProposalService>.Instance);
        _ha.Entities.Add(Build.Entity("light.hall", friendlyName: "Hall Light"));

        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        Assert.Equal(ProposalStatus.Failed, proposal.Status);
        Assert.Equal(2, _llm.Calls);
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

    /// <summary>
    /// Two clicks on Create, or one click and a retry, must not put two copies of the automation in someone's
    /// house. Checking the status and then writing is not enough: both requests read Draft before either
    /// writes. The draft is claimed in a single conditional update, so exactly one caller can proceed.
    /// </summary>
    [Fact]
    public async Task Two_confirms_at_once_still_write_one_automation()
    {
        // Both confirms are held at the claim, so both have already passed the ordinary status check when
        // they get there. That is the only interleaving this guard exists for, and it is not one scheduling
        // will produce on its own: SQLite completes synchronously, so left to itself the first confirm runs
        // to completion before the second starts and is turned away by the plain check instead. Written that
        // way, the test passed just as happily with the atomic claim deleted.
        var gated = new GatedStore(Store);

        _llm.Response = GoodDraft;
        var service = Service(gated);
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        gated.ClaimGate = new TaskCompletionSource();

        var first = service.ConfirmAsync(proposal.Id, CancellationToken.None);
        var second = service.ConfirmAsync(proposal.Id, CancellationToken.None);

        gated.ClaimGate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(_ha.Created);
        Assert.Single(results, result => result.Status == OperationStatus.Ok);
        Assert.Single(results, result => result.Status == OperationStatus.Conflict);
    }

    /// <summary>
    /// Two different proposals confirmed in the same millisecond used to be handed the same automation id,
    /// and Home Assistant treats a repeated id as an edit -- so the second silently replaced the first.
    /// </summary>
    [Fact]
    public async Task Two_proposals_confirmed_at_the_same_instant_get_different_automation_ids()
    {
        _llm.Response = GoodDraft;
        var service = Service();

        var one = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);
        _llm.Response = GoodDraft;
        var two = await service.DraftAsync("turn off lights again", ProposalSource.User, null, CancellationToken.None);

        // The clock does not move between them, which is the whole point.
        await service.ConfirmAsync(one.Id, CancellationToken.None);
        await service.ConfirmAsync(two.Id, CancellationToken.None);

        Assert.Equal(2, _ha.Created.Count);
        Assert.NotEqual(_ha.Created[0].Id, _ha.Created[1].Id);
    }

    /// <summary>
    /// Rejecting takes the same claim as confirming, so a reject arriving while a write is in flight cannot
    /// leave a live automation recorded here as rejected.
    /// </summary>
    [Fact]
    public async Task Rejecting_a_draft_that_is_already_being_confirmed_is_a_conflict()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        _ha.CreateGate = new TaskCompletionSource();
        var confirming = service.ConfirmAsync(proposal.Id, CancellationToken.None);

        var rejected = await service.RejectAsync(proposal.Id, CancellationToken.None);

        _ha.CreateGate.SetResult();
        var confirmed = await confirming;

        Assert.Equal(OperationStatus.Conflict, rejected.Status);
        Assert.Equal(OperationStatus.Ok, confirmed.Status);
        Assert.Equal(ProposalStatus.Created, (await Store.GetProposalAsync(proposal.Id, CancellationToken.None))!.Status);
    }

    /// <summary>
    /// Refining reads the parent, then spends ten to sixty seconds on a local model. Writing that whole row
    /// back afterwards erased anything decided in between — and confirming stays possible the entire time.
    /// The automation ends up live in the house while the record says Superseded with no Home Assistant id,
    /// which means nothing supervises it, nothing can find it, and the row is deleted after the retention
    /// window. Verified against the real store, which is the only way this was ever going to be found.
    /// </summary>
    [Fact]
    public async Task A_confirm_that_lands_while_a_refine_is_thinking_is_not_overwritten()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var parent = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        // The refine is in flight: the model has been asked and has not answered yet.
        _llm.Gate = new TaskCompletionSource();
        _llm.Response = GoodDraft.Replace("Lights off when away", "Hall light off when away", StringComparison.Ordinal);
        var refining = service.RefineAsync(parent.Id, "call it hall light off", CancellationToken.None);

        // Meanwhile the user gets impatient and clicks Create.
        var confirmed = await service.ConfirmAsync(parent.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Ok, confirmed.Status);

        _llm.Gate.SetResult();
        await refining;

        // The live automation's record is intact: still Created, still carrying the id it was written under.
        var stored = await Store.GetProposalAsync(parent.Id, CancellationToken.None);

        Assert.Equal(ProposalStatus.Created, stored!.Status);
        Assert.Equal(_ha.Created.Single().Id, stored.HaAutomationId);
    }

    /// <summary>
    /// Home Assistant answering 401 means the write definitively did not happen, and the commonest cause —
    /// a token that is not an admin's — is something the user goes and fixes. Burning the draft made them
    /// redo a minute of model time and a careful read of the YAML for a problem that was never in the draft.
    /// </summary>
    [Fact]
    public async Task A_write_home_assistant_refuses_leaves_the_draft_ready_to_try_again()
    {
        _llm.Response = GoodDraft;
        _ha.CreateFailure = new HomeAssistantException(
            "Home Assistant rejected the write. Creating automations needs a long-lived token belonging to an admin user.",
            refused: true);

        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        var refused = await service.ConfirmAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, refused.Status);

        // Still a draft, with the reason recorded and the validated automation untouched.
        var stored = await Store.GetProposalAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(ProposalStatus.Draft, stored!.Status);
        Assert.Contains("admin user", stored.Error);
        Assert.Contains("Lights off when away", stored.ConfigJson);

        // The user fixes the token and tries the very same draft again.
        _ha.CreateFailure = null;
        var second = await service.ConfirmAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(OperationStatus.Ok, second.Status);
        Assert.Single(_ha.Created);
    }

    /// <summary>
    /// A timeout is NOT a refusal: Home Assistant may well have saved it. Retrying reuses the same automation
    /// id, because Home Assistant treats a repeated id as an edit — so the retry converges on one automation
    /// whether or not the first attempt landed, which is what makes offering it safe.
    /// </summary>
    [Fact]
    public async Task A_write_whose_outcome_is_unknown_can_be_retried_without_making_a_second_automation()
    {
        _llm.Response = GoodDraft;
        _ha.CreateFailure = new TaskCanceledException("The request timed out.");

        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        var failed = await service.ConfirmAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, failed.Status);

        var stored = await Store.GetProposalAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(ProposalStatus.Failed, stored!.Status);
        Assert.NotNull(stored.HaAutomationId);
        Assert.Contains("not certain", stored.Error);

        // Home Assistant comes back; the user presses Create again.
        _ha.CreateFailure = null;
        var retried = await service.ConfirmAsync(proposal.Id, CancellationToken.None);

        Assert.Equal(OperationStatus.Ok, retried.Status);
        Assert.Equal(stored.HaAutomationId, _ha.Created.Single().Id);
    }

    /// <summary>
    /// The claim marks the row Created before Home Assistant is written to, which means a dismissal arriving
    /// in that window passes its own "not a draft" check and reads a row whose automation id is not there
    /// yet. Writing that snapshot back afterwards blanked the id the confirm had just recorded — an
    /// automation live in the house with nothing pointing at it, unsupervised and unfindable. Both sides now
    /// write only their own column, so neither can flatten the other whichever order they land in.
    /// </summary>
    [Fact]
    public async Task A_dismissal_landing_mid_confirm_cannot_erase_the_automation_id()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        var gated = new GatedStore(Store);
        service = Service(gated);

        _ha.CreateGate = new TaskCompletionSource();
        var confirming = service.ConfirmAsync(proposal.Id, CancellationToken.None);

        // The row is Created (claimed) but the id has not been written yet. A dismiss reads it here and is
        // then held, so its write lands AFTER the confirm's -- the ordering two HTTP requests on different
        // threads can perfectly well produce once SQLite blocks either of them.
        gated.DismissGate = new TaskCompletionSource();
        var dismissing = service.DismissAsync(proposal.Id, true, CancellationToken.None);

        _ha.CreateGate.SetResult();
        await confirming;

        gated.DismissGate.SetResult();
        var dismissed = await dismissing;

        var stored = await Store.GetProposalAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(OperationStatus.Ok, dismissed.Status);
        Assert.False(string.IsNullOrEmpty(stored!.HaAutomationId),
            $"automation {_ha.Created.Single().Id} is LIVE but the row records id='{stored.HaAutomationId}'");
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
    public async Task A_created_automation_can_be_put_out_of_sight_without_being_touched()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);
        await service.ConfirmAsync(proposal.Id, CancellationToken.None);

        var dismissed = await service.DismissAsync(proposal.Id, dismissed: true, CancellationToken.None);

        Assert.Equal(OperationStatus.Ok, dismissed.Status);
        Assert.NotNull(dismissed.Value!.DismissedUtc);

        // Still created, still written, nothing undone in Home Assistant.
        Assert.Equal(ProposalStatus.Created, dismissed.Value.Status);
        Assert.Single(_ha.Created);

        // Gone from what a person is shown, still there for the supervision that watches it.
        Assert.Empty(await Store.ListProposalsAsync(null, 50, false, CancellationToken.None));
        Assert.Single(await Store.ListProposalsAsync(null, 50, true, CancellationToken.None));

        var restored = await service.DismissAsync(proposal.Id, dismissed: false, CancellationToken.None);
        Assert.Null(restored.Value!.DismissedUtc);
        Assert.Single(await Store.ListProposalsAsync(null, 50, false, CancellationToken.None));
    }

    [Fact]
    public async Task A_draft_cannot_be_dismissed_because_it_is_a_decision_not_a_record()
    {
        _llm.Response = GoodDraft;
        var service = Service();
        var proposal = await service.DraftAsync("turn off lights", ProposalSource.User, null, CancellationToken.None);

        var result = await service.DismissAsync(proposal.Id, dismissed: true, CancellationToken.None);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Contains("still a draft", result.Error);
    }

    [Fact]
    public async Task Dismissing_something_that_is_not_there_is_a_not_found()
    {
        Assert.Equal(OperationStatus.NotFound, (await Service().DismissAsync(999, true, CancellationToken.None)).Status);
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

    private AnomalyScanner Scanner(HousekeeperOptions options) =>
        new(_ha, Store, new FakeSettings(options), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options()
    {
        // Narrowed on purpose. These tests are about what the detectors do, so the watch list is pinned to
        // the entities each one sets up rather than left at the watch-everything default.
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
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

        Assert.Empty(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));

        // Now it opens and stays open.
        Clock.Advance(TimeSpan.FromHours(2));
        var openedAt = Clock.GetUtcNow();
        _ha.Entities[0] = door with { State = "on", LastChanged = openedAt };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(TimeSpan.FromMinutes(14));
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Raised);
        var open = await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None);
        Assert.Single(open);
        Assert.Contains("14 minutes", open[0].Summary);
    }

    [Fact]
    public async Task A_dismissed_finding_stays_dismissed_until_the_window_passes()
    {
        var options = Options();
        options.Scan.RedetectAfter = TimeSpan.FromDays(7);
        var scanner = Scanner(options);

        // The condition has to be genuinely detectable, or the window is never the thing being tested: a
        // door that is closed, or has only just opened, is not a finding for reasons that have nothing to do
        // with dismissal, and the raise path is then never reached at all.
        var door = Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow().AddDays(-2), "Freezer Door");
        _ha.Entities.Add(door);

        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);

            Clock.Advance(TimeSpan.FromSeconds(45));
            _ha.Entities[0] = door with { State = "off", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        Clock.Advance(TimeSpan.FromHours(2));
        _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(1, (await scanner.ScanAsync(CancellationToken.None)).Raised);

        var raised = (await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None)).Single();
        await Store.UpdateAnomalyAsync(
            raised with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow() }, CancellationToken.None);

        // Still open, still detectable, still silenced: a dismissal the user made has to mean something.
        Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, (await scanner.ScanAsync(CancellationToken.None)).Raised);
        Assert.Equal(
            AnomalyStatus.Dismissed,
            (await Store.GetAnomalyAsync(raised.Id, CancellationToken.None))!.Status);

        // Past the window it is allowed to speak up again, on the same row rather than as a second finding.
        Clock.Advance(options.Scan.RedetectAfter);
        Assert.Equal(1, (await scanner.ScanAsync(CancellationToken.None)).Raised);

        var reopened = await Store.GetAnomalyAsync(raised.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Open, reopened!.Status);
        Assert.Null(reopened.DecidedUtc);
    }

    /// <summary>
    /// Promoting a finding into a draft used to be a permanent silence. Promoted fell through every arm of
    /// the raise switch, nothing else ever moved the row, and the dedup key is unique — so the freezer door
    /// could report that it had been left open exactly once, ever, for the life of the install.
    /// </summary>
    [Fact]
    public async Task Promoting_a_finding_silences_it_for_the_window_and_no_longer()
    {
        var options = Options();
        options.Scan.RedetectAfter = TimeSpan.FromDays(7);
        var scanner = Scanner(options);

        var door = Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow().AddDays(-2), "Freezer Door");
        _ha.Entities.Add(door);

        async Task SetAsync(string state)
        {
            _ha.Entities[0] = door with { State = state, LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        // Each occurrence has to outlast the previous one by more than the multiplier: closing the door turns
        // the stretch just ended into part of what "normal" now means.
        async Task LeaveItOpenAsync(TimeSpan howLong)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            await SetAsync("on");
            Clock.Advance(howLong);
            await scanner.ScanAsync(CancellationToken.None);
        }

        // Twelve ordinary short openings teach it what normal looks like.
        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            await SetAsync("on");
            Clock.Advance(TimeSpan.FromSeconds(45));
            await SetAsync("off");
        }

        await LeaveItOpenAsync(TimeSpan.FromMinutes(14));
        var raised = (await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None)).Single();

        // The user turns it into a draft.
        await Store.UpdateAnomalyAsync(
            raised with { Status = AnomalyStatus.Promoted, ProposalId = 1, DecidedUtc = Clock.GetUtcNow() },
            CancellationToken.None);

        // Shut it, let a day pass, and it happens again. Still inside the window, so it is not nagged about.
        await SetAsync("off");
        Clock.Advance(TimeSpan.FromDays(1));
        await LeaveItOpenAsync(TimeSpan.FromHours(2));

        Assert.Equal(
            AnomalyStatus.Promoted,
            (await Store.GetAnomalyAsync(raised.Id, CancellationToken.None))!.Status);

        // Shut it again and wait the window out. Now it has to be heard rather than silenced for ever.
        await SetAsync("off");
        Clock.Advance(options.Scan.RedetectAfter);
        await LeaveItOpenAsync(TimeSpan.FromHours(9));

        var reopened = await Store.GetAnomalyAsync(raised.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Open, reopened!.Status);
    }

    /// <summary>
    /// Two things make a detector go quiet: the condition ending, and there not being enough history to have
    /// an opinion. Closing a finding on the second is how a sensor that is still dead quietly disappears off
    /// the dashboard, which is the one outcome the user would never find out about.
    /// </summary>
    [Fact]
    public async Task A_finding_is_not_closed_just_because_its_entity_can_no_longer_be_judged()
    {
        var options = Options();
        options.Scan.History = TimeSpan.FromHours(6);
        options.Scan.Include = ["binary_sensor.*", "sensor.*"];
        var scanner = Scanner(options);

        var sensor = Build.Entity("sensor.boiler_pressure", "1.4", Clock.GetUtcNow().AddDays(-3));
        _ha.Entities.Add(sensor);

        await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = "outlier:sensor.boiler_pressure",
            EntityId = "sensor.boiler_pressure",
            Kind = AnomalyKind.NumericOutlier,
            Summary = "reads 3.2, well outside its normal range",
            SuggestedRequest = "notify me when sensor.boiler_pressure goes above 2",
            Status = AnomalyStatus.Open,
            DetectedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        // The entity is watched and reporting, but nothing has been recorded about it inside the six-hour
        // window, so no detector can say anything either way.
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.Resolved);
        Assert.Equal(0, report.Judged);
        Assert.Equal(
            AnomalyStatus.Open,
            (await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, false, CancellationToken.None)).Single().Status);
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
        var samples = await Store.GetSamplesAsync(Clock.GetUtcNow().AddDays(-1), 250, CancellationToken.None);
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
        var found = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));
        Assert.Equal(AnomalyKind.MissingEntity, found.Kind);
        Assert.Contains("light.gone", found.Summary);
        Assert.Contains("Door alert", found.Summary);
        Assert.Equal("notify me when the door opens", found.SuggestedRequest);

        // Still missing next time round: the same finding is refreshed, not duplicated.
        Assert.Equal(0, (await Scanner(Options()).ScanAsync(CancellationToken.None)).Raised);
        Assert.Single(await Store.ListAnomaliesAsync(null, 10, true, CancellationToken.None));

        // Renamed back, or the integration finally loaded: the automation works again, so the finding goes.
        _ha.Entities.Add(Build.Entity("light.gone", "off", Clock.GetUtcNow()));
        Assert.Equal(1, (await Scanner(Options()).ScanAsync(CancellationToken.None)).Resolved);
        Assert.Equal(AnomalyStatus.Resolved, (await Store.GetAnomalyAsync(found.Id, CancellationToken.None))!.Status);
    }

    /// <summary>
    /// What a finding describes is a moment, not a verdict. Once the door is shut — or the stretch that
    /// looked unusual has been absorbed into what this entity normally does — it closes itself.
    /// </summary>
    [Fact]
    public async Task A_finding_closes_itself_once_the_condition_passes_and_returns_if_it_happens_again()
    {
        var options = Options();
        // Long enough that a dismissal would still be silent, so reopening cannot be the dismissal path.
        options.Scan.RedetectAfter = TimeSpan.FromDays(30);
        var scanner = Scanner(options);

        var door = Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow().AddDays(-2), "Freezer Door");
        _ha.Entities.Add(door);

        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);

            Clock.Advance(TimeSpan.FromSeconds(45));
            _ha.Entities[0] = door with { State = "off", LastChanged = Clock.GetUtcNow() };
            await scanner.ScanAsync(CancellationToken.None);
        }

        Clock.Advance(TimeSpan.FromHours(2));
        _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(TimeSpan.FromMinutes(14));
        await scanner.ScanAsync(CancellationToken.None);
        var raised = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));

        Clock.Advance(TimeSpan.FromMinutes(1));
        _ha.Entities[0] = door with { State = "off", LastChanged = Clock.GetUtcNow() };
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Resolved);
        Assert.Empty(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));
        var closed = await Store.GetAnomalyAsync(raised.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Resolved, closed!.Status);
        Assert.Equal(Clock.GetUtcNow(), closed.DecidedUtc);

        // It happens again, and it must be heard: nobody silenced this one, it simply stopped being true.
        Clock.Advance(TimeSpan.FromHours(2));
        _ha.Entities[0] = door with { State = "on", LastChanged = Clock.GetUtcNow() };
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, (await scanner.ScanAsync(CancellationToken.None)).Raised);

        var reopened = await Store.GetAnomalyAsync(raised.Id, CancellationToken.None);
        Assert.Equal(AnomalyStatus.Open, reopened!.Status);
        Assert.Null(reopened.DecidedUtc);
    }

    [Fact]
    public async Task A_finding_stays_open_while_its_entity_is_out_of_sight()
    {
        var scanner = Scanner(Options());

        var anomaly = await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = "stuck:binary_sensor.shed_door",
            EntityId = "binary_sensor.shed_door",
            Kind = AnomalyKind.StuckState,
            Summary = "open too long",
            SuggestedRequest = "notify me",
            Status = AnomalyStatus.Open,
            DetectedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        // Home Assistant reports another entity; nothing was learned about the shed door either way.
        _ha.Entities.Add(Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow()));
        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.Resolved);
        Assert.Equal(AnomalyStatus.Open, (await Store.GetAnomalyAsync(anomaly.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Closed_findings_are_forgotten_once_they_outlive_both_windows()
    {
        var options = Options();
        options.Scan.RedetectAfter = TimeSpan.FromDays(7);
        options.Storage.KeepDecidedFor = TimeSpan.FromDays(30);

        var old = Clock.GetUtcNow().AddDays(-31);
        foreach (var (key, status, decided) in new[]
                 {
                     ("stuck:a", AnomalyStatus.Resolved, old),
                     ("stuck:b", AnomalyStatus.Dismissed, old),
                     ("stuck:c", AnomalyStatus.Resolved, Clock.GetUtcNow().AddDays(-2)),
                     ("stuck:d", AnomalyStatus.Promoted, old),
                 })
        {
            await Store.UpsertAnomalyAsync(new Anomaly
            {
                DedupKey = key,
                EntityId = "binary_sensor." + key[^1],
                Kind = AnomalyKind.StuckState,
                Summary = "held too long",
                SuggestedRequest = "notify me",
                Status = status,
                DetectedUtc = old,
                DecidedUtc = decided,
            }, CancellationToken.None);
        }

        _ha.Entities.Add(Build.Entity("binary_sensor.freezer_door", "off", Clock.GetUtcNow()));
        await Scanner(options).ScanAsync(CancellationToken.None);

        var left = (await Store.ListAnomaliesAsync(null, 10, true, CancellationToken.None))
            .Select(anomaly => anomaly.DedupKey).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["stuck:c", "stuck:d"], left);
    }

    [Fact]
    public async Task History_is_still_pruned_when_the_watch_list_no_longer_covers_anything()
    {
        var options = Options();
        options.Scan.Include = ["nothing.matches_this"];

        await Store.AddSamplesAsync(
            [("binary_sensor.door", new StateSample("on", null, Clock.GetUtcNow().AddDays(-30)))],
            CancellationToken.None);

        _ha.Entities.Add(Build.Entity("binary_sensor.door", "off", Clock.GetUtcNow()));

        var report = await Scanner(options).ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.Observed);
        Assert.Equal(1, report.Pruned);
        Assert.Empty(await Store.GetSamplesAsync(Clock.GetUtcNow().AddDays(-60), 250, CancellationToken.None));
    }

    private async Task<Proposal> LiveAutomationAsync(string configId, TimeSpan age) =>
        await Store.AddProposalAsync(new Proposal
        {
            Request = "turn off the hall light when everyone leaves",
            Status = ProposalStatus.Created,
            Alias = "Away lights",
            Entities = ["light.hall"],
            HaAutomationId = configId,
            CreatedUtc = Clock.GetUtcNow() - age,
            DecidedUtc = Clock.GetUtcNow() - age,
        }, CancellationToken.None);

    [Fact]
    public async Task Notices_when_an_automation_it_created_was_deleted_in_home_assistant()
    {
        var mine = await LiveAutomationAsync("1699", TimeSpan.FromHours(1));

        // Its entity has also gone, so a missing-entity finding is already open against it.
        await Store.UpsertAnomalyAsync(new Anomaly
        {
            DedupKey = $"missing:{mine.Id}",
            EntityId = "light.hall",
            Kind = AnomalyKind.MissingEntity,
            Summary = "gone",
            SuggestedRequest = mine.Request,
            DetectedUtc = Clock.GetUtcNow(),
        }, CancellationToken.None);

        // Home Assistant still has automations, just not this one.
        _ha.Entities.Add(Build.Entity("automation.someone_elses", "on", automationConfigId: "4242"));

        await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(ProposalStatus.Removed, (await Store.GetProposalAsync(mine.Id, CancellationToken.None))!.Status);
        Assert.Equal(AnomalyStatus.Dismissed, (await Store.FindAnomalyAsync($"missing:{mine.Id}", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_dismissed_automation_is_still_watched_for_entities_that_disappear()
    {
        var mine = await LiveAutomationAsync("1699", TimeSpan.FromHours(1));
        await Store.UpdateProposalAsync(mine with { DismissedUtc = Clock.GetUtcNow() }, CancellationToken.None);

        // Its entity is gone, and the automation itself is still in Home Assistant.
        _ha.Entities.Add(Build.Entity("automation.away_lights", "on", automationConfigId: "1699"));

        var report = await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.Raised);
        var found = Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Open, 10, true, CancellationToken.None));
        Assert.Equal(AnomalyKind.MissingEntity, found.Kind);
    }

    [Fact]
    public async Task A_freshly_created_automation_is_given_time_to_register_before_being_declared_gone()
    {
        var mine = await LiveAutomationAsync("1699", TimeSpan.FromMinutes(2));
        _ha.Entities.Add(Build.Entity("automation.someone_elses", "on", automationConfigId: "4242"));

        await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(ProposalStatus.Created, (await Store.GetProposalAsync(mine.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task No_automations_at_all_means_the_integration_is_down_not_that_everything_was_deleted()
    {
        var mine = await LiveAutomationAsync("1699", TimeSpan.FromHours(1));
        _ha.Entities.Add(Build.Entity("light.hall", "off", Clock.GetUtcNow()));

        await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(ProposalStatus.Created, (await Store.GetProposalAsync(mine.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_live_automation_that_is_still_there_is_left_alone()
    {
        var mine = await LiveAutomationAsync("1699", TimeSpan.FromHours(1));
        _ha.Entities.Add(Build.Entity("automation.away_lights", "on", automationConfigId: "1699"));
        _ha.Entities.Add(Build.Entity("light.hall", "off", Clock.GetUtcNow()));

        await Scanner(Options()).ScanAsync(CancellationToken.None);

        Assert.Equal(ProposalStatus.Created, (await Store.GetProposalAsync(mine.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task The_scanner_remembers_what_its_last_run_did()
    {
        var scanner = Scanner(Options());
        Assert.Null(scanner.Last);

        _ha.Entities.AddRange([
            Build.Entity("binary_sensor.door", "off", Clock.GetUtcNow()),
            Build.Entity("light.ignored", "on", Clock.GetUtcNow()),
        ]);

        await scanner.ScanAsync(CancellationToken.None);

        Assert.NotNull(scanner.Last);
        Assert.Null(scanner.Last.Error);
        Assert.Equal(Clock.GetUtcNow(), scanner.Last.FinishedUtc);
        Assert.Equal(1, scanner.Last.Report!.Observed);

        // Everything Home Assistant reported, not only what the watch list took.
        Assert.Equal(2, scanner.Last.Report.Visible);
    }

    [Fact]
    public async Task A_failed_scan_is_remembered_too_rather_than_looking_like_silence()
    {
        var scanner = Scanner(Options());
        _ha.EntitiesFailure = new HomeAssistantException("Home Assistant returned HTTP 503.");

        await Assert.ThrowsAsync<HomeAssistantException>(() => scanner.ScanAsync(CancellationToken.None));

        Assert.NotNull(scanner.Last);
        Assert.Null(scanner.Last.Report);
        Assert.Contains("503", scanner.Last.Error);
    }

    [Fact]
    public async Task The_scan_counts_what_it_could_actually_judge()
    {
        var options = Options();
        var scanner = Scanner(options);

        // One entity with a long history, one seen for the first time.
        await Store.AddSamplesAsync(
            [.. Enumerable.Range(0, 15).Select(i =>
                ("binary_sensor.busy", new StateSample(i % 2 == 0 ? "on" : "off", null, Clock.GetUtcNow().AddHours(-15 + i))))],
            CancellationToken.None);

        _ha.Entities.AddRange([
            Build.Entity("binary_sensor.busy", "on", Clock.GetUtcNow()),
            Build.Entity("binary_sensor.brand_new", "off", Clock.GetUtcNow()),
        ]);

        var report = await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(2, report.Observed);
        Assert.Equal(1, report.Judged);
    }

    [Fact]
    public async Task Closed_findings_are_left_out_of_the_list_unless_asked_for()
    {
        foreach (var (key, status) in new[]
                 {
                     ("stuck:a", AnomalyStatus.Open),
                     ("stuck:b", AnomalyStatus.Promoted),
                     ("stuck:c", AnomalyStatus.Dismissed),
                     ("stuck:d", AnomalyStatus.Resolved),
                 })
        {
            await Store.UpsertAnomalyAsync(new Anomaly
            {
                DedupKey = key,
                EntityId = "binary_sensor." + key[^1],
                Kind = AnomalyKind.StuckState,
                Summary = "s",
                SuggestedRequest = "r",
                Status = status,
                DetectedUtc = Clock.GetUtcNow(),
            }, CancellationToken.None);
        }

        var shown = await Store.ListAnomaliesAsync(null, 50, false, CancellationToken.None);
        Assert.Equal(
            [AnomalyStatus.Open, AnomalyStatus.Promoted],
            shown.Select(a => a.Status).OrderBy(s => s));

        Assert.Equal(4, (await Store.ListAnomaliesAsync(null, 50, true, CancellationToken.None)).Count);

        // Asking for a closed status by name still returns it; the filter is a default, not a ban.
        Assert.Single(await Store.ListAnomaliesAsync(AnomalyStatus.Resolved, 50, false, CancellationToken.None));
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
        Assert.Empty(await Store.ListAnomaliesAsync(null, 10, true, CancellationToken.None));
    }
}
