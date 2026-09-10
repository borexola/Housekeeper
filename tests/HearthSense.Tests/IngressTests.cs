using System.Net;
using System.Net.NetworkInformation;
using HearthSense.Api;
using HearthSense.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HearthSense.Tests;

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
        var options = new HearthSenseOptions();
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
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"hearthsense-addon-{Guid.NewGuid():N}");
    private readonly string? _previousDataDirectory;
    private readonly WebApplicationFactory<Program> _factory;

    public AddonHostTests(TestApp shared)
    {
        // Build the shared application first so it reads its own data directory before this one moves it.
        _ = shared.CreateClient();

        _previousDataDirectory = Environment.GetEnvironmentVariable("HEARTHSENSE_DATA_DIR");
        Environment.SetEnvironmentVariable("HEARTHSENSE_DATA_DIR", _dataDirectory);

        _factory = new AddonApp(_dataDirectory);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("HEARTHSENSE_DATA_DIR", _previousDataDirectory);
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }

        GC.SuppressFinalize(this);
    }

    private sealed class AddonApp(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("HearthSense:Storage:Path", Path.Combine(dataDirectory, "hearthsense.db"));
            builder.UseSetting("HearthSense:Scan:Enabled", "false");

            // Exactly what hearthsense/run.sh exports inside the add-on.
            builder.UseSetting("HearthSense:Api:BindAddress", "0.0.0.0");
            builder.UseSetting("HearthSense:Api:IngressAddress", "172.30.32.2");

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
        var type = typeof(Endpoints).Assembly.GetType(which == "dashboard" ? "HearthSense.Api.Dashboard" : "HearthSense.Api.SettingsPage");
        return (string)type!.GetField("Html")!.GetValue(null)!;
    }
}
