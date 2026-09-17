using System.Net;
using System.Text;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Housekeeper.Tests;

/// <summary>
/// The Home Assistant client against a scripted HTTP handler: what it asks for, how often, and what it makes
/// of the answers. The caching is the part worth pinning down, because a wrong cache is invisible until a
/// user wonders why the duplicate warning is stale.
/// </summary>
public class HomeAssistantClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>Every request as "METHOD /path", in order.</summary>
        public List<string> Requests { get; } = [];

        public Func<HttpRequestMessage, Task<string>> Body { get; set; } = _ => Task.FromResult("{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(await Body(request), Encoding.UTF8, "application/json"),
            };
        }

        public int Count(string prefix) => Requests.Count(r => r.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static (HomeAssistantClient Client, StubHandler Handler, FakeTimeProvider Clock) Make(
        string baseUrl = "http://ha.test:8123",
        bool resolveAreas = false)
    {
        var handler = new StubHandler();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        var settings = new FakeSettings();
        settings.Current.HomeAssistant.BaseUrl = baseUrl;
        settings.Current.HomeAssistant.ResolveAreas = resolveAreas;

        var secrets = new SecretStore(
            Path.Combine(Path.GetTempPath(), $"hs-client-test-{Guid.NewGuid():N}.json"),
            NullLogger<SecretStore>.Instance);

        var client = new HomeAssistantClient(new HttpClient(handler), settings, secrets, clock, NullLogger<HomeAssistantClient>.Instance);
        return (client, handler, clock);
    }

    private const string OneAutomation =
        """{"alias":"Away lights","trigger":[{"platform":"state","entity_id":"person.sam"}],"action":[{"service":"light.turn_off","entity_id":"light.hall"}]}""";

    [Fact]
    public async Task Reads_the_automation_config_id_off_the_entity()
    {
        var (client, handler, _) = Make();
        handler.Body = _ => Task.FromResult("""
            [
              {"entity_id":"automation.away","state":"on","last_changed":"2026-03-01T10:00:00+00:00","last_updated":"2026-03-01T10:00:00+00:00",
               "attributes":{"friendly_name":"Away","id":"1699"}},
              {"entity_id":"automation.numeric_id","state":"on","last_changed":"2026-03-01T10:00:00+00:00","last_updated":"2026-03-01T10:00:00+00:00",
               "attributes":{"id":1700}},
              {"entity_id":"light.hall","state":"off","last_changed":"2026-03-01T10:00:00+00:00","last_updated":"2026-03-01T10:00:00+00:00",
               "attributes":{"friendly_name":"Hall"}}
            ]
            """);

        var entities = await client.GetEntitiesAsync(CancellationToken.None);

        Assert.Equal("1699", entities.Single(e => e.EntityId == "automation.away").AutomationConfigId);
        Assert.Equal("1700", entities.Single(e => e.EntityId == "automation.numeric_id").AutomationConfigId);
        Assert.Null(entities.Single(e => e.EntityId == "light.hall").AutomationConfigId);
    }

    [Fact]
    public async Task The_registry_lookup_fills_area_device_and_device_name()
    {
        var (client, handler, _) = Make(resolveAreas: true);
        handler.Body = request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/template", StringComparison.Ordinal)
            ? """[{"e":"binary_sensor.garage_motion","a":"Garage","d":"dev1","n":"Garage Cam"},{"e":"light.hall","a":"","d":"","n":""}]"""
            : """
              [
                {"entity_id":"binary_sensor.garage_motion","state":"off","last_changed":"2026-03-01T10:00:00+00:00","last_updated":"2026-03-01T10:00:00+00:00","attributes":{}},
                {"entity_id":"light.hall","state":"off","last_changed":"2026-03-01T10:00:00+00:00","last_updated":"2026-03-01T10:00:00+00:00","attributes":{}}
              ]
              """);

        var entities = await client.GetEntitiesAsync(CancellationToken.None);

        var motion = entities.Single(e => e.EntityId == "binary_sensor.garage_motion");
        Assert.Equal("Garage", motion.Area);
        Assert.Equal("dev1", motion.DeviceId);
        Assert.Equal("Garage Cam", motion.DeviceName);

        var hall = entities.Single(e => e.EntityId == "light.hall");
        Assert.Null(hall.Area);
        Assert.Null(hall.DeviceId);
        Assert.Null(hall.DeviceName);
        Assert.Equal(["GET /api/states", "POST /api/template"], handler.Requests);
    }

    [Fact]
    public async Task An_address_typed_with_its_api_segment_is_not_doubled()
    {
        var (client, handler, _) = Make("http://ha.test:8123/api");
        handler.Body = _ => Task.FromResult("[]");

        await client.GetServicesAsync(CancellationToken.None);

        Assert.Equal(["GET /api/services"], handler.Requests);
    }

    [Fact]
    public async Task The_service_list_is_read_once_and_then_trusted_for_a_while()
    {
        var (client, handler, clock) = Make();
        handler.Body = _ => Task.FromResult("""[{"domain":"light","services":{"turn_on":{},"turn_off":{}}}]""");

        var first = await client.GetServicesAsync(CancellationToken.None);
        var second = await client.GetServicesAsync(CancellationToken.None);

        Assert.Contains("light.turn_on", first);
        Assert.Same(first, second);
        Assert.Equal(1, handler.Count("GET /api/services"));

        clock.Advance(TimeSpan.FromMinutes(6));
        await client.GetServicesAsync(CancellationToken.None);

        Assert.Equal(2, handler.Count("GET /api/services"));
    }

    [Fact]
    public async Task Automation_configs_are_read_from_the_entities_given_not_from_a_second_state_fetch()
    {
        var (client, handler, _) = Make();
        handler.Body = _ => Task.FromResult(OneAutomation);

        var away = Build.Entity("automation.away", "on", friendlyName: "Away", automationConfigId: "1699");

        var automations = await client.GetAutomationsAsync([away], CancellationToken.None);

        Assert.Single(automations);
        Assert.Equal("Away lights", automations[0].Alias);
        Assert.Contains("light.hall", automations[0].Entities);
        Assert.Equal(0, handler.Count("GET /api/states"));
        Assert.Equal(1, handler.Count("GET /api/config/automation/config/"));
    }

    [Fact]
    public async Task Automation_configs_are_cached_by_the_set_of_ids_and_refetched_when_it_changes()
    {
        var (client, handler, clock) = Make();
        handler.Body = _ => Task.FromResult(OneAutomation);

        var away = Build.Entity("automation.away", "on", automationConfigId: "1699");
        var night = Build.Entity("automation.night", "on", automationConfigId: "1700");

        await client.GetAutomationsAsync([away], CancellationToken.None);
        await client.GetAutomationsAsync([away], CancellationToken.None);
        Assert.Equal(1, handler.Count("GET /api/config/"));

        // A new automation in the house: noticed at once, both are re-read.
        await client.GetAutomationsAsync([away, night], CancellationToken.None);
        Assert.Equal(3, handler.Count("GET /api/config/"));

        // The same set, still fresh: nothing more is read.
        await client.GetAutomationsAsync([night, away], CancellationToken.None);
        Assert.Equal(3, handler.Count("GET /api/config/"));

        // Aged out: read again.
        clock.Advance(TimeSpan.FromMinutes(6));
        await client.GetAutomationsAsync([away, night], CancellationToken.None);
        Assert.Equal(5, handler.Count("GET /api/config/"));
    }

    [Fact]
    public async Task Creating_an_automation_discards_what_the_cache_thought_existed()
    {
        var (client, handler, _) = Make();
        handler.Body = _ => Task.FromResult(OneAutomation);

        var away = Build.Entity("automation.away", "on", automationConfigId: "1699");

        await client.GetAutomationsAsync([away], CancellationToken.None);
        Assert.Equal(1, handler.Count("GET /api/config/"));

        await client.CreateAutomationAsync("1701", "{}", CancellationToken.None);
        Assert.Equal(1, handler.Count("POST /api/config/automation/config/1701"));

        await client.GetAutomationsAsync([away], CancellationToken.None);
        Assert.Equal(2, handler.Count("GET /api/config/"));
    }

    [Fact]
    public async Task Two_drafts_arriving_together_share_one_read()
    {
        var (client, handler, _) = Make();
        var gate = new TaskCompletionSource();
        handler.Body = async _ =>
        {
            await gate.Task;
            return """[{"domain":"light","services":{"turn_on":{}}}]""";
        };

        var one = client.GetServicesAsync(CancellationToken.None);
        var two = client.GetServicesAsync(CancellationToken.None);
        gate.SetResult();

        await Task.WhenAll(one, two);

        Assert.Equal(1, handler.Count("GET /api/services"));
    }
}
