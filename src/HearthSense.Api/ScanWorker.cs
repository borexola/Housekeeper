using HearthSense.Core;

namespace HearthSense.Api;

/// <summary>
/// Runs the anomaly scan on a schedule. A failed scan is logged and retried next tick, never fatal.
/// The schedule and the on/off switch are re-read every tick, so both can be changed from the settings
/// page without restarting; the loop keeps running while scanning is off so it can be turned back on.
/// </summary>
public sealed class ScanWorker(
    AnomalyScanner scanner,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<ScanWorker> logger) : BackgroundService
{
    /// <summary>Grace period so Home Assistant has a moment to come up when both start together.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);

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

            if (!scan.Enabled) continue;

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
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static TimeSpan IntervalOf(ScanOptions scan) =>
        scan.Interval > TimeSpan.Zero ? scan.Interval : FallbackInterval;

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
