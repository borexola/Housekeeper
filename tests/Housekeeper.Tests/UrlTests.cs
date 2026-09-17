using Housekeeper.Api;

namespace Housekeeper.Tests;

/// <summary>
/// Joining a base address to an API path. Every OpenAI SDK takes a base URL ending in /v1, so that is what
/// people paste; a Home Assistant address likewise often arrives with /api on the end. Getting this wrong
/// produces a 404 from a server that is working perfectly, which is an unusually hard thing to diagnose.
/// </summary>
public class UrlTests
{
    [Theory]
    [InlineData("http://model.test:8080/v1", "http://model.test:8080/v1/models")]
    [InlineData("http://model.test:8080/v1/", "http://model.test:8080/v1/models")]
    [InlineData("http://model.test:8080", "http://model.test:8080/v1/models")]
    [InlineData("http://model.test:8080/", "http://model.test:8080/v1/models")]
    [InlineData("  http://model.test:8080/v1  ", "http://model.test:8080/v1/models")]
    [InlineData("http://model.test:8080/openai/v1", "http://model.test:8080/openai/v1/models")]
    public void A_segment_the_user_already_typed_is_not_added_twice(string baseUrl, string expected) =>
        Assert.Equal(expected, Urls.Under(baseUrl, "v1/models").ToString());

    /// <summary>
    /// The authority is preceded by "//", so "http://api" ends with "/api" as plain text. Matching the
    /// de-duplication prefix against the raw string ate the host, leaving "http:/", and a correct address
    /// was reported back as malformed with nothing the user could do about it.
    /// </summary>
    [Theory]
    [InlineData("http://api", "api/states", "http://api/api/states")]
    [InlineData("http://v1", "v1/models", "http://v1/v1/models")]
    [InlineData("http://api.lan", "api/states", "http://api.lan/api/states")]
    public void A_host_whose_name_is_the_segment_is_left_alone(string baseUrl, string path, string expected) =>
        Assert.Equal(expected, Urls.Under(baseUrl, path).ToString());

    [Theory]
    [InlineData("homeassistant.local:8123")]
    [InlineData("ftp://homeassistant.local")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_an_http_address_is_refused_by_name(string? baseUrl)
    {
        var error = Assert.Throws<UriFormatException>(() => Urls.Under(baseUrl, "api/states"));

        Assert.Contains("http", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
