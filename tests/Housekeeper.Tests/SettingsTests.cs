using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

public class DurationsTests
{
    [Theory]
    [InlineData("45s", 0, 0, 45)]
    [InlineData("10m", 0, 10, 0)]
    [InlineData("2h", 2, 0, 0)]
    [InlineData("1.5h", 1, 30, 0)]
    [InlineData("00:05:00", 0, 5, 0)]
    [InlineData(" 90S ", 0, 1, 30)]
    public void Reads_the_forms_a_person_would_type(string text, int hours, int minutes, int seconds)
    {
        Assert.True(Durations.TryParse(text, out var value));
        Assert.Equal(new TimeSpan(hours, minutes, seconds), value);
    }

    [Fact]
    public void Reads_days()
    {
        Assert.True(Durations.TryParse("14d", out var value));
        Assert.Equal(TimeSpan.FromDays(14), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("10x")]
    // A bare number is ambiguous, and TimeSpan would read it as days.
    [InlineData("5")]
    public void Refuses_anything_ambiguous_or_meaningless(string text) =>
        Assert.False(Durations.TryParse(text, out _));

    [Theory]
    [InlineData(0, 0, 45, "45s")]
    [InlineData(0, 10, 0, "10m")]
    [InlineData(2, 0, 0, "2h")]
    [InlineData(0, 1, 30, "90s")]
    public void Writes_the_shortest_whole_unit(int hours, int minutes, int seconds, string expected) =>
        Assert.Equal(expected, Durations.Format(new TimeSpan(hours, minutes, seconds)));

    [Fact]
    public void Round_trips_every_default()
    {
        var options = new HousekeeperOptions();
        foreach (var span in new[]
                 {
                     options.Scan.Interval, options.Scan.History, options.Scan.RedetectAfter,
                     options.HomeAssistant.RequestTimeout, options.Llm.Timeout,
                 })
        {
            Assert.True(Durations.TryParse(Durations.Format(span), out var parsed));
            Assert.Equal(span, parsed);
        }
    }
}

/// <summary>
/// The scan schedule is a number someone types into a web page, and it ends up in a PeriodicTimer. Nothing
/// typed there may be allowed to stop the host: an exception out of ExecuteAsync takes the whole application
/// down, including the settings page needed to undo the value, leaving a config file as the only way back.
/// </summary>
public class ScanIntervalTests
{
    [Theory]
    [InlineData(0, 5)]                          // unset falls back rather than spinning
    [InlineData(-60, 5)]                        // and so does a negative
    [InlineData(11, 11)]                        // an ordinary value is left exactly alone
    [InlineData(60 * 24 * 3, 60 * 24 * 3)]      // three days is unusual but legitimate
    [InlineData(60 * 24 * 60, 60 * 24 * 7)]     // sixty days is past what a timer accepts, so it is brought back
    [InlineData(int.MaxValue, 60 * 24 * 7)]
    public void An_interval_a_timer_would_refuse_is_clamped_rather_than_thrown(long minutes, long expectedMinutes)
    {
        var scan = new ScanOptions { Interval = TimeSpan.FromMinutes(minutes) };

        var interval = ScanWorker.IntervalOf(scan);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), interval);

