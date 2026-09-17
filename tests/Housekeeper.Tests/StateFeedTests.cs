using Housekeeper.Api;
using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// The live feed exists for one thing: the transitions that fall between two polls. A scan reads every
/// entity once an interval and stores what changed, so it can never record more than one sample per entity
/// per interval — a door opened and shut inside that window leaves two stored samples carrying the same
/// state, which the stuck-state detector has to discard rather than count as a stretch it never saw.
///
/// The cost of the feed is volume, and the filter is what keeps it affordable, so most of what is tested
/// here is what gets dropped.
/// </summary>
public class StateFeedTests
{
    /// <summary>One state_changed frame, shaped the way Home Assistant sends it.</summary>
    private static string Event(string entityId, string state, string? was = "off", string? changed = null)
    {
        var at = changed ?? "2026-03-08T21:14:05.123456+00:00";
        var old = was is null ? "null" : "{\"entity_id\":\"" + entityId + "\",\"state\":\"" + was + "\"}";

        return "{\"id\":1,\"type\":\"event\",\"event\":{\"event_type\":\"state_changed\",\"data\":{"
             + "\"entity_id\":\"" + entityId + "\","
             + "\"old_state\":" + old + ","
             + "\"new_state\":{\"entity_id\":\"" + entityId + "\",\"state\":\"" + state + "\","
             + "\"last_changed\":\"" + at + "\"}"
             + "}}}";
    }

    [Fact]
    public void A_state_change_becomes_one_sample()
    {
        var changes = HomeAssistantEventStream.Read(Event("binary_sensor.freezer_door", "on"));

        var change = Assert.Single(changes);
        Assert.Equal("binary_sensor.freezer_door", change.EntityId);
        Assert.Equal("on", change.State);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 21, 14, 5, 123, TimeSpan.Zero), change.ChangedUtc, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// The line that makes the feed affordable. A power meter reporting every five seconds is twelve rows a
    /// minute that nothing will ever read — the outlier detector is handed a deliberately thinned baseline,
    /// a couple of readings an hour, because the full stream is noise it throws away again. Storing those
    /// live would multiply the sample table many times over for no gain at all.
    /// </summary>
    [Theory]
    [InlineData("13.31", false)]
    [InlineData("0", false)]
    [InlineData("-18.5", false)]
    [InlineData("6.42e3", false)]
    // Everything a stretch can be measured between is kept.
    [InlineData("on", true)]
    [InlineData("off", true)]
    [InlineData("open", true)]
    [InlineData("docked", true)]
    [InlineData("unavailable", true)]
    [InlineData("unknown", true)]
    public void Numbers_are_sampled_and_states_are_witnessed(string state, bool kept)
    {
        Assert.Equal(kept, HomeAssistantEventStream.Keep(state));
        Assert.Equal(kept, HomeAssistantEventStream.Read(Event("sensor.thing", state, was: "previous")).Count > 0);
    }

    /// <summary>
    /// Home Assistant raises state_changed when an attribute moves under an unchanged state — a light's
    /// brightness, a media player's track. Those carry the previous last_changed, so storing one would be a
    /// duplicate row at a timestamp already held, and on a dimmer being swept it would be hundreds of them.
    /// </summary>
    [Fact]
    public void An_attribute_moving_under_an_unchanged_state_is_not_a_transition()
    {
        Assert.Empty(HomeAssistantEventStream.Read(Event("light.hall", "on", was: "on")));

        // The same entity genuinely changing is still a transition.
        Assert.Single(HomeAssistantEventStream.Read(Event("light.hall", "on", was: "off")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"type":"result","success":true,"result":null}""")]
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{}}}""")]
    // A removed entity has no new_state, and nothing can be recorded about where it went.
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"light.x","new_state":null}}}""")]
    // Without last_changed there is no timestamp to store the sample at.
    [InlineData("""{"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"light.x","new_state":{"state":"on"}}}}""")]
    public void Anything_it_cannot_use_is_dropped_rather_than_thrown(string message) =>
        Assert.Empty(HomeAssistantEventStream.Read(message));

    /// <summary>
    /// The feed and the scan write into the same table, and the sample key is (entity, changed) — so the
    /// same transition arriving from both is one row, and neither writer has to know about the other.
    /// </summary>
    [Fact]
    public async Task The_feed_and_the_scan_can_both_record_the_same_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"housekeeper-feed-{Guid.NewGuid():N}.db");
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            SqliteStore.ConnectionStringFor(path))
        { Pooling = false }.ToString();

        try
        {
            var store = new SqliteStore(connection);
            await store.InitialiseAsync(CancellationToken.None);

            var at = new DateTimeOffset(2026, 3, 8, 21, 14, 5, TimeSpan.Zero);
            List<(string, StateSample)> same = [("binary_sensor.freezer_door", new StateSample("on", null, at))];

            Assert.Equal(1, await store.AddSamplesAsync(same, CancellationToken.None));
            Assert.Equal(0, await store.AddSamplesAsync(same, CancellationToken.None));

            var history = await store.GetSamplesAsync(at.AddDays(-1), 100, CancellationToken.None);
            Assert.Single(history["binary_sensor.freezer_door"]);
        }
        finally
        {
            foreach (var leftover in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(leftover);
        }
    }

    /// <summary>
    /// What the feed is for, end to end: a door that opens and shuts faster than the scan interval. Polled,
    /// the two stored samples both read "closed" and the stretch between them is discarded as unobserved.
    /// Witnessed, the open stretch is on record and counts towards what normal looks like.
    /// </summary>
    [Fact]
    public void A_stretch_shorter_than_the_scan_interval_is_only_visible_when_witnessed()
    {
        var now = new DateTimeOffset(2026, 3, 8, 21, 0, 0, TimeSpan.Zero);
        var entity = Build.Entity("binary_sensor.freezer_door", "on", now.AddMinutes(-20), deviceClass: "door");

        // Polled once a minute: the door opened at :10 and shut at :40, so both polls saw "off".
        List<StateSample> polled = [];
        List<StateSample> witnessed = [];

        for (var i = 12; i > 0; i--)
        {
            var minute = now.AddHours(-i * 3);
            polled.Add(new StateSample("off", null, minute));
            polled.Add(new StateSample("off", null, minute.AddMinutes(1)));

            witnessed.Add(new StateSample("on", null, minute.AddSeconds(10)));
            witnessed.Add(new StateSample("off", null, minute.AddSeconds(40)));
        }

        // The polled history contains no completed "on" stretch at all, so there is nothing to judge
        // against and the detector cannot speak about a door held open for twenty minutes.
        Assert.Null(AnomalyDetection.DetectStuckState(entity, polled, Options(), now));

        // The witnessed history knows this door is normally open for thirty seconds.
        var anomaly = AnomalyDetection.DetectStuckState(entity, witnessed, Options(), now);

        Assert.NotNull(anomaly);
        Assert.Contains("30 seconds", anomaly.Summary);

        static ScanOptions Options() => new();
    }

    [Theory]
    [InlineData("http://homeassistant.local:8123", "ws://homeassistant.local:8123/api/websocket")]
    [InlineData("http://supervisor/core", "ws://supervisor/core/websocket")]
    [InlineData("https://ha.example.com", "wss://ha.example.com/api/websocket")]
    public void The_feed_and_the_registry_agree_on_the_address(string baseUrl, string expected)
    {
        Assert.Equal(expected, HaSocket.UriFor(baseUrl).ToString());
        Assert.Equal(expected, EntityRegistry.WebSocketUri(baseUrl).ToString());
    }
}
