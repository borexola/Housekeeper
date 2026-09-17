using System.Globalization;

namespace Housekeeper.Core;

/// <summary>
/// Durations the way a person types them into a settings box: "45s", "5m", "2h", "14d", "1.5h".
/// The <see cref="TimeSpan"/> forms ("00:05:00", "14.00:00:00") still parse, so a config file written
/// before the settings UI existed keeps working.
/// </summary>
public static class Durations
{
    private static readonly (char Suffix, long Ticks)[] Units =
    [
        ('d', TimeSpan.TicksPerDay),
        ('h', TimeSpan.TicksPerHour),
        ('m', TimeSpan.TicksPerMinute),
        ('s', TimeSpan.TicksPerSecond),
    ];

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();

        // A bare number is ambiguous, and TimeSpan.Parse would read "5" as five days. Refuse it.
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;

        foreach (var (suffix, ticks) in Units)
        {
            if (char.ToLowerInvariant(trimmed[^1]) != suffix) continue;

            if (!double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
                !double.IsFinite(amount) || amount < 0 || amount > 3_650_000)
                return false;

            value = TimeSpan.FromTicks((long)(amount * ticks));
            return true;
        }

        return TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Renders a duration in the shortest whole unit that represents it exactly.</summary>
    public static string Format(TimeSpan span)
    {
        if (span == TimeSpan.Zero) return "0s";
        if (span < TimeSpan.Zero) return span.ToString("c", CultureInfo.InvariantCulture);

        foreach (var (suffix, ticks) in Units)
            if (span.Ticks % ticks == 0)
                return (span.Ticks / ticks).ToString(CultureInfo.InvariantCulture) + suffix;

        return span.ToString("c", CultureInfo.InvariantCulture);
    }
}
