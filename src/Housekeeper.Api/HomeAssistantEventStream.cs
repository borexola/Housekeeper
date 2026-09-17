using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// Home Assistant's <c>state_changed</c> events, for the transitions that fall between two polls.
///
/// The scan reads every entity once a minute and stores what changed, which caps it at one sample per
/// entity per minute and makes anything faster than that invisible. A door opened and shut inside the same
/// minute leaves two stored samples carrying the same state, and the stuck-state detector has to throw that
/// pair away rather than count a stretch it never saw the ends of. A motion sensor's real on-durations are
/// almost entirely in that blind spot.
///
/// Reconnects on its own with backoff and simply goes quiet while it cannot connect. Nothing downstream
/// treats an outage as an error, because the poller is still running and the next poll closes the gap —
/// which is the whole reason this could be added without a resynchronisation protocol.
/// </summary>
public sealed class HomeAssistantEventStream(
    ISettingsProvider settings,
    ISecretSource secrets,
    ILogger<HomeAssistantEventStream> logger) : IStateFeed
{
    /// <summary>Backoff between reconnection attempts, growing to a ceiling and staying there.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the stream will sit in silence before deciding the connection is dead.
    ///
    /// A TCP connection that has gone away without saying so reads exactly like a quiet house, and a socket
    /// blocked forever on a receive is a feed that never reconnects and never complains. Home Assistant
    /// emits something well inside this on any live install.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeGivingUp = TimeSpan.FromMinutes(5);

    public async IAsyncEnumerable<StateEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retry = FirstRetry;

        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;

            // Each connection is its own enumerator. Anything thrown inside it ends that connection only;
            // the loop backs off and starts another, which is what makes a Home Assistant restart a pause
            // rather than the end of the feed.
            await foreach (var change in OneConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!connected)
                {
                    connected = true;
                    retry = FirstRetry;
                    logger.LogInformation("Watching Home Assistant for live state changes.");
                }

                yield return change;
            }

            if (cancellationToken.IsCancellationRequested) break;

            logger.LogDebug("The live state feed is not connected; retrying in {Retry}.", Durations.Format(retry));

            try
            {
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retry = retry >= LongestRetry ? LongestRetry : retry + retry;
        }
    }

    /// <summary>One connection's worth of events, ending quietly on any failure.</summary>
    private async IAsyncEnumerable<StateEvent> OneConnectionAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = settings.Current.HomeAssistant;

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            Certificates.Accept(certificate, errors, options.CertificateFingerprint, options.AcceptAnyCertificate);

        // The same close-the-socket-on-silence timer guards the handshake, so a connection that opens and
        // then says nothing cannot wedge the feed before it has started.
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!await TryOpenAsync(socket, options, quiet, cancellationToken).ConfigureAwait(false)) yield break;

        while (!cancellationToken.IsCancellationRequested)
        {
            string message;
            try
            {
                quiet.CancelAfter(SilenceBeforeGivingUp);
                message = await HaSocket.ReceiveAsync(socket, quiet.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "The live state feed dropped.");
                yield break;
            }

            foreach (var change in Read(message)) yield return change;
        }
    }

    private async Task<bool> TryOpenAsync(
        ClientWebSocket socket,
        HomeAssistantOptions options,
        CancellationTokenSource quiet,
        CancellationToken cancellationToken)
    {
        try
        {
            quiet.CancelAfter(SilenceBeforeGivingUp);

            await HaSocket.ConnectAsync(
                socket,
                HaSocket.UriFor(options.BaseUrl),
                secrets.Resolve(SecretStore.HomeAssistantToken, options.TokenEnvironmentVariable),
                quiet.Token).ConfigureAwait(false);

            await HaSocket.SendAsync(
                socket,
                """{"id":1,"type":"subscribe_events","event_type":"state_changed"}""",
                quiet.Token).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not start the live state feed.");
            return false;
        }
    }

    /// <summary>
    /// The state changes in one message, or nothing at all.
    ///
    /// Filtered hard, because the value of this feed is entirely in the transitions and the cost of it is
    /// entirely in the volume. What is kept and what is dropped is <see cref="Keep"/>.
    /// </summary>
    internal static IReadOnlyList<StateEvent> Read(string message)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];

            if (!root.TryGetProperty("event", out var e) || e.ValueKind != JsonValueKind.Object) return [];
            if (!e.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return [];

            if (!data.TryGetProperty("entity_id", out var id) || id.ValueKind != JsonValueKind.String) return [];
            var entityId = id.GetString();
            if (string.IsNullOrWhiteSpace(entityId)) return [];

            if (!data.TryGetProperty("new_state", out var fresh) || fresh.ValueKind != JsonValueKind.Object) return [];

            var state = fresh.TryGetProperty("state", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

            if (!Keep(state)) return [];

            // last_changed moves only when the state string does, which is exactly the sample being stored.
            // Home Assistant also raises state_changed for an attribute moving under an unchanged state --
            // a light's brightness, a media player's track -- and those carry the previous last_changed.
            if (!fresh.TryGetProperty("last_changed", out var changed) || changed.ValueKind != JsonValueKind.String)
                return [];

            if (!DateTimeOffset.TryParse(
                    changed.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var changedUtc))
                return [];

            // The one case worth checking against the old state: an attribute-only change repeats the state
            // string, and storing it would be a duplicate row at a timestamp already held.
            if (data.TryGetProperty("old_state", out var previous) &&
                previous.ValueKind == JsonValueKind.Object &&
                previous.TryGetProperty("state", out var was) &&
                was.ValueKind == JsonValueKind.String &&
                string.Equals(was.GetString(), state, StringComparison.Ordinal))
                return [];

            return [new StateEvent(entityId, state, changedUtc)];
        }
    }

    /// <summary>
    /// Numbers are sampled; states are witnessed.
    ///
    /// This one line is what keeps the feed affordable. A numeric reading feeds the outlier detector, which
    /// wants a DISTRIBUTION — and is handed a deliberately thinned one, a couple of readings an hour across
    /// the whole window, because the full stream is noise it would only throw away again. Those are also
    /// every chatty entity in the house: a power meter reporting every five seconds is twelve rows a minute
    /// that nothing will ever read. Storing them live would multiply the sample table many times over, on
    /// whatever flash a Home Assistant box runs on, to no end.
    ///
    /// A non-numeric state is the opposite on both counts. It feeds the stuck-state detector, which measures
    /// stretches between one sample and the next and so needs every transition; and doors, locks, lights and
    /// motion sensors change a few dozen times a day, not a few thousand. Unavailable and unknown are kept
    /// for the same reason — an entity going quiet is a transition like any other.
    /// </summary>
    internal static bool Keep(string state) => !Ha.TryNumeric(state, out _);
}
