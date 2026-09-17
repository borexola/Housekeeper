using System.Net;
using System.Net.NetworkInformation;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Housekeeper.Tests;

public class TrustedIngressTests
{
    [Theory]
    [InlineData("172.30.32.2", "172.30.32.2", true)]
    [InlineData("172.30.32.3", "172.30.32.2", false)]
    // The add-on writes the Supervisor address; anything else on the network is still a stranger.
    [InlineData("192.168.1.50", "172.30.32.2", false)]
    // Nothing configured means nothing is trusted.
    [InlineData("172.30.32.2", "", false)]
    [InlineData("172.30.32.2", null, false)]
    [InlineData("172.30.32.2", "not-an-address", false)]
    public void Only_the_configured_address_is_let_in(string remote, string? configured, bool expected) =>
        Assert.Equal(expected, Addresses.IsTrustedIngress(IPAddress.Parse(remote), configured));

    [Fact]
    public void A_request_with_no_address_at_all_is_not_trusted() =>
        Assert.False(Addresses.IsTrustedIngress(null, "172.30.32.2"));

    [Fact]
    public void An_ipv4_caller_arriving_as_ipv6_still_matches()
    {
        var mapped = IPAddress.Parse("172.30.32.2").MapToIPv6();

        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(Addresses.IsTrustedIngress(mapped, "172.30.32.2"));
    }

    [Fact]
    public void An_ingress_address_has_to_look_like_an_address()
    {
        var options = new HousekeeperOptions();
        options.Api.IngressAddress = "supervisor";

        Assert.Contains(options.Validate().Errors, error => error.Contains("IngressAddress"));

        options.Api.IngressAddress = "172.30.32.2";
        Assert.True(options.Validate().IsValid);

        // Empty is the normal case and must stay valid.
        options.Api.IngressAddress = "";
        Assert.True(options.Validate().IsValid);
    }
}

