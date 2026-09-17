using System.Threading.Channels;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// Writes what the live feed sees into the same sample table the scan writes to.
///
/// Batched rather than a row per event: an integration reloading re-announces every entity it owns at once,
/// and a few hundred individual inserts into SQLite on an SD card is a stall. Duplicates are not a concern
/// in either direction — the sample table's primary key is (entity, changed) and the insert ignores
/// conflicts — so the poller and the feed can both write the same change without coordinating.
/// </summary>
public sealed class StateFeedWorker(
    IStateFeed feed,
    IStore store,
    AnomalyScanner scanner,
    ISettingsProvider settings,
    TimeProvider clock,
    ILogger<StateFeedWorker> logger) : BackgroundService
{
    /// <summary>
    /// How many events are held before the oldest are dropped.
    ///
    /// Bounded, and dropping rather than blocking, because back-pressure here would stall the socket and
    /// then the reconnect logic would treat a busy house as a dead one. Losing a few transitions during a
    /// storm costs a little accuracy; the poller still has the entity's current state either way.
    /// </summary>
    private const int MostHeld = 10_000;

    private const int MostPerWrite = 250;

    /// <summary>How long a partial batch waits for company before being written anyway.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);

    /// <summary>Grace period so Home Assistant has a moment to come up when both start together.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

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

        var channel = Channel.CreateBounded<StateEvent>(new BoundedChannelOptions(MostHeld)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var reading = DrainAsync(channel.Reader, stoppingToken);

        try
        {
            await foreach (var change in feed.WatchAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!settings.Current.Scan.RealtimeUpdates) continue;

                // Only what the scan itself is watching. Two writers into one table have to agree on that,
                // or the feed fills the database with entities the watch list and its cap exclude.
                if (!scanner.Watching.Contains(change.EntityId)) continue;

                await channel.Writer.WriteAsync(change, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        await reading.ConfigureAwait(false);
    }

    private async Task DrainAsync(ChannelReader<StateEvent> reader, CancellationToken stoppingToken)
    {
        List<(string EntityId, StateSample Sample)> batch = [];

        while (await WaitAsync(reader, stoppingToken).ConfigureAwait(false))
        {
            // Take everything already queued, up to a write's worth, then give stragglers a moment to
            // arrive rather than writing a row at a time through a burst.
            while (batch.Count < MostPerWrite && reader.TryRead(out var change))
                batch.Add((change.EntityId, new StateSample(change.State, null, change.ChangedUtc)));

            if (batch.Count == 0) continue;

            if (batch.Count < MostPerWrite)
            {
                try
                {
                    await Task.Delay(Linger, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Write what is in hand before going.
                }

                while (batch.Count < MostPerWrite && reader.TryRead(out var late))
                    batch.Add((late.EntityId, new StateSample(late.State, null, late.ChangedUtc)));
            }

            try
            {
                var written = await store.AddSamplesAsync(batch, CancellationToken.None).ConfigureAwait(false);
                logger.LogDebug("Live feed stored {Written} of {Seen} changes.", written, batch.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed write costs the transitions in this batch and nothing else. The poller is still
                // recording state, and the next batch is unaffected.
                logger.LogWarning(ex, "Could not store {Count} live state changes.", batch.Count);
            }

            batch.Clear();
        }
    }

    private static async Task<bool> WaitAsync(ChannelReader<StateEvent> reader, CancellationToken stoppingToken)
    {
        try
        {
            return await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
