namespace Housekeeper.Core;

/// <summary>
/// Shapes what the recorder hands back into what the sample table would have held had Housekeeper been
/// watching all along.
///
/// The recorder keeps every change, and for a power meter reporting every ten seconds that is a quarter of
/// a million rows for a month, of which the outlier detector reads two an hour. Storing them all would make
/// the first scan the most expensive thing the app ever does, for rows nothing reads. Numeric readings are
/// therefore thinned to the same density the detectors read at; non-numeric states are kept whole, because
/// the stuck-state detector measures the gap between one change and the next and a missing row is a gap
/// that never happened.
/// </summary>
public static class Backfill
{
    /// <summary>
    /// Thins one entity's recorder history, oldest first. Consecutive identical states collapse to the first,
    /// numeric readings keep at most <paramref name="numericPerHour"/> per clock hour, and anything before
    /// <paramref name="cutoffUtc"/> is dropped because the retention prune would delete it again.
    /// </summary>
    public static IReadOnlyList<StateSample> Thin(IReadOnlyList<StateSample> history, DateTimeOffset cutoffUtc, int numericPerHour)
    {
        if (history.Count == 0) return [];

        var perHour = Math.Max(1, numericPerHour);
        List<StateSample> kept = [];
        string? previousState = null;
        long bucket = long.MinValue;
        var inBucket = 0;

        foreach (var sample in history.OrderBy(s => s.ChangedUtc))
        {
            if (sample.ChangedUtc < cutoffUtc) continue;
            if (string.Equals(sample.State, previousState, StringComparison.Ordinal)) continue;

            previousState = sample.State;

            if (sample.Numeric is not null)
            {
                var hour = sample.ChangedUtc.ToUnixTimeSeconds() / 3600;
                if (hour != bucket)
                {
                    bucket = hour;
                    inBucket = 0;
                }

                if (++inBucket > perHour) continue;
            }

            kept.Add(sample);
        }

        return kept;
    }
}
