namespace Housekeeper.Api;

/// <summary>Joins a configured base address and an API path without doubling a segment the user already typed.</summary>
public static class Urls
{
    /// <summary>
    /// "http://host:8080/v1" plus "v1/models" is "http://host:8080/v1/models", not ".../v1/v1/models". Every
    /// OpenAI SDK expects its base URL to include /v1, so that is what people paste, and a Home Assistant
    /// address may likewise arrive ending in /api. A base that already ends with the path's first segment is
    /// not extended with it again.
    /// </summary>
    public static Uri Under(string? baseUrl, string path)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');

        if (!Uri.TryCreate(trimmed + "/", UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
            throw new UriFormatException($"'{baseUrl}' is not a valid http or https address.");

        // Compared against the parsed path, never against the raw text. The authority is preceded by "//",
        // so "http://api" ends with "/api" as a string and matching there ate the host itself, leaving
        // "http:/" -- a correct address reported back to the user as malformed.
        var segment = path.Split('/')[0];
        var existing = root.AbsolutePath.TrimEnd('/');

        if (existing.Length > 0 && existing.EndsWith("/" + segment, StringComparison.OrdinalIgnoreCase))
        {
            var shortened = existing[..^(segment.Length + 1)];
            root = new UriBuilder(root) { Path = shortened + "/" }.Uri;
        }

        return new Uri(root, path);
    }
}
