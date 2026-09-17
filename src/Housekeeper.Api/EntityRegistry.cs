using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Housekeeper.Api;

/// <summary>What the entity registry knows about one entity that its state does not say.</summary>
/// <param name="EntityCategory"><c>config</c>, <c>diagnostic</c>, or null for an ordinary entity.</param>
/// <param name="Hidden">The user has hidden it, which is as close to "do not tell me about this" as exists.</param>
public sealed record RegistryEntity(string EntityId, string? EntityCategory, bool Hidden);

/// <summary>
/// Reads Home Assistant's entity registry, which is the only place two useful facts live.
///
/// <c>entity_category</c> is how Home Assistant itself separates the things a house does from the knobs that
/// configure it — a plug's "Auto-off enabled" switch, a sensor's link quality — and hiding an entity is the
/// user saying outright that they do not want to hear about it. Neither is in <c>/api/states</c>, which is
/// why Housekeeper was reduced to guessing from entity ids, and why it reported a fridge plug's unticked
/// checkbox as though the fridge were misbehaving.
///
/// The registry is only served over the WebSocket API, so this is the one part of Housekeeper that speaks
/// it. Deliberately a single request-and-close rather than a subscription: the registry changes when someone
/// adds a device, the answer is cached for minutes, and a long-lived socket would be a reconnect state
/// machine earning nothing.
/// </summary>
public static class EntityRegistry
{
    /// <inheritdoc cref="HaSocket.UriFor"/>
    public static Uri WebSocketUri(string? baseUrl) => HaSocket.UriFor(baseUrl);

    /// <summary>
    /// The registry entries out of a <c>config/entity_registry/list</c> result.
    ///
    /// Separated from the transport because it is the part with rules in it, and because a WebSocket server
    /// is a poor thing to need in a unit test. Anything unrecognised is skipped rather than refused: a
    /// registry that gains a field must not stop Housekeeper reading the fields it already understood.
    /// </summary>
    public static IReadOnlyDictionary<string, RegistryEntity> Parse(string json)
    {
        Dictionary<string, RegistryEntity> registry = new(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return registry;

        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
            return registry;

        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("entity_id", out var id) || id.ValueKind != JsonValueKind.String) continue;

            var entityId = id.GetString();
            if (string.IsNullOrWhiteSpace(entityId)) continue;

            // hidden_by names who hid it -- "user", "integration" -- and is null when nobody did. Any
            // non-null value means hidden; which of them it was is not something Housekeeper acts on.
            var hidden = item.TryGetProperty("hidden_by", out var hiddenBy) &&
                         hiddenBy.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

            registry[entityId] = new RegistryEntity(entityId, Text(item, "entity_category"), hidden);
        }

        return registry;

        static string? Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
    }

    /// <summary>How much of a registry answer is read before giving up, so a bad peer cannot exhaust memory.</summary>
    private const int MostBytes = 32 * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Connects, authenticates, asks for the registry once, and closes.
    ///
    /// Throws on anything unexpected. Every caller treats this as best effort — the registry sharpens what
    /// Housekeeper stays quiet about and nothing depends on it — so a Home Assistant too old to answer, a
    /// token without the rights, or a proxy that does not pass WebSockets all degrade to the heuristics
    /// rather than failing a scan.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, RegistryEntity>> ReadAsync(
        ClientWebSocket socket,
        Uri uri,
        string? token,
        CancellationToken cancellationToken)
    {
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        // Home Assistant speaks first, with auth_required and its version.
        var greeting = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (TypeOf(greeting) == "auth_required")
        {
            await SendAsync(socket, $$"""{"type":"auth","access_token":{{JsonSerializer.Serialize(token ?? "")}}}""", cancellationToken)
                .ConfigureAwait(false);

            var authenticated = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (TypeOf(authenticated) != "auth_ok")
                throw new InvalidOperationException("Home Assistant rejected the token on the WebSocket API.");
        }

        await SendAsync(socket, """{"id":1,"type":"config/entity_registry/list"}""", cancellationToken)
            .ConfigureAwait(false);

        // Skip anything that is not the answer to the request just made. A fresh connection is not
        // subscribed to anything, so in practice the next frame is the result -- but the protocol permits
        // the server to speak first and matching on the id is what makes that harmless rather than fatal.
        for (var frame = 0; frame < 8; frame++)
        {
            var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(message);

            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            if (!document.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            if (id.GetInt32() != 1) continue;

            if (document.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("Home Assistant refused the entity registry request.");

            return Parse(message);
        }

        throw new InvalidOperationException("Home Assistant never answered the entity registry request.");
    }

    private static string? TypeOf(string message)
    {
        using var document = JsonDocument.Parse(message);

        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty("type", out var type) &&
               type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
    }

    private static Task SendAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>One whole message, however many frames it arrived in. A big house is a big answer.</summary>
    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkBytes];
        using var message = new MemoryStream();

        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Home Assistant closed the WebSocket before answering.");

            message.Write(buffer, 0, received.Count);

            if (message.Length > MostBytes)
                throw new InvalidOperationException("The entity registry answer was larger than Housekeeper will read.");

            if (received.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }
}
