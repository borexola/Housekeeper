using Housekeeper.Api;
using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// The entity registry is the authoritative answer to "is this a thing the house does, or a knob that
/// configures it?" — and it is the one thing Housekeeper reads over the WebSocket API, so the parsing is
/// kept separate from the transport and tested on its own.
/// </summary>
public class EntityRegistryTests
{
    private const string Answer = """
        {
          "id": 1,
          "type": "result",
          "success": true,
          "result": [
            { "entity_id": "sensor.kitchen_temperature", "entity_category": null, "hidden_by": null },
            { "entity_id": "switch.fridge_plug_auto_off_enabled", "entity_category": "config", "hidden_by": null },
            { "entity_id": "sensor.presence_linkquality", "entity_category": "diagnostic", "hidden_by": null },
            { "entity_id": "sensor.noisy_thing", "entity_category": null, "hidden_by": "user" },
            { "entity_id": "sensor.integration_hid_this", "hidden_by": "integration" }
          ]
        }
        """;

    [Fact]
    public void Reads_categories_and_hiding_out_of_a_registry_answer()
    {
        var registry = EntityRegistry.Parse(Answer);

        Assert.Equal(5, registry.Count);
        Assert.Null(registry["sensor.kitchen_temperature"].EntityCategory);
        Assert.False(registry["sensor.kitchen_temperature"].Hidden);

        Assert.Equal("config", registry["switch.fridge_plug_auto_off_enabled"].EntityCategory);
        Assert.Equal("diagnostic", registry["sensor.presence_linkquality"].EntityCategory);

        // hidden_by names who hid it; any non-null value means hidden.
        Assert.True(registry["sensor.noisy_thing"].Hidden);
        Assert.True(registry["sensor.integration_hid_this"].Hidden);
    }

    /// <summary>A registry that gains a field must not stop Housekeeper reading the fields it understood.</summary>
    [Theory]
    [InlineData("""{"id":1,"type":"result","success":true,"result":[]}""")]
    [InlineData("""{"id":1,"type":"result","success":true,"result":[{"no_entity_id":true}]}""")]
    [InlineData("""{"id":1,"type":"result","success":true,"result":"not an array"}""")]
    [InlineData("""{"id":1,"type":"result","success":true}""")]
    [InlineData("[]")]
    public void An_answer_it_cannot_use_yields_nothing_rather_than_throwing(string json) =>
        Assert.Empty(EntityRegistry.Parse(json));

    [Theory]
    [InlineData("http://homeassistant.local:8123", "ws://homeassistant.local:8123/api/websocket")]
    [InlineData("https://ha.example.com", "wss://ha.example.com/api/websocket")]
    [InlineData("http://10.1.0.4:8123/", "ws://10.1.0.4:8123/api/websocket")]
    // A base someone pasted with /api already on it must not become /api/api/websocket.
    [InlineData("http://homeassistant.local:8123/api", "ws://homeassistant.local:8123/api/websocket")]
    public void The_websocket_address_follows_the_configured_one(string baseUrl, string expected) =>
        Assert.Equal(expected, EntityRegistry.WebSocketUri(baseUrl).ToString());

    /// <summary>
    /// The add-on's default path, and the one that matters most because it is how most installations run.
    ///
    /// Left to Supervisor credentials, run.sh points the app at http://supervisor/core — and the Supervisor
    /// does not proxy the socket where it proxies REST. REST is /core/api/..., the socket is /core/websocket
    /// with no "api" in it, so the obvious address reaches nothing at all. The registry read is best effort,
    /// so getting this wrong costs no error anywhere: entity categories would simply never arrive.
    /// </summary>
    [Theory]
    [InlineData("http://supervisor/core", "ws://supervisor/core/websocket")]
    [InlineData("http://supervisor/core/", "ws://supervisor/core/websocket")]
    [InlineData("http://supervisor/CORE", "ws://supervisor/CORE/websocket")]
    public void Through_the_supervisor_proxy_the_socket_is_not_under_api(string baseUrl, string expected) =>
        Assert.Equal(expected, EntityRegistry.WebSocketUri(baseUrl).ToString());

    /// <summary>
    /// What the registry buys: Home Assistant's own word, rather than Housekeeper guessing from the name.
    /// A config entity called something the naming rules would never have caught is still recognised.
    /// </summary>
    [Fact]
    public void Home_assistants_own_classification_is_believed()
    {
        var setting = Build.Entity("switch.living_room_wotsit", "off", deviceClass: null, entityCategory: "config");
        var diagnostic = Build.Entity("sensor.living_room_wotsit_2", "12", entityCategory: "diagnostic");

        Assert.NotNull(Baselines.DiagnosticReason(setting));
        Assert.NotNull(Baselines.DiagnosticReason(diagnostic));

        // Neither name matches anything in the heuristics, so the registry is doing all the work here.
        var unclassified = setting with { EntityCategory = null };
        Assert.Null(Baselines.DiagnosticReason(unclassified));
    }

    /// <summary>Hiding an entity in Home Assistant is as close to "stop telling me about this" as exists.</summary>
    [Fact]
    public void A_hidden_entity_is_never_a_finding()
    {
        var hidden = Build.Entity("sensor.kitchen_temperature", "21.5", hidden: true);

        Assert.NotNull(Baselines.DiagnosticReason(hidden));
        Assert.Null(Baselines.DiagnosticReason(hidden with { Hidden = false }));
    }

    /// <summary>
    /// Without the registry — an older Home Assistant, a proxy that will not pass WebSockets, or the setting
    /// turned off — the naming rules still have to catch the common cases on their own.
    /// </summary>
    [Fact]
    public void The_heuristics_still_answer_when_the_registry_is_absent()
    {
        foreach (var entityId in new[]
        {
            "switch.fridge_plug_auto_off_enabled",
            "sensor.0x54ef44100129b748_linkquality",
            "sensor.pi_uptime",
        })
            Assert.NotNull(Baselines.DiagnosticReason(Build.Entity(entityId, "1")));
    }
}
