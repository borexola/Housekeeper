using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

/// <summary>
/// The coverage note through a real scan: what the scan reads, when, and how often, and whether the card is
/// answered from what is true now. Reading the automations is one request per automation to Home Assistant,
/// so how rarely it happens matters as much as what comes of it.
/// </summary>
public class CoverageScanTests : StoreFixture
{
    private readonly FakeHomeAssistant _ha = new();

    private AnomalyScanner Scanner(bool learnHabits = false) =>
        new(_ha, Store, new FakeSettings(Options(learnHabits)), Clock, NullLogger<AnomalyScanner>.Instance);

    private static HousekeeperOptions Options(bool learnHabits)
    {
        var options = new HousekeeperOptions();
        options.Scan.IncludeAll = false;
        options.Scan.Include = ["binary_sensor.*"];
        options.Scan.BackfillFromRecorder = false;
        options.Scan.LearnHabits = learnHabits;
        return options;
    }

    private const string Door = "binary_sensor.freezer_door";
    private const string Alert = "automation.freezer_door_left_open";

    /// <summary>The automation that already does what the stuck-door finding would suggest, with its entity in the state list.</summary>
    private void FreezerAlertExists()
    {
        _ha.Entities.Add(Build.Entity(Alert, "on", Clock.GetUtcNow().AddDays(-30), "Freezer door left open", automationConfigId: "cfg-1"));
        _ha.Automations.Add(new ExistingAutomation("cfg-1", Alert, "Freezer door left open",
            new HashSet<string>([Door], StringComparer.Ordinal),
            new HashSet<string>(["state"], StringComparer.Ordinal),
            [new AutomationTrigger("state", [Door], To: ["on"], For: TimeSpan.FromMinutes(10))]));
    }

    /// <summary>An automation that has nothing to do with the door, so that the state list goes on holding automations.</summary>
    private void PorchLightExists() =>
        _ha.Entities.Add(Build.Entity("automation.porch_light", "on", Clock.GetUtcNow().AddDays(-30), "Porch light", automationConfigId: "cfg-2"));

    /// <summary>Twelve short openings on record, then the door left open for three hours: the next scan raises a stuck-state finding.</summary>
    private async Task DoorLeftOpenAsync(AnomalyScanner scanner)
    {
        var door = Build.Entity(Door, "off", Clock.GetUtcNow().AddDays(-2), "Freezer Door", "door");
        _ha.Entities.Add(door);

        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromHours(2));
            Set(door with { State = "on", LastChanged = Clock.GetUtcNow() });
            await scanner.ScanAsync(CancellationToken.None);