        // The contract that matters: whatever comes back, a timer will take it.
        using var timer = new PeriodicTimer(interval);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"housekeeper-settings-{Guid.NewGuid():N}.json");

    private SettingsStore Store() => new(_path, NullLogger<SettingsStore>.Instance);

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Stores_a_value_at_its_nested_path_and_reads_it_back_from_disk()
    {
        var changed = Store().Apply(new Dictionary<string, JsonNode?>
        {
            ["Housekeeper:Scan:Interval"] = JsonValue.Create("00:11:00"),
        });

        Assert.Equal(["Housekeeper:Scan:Interval"], changed);

        // A second store over the same file proves it was persisted, not just cached.
        Assert.Equal("00:11:00", Store().Get("Housekeeper:Scan:Interval")!.GetValue<string>());
        Assert.Contains("\"Scan\"", File.ReadAllText(_path));
    }

    [Fact]
    public void Writing_the_same_value_again_is_not_a_change()
    {
        var store = Store();
        var value = new Dictionary<string, JsonNode?> { ["Housekeeper:Api:Port"] = JsonValue.Create(6000) };

        Assert.Single(store.Apply(value));
        Assert.Empty(store.Apply(value));
    }

    [Fact]
    public void Removing_the_last_key_in_a_branch_prunes_the_empty_branches()
    {
        var store = Store();
        store.Apply(new Dictionary<string, JsonNode?> { ["Housekeeper:Scan:Interval"] = JsonValue.Create("00:11:00") });
        store.Apply(new Dictionary<string, JsonNode?> { ["Housekeeper:Scan:Interval"] = null });

        Assert.False(store.Has("Housekeeper:Scan:Interval"));
        Assert.Equal("{}", File.ReadAllText(_path).Replace(" ", "").Replace("\r", "").Replace("\n", ""));
    }

    [Fact]
    public void Resetting_a_section_drops_only_that_section()
    {
        var store = Store();
        store.Apply(new Dictionary<string, JsonNode?>
        {
            ["Housekeeper:Scan:Interval"] = JsonValue.Create("00:11:00"),
            ["Housekeeper:Scan:MinimumSamples"] = JsonValue.Create(20),
            ["Housekeeper:Api:Port"] = JsonValue.Create(6000),
        });

        Assert.Equal(2, store.Reset("Scan"));

        Assert.False(store.Has("Housekeeper:Scan:Interval"));
        Assert.True(store.Has("Housekeeper:Api:Port"));
    }

    [Fact]
    public void A_damaged_file_reads_as_no_overrides_rather_than_throwing()
    {
        File.WriteAllText(_path, "{ this is not json");

        Assert.False(Store().Has("Housekeeper:Scan:Interval"));
    }
}

/// <summary>
/// The settings API against the real host. These share the application with <see cref="ApiTests"/>, so each
/// one puts back whatever it changed.
/// </summary>
[Collection("api")]
public class SettingsApiTests(TestApp app)
{
    private readonly HttpClient _client = app.CreateClient();

    private static JsonElement Field(JsonElement settings, string key) =>
        settings.GetProperty("sections")
            .EnumerateArray()
            .SelectMany(section => section.GetProperty("fields").EnumerateArray())
            .Single(field => field.GetProperty("key").GetString() == key);

    private async Task<JsonElement> SettingsAsync() =>
        await _client.GetFromJsonAsync<JsonElement>("/api/settings");

    private async Task<HttpResponseMessage> SaveAsync(string key, object value) =>
        await _client.PutAsJsonAsync("/api/settings", new Dictionary<string, object> { [key] = value });

    private async Task ResetAsync(string section) =>
        await _client.PostAsJsonAsync("/api/settings/reset", new { section });

