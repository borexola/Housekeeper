using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// A house has a weekly rhythm as well as a daily one. With only a daily band, a door that is open for an hour
/// every weekend morning sets the bar for weekday mornings too, and a weekday morning it is stuck open for
/// half an hour is never reported. Once three weeks of history exist, the same part of the week is its own
/// baseline. Moments are chosen at midday UTC so the day of the week is the same in every time zone.
/// </summary>
public class WeeklyBaselineTests
{
    // A Wednesday.
    private static readonly DateTimeOffset Now = new(2026, 3, 25, 13, 0, 0, TimeSpan.Zero);

    private static ScanOptions Options => new();

    /// <summary>Four weeks of one opening a day in the 12:00-16:00 band: an hour at weekends, two minutes on weekdays.</summary>
    private static List<StateSample> FourWeeks()
    {
        List<StateSample> history = [];
        for (var day = 28; day >= 1; day--)
        {
            var opened = Now.AddDays(-day).AddHours(-1);
            var weekend = opened.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            history.Add(new StateSample("on", null, opened));
            history.Add(new StateSample("off", null, opened.AddMinutes(weekend ? 60 : 2)));
        }

        return history;
    }

    [Fact]
    public void A_weekday_stretch_is_judged_against_weekdays_once_three_weeks_exist()
    {
        var door = Build.Entity("binary_sensor.garage_door", "on", Now.AddMinutes(-30), "Garage door", "door");

        var anomaly = AnomalyDetection.DetectStuckState(door, FourWeeks(), Options, Now);

        Assert.NotNull(anomaly);
        Assert.Contains("on weekdays", anomaly.Summary);
        Assert.Contains("\"baseline\":\"time_of_week\"", anomaly.EvidenceJson);
        Assert.Contains("\"week_part\":\"weekday\"", anomaly.EvidenceJson);
    }

    [Fact]
    public void With_only_two_weeks_the_weekend_hour_still_sets_the_bar()
    {
        var door = Build.Entity("binary_sensor.garage_door", "on", Now.AddMinutes(-30), "Garage door", "door");
        var twoWeeks = FourWeeks().Where(sample => sample.ChangedUtc >= Now.AddDays(-14)).ToList();

        // Same weekday, same half hour, but the history does not reach back far enough to split the week, so
        // the weekend openings are in the baseline and half an hour is under three times an hour.
        Assert.Null(AnomalyDetection.DetectStuckState(door, twoWeeks, Options, Now));
    }

    [Fact]
    public void A_weekend_stretch_is_not_reported_for_being_a_weekend()
    {
        // The following Saturday, open for fifty minutes: ordinary for a weekend.
        var saturday = new DateTimeOffset(2026, 3, 28, 13, 0, 0, TimeSpan.Zero);
        var door = Build.Entity("binary_sensor.garage_door", "on", saturday.AddMinutes(-50), "Garage door", "door");
        var history = FourWeeks().Where(sample => sample.ChangedUtc < saturday).ToList();

        Assert.Null(AnomalyDetection.DetectStuckState(door, history, Options, saturday));
    }
}
