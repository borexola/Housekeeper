using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// A numeric finding has to have lasted. A z-score is a verdict on one reading, and one reading is what a
/// kettle produces: a real house watched its list go from seven findings to three between two refreshes,
/// every one of the four a spike that was gone by the next scan. So the reading has to have been beyond
/// the bar for a while, judged from the stored readings in between -- however often, or rarely, the sensor
/// reports.
/// </summary>
public class ExcursionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 2, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    /// <summary>
    /// Forty readings near a hundred, twenty minutes apart (a little over thirteen hours), ending just before
    /// <paramref name="until"/>. Long enough to be judged at all, past the six-hour and thirty-reading
    /// minimums, and deliberately far too short for the time-of-day or time-of-week baselines, so every test
    /// here runs against the all-history one.
    /// </summary>
    private static List<StateSample> Baseline(DateTimeOffset until)
    {
        var step = TimeSpan.FromMinutes(20);
        const int count = 40;
        var start = until - step * count;
        return [.. Enumerable.Range(0, count).Select(i => Reading(100 + (((i % 4) - 1.5) * 1.0), start + step * i))];
    }

    private static StateSample Reading(double value, DateTimeOffset at) => new(Ha.Number(value), value, at);

    private static HaEntity Plug(double reading, DateTimeOffset changed) =>
        Build.Entity("sensor.plug_power", Ha.Number(reading), changed, deviceClass: "power", unit: "W");

    /// <summary>
    /// What the scanner hands a detector: the contiguous recent readings, and the baseline thinned to a
    /// couple an hour. A chatty sensor's last twelve readings are all in the first and mostly absent from
    /// the second, which is what keeps twelve minutes of a new level from becoming "somewhere it already
    /// goes" before anyone has been told about it.
    /// </summary>
    private static EntityHistory History(List<StateSample> baseline, IEnumerable<StateSample> recent) =>
        new([.. baseline.Concat(recent).OrderBy(s => s.ChangedUtc)], baseline);

    [Fact]
    public void One_reading_out_of_range_is_a_spike_not_a_finding()
    {
        var changed = Now.AddMinutes(-1);
        var history = Baseline(changed.AddMinutes(-19));
        history.Add(Reading(160, changed));

        Assert.Null(AnomalyDetection.DetectNumericOutlier(Plug(160, changed), history, Options, Now));

        // The wait is the only thing in the way.
        var atOnce = new ScanOptions { MinimumExcursion = TimeSpan.Zero };
        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(Plug(160, changed), history, atOnce, Now));
    }

    [Fact]
    public void A_sensor_that_reports_rarely_counts_from_its_last_change()
    {
        var changed = Now.AddMinutes(-15);
        var history = Baseline(changed.AddMinutes(-5));
        history.Add(Reading(160, changed));

        var finding = AnomalyDetection.DetectNumericOutlier(Plug(160, changed), history, Options, Now);

        Assert.NotNull(finding);
        Assert.Contains("and has for 15 minutes", finding.Summary);
        Assert.Contains("\"excursion_seconds\":900", finding.EvidenceJson);
    }

    [Fact]
    public void A_chatty_sensor_is_judged_on_how_long_it_has_been_out_not_how_often_it_says_so()
    {
        var changed = Now.AddMinutes(-1);
        var baseline = Baseline(Now.AddMinutes(-30));
        var run = Enumerable.Range(1, 12).Select(minute => Reading(160, Now.AddMinutes(-minute))).ToList();

        // Six minutes of readings at 160, one a minute: not yet.
        Assert.Null(AnomalyDetection.DetectNumericOutlier(Plug(160, changed), History(baseline, run.Take(6)), Options, Now));

        // Twelve minutes of them: a finding, dated from the first.
        var finding = AnomalyDetection.DetectNumericOutlier(Plug(160, changed), History(baseline, run), Options, Now);
        Assert.NotNull(finding);
        Assert.Contains("\"excursion_seconds\":720", finding.EvidenceJson);
    }

    [Fact]
    public void One_reading_back_inside_the_range_does_not_end_the_run_but_two_in_a_dozen_do()
    {
        var changed = Now.AddMinutes(-1);
        var baseline = Baseline(Now.AddMinutes(-30));
        var oneDip = Enumerable.Range(1, 12).Select(minute => Reading(minute == 6 ? 100 : 160, Now.AddMinutes(-minute)));

        Assert.NotNull(AnomalyDetection.DetectNumericOutlier(Plug(160, changed), History(baseline, oneDip), Options, Now));

        var flickering = Enumerable.Range(1, 12).Select(minute => Reading(minute is 6 or 4 ? 100 : 160, Now.AddMinutes(-minute)));

        Assert.Null(AnomalyDetection.DetectNumericOutlier(Plug(160, changed), History(baseline, flickering), Options, Now));
    }
}
