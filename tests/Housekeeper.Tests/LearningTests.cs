using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// What a finding does as the history under it grows.
///
/// Every detector is a pure function of the history at the moment it runs, and the scanner rewrites an open
/// finding's sentence, threshold and severity on every scan — so a verdict is never frozen at the moment it
/// was first reached. These tests hold that to account: the same entity, judged against a longer history,
/// has to reach a different answer and has to stop being a finding at all once its behaviour turns out to
/// be ordinary.
/// </summary>
public class LearningTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 8, 21, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    /// <summary>
    /// Open/closed cycles ending before <paramref name="until"/>, one every <paramref name="every"/>.
    ///
    /// The stride is separate from the duration so a test can vary how LONG each stretch was without
    /// changing how long the entity was watched for — which is now two different bars, and conflating them
    /// is how the first draft of these tests failed for a reason that had nothing to do with what they
    /// were checking.
    /// </summary>
    private static List<StateSample> Cycles(DateTimeOffset until, int count, TimeSpan open, TimeSpan? every = null)
    {
        var stride = every ?? TimeSpan.FromHours(6);
        List<StateSample> history = [];
        var at = until - (count * stride);

        for (var i = 0; i < count; i++)
        {
            history.Add(new StateSample("open", null, at));
            history.Add(new StateSample("closed", null, at + open));
            at += stride;
        }

        return history;
    }

    /// <summary>
    /// The scenario as reported: a light switch held off far longer than the short evening it had been
    /// watched for, then the same switch a few days later once its real rhythm is on record.
    ///
    /// This is a `switch.` rather than a `light.` on purpose — the resting-state rule covers both now, so
    /// the test uses a domain where the active side is still judged, to prove the WITHDRAWAL rather than
    /// the exemption.
    /// </summary>
    [Fact]
    public void A_finding_withdraws_itself_once_the_baseline_catches_up()
    {
        var heldSince = Now.AddHours(-5);
        var entity = Build.Entity("cover.garage_door", "open", heldSince, "Garage Door");

        // Day one: four short stretches spread across a day. Five hours open is far past anything seen.
        List<StateSample> earlyDays = [];
        for (var i = 8; i > 0; i -= 2)
        {
            var at = heldSince.AddHours(-i * 3);
            earlyDays.Add(new StateSample("open", null, at));
            earlyDays.Add(new StateSample("closed", null, at.AddMinutes(13)));
        }

        var raised = AnomalyDetection.DetectStuckState(entity, earlyDays, Options, Now);
        Assert.NotNull(raised);
        Assert.Contains("5 hours", raised.Summary);

        // Days two and three: it turns out this door really is left open for hours at a time. The same
        // five-hour stretch is now unremarkable, and the detector says nothing.
        List<StateSample> withLongStretches = [.. earlyDays];
        for (var day = 3; day >= 2; day--)
        {
            var at = heldSince.AddDays(-day);
            withLongStretches.Insert(0, new StateSample("closed", null, at.AddHours(-1)));
            withLongStretches.Insert(1, new StateSample("open", null, at));
            withLongStretches.Insert(2, new StateSample("closed", null, at.AddHours(6)));
        }

        Assert.Null(AnomalyDetection.DetectStuckState(entity, withLongStretches, Options, Now));
    }

    /// <summary>
    /// The scanner acts on that silence: a finding whose condition no longer holds is closed rather than
    /// left on the dashboard. Without this the detector could learn all it liked and the user would still
    /// be looking at the old card.
    /// </summary>
    [Fact]
    public void The_scanner_can_tell_learning_from_not_knowing()
    {
        var heldSince = Now.AddHours(-5);
        var entity = Build.Entity("cover.garage_door", "open", heldSince);

        // A history with long stretches on record: the detector stays quiet AND the scanner is able to say
        // so positively, which is what lets the open finding be closed rather than merely going stale.
        List<StateSample> settled = [];
        for (var day = 6; day >= 1; day--)
        {
            var at = heldSince.AddDays(-day);
            settled.Add(new StateSample("open", null, at));
            settled.Add(new StateSample("closed", null, at.AddHours(6)));
        }

        Assert.Null(AnomalyDetection.DetectStuckState(entity, settled, Options, Now));
        Assert.Contains(AnomalyKind.StuckState, AnomalyDetection.Resolvable(entity, settled, Options, Now));
    }

    /// <summary>
    /// While a finding is open its sentence is rewritten from the current history, so the threshold it
    /// offers tracks what the entity has since been seen to do rather than freezing at first sight.
    /// </summary>
    [Fact]
    public void The_suggested_threshold_moves_with_the_evidence()
    {
        var heldSince = Now.AddHours(-9);
        var entity = Build.Entity("cover.garage_door", "open", heldSince);

        var thin = AnomalyDetection.DetectStuckState(entity, Cycles(heldSince, 6, TimeSpan.FromMinutes(13)), Options, Now);
        var richer = AnomalyDetection.DetectStuckState(entity, Cycles(heldSince, 6, TimeSpan.FromMinutes(75)), Options, Now);

        Assert.NotNull(thin);
        Assert.NotNull(richer);

        // Same entity, same nine hours held, different history: the offered automation is not the same one.
        Assert.NotEqual(thin.SuggestedRequest, richer.SuggestedRequest);

        static double Threshold(Anomaly a) =>
            JsonDocument.Parse(a.EvidenceJson).RootElement.GetProperty("longest_previous_seconds").GetDouble();

        Assert.True(Threshold(richer) > Threshold(thin), "a longer worst case has to raise the bar");
    }

    /// <summary>
    /// Four stretches in one evening is an evening. The count said "across 4 earlier stretches", which
    /// reads as settled fact, and nothing said over what — so a switch watched since teatime produced a
    /// confident claim about its daily rhythm.
    /// </summary>
    [Fact]
    public void An_evenings_worth_of_watching_is_not_a_verdict()
    {
        var heldSince = Now.AddHours(-2);
        var entity = Build.Entity("cover.garage_door", "open", heldSince);

        // Four complete stretches, all inside a single evening.
        var oneEvening = Cycles(heldSince, 4, TimeSpan.FromMinutes(13), every: TimeSpan.FromMinutes(40));
        Assert.Null(AnomalyDetection.DetectStuckState(entity, oneEvening, Options, Now));

        // The same four stretches spread over a couple of days are worth an opinion.
        List<StateSample> spread = [];
        for (var i = 4; i > 0; i--)
        {
            var at = heldSince.AddHours(-i * 12);
            spread.Add(new StateSample("open", null, at));
            spread.Add(new StateSample("closed", null, at.AddMinutes(13)));
        }

        Assert.NotNull(AnomalyDetection.DetectStuckState(entity, spread, Options, Now));
    }

    /// <summary>How much evidence is behind a finding is now on the card, so growing confidence is visible.</summary>
    [Fact]
    public void The_card_says_how_long_it_has_been_watching()
    {
        var heldSince = Now.AddHours(-9);
        var entity = Build.Entity("cover.garage_door", "open", heldSince);

        var anomaly = AnomalyDetection.DetectStuckState(entity, Cycles(heldSince, 8, TimeSpan.FromMinutes(20)), Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("seen over", anomaly.Summary);
        Assert.Contains("\"witnessed_seconds\"", anomaly.EvidenceJson);
    }

    /// <summary>
    /// The other half of the reported case, and the reason it was ever raised: a light being off is where a
    /// light lives. No length of it is news, however little history there is.
    /// </summary>
    [Theory]
    [InlineData("light.backyard_light_switch", "off")]
    [InlineData("switch.backyard_smart_switch", "off")]
    [InlineData("lock.front_door", "locked")]
    [InlineData("cover.garage_door", "closed")]
    [InlineData("media_player.lounge_tv", "idle")]
    [InlineData("vacuum.roomba", "docked")]
    [InlineData("automation.porch_light", "on")]
    public void Nothing_is_reported_for_sitting_where_it_always_sits(string entityId, string state)
    {
        var heldSince = Now.AddHours(-5);
        var entity = Build.Entity(entityId, state, heldSince);

        List<StateSample> history = [];
        for (var i = 10; i > 0; i--)
        {
            var at = heldSince.AddHours(-i * 6);
            history.Add(new StateSample(state, null, at));
            history.Add(new StateSample("__active__", null, at.AddMinutes(13)));
        }

        Assert.Null(AnomalyDetection.DetectStuckState(entity, history, Options, Now));
    }

    /// <summary>And the active side is still judged, which is the whole point of naming a resting side.</summary>
    [Fact]
    public void A_light_left_on_all_day_is_still_reported()
    {
        var heldSince = Now.AddHours(-14);
        var entity = Build.Entity("light.porch", "on", heldSince, "Porch Light");

        List<StateSample> history = [];
        for (var i = 10; i > 0; i--)
        {
            var at = heldSince.AddHours(-i * 6);
            history.Add(new StateSample("on", null, at));
            history.Add(new StateSample("off", null, at.AddMinutes(40)));
        }

        var anomaly = AnomalyDetection.DetectStuckState(entity, history, Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("14 hours", anomaly.Summary);
    }
}