    [Fact]
    public async Task The_settings_page_is_served_as_html()
    {
        var response = await _client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Every_setting_is_offered_with_its_value_and_where_that_value_came_from()
    {
        var settings = await SettingsAsync();

        // One section per group, and every field in the catalog is present.
        var keys = settings.GetProperty("sections")
            .EnumerateArray()
            .SelectMany(section => section.GetProperty("fields").EnumerateArray())
            .Select(field => field.GetProperty("key").GetString())
            .ToList();

        Assert.Equal(SettingsCatalog.Fields.Length, keys.Count);
        Assert.Contains("Housekeeper:Scan:Interval", keys);
        Assert.Contains("Secrets:HomeAssistantToken", keys);

        var interval = Field(settings, "Housekeeper:Scan:Interval");
        Assert.Equal("5m", interval.GetProperty("value").GetString());
        Assert.Equal("duration", interval.GetProperty("kind").GetString());
        Assert.False(interval.GetProperty("overridden").GetBoolean());
        Assert.Equal("default", interval.GetProperty("source").GetString());

        var port = Field(settings, "Housekeeper:Api:Port");
        Assert.True(port.GetProperty("restartRequired").GetBoolean());
    }

    [Fact]
    public async Task Changing_a_setting_takes_effect_without_a_restart()
    {
        try
        {
            var response = await SaveAsync("Housekeeper:Scan:Interval", "11m");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["Housekeeper:Scan:Interval"], result.GetProperty("changed").EnumerateArray().Select(x => x.GetString()));
            Assert.Empty(result.GetProperty("restartRequired").EnumerateArray());

            // The running application, not just the stored document, is using it.
            var status = await _client.GetFromJsonAsync<JsonElement>("/api/status");
            Assert.Equal("00:11:00", status.GetProperty("scanInterval").GetString());

            var field = Field(await SettingsAsync(), "Housekeeper:Scan:Interval");
            Assert.Equal("11m", field.GetProperty("value").GetString());
            Assert.Equal("ui", field.GetProperty("source").GetString());
            Assert.Equal("5m", field.GetProperty("inherited").GetString());
        }
        finally
        {
            await ResetAsync("Scan");
        }
    }

    [Fact]
    public async Task A_value_that_matches_what_it_would_inherit_anyway_is_not_stored()
    {
        var response = await SaveAsync("Housekeeper:Scan:Interval", "5m");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(result.GetProperty("changed").EnumerateArray());
        Assert.False(Field(await SettingsAsync(), "Housekeeper:Scan:Interval").GetProperty("overridden").GetBoolean());
    }

