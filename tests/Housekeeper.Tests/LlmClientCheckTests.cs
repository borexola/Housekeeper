using System.Net;
using System.Text;
using System.Text.Json;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Housekeeper.Tests;

/// <summary>The model client against a scripted server: where it sends requests, what it sends, and what it reports.</summary>
public class LlmClientCheckTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? LastBody { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsoluteUri);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }

    private static (LlmClient Client, StubHandler Handler) Make(string provider, string endpoint, string model)
    {
        var handler = new StubHandler();
        var settings = new FakeSettings();
        settings.Current.Llm.Provider = provider;
        settings.Current.Llm.Endpoint = endpoint;
        settings.Current.Llm.Model = model;

        var secrets = new SecretStore(
            Path.Combine(Path.GetTempPath(), $"hs-llm-test-{Guid.NewGuid():N}.json"), NullLogger<SecretStore>.Instance);

        return (new LlmClient(new HttpClient(handler), settings, secrets, NullLogger<LlmClient>.Instance), handler);
    }

    private const string OpenAiModels = """{"data":[{"id":"qwen2.5-7b-instruct"},{"id":"phi4"}]}""";
    private const string OllamaModels = """{"models":[{"name":"qwen2.5:7b"},{"name":"phi4:latest"}]}""";

    /// <summary>
    /// Every OpenAI SDK takes a base URL that already ends in /v1, so that is what people paste. Appending
    /// v1/models to it asked for /v1/v1/models and got a 404 from a server that was working perfectly.
    /// </summary>
    [Theory]
    [InlineData("http://model.test:8080/v1")]
    [InlineData("http://model.test:8080/v1/")]
    [InlineData("http://model.test:8080")]
    [InlineData("http://model.test:8080/")]
    public async Task An_endpoint_with_or_without_v1_reaches_the_same_place(string endpoint)
    {
        var (client, handler) = Make("OpenAI", endpoint, "phi4");
        handler.Body = OpenAiModels;

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(["http://model.test:8080/v1/models"], handler.Paths);
    }

    [Fact]
    public async Task An_ollama_endpoint_typed_with_its_api_segment_is_not_doubled_either()
    {
        var (client, handler) = Make("Ollama", "http://model.test:11434/api", "phi4");
        handler.Body = OllamaModels;

        await client.CheckAsync(CancellationToken.None);

        Assert.Equal(["http://model.test:11434/api/tags"], handler.Paths);
    }

    [Fact]
    public async Task A_refusal_names_the_exact_address_that_was_tried()
    {
        var (client, handler) = Make("OpenAI", "http://model.test:8080/v1", "phi4");
        handler.Status = HttpStatusCode.NotFound;

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("http://model.test:8080/v1/models", result.Detail);
        Assert.Contains("404", result.Detail);
    }

    [Fact]
    public async Task Without_a_model_an_openai_style_server_is_fine_and_what_it_offers_is_listed()
    {
        var (client, handler) = Make("OpenAI", "http://model.test:8080/v1", "");
        handler.Body = OpenAiModels;

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Contains("loaded model", result.Detail);
        Assert.Contains("qwen2.5-7b-instruct", result.Detail);
    }

    [Fact]
    public async Task Without_a_model_ollama_is_told_to_pick_one_from_what_it_has()
    {
        var (client, handler) = Make("Ollama", "http://model.test:11434", "");
        handler.Body = OllamaModels;

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("needs a model", result.Detail);
        Assert.Contains("qwen2.5:7b", result.Detail);
    }

    [Fact]
    public async Task The_model_field_is_left_out_of_an_openai_request_when_none_is_chosen()
    {
        var (client, handler) = Make("OpenAI", "http://model.test:8080/v1", "");
        handler.Body = """{"choices":[{"message":{"content":"{}"}}]}""";

        await client.CompleteJsonAsync("system", "user", CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.False(sent.RootElement.TryGetProperty("model", out _));
        Assert.Equal("http://model.test:8080/v1/chat/completions", handler.Paths.Single());
    }

    [Fact]
    public async Task The_model_field_is_sent_when_one_is_chosen()
    {
        var (client, handler) = Make("OpenAI", "http://model.test:8080/v1", "phi4");
        handler.Body = """{"choices":[{"message":{"content":"{}"}}]}""";

        await client.CompleteJsonAsync("system", "user", CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("phi4", sent.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// Ollama takes the context window from the model's Modelfile when it is not told otherwise -- commonly
    /// 2048 -- and then truncates the prompt to fit instead of failing. A silently halved entity catalogue is
    /// worse than an error, because the model goes on to invent the entities it can no longer see.
    /// </summary>
    [Fact]
    public async Task An_ollama_request_asks_for_a_context_window_big_enough_for_the_prompt()
    {
        var (client, handler) = Make("Ollama", "http://model.test:11434", "phi4");
        handler.Body = """{"message":{"content":"{}"}}""";

        await client.CompleteJsonAsync(Prompts.System, "user", CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var context = sent.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32();

        Assert.Equal(new LlmOptions().ContextTokens, context);

        // Four characters per token is the usual rough conversion, and the instructions alone must fit with
        // room left over for the house. If the prompt grows past this, the default has to grow with it.
        Assert.True(context > Prompts.System.Length / 4 * 2, $"num_ctx {context} is too small for the instructions.");
    }

    /// <summary>
    /// Ollama uses the tag to tell quantisations and sizes apart. Ignoring it on the side the user typed as
    /// well meant "Test connection" cheerfully confirmed a 32b model because a 7b one had been pulled, and
    /// the 404 arrived later, at the moment someone was waiting on a draft.
    /// </summary>
    [Theory]
    [InlineData("qwen2.5:7b", true)]     // exactly what is there
    [InlineData("QWEN2.5:7B", true)]     // case is not the point
    [InlineData("phi4:latest", true)]
    // Ollama expands a bare name to "<name>:latest" and 404s if only a sized tag was pulled, so a missing
    // tag is not a wildcard. Treating it as one made Test connection pass on a model every draft then failed
    // against — the worst possible place to find out, since drafting costs a minute of a slow local model.
    [InlineData("phi4", true)]           // phi4:latest really is pulled
    [InlineData("qwen2.5", false)]       // only qwen2.5:7b is, so bare "qwen2.5" would 404
    [InlineData("qwen2.5:32b", false)]
    [InlineData("qwen2.5:latest", false)]
    [InlineData("llama3", false)]
    public async Task A_tag_the_user_typed_has_to_be_one_the_server_really_has(string model, bool expected)
    {
        var (client, handler) = Make("Ollama", "http://model.test:11434", model);
        handler.Body = OllamaModels;

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.Equal(expected, result.Ok);
        if (!expected) Assert.Contains("is not there", result.Detail);
    }

    [Fact]
    public async Task When_the_model_is_pulled_under_another_tag_the_message_names_that_tag()
    {
        // "not there" is unhelpful when it IS there, under a name one character different.
        var (client, handler) = Make("Ollama", "http://model.test:11434", "qwen2.5");
        handler.Body = OllamaModels;

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("qwen2.5:7b", result.Detail);
        Assert.Contains("use one of those names exactly", result.Detail);
    }

    [Fact]
    public async Task An_openai_style_server_has_no_tag_convention_so_a_family_match_still_counts()
    {
        var (client, handler) = Make("OpenAI", "http://model.test:8080/v1", "qwen2.5-7b-instruct");
        handler.Body = OpenAiModels;

        Assert.True((await client.CheckAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public void The_name_reads_well_without_a_model()
    {
        Assert.Equal("OpenAI (server's loaded model)", Make("OpenAI", "http://model.test:8080/v1", "").Client.Name);
        Assert.Equal("Ollama (no model chosen)", Make("Ollama", "http://model.test:11434", "").Client.Name);
        Assert.Equal("Ollama (phi4)", Make("Ollama", "http://model.test:11434", "phi4").Client.Name);
    }
}
