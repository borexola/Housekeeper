using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// The bits of Home Assistant's WebSocket API that both users of it need: where it lives, how to say hello,
/// and how to read one whole message out of however many frames it arrived in.
///
/// Two things talk this protocol — the entity registry read, which connects and closes again, and the live
/// state feed, which stays. They agree on the address and the handshake and disagree about everything else,
/// so that much is here and the rest is theirs.
/// </summary>
public static class HaSocket
{
    /// <summary>
    /// The WebSocket endpoint for a configured base address.
    ///
    /// Home Assistant serves it at <c>/api/websocket</c>, beside the REST API. The Supervisor's Core proxy
    /// does not mirror that: it serves REST under <c>/core/api/</c> but the socket at <c>/core/websocket</c>,
    /// with no <c>api</c> segment. An add-on left to Supervisor credentials — which is the default, and so
    /// how most installations run — gets its base URL set to <c>http://supervisor/core</c>, and asking that
    /// for <c>/api/websocket</c> reaches nothing at all.
    /// </summary>
    public static Uri UriFor(string? baseUrl)
    {
        var viaSupervisor = (baseUrl ?? "").Trim().TrimEnd('/')
            .EndsWith("/core", StringComparison.OrdinalIgnoreCase);

        var http = Urls.Under(baseUrl, viaSupervisor ? "websocket" : "api/websocket");

        return new UriBuilder(http)
        {
            Scheme = http.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;
    }

    /// <summary>How much of one message is read before giving up, so a bad peer cannot exhaust memory.</summary>
    private const int MostBytes = 32 * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    /// <summary>Connects and completes the auth handshake, leaving the socket ready for requests.</summary>
    public static async Task ConnectAsync(
        ClientWebSocket socket,
        Uri uri,
        string? token,
        CancellationToken cancellationToken)
    {
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        // Home Assistant speaks first, with auth_required and its version.
        var greeting = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (TypeOf(greeting) != "auth_required") return;

        await SendAsync(socket, $$"""{"type":"auth","access_token":{{JsonSerializer.Serialize(token ?? "")}}}""", cancellationToken)
            .ConfigureAwait(false);

        var authenticated = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (TypeOf(authenticated) != "auth_ok")
            throw new HomeAssistantException(
                "Home Assistant rejected the token on the WebSocket API. Reading the entity registry and "
                + "watching live state changes both need a token belonging to a real user.",
                refused: true);
    }

    public static Task SendAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>One whole message, however many frames it arrived in. A big house is a big answer.</summary>
    public static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkBytes];
        using var message = new MemoryStream();

        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close)
                throw new HomeAssistantException("Home Assistant closed the WebSocket.");

            message.Write(buffer, 0, received.Count);

            if (message.Length > MostBytes)
                throw new HomeAssistantException("A WebSocket message was larger than Housekeeper will read.");

            if (received.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    public static string? TypeOf(string message)
    {
        using var document = JsonDocument.Parse(message);

        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty("type", out var type) &&
               type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
    }
}