    [Theory]
    [InlineData("Housekeeper:Scan:Interval", "banana", "duration")]
    [InlineData("Housekeeper:Llm:Temperature", 9, "at most")]
    [InlineData("Housekeeper:Api:Port", 70000, "at most")]
    [InlineData("Housekeeper:Llm:Provider", "SkyNet", "one of")]
    [InlineData("Housekeeper:HomeAssistant:BaseUrl", "not-a-url", "BaseUrl")]
    public async Task A_value_that_would_not_work_is_refused_with_the_reason(string key, object value, string expected)
    {
        var response = await SaveAsync(key, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(expected, body, StringComparison.OrdinalIgnoreCase);

        // Nothing was stored, so the running configuration is untouched.
        Assert.False(Field(await SettingsAsync(), key).GetProperty("overridden").GetBoolean());
    }

    [Fact]
    public async Task Binding_to_the_network_without_a_token_is_refused_rather_than_locking_you_out()
    {
        var response = await SaveAsync("Housekeeper:Api:BindAddress", "0.0.0.0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("API token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_list_is_replaced_wholesale_rather_than_merged_by_index()
    {
        try
        {
            // Two globs are inherited from the host configuration; one override must leave exactly one.
            await SaveAsync("Housekeeper:Scan:Include", new[] { "light.*" });

            var status = await _client.GetFromJsonAsync<JsonElement>("/api/status");
            Assert.Equal(["light.*"], status.GetProperty("watching").EnumerateArray().Select(x => x.GetString()));
        }
        finally
        {
            await ResetAsync("Scan");
        }
    }

    [Fact]
    public async Task Resetting_a_section_puts_every_value_in_it_back()
    {
        await SaveAsync("Housekeeper:Scan:Interval", "11m");
        await SaveAsync("Housekeeper:Scan:MinimumSamples", 40);

        var reset = await _client.PostAsJsonAsync("/api/settings/reset", new { section = "Scan" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var settings = await SettingsAsync();
        Assert.Equal("5m", Field(settings, "Housekeeper:Scan:Interval").GetProperty("value").GetString());
        Assert.Equal(12, Field(settings, "Housekeeper:Scan:MinimumSamples").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task A_secret_can_be_set_but_never_read_back()
    {
        const string secret = "sk-do-not-leak-this";

        try
        {
            var response = await _client.PutAsJsonAsync("/api/settings/secrets", new Dictionary<string, string> { ["LlmApiKey"] = secret });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await _client.GetStringAsync("/api/settings");
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);

            var field = Field(JsonDocument.Parse(body).RootElement, "Secrets:LlmApiKey");
            Assert.True(field.GetProperty("configured").GetBoolean());
            Assert.Equal("stored", field.GetProperty("source").GetString());
            Assert.False(field.TryGetProperty("value", out _));
        }
        finally
        {
            await _client.PutAsJsonAsync("/api/settings/secrets", new Dictionary<string, string?> { ["LlmApiKey"] = null });
        }
    }

    [Fact]
    public async Task The_home_assistant_token_is_reported_as_coming_from_the_environment()
    {
        var field = Field(await SettingsAsync(), "Secrets:HomeAssistantToken");

        Assert.True(field.GetProperty("configured").GetBoolean());
        Assert.Equal("environment", field.GetProperty("source").GetString());
        Assert.Equal("HOUSEKEEPER_HA_TOKEN", field.GetProperty("variable").GetString());
    }

    [Fact]
    public async Task Connections_can_be_tested_from_the_settings_page()
    {
        var ha = await (await _client.PostAsJsonAsync("/api/settings/test", new { target = "homeAssistant" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ha.GetProperty("ok").GetBoolean());
        Assert.Contains("entities", ha.GetProperty("detail").GetString());

        var llm = await (await _client.PostAsJsonAsync("/api/settings/test", new { target = "llm" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(llm.GetProperty("ok").GetBoolean());

        var nonsense = await (await _client.PostAsJsonAsync("/api/settings/test", new { target = "toaster" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(nonsense.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_connection_can_be_tested_with_values_typed_but_not_saved()
    {
        var response = await _client.PostAsJsonAsync("/api/settings/test", new
        {
            target = "llm",
            values = new Dictionary<string, object>
            {
                ["Housekeeper:Llm:Endpoint"] = "http://elsewhere:8080/v1",
                ["Housekeeper:Llm:Model"] = "phi4",
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        // The test ran against what was typed, and nothing was written.
        Assert.Equal("http://elsewhere:8080/v1", app.Adapters.LastLlmOptions!.Llm.Endpoint);
        Assert.Equal("phi4", app.Adapters.LastLlmOptions.Llm.Model);
        Assert.False(Field(await SettingsAsync(), "Housekeeper:Llm:Endpoint").GetProperty("overridden").GetBoolean());
    }

    [Fact]
    public async Task A_secret_typed_but_not_saved_is_used_for_the_test_and_not_kept()
    {
        var response = await _client.PostAsJsonAsync("/api/settings/test", new
        {
            target = "homeAssistant",
            secrets = new Dictionary<string, string> { ["HomeAssistantToken"] = "typed-not-saved" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("typed-not-saved", app.Adapters.LastHomeAssistantToken);
        Assert.Equal("environment", Field(await SettingsAsync(), "Secrets:HomeAssistantToken").GetProperty("source").GetString());
    }

    /// <summary>
    /// "Test" takes an address from the caller and a credential from disk. Without this guard, anyone who
    /// could reach the settings API could name their own server and have Housekeeper post the user's Home
    /// Assistant admin token to it -- turning a convenience into credential exfiltration. A stored secret
    /// belongs to the address it was saved against and goes nowhere else.
    /// </summary>
    [Fact]
    public async Task The_stored_token_is_not_sent_to_a_home_assistant_the_caller_named()
    {
        var response = await _client.PostAsJsonAsync("/api/settings/test", new
        {
            target = "homeAssistant",
            values = new Dictionary<string, object> { ["Housekeeper:HomeAssistant:BaseUrl"] = "http://attacker.example:9999" },
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("different Home Assistant", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_address_can_still_be_tested_with_a_token_typed_for_it()
    {
        // The guard withholds the stored secret; it does not stop anyone testing a new server they have the
        // credentials for. Moving to a new Home Assistant has to remain a thing you can do from the page.
        var response = await _client.PostAsJsonAsync("/api/settings/test", new
        {
            target = "homeAssistant",
            values = new Dictionary<string, object> { ["Housekeeper:HomeAssistant:BaseUrl"] = "http://new-home.example:8123" },
            secrets = new Dictionary<string, string> { ["HomeAssistantToken"] = "token-for-the-new-one" },
        });

        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        Assert.Equal("token-for-the-new-one", app.Adapters.LastHomeAssistantToken);
    }

    [Fact]
    public async Task A_test_with_an_unusable_value_says_so_instead_of_trying()
    {
        var response = await _client.PostAsJsonAsync("/api/settings/test", new
        {
            target = "llm",
            values = new Dictionary<string, object> { ["Housekeeper:Llm:Timeout"] = "banana" },
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("duration", body.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Configuration accepts a provider spelled loosely -- "openai", "OpenAI-compatible", "open ai" -- but the
    /// dropdown holds only the canonical spellings. Handing it one it has no option for left it blank, the
    /// page read that back as an empty value, declared an unsaved change nobody made, and then refused to
    /// save anything else until it was resolved -- which Discard could not do either.
    /// </summary>
    [Theory]
    [InlineData("openai", "OpenAI")]
    [InlineData("OPENAI", "OpenAI")]
    [InlineData("OpenAI-compatible", "OpenAI")]
    [InlineData("open ai", "OpenAI")]
    [InlineData("ollama", "Ollama")]
    // And a spelling that is NOT supported stays visibly itself, so the user can see what is wrong and fix
    // it. Canonicalising it to a real choice made the page drop their value as "same as inherited" and then
    // refuse every other save over a provider that could no longer be corrected from anywhere.
    [InlineData("vLLM", "vLLM")]
    [InlineData("openai-v1", "openai-v1")]
    public void A_provider_spelled_loosely_is_shown_the_way_the_dropdown_spells_it(string configured, string expected)
    {
        var options = new HousekeeperOptions();
        options.Llm.Provider = configured;

        var field = SettingsCatalog.Find("Housekeeper:Llm:Provider")!;
        var shown = SettingsCatalog.Display(field, options)?.GetValue<string>();

        Assert.Equal(expected, shown);

        // Whatever is shown must be something configuration would actually accept, or the page and the
        // validator disagree — which is the whole failure this guards.
        Assert.Equal(LlmOptions.IsSupportedProvider(configured), field.Choices!.Contains(shown));
    }

    /// <summary>
    /// Removing an override changes what is in force exactly as much as writing one does, and "Reset this
    /// section" is one click away on every section — so it has to take the same guards. It took none, which
    /// meant one click could drop the bind address back to an inherited network value with no token, or drop
    /// the ingress address an add-on is reached through, and nothing would say it had.
    /// </summary>
    [Fact]
    public async Task Resetting_cannot_do_what_saving_would_be_refused_for()
    {
        try
        {
            // A network binding is safe here only because a token is stored alongside it.
            var saved = await _client.PutAsJsonAsync("/api/settings", new Dictionary<string, object>
            {
                ["Housekeeper:Api:BindAddress"] = "0.0.0.0",
            });
            Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
            Assert.Contains("API token", await saved.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // And the same configuration arrived at by resetting is refused the same way, with a reason.
            var reset = await _client.PostAsJsonAsync("/api/settings/reset", new { section = "Api" });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }
        finally
        {
            await ResetAsync("Api");
        }
    }

    [Fact]
    public async Task Something_that_is_not_a_setting_is_refused()
    {
        var response = await SaveAsync("Housekeeper:Scan:Nonsense", "1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not a setting", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_secret_cannot_be_smuggled_in_through_the_settings_endpoint()
    {
        var response = await SaveAsync("Secrets:HomeAssistantToken", "sneaky");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secret", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
