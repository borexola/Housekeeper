using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// Runs the anomaly scan on a schedule. A failed scan is logged and retried next tick, never fatal.
/// The schedule and the on/off switch are re-read every tick, so both can be changed from the settings
/// page without restarting; the loop keeps running while scanning is off so it can be turned back on.
/// </summary>
public sealed class ScanWorker(
    AnomalyScanner scanner,
    ConcernService concerns,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<ScanWorker> logger) : BackgroundService
{
    /// <summary>Grace period so Home Assistant has a moment to come up when both start together.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// PeriodicTimer refuses anything past ~49.7 days, from the constructor and the Period setter alike.
    /// An exception escaping ExecuteAsync stops the whole host -- including the settings page needed to undo
    /// the value -- so the interval is clamped rather than trusted.
    /// </summary>
    private static readonly TimeSpan LongestInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// And PeriodicTimer refuses anything under a millisecond, at the other end. A scan every fraction of a
    /// second is not a thing anyone wants either way, so the floor is set somewhere sane rather than at the
    /// exact point the constructor starts throwing.
    /// </summary>
    private static readonly TimeSpan ShortestInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = IntervalOf(settings.Current.Scan);
        using var timer = new PeriodicTimer(interval, clock);

        bool? announced = null;

        do
        {
            var scan = settings.Current.Scan;

            var wanted = IntervalOf(scan);
            if (wanted != interval)
            {
                interval = wanted;
                timer.Period = interval;
                logger.LogInformation("Scan interval changed to {Interval}.", interval);
            }

            if (announced != scan.Enabled)
            {
                announced = scan.Enabled;
                if (scan.Enabled) logger.LogInformation("Anomaly scanning every {Interval}.", interval);
                else logger.LogInformation("Anomaly scanning is off. Turn it on from the settings page.");
            }

            // Published before the wait, so the dashboard counts down to the tick that is really coming.
            scanner.NextUtc = scan.Enabled ? clock.GetUtcNow() + interval : null;

            if (scan.Enabled)
            {
                try
                {
                    await scanner.ScanAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scan failed; retrying in {Interval}.", interval);
                }
            }

            // A concern added while the model was down, or before one was chosen, is read the moment it can
            // be. On the scan's tick whether or not scanning is on, because the concern is the user's ask
            // and the model coming back is the whole of what it is waiting for -- but AFTER the scan, since
            // a model that takes its full timeout to answer would otherwise delay the scan the dashboard is
            // counting down to by a minute and a half, every tick, for as long as it stayed slow.
            try
            {
                if (await concerns.ReadPendingAsync(stoppingToken).ConfigureAwait(false) > 0)
                    logger.LogInformation("Read a concern the model had not yet had its say on.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read a pending concern; it will be tried again next tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>The tick this schedule really runs at, with anything a PeriodicTimer would refuse brought back.</summary>
    public static TimeSpan IntervalOf(ScanOptions scan)
    {
        if (scan.Interval <= TimeSpan.Zero) return FallbackInterval;
        if (scan.Interval < ShortestInterval) return ShortestInterval;

        return scan.Interval < LongestInterval ? scan.Interval : LongestInterval;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
