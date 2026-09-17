using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// One client for both supported shapes: Ollama's <c>/api/chat</c> and any OpenAI-compatible
/// <c>/v1/chat/completions</c>. Both are asked for JSON-only output. Never throws into the caller —
/// an unreachable or badly behaved model surfaces as a failed proposal, not a 500.
///
/// Endpoint, model, timeout and key are read from the current settings per request, so a change on the
/// settings page applies to the next draft without a restart.
/// </summary>
public sealed class LlmClient(
    HttpClient http,
    ISettingsProvider settings,
    ISecretSource secrets,
    ILogger<LlmClient> logger) : ILlmClient
{
    public const string HttpClientName = "llm";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name
    {
        get
        {
            var options = settings.Current.Llm;
            var provider = options.IsOllama ? "Ollama" : options.Provider;
            var model = !string.IsNullOrWhiteSpace(options.Model) ? options.Model.Trim()
                : options.IsOllama ? "no model chosen"
                : "server's loaded model";
            return $"{provider} ({model})";
        }
    }

    public async Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var options = settings.Current.Llm;
        var path = options.IsOllama ? "api/chat" : "v1/chat/completions";
        var body = options.IsOllama ? OllamaBody(options, systemPrompt, userPrompt) : OpenAiBody(options, systemPrompt, userPrompt);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var (status, payload) = await SendAsync(
                HttpMethod.Post, path, JsonSerializer.Serialize(body, Json), options, cancellationToken).ConfigureAwait(false);

            if ((int)status is < 200 or >= 300)
            {
                logger.LogWarning("{Provider} returned HTTP {Status}: {Detail}",
                    Name, (int)status, payload.Length > 200 ? payload[..200] : payload);
                return null;
            }

            var answer = Extract(payload);
            logger.LogInformation("{Provider} answered in {Seconds}s: {Prompt} chars in, {Answer} chars out.",
                Name, Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, 1),
                systemPrompt.Length + userPrompt.Length, answer?.Length ?? 0);
            return answer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{Provider} timed out after {Timeout}.", Name, options.Timeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException)
        {
            logger.LogWarning(ex, "{Provider} could not be reached.", Name);
            return null;
        }
    }

    /// <summary>
    /// Asks the endpoint what models it has, so the settings page can tell "server is down" apart from
    /// "server is up but that model was never pulled". Costs nothing; the model is not run.
    /// </summary>
    public async Task<CheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var options = settings.Current.Llm;
        var path = options.IsOllama ? "api/tags" : "v1/models";
        var wanted = options.Model?.Trim() ?? "";

        try
        {
            var (status, payload) = await SendAsync(HttpMethod.Get, path, null, options, cancellationToken).ConfigureAwait(false);

            // The exact address, so a wrong path is a thing you can see rather than a thing you have to guess.
            if ((int)status is < 200 or >= 300)
                return CheckResult.Fail($"{Urls.Under(options.Endpoint, path)} answered HTTP {(int)status} when asked which models it has.");

            var available = ModelNames(payload, options.IsOllama);
            var offers = available.Count == 0 ? "It lists no models." : $"It offers: {string.Join(", ", available.Take(12))}.";

            if (wanted.Length == 0)
            {
                // Ollama insists on a model name. An OpenAI-style server with one model loaded does not.
                return options.IsOllama
                    ? CheckResult.Fail($"Connected to {options.Endpoint}, but Ollama needs a model name. {offers} Pick one and save.")
                    : CheckResult.Pass($"Connected to {options.Endpoint}. No model chosen, so the server's loaded model will be used. {offers}");
            }

            if (available.Count == 0)
                return CheckResult.Pass($"Connected to {options.Endpoint}, but it listed no models, so '{wanted}' could not be confirmed.");

            // Matched the way the server itself will resolve it. Ollama expands a bare name to "<name>:latest"
            // and 404s if only a sized tag was pulled, so treating a missing tag as "any tag will do" made
            // Test connection pass on a model every draft would then fail against. An OpenAI-style server has
            // no tag convention, so there a family match is genuinely the right answer.
            var present = available.Any(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) ||
                (options.IsOllama && !wanted.Contains(':', StringComparison.Ordinal) &&
                 available.Any(name => string.Equals(name, wanted + ":latest", StringComparison.OrdinalIgnoreCase))) ||
                (!options.IsOllama &&
                 available.Any(name => string.Equals(name.Split(':')[0], wanted, StringComparison.OrdinalIgnoreCase)));

            if (present) return CheckResult.Pass($"Connected. '{wanted}' is available at {options.Endpoint}.");

            // Name the exact tag to type. "not there" is unhelpful when the model IS pulled under one tag.
            var sameFamily = available
                .Where(name => string.Equals(name.Split(':')[0], wanted.Split(':')[0], StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sameFamily.Count > 0)
                return CheckResult.Fail(
                    $"Connected to {options.Endpoint}, but '{wanted}' is not there. It has {string.Join(", ", sameFamily)} — "
                    + "use one of those names exactly.");

            var pull = options.IsOllama ? $" Pull it with: ollama pull {wanted}" : " Clear the model to use whatever the server has loaded.";
            return CheckResult.Fail($"Connected to {options.Endpoint}, but '{wanted}' is not there. {offers}{pull}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckResult.Fail($"{options.Endpoint} did not answer within {Durations.Format(options.Timeout)}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException)
        {
            return CheckResult.Fail($"{options.Endpoint} could not be reached. {ex.Message}");
        }
    }

    private static List<string> ModelNames(string payload, bool ollama)
    {
        List<string> names = [];

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return names;

            // Ollama: { "models": [ { "name": "qwen2.5:7b" } ] } — OpenAI: { "data": [ { "id": "gpt-4o" } ] }
            var arrayName = ollama ? "models" : "data";
            var field = ollama ? "name" : "id";

            if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) return names;

            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty(field, out var name) &&
                    name.ValueKind == JsonValueKind.String &&
                    name.GetString() is { Length: > 0 } text)
                    names.Add(text);
        }
        catch (JsonException)
        {
            // An endpoint that answers with something other than JSON tells us nothing about its models.
        }

        return names;
    }

    private async Task<(System.Net.HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        LlmOptions options,
        CancellationToken cancellationToken)
    {
        // Clamped, not trusted: past what a timer accepts, CancelAfter throws before the request is made.
        var timeoutAfter = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(90);
        if (timeoutAfter > LlmOptions.LongestTimeout) timeoutAfter = LlmOptions.LongestTimeout;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);

        using var request = new HttpRequestMessage(method, Urls.Under(options.Endpoint, path));

        var apiKey = secrets.Resolve(SecretStore.LlmApiKey, options.ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        return (response.StatusCode, body);
    }

    private static Dictionary<string, object?> OllamaBody(LlmOptions options, string system, string user) => new()
    {
        // Trimmed like every other use of it. Untrimmed, a stray space from an environment variable made
        // "Test connection" pass against the trimmed name and every draft 404 against the padded one.
        ["model"] = options.Model?.Trim(),
        ["stream"] = false,
        ["format"] = "json",
        ["messages"] = Messages(system, user),
        // num_ctx is sent explicitly because Ollama otherwise takes it from the model's Modelfile -- often
        // 2048 -- and truncates the prompt to fit rather than failing. A silently halved catalogue is worse
        // than an error: the model then invents the entities it can no longer see.
        ["options"] = new
        {
            temperature = options.Temperature,
            num_predict = options.MaxOutputTokens,
            num_ctx = options.ContextTokens,
        },
    };

    /// <summary>With no model chosen the field is left out, and a server with one model loaded answers with it.</summary>
    private static Dictionary<string, object?> OpenAiBody(LlmOptions options, string system, string user)
    {
        Dictionary<string, object?> body = new()
        {
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxOutputTokens,
            ["response_format"] = new { type = "json_object" },
            ["messages"] = Messages(system, user),
        };

        if (!string.IsNullOrWhiteSpace(options.Model)) body["model"] = options.Model.Trim();
        return body;
    }

    private static object[] Messages(string system, string user) =>
    [
        new { role = "system", content = system },
        new { role = "user", content = user },
    ];

    /// <summary>Pulls the assistant message out of either response shape.</summary>
    internal static string? Extract(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Ollama: { "message": { "content": "..." } }
        if (root.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var ollamaContent) &&
            ollamaContent.ValueKind == JsonValueKind.String)
        {
            return ollamaContent.GetString();
        }

        // OpenAI-compatible: { "choices": [ { "message": { "content": "..." } } ] }
        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("message", out var chatMessage) &&
                chatMessage.ValueKind == JsonValueKind.Object &&
                chatMessage.TryGetProperty("content", out var openAiContent) &&
                openAiContent.ValueKind == JsonValueKind.String)
            {
                return openAiContent.GetString();
            }
        }

        return null;
    }
}