/// <summary>
/// The add-on binds every interface and has no API token, because only the Supervisor can reach the port.
/// Booting at all is the thing being tested: without a trusted ingress address this configuration is fatal.
/// </summary>
[Collection("api")]
public class AddonHostTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"housekeeper-addon-{Guid.NewGuid():N}");
    private readonly string? _previousDataDirectory;
    private readonly WebApplicationFactory<Program> _factory;

    public AddonHostTests(TestApp shared)
    {
        // Build the shared application first so it reads its own data directory before this one moves it.
        _ = shared.CreateClient();

        _previousDataDirectory = Environment.GetEnvironmentVariable("HOUSEKEEPER_DATA_DIR");
        Environment.SetEnvironmentVariable("HOUSEKEEPER_DATA_DIR", _dataDirectory);

        _factory = new AddonApp(_dataDirectory);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("HOUSEKEEPER_DATA_DIR", _previousDataDirectory);
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }

        GC.SuppressFinalize(this);
    }

    private sealed class AddonApp(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Housekeeper:Storage:Path", Path.Combine(dataDirectory, "housekeeper.db"));
            builder.UseSetting("Housekeeper:Scan:Enabled", "false");

            // Exactly what housekeeper/run.sh exports inside the add-on.
            builder.UseSetting("Housekeeper:Api:BindAddress", "0.0.0.0");
            builder.UseSetting("Housekeeper:Api:IngressAddress", "172.30.32.2");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHomeAssistant>();
                services.AddSingleton<IHomeAssistant>(new FakeHomeAssistant());
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new FakeLlm());
            });
        }
    }

    [Fact]
    public async Task It_starts_bound_to_every_interface_with_no_api_token()
    {
        var client = _factory.CreateClient();

        // Reaching this line at all means startup did not refuse the add-on's configuration.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task The_page_shells_load_so_the_sidebar_shows_something()
    {
        var client = _factory.CreateClient();

        foreach (var path in new[] { "/", "/dashboard", "/settings" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Anything_that_is_not_the_supervisor_is_still_refused()
    {
        var client = _factory.CreateClient();

        // The test host presents no remote address, which is not the trusted one, so this is the
        // "something else on the network found the port" case.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);
    }
}

/// <summary>
/// A network-bound instance with a token. This is the configuration where the bearer gate is the only thing
/// between the open port and writing automations into someone's home, and it had no test at all: the one
/// test touching the middleware ran without a token, where the comparison short-circuits before it is
/// reached, so neither the accept path nor the reject path was ever executed.
/// </summary>
[Collection("api")]
public class TokenGateTests : IDisposable
{
    private const string Token = "correct-horse-battery-staple";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"housekeeper-token-{Guid.NewGuid():N}");
    private readonly string? _previousDataDirectory;
    private readonly string? _previousToken;
    private readonly WebApplicationFactory<Program> _factory;

    public TokenGateTests(TestApp shared)
    {
        // Build the shared application first so it reads its own data directory before this one moves it.
        _ = shared.CreateClient();

        _previousDataDirectory = Environment.GetEnvironmentVariable("HOUSEKEEPER_DATA_DIR");
        _previousToken = Environment.GetEnvironmentVariable("HOUSEKEEPER_API_TOKEN");

        Environment.SetEnvironmentVariable("HOUSEKEEPER_DATA_DIR", _dataDirectory);
        Environment.SetEnvironmentVariable("HOUSEKEEPER_API_TOKEN", Token);

        _factory = new TokenApp(_dataDirectory);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("HOUSEKEEPER_DATA_DIR", _previousDataDirectory);
        Environment.SetEnvironmentVariable("HOUSEKEEPER_API_TOKEN", _previousToken);
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }

        GC.SuppressFinalize(this);
    }

    private sealed class TokenApp(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Housekeeper:Storage:Path", Path.Combine(dataDirectory, "housekeeper.db"));
            builder.UseSetting("Housekeeper:Scan:Enabled", "false");

            // Bound to the network, and with no ingress address, so the token is the only way in.
            builder.UseSetting("Housekeeper:Api:BindAddress", "0.0.0.0");
            builder.UseSetting("Housekeeper:Api:IngressAddress", "");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHomeAssistant>();
                services.AddSingleton<IHomeAssistant>(new FakeHomeAssistant());
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new FakeLlm());
            });
        }
    }

    private async Task<HttpStatusCode> StatusAsync(string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task The_right_token_gets_in() =>
        Assert.Equal(HttpStatusCode.OK, await StatusAsync($"Bearer {Token}"));

    [Fact]
    public async Task The_scheme_is_matched_however_it_is_capitalised() =>
        Assert.Equal(HttpStatusCode.OK, await StatusAsync($"bearer {Token}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer ")]
    // The token itself is compared exactly: nearly right is wrong.
    [InlineData("Bearer Correct-Horse-Battery-Staple")]
    [InlineData("Bearer correct-horse-battery-stapl")]
    [InlineData("Bearer correct-horse-battery-staplee")]
    // A token in the wrong place is not a token.
    [InlineData("Basic correct-horse-battery-staple")]
    [InlineData("correct-horse-battery-staple")]
    public async Task Anything_else_is_refused(string? authorization) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(authorization));

    [Fact]
    public async Task The_page_shells_still_load_so_a_token_can_be_pasted_into_them()
    {
        var client = _factory.CreateClient();

        foreach (var path in new[] { "/", "/dashboard", "/settings", "/health" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Writing_an_automation_is_behind_the_gate_like_everything_else()
    {
        var client = _factory.CreateClient();

        var refused = await client.PostAsync("/api/proposals/1/confirm", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // And with the token it reaches the handler, which has its own opinion about proposal 1.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/proposals/1/confirm");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }
}

public class IngressPathTests
{
    /// <summary>
    /// Home Assistant serves an add-on under /api/hassio_ingress/&lt;token&gt;/ and does not rewrite the HTML.
    /// Absolute URLs in a page would escape that prefix, so both pages must address the API relatively.
    /// </summary>
    [Theory]
    [InlineData("dashboard")]
    [InlineData("settings")]
    public void Neither_page_addresses_the_api_from_the_site_root(string page)
    {
        var html = Page(page);

        Assert.DoesNotContain("'/api/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/api/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/", html, StringComparison.Ordinal);
        Assert.Contains("'api/", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pages_link_to_each_other_by_relative_name()
    {
        Assert.Contains("href=\"settings\"", Page("dashboard"), StringComparison.Ordinal);
        Assert.Contains("href=\"dashboard\"", Page("settings"), StringComparison.Ordinal);
    }

    private static string Page(string which)
    {
        var type = typeof(Endpoints).Assembly.GetType(which == "dashboard" ? "Housekeeper.Api.Dashboard" : "Housekeeper.Api.SettingsPage");
        return (string)type!.GetField("Html")!.GetValue(null)!;
    }
}