            Clock.Advance(TimeSpan.FromSeconds(45));
            Set(door with { State = "off", LastChanged = Clock.GetUtcNow() });
            await scanner.ScanAsync(CancellationToken.None);
        }

        Clock.Advance(TimeSpan.FromHours(2));
        Set(door with { State = "on", LastChanged = Clock.GetUtcNow() });
        await scanner.ScanAsync(CancellationToken.None);

        Clock.Advance(TimeSpan.FromHours(3));
    }

    private void Set(HaEntity entity) => _ha.Entities[_ha.Entities.FindIndex(e => e.EntityId == entity.EntityId)] = entity;

    private async Task<Anomaly> FindingAsync()
    {
        var open = await Store.ListAnomaliesAsync(AnomalyStatus.Open, 50, false, CancellationToken.None);
        return Assert.Single(open, a => a.Kind == AnomalyKind.StuckState);
    }

    /// <summary>Asked the way the Noticed page asks: through the endpoint's own helper, from what the scan published.</summary>
    private static IReadOnlyList<CoveringAutomation>? CoveredBy(Anomaly finding, AnomalyScanner scanner) =>
        Endpoints.CoveredBy(finding, scanner.Automations);

    [Fact]
    public async Task A_finding_an_automation_already_fires_on_says_so()
    {
        FreezerAlertExists();
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);
        await scanner.ScanAsync(CancellationToken.None);

        var one = Assert.Single(CoveredBy(await FindingAsync(), scanner)!);
        Assert.Equal("Freezer door left open", one.Alias);
        Assert.Equal(Alert, one.EntityId);
        Assert.Equal("fires when it stays open for 10 minutes", one.Why);
    }

    /// <summary>
    /// Worked out when asked, from the states the last scan took, so a card never goes on naming an automation
    /// switched off or deleted since -- including a card the scan kept open without raising it again.
    /// </summary>
    [Fact]
    public async Task An_automation_switched_off_or_deleted_since_is_not_named()
    {
        FreezerAlertExists();
        PorchLightExists();
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);
        await scanner.ScanAsync(CancellationToken.None);
        Assert.Single(CoveredBy(await FindingAsync(), scanner)!);

        Set(_ha.Entities.Single(e => e.EntityId == Alert) with { State = "off" });
        Clock.Advance(TimeSpan.FromMinutes(5));
        await scanner.ScanAsync(CancellationToken.None);
        Assert.Empty(CoveredBy(await FindingAsync(), scanner)!);

        // Deleted, and the door renamed out from under its card: the card stays open, raised by nobody, and
        // still names nothing.
        _ha.Entities.RemoveAll(e => e.EntityId is Alert or Door);
        Clock.Advance(TimeSpan.FromMinutes(5));
        await scanner.ScanAsync(CancellationToken.None);
        Assert.Empty(CoveredBy(await FindingAsync(), scanner) ?? []);
    }

    /// <summary>Nothing is read unless an open finding could use it: a history with nothing wrong in it, or a dismissed card, costs no requests.</summary>
    [Fact]
    public async Task Nothing_is_read_while_no_open_finding_could_use_it()
    {
        FreezerAlertExists();
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);

        Assert.Equal(0, _ha.AutomationReads);
        Assert.Null(scanner.Automations);

        await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(1, _ha.AutomationReads);

        var finding = await FindingAsync();
        await Store.UpdateAnomalyAsync(finding with { Status = AnomalyStatus.Dismissed, DecidedUtc = Clock.GetUtcNow(), Dismissals = 1 }, CancellationToken.None);

        // The door is still open, so the detector still fires every scan -- on a card nobody will see.
        Clock.Advance(TimeSpan.FromMinutes(5));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(1, _ha.AutomationReads);
        Assert.Null(scanner.Automations);
    }

    /// <summary>
    /// With a token Home Assistant refuses, every config request is logged there as a failed login and can get
    /// this address banned. A failed read is therefore not tried again every scan, but hourly, as before.
    /// </summary>
    [Fact]
    public async Task A_failed_read_is_not_tried_again_for_an_hour()
    {
        FreezerAlertExists();
        _ha.AutomationsFailure = new HomeAssistantException("None of the automations in Home Assistant could be read.");
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);

        await scanner.ScanAsync(CancellationToken.None);
        Assert.Equal(1, _ha.AutomationReads);
        Assert.Null(CoveredBy(await FindingAsync(), scanner));

        for (var i = 0; i < 6; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(5));
            await scanner.ScanAsync(CancellationToken.None);
        }

        Assert.Equal(1, _ha.AutomationReads);

        // An hour on, it is asked once more -- and this time the token is right.
        _ha.AutomationsFailure = null;
        Clock.Advance(TimeSpan.FromMinutes(31));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(2, _ha.AutomationReads);
        Assert.Single(CoveredBy(await FindingAsync(), scanner)!);
    }

    /// <summary>A config endpoint that has stopped answering is given a minute, all told, and the scan goes on without it.</summary>
    [Fact]
    public async Task A_read_that_hangs_is_given_up_on_and_the_scan_finishes()
    {
        FreezerAlertExists();
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);

        _ha.AutomationsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = scanner.ScanAsync(CancellationToken.None);
        Assert.False(scan.IsCompleted);

        Clock.Advance(TimeSpan.FromMinutes(1));
        var report = await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, report.Raised);
        Assert.Equal(1, _ha.AutomationReads);
        Assert.Null(scanner.Automations);
    }

    /// <summary>
    /// Mid-restart the state list has no automations in it, and every one of them would read as switched off.
    /// What was known before stands through the restart -- for as long as a restart takes, and no longer.
    /// </summary>
    [Fact]
    public async Task What_was_known_before_a_restart_stands_through_it()
    {
        FreezerAlertExists();
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);
        await scanner.ScanAsync(CancellationToken.None);
        var known = scanner.Automations;
        Assert.Single(CoveredBy(await FindingAsync(), scanner)!);

        _ha.Entities.RemoveAll(e => e.Domain == "automation");
        Clock.Advance(TimeSpan.FromMinutes(5));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Same(known, scanner.Automations);
        Assert.Single(CoveredBy(await FindingAsync(), scanner)!);
        Assert.Equal(1, _ha.AutomationReads);

        // A "restart" that has gone on for an hour is a house that has lost its automations.
        for (var i = 0; i < 12; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(5));
            await scanner.ScanAsync(CancellationToken.None);
        }

        Assert.Null(scanner.Automations);
    }

    /// <summary>
    /// A config request that timed out is not an automation deleted. One that could not be read this time, and
    /// has not changed since it last was, keeps its last reading; one that has changed since is not vouched for.
    /// </summary>
    [Fact]
    public async Task An_automation_that_could_not_be_read_this_time_keeps_its_last_reading()
    {
        FreezerAlertExists();
        var scanner = Scanner();
        await DoorLeftOpenAsync(scanner);
        await scanner.ScanAsync(CancellationToken.None);

        var alert = _ha.Automations.Single();
        _ha.Automations.Clear();
        Clock.Advance(TimeSpan.FromMinutes(5));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(2, _ha.AutomationReads);
        Assert.Single(CoveredBy(await FindingAsync(), scanner)!);

        // Edited in Home Assistant since -- a reloaded automation is a new state for its entity -- and still
        // unreadable: its last reading describes an automation that no longer exists.
        Set(_ha.Entities.Single(e => e.EntityId == alert.EntityId) with { LastChanged = Clock.GetUtcNow() });
        Clock.Advance(TimeSpan.FromMinutes(5));
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Empty(CoveredBy(await FindingAsync(), scanner)!);
    }

    /// <summary>The routine search and the card both need the automations; a scan that serves both reads them once.</summary>
    [Fact]
    public async Task The_routine_search_and_the_card_share_one_read()
    {
        FreezerAlertExists();
        var scanner = Scanner(learnHabits: true);
        await DoorLeftOpenAsync(scanner);

        var before = _ha.AutomationReads;
        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(before + 1, _ha.AutomationReads);
        Assert.NotNull(scanner.LastRoutineSearch);
        Assert.Single(CoveredBy(await FindingAsync(), scanner)!);
    }
}
