using System.Security.Cryptography;
using System.Text;
using HearthSense.Api;
using HearthSense.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

// Container health check. Keeps the runtime image free of curl for the sake of one HTTP GET.
if (args.Contains("--healthcheck"))
{
    var probePort = Environment.GetEnvironmentVariable("HEARTHSENSE__Api__Port") ?? "5080";
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        using var probeResponse = await probe.GetAsync($"http://127.0.0.1:{probePort}/health");
        return probeResponse.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

AddConfigFile(builder.Configuration);

// What configuration would say without the settings UI in the picture. The settings page shows this as the
// value each field falls back to, and "reset" puts a field back to it.
var inheritedBuilder = new ConfigurationBuilder();
foreach (var source in builder.Configuration.Sources.ToList()) inheritedBuilder.Add(source);
var inherited = inheritedBuilder.Build();

// Settings and secrets written from the UI live here. It is deliberately not itself UI-configurable: it is
// where the answer to "where are the settings?" has to be knowable before any settings have been read.
var dataDirectory = DataDirectoryOf(inherited);
Directory.CreateDirectory(dataDirectory);

var settingsFile = Path.Combine(dataDirectory, "settings.json");
var secretsFile = Path.Combine(dataDirectory, "secrets.json");

// Last source wins, so what the user set on the settings page beats the environment.
builder.Configuration.AddJsonFile(
    new PhysicalFileProvider(dataDirectory), "settings.json", optional: true, reloadOnChange: true);

// Bound eagerly only to choose the listen address; everything else resolves the live provider below.
var listen = new HearthSenseOptions();
builder.Configuration.GetSection(HearthSenseOptions.SectionName).Bind(listen);
builder.WebHost.UseUrls($"http://{listen.Api.BindAddress}:{listen.Api.Port}");

builder.Services.AddSingleton(new InheritedConfiguration(inherited));
builder.Services.AddSingleton(sp => new SettingsStore(settingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));
builder.Services.AddSingleton(sp => new SecretStore(secretsFile, sp.GetRequiredService<ILogger<SecretStore>>()));
builder.Services.AddSingleton(sp => new SettingsProvider(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<SettingsStore>()));
builder.Services.AddSingleton<ISettingsProvider>(sp => sp.GetRequiredService<SettingsProvider>());
builder.Services.AddSingleton(new BootSnapshot(RestartValuesOf(listen), dataDirectory));
builder.Services.AddSingleton<SettingsContext>();

builder.Services.AddSingleton(TimeProvider.System);

// The clients read their address, timeout and credentials per request, so these are plain pooled handlers.
builder.Services.AddHttpClient(HomeAssistantClient.HttpClientName);
builder.Services.AddHttpClient(LlmClient.HttpClientName);

builder.Services.AddSingleton<IHomeAssistant>(sp => new HomeAssistantClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(HomeAssistantClient.HttpClientName),
    sp.GetRequiredService<ISettingsProvider>(),
    sp.GetRequiredService<SecretStore>(),
    sp.GetRequiredService<ILogger<HomeAssistantClient>>()));

builder.Services.AddSingleton<ILlmClient>(sp => new LlmClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmClient.HttpClientName),
    sp.GetRequiredService<ISettingsProvider>(),
    sp.GetRequiredService<SecretStore>(),
    sp.GetRequiredService<ILogger<LlmClient>>()));

builder.Services.AddSingleton<SqliteStore>(sp => new SqliteStore(
    SqliteStore.ConnectionStringFor(sp.GetRequiredService<ISettingsProvider>().Current.Storage.Path)));
builder.Services.AddSingleton<IStore>(sp => sp.GetRequiredService<SqliteStore>());

builder.Services.AddSingleton<ProposalService>();
builder.Services.AddSingleton<AnomalyScanner>();
builder.Services.AddHostedService<ScanWorker>();

builder.Services.AddOpenApi();

var app = builder.Build();

var settings = app.Services.GetRequiredService<SettingsProvider>();
var secrets = app.Services.GetRequiredService<SecretStore>();
var options = settings.Current;
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HearthSense");

// --- Configuration problems are reported, not fatal: they are all fixable on the settings page ---
var validation = options.Validate();
foreach (var warning in validation.Warnings)
    logger.LogWarning("Config warning: {Warning}", warning);

foreach (var error in validation.Errors)
    logger.LogError("Config error: {Error}", error);

if (string.IsNullOrWhiteSpace(secrets.Resolve(SecretStore.HomeAssistantToken, options.HomeAssistant.TokenEnvironmentVariable)))
    logger.LogWarning(
        "No Home Assistant token is set, so nothing can be drafted yet. Add one at /settings; it needs to come from an admin user.");

// --- An upstream that is down is a 502 with a reason, not a stack trace ---
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var (status, message) = feature?.Error switch
    {
        HomeAssistantException ex => (StatusCodes.Status502BadGateway, ex.Message),
        HttpRequestException ex => (StatusCodes.Status502BadGateway, $"An upstream service could not be reached. {ex.Message}"),
        _ => (StatusCodes.Status500InternalServerError, "Something went wrong; see the HearthSense log for details."),
    };

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = message });
}));

// --- Loopback is trusted; anything else needs a bearer token or we refuse to start ---
var requiresAuth = !Addresses.IsLoopback(options.Api.BindAddress);

if (requiresAuth)
{
    var ingressOnly = string.IsNullOrWhiteSpace(secrets.Resolve(SecretStore.ApiToken, options.Api.TokenEnvironmentVariable));

    // Running as a Home Assistant add-on there is no token and no need for one: the port is not published,
    // and the only thing that can reach it is the Supervisor, which has already authenticated the user.
    if (ingressOnly && string.IsNullOrWhiteSpace(options.Api.IngressAddress))
        throw new InvalidOperationException(
            $"The API is bound to '{options.Api.BindAddress}' but no API token is set. Set ${options.Api.TokenEnvironmentVariable} " +
            "or bind to 127.0.0.1. Refusing to expose automation writing without a token.");

    if (ingressOnly)
        logger.LogInformation(
            "No API token is set; only {Ingress} may reach this instance and everything else is refused.",
            options.Api.IngressAddress);

    app.Use(async (context, next) =>
    {
        // The page shells hold no data and need to load so a token can be pasted into them.
        var path = context.Request.Path;
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.Equals("/", StringComparison.Ordinal) ||
             path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/settings", StringComparison.OrdinalIgnoreCase)))
        {
            await next();
            return;
        }

        // Home Assistant ingress: the Supervisor already authenticated whoever is on the other end.
        var current = settings.Current;
        if (Addresses.IsTrustedIngress(context.Connection.RemoteIpAddress, current.Api.IngressAddress))
        {
            await next();
            return;
        }

        // Resolved per request so rotating the token on the settings page takes effect at once.
        var expected = secrets.Resolve(SecretStore.ApiToken, current.Api.TokenEnvironmentVariable);
        var provided = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (string.IsNullOrWhiteSpace(expected) ||
            !provided.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(provided[prefix.Length..])))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Bearer token required." });
            return;
        }

        await next();
    });
}

// Served at the root as well as by name: Home Assistant ingress opens the add-on at its own root, and a
// redirect to an absolute /dashboard would jump out of the ingress path.
app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html")).ExcludeFromDescription();
app.MapGet("/dashboard", () => Results.Content(Dashboard.Html, "text/html")).ExcludeFromDescription();
app.MapGet("/settings", () => Results.Content(SettingsPage.Html, "text/html")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Json(new { status = "ok" })).ExcludeFromDescription();

app.MapGet("/ready", async (IHomeAssistant homeAssistant, CancellationToken cancellationToken) =>
{
    var connected = await homeAssistant.PingAsync(cancellationToken);
    return Results.Json(
        new { status = connected ? "ready" : "not-ready", homeAssistant = connected ? "connected" : "unreachable" },
        statusCode: connected ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).ExcludeFromDescription();

app.MapHearthSense();
app.MapSettings();
app.MapOpenApi();

await app.Services.GetRequiredService<SqliteStore>().InitialiseAsync(CancellationToken.None);

logger.LogInformation(
    "HearthSense listening on http://{Bind}:{Port} (auth: {Auth}), data in {DataDirectory}, Home Assistant at {Ha}, model {Model}.",
    options.Api.BindAddress, options.Api.Port, requiresAuth ? "token" : "loopback only",
    dataDirectory, options.HomeAssistant.BaseUrl, options.Llm.Model);

await app.RunAsync();
return 0;

/// <summary>Layers in an optional JSON file, kept below environment variables so those still win.</summary>
static void AddConfigFile(IConfigurationBuilder configuration)
{
    var path = Environment.GetEnvironmentVariable("HEARTHSENSE_CONFIG_FILE");
    if (string.IsNullOrWhiteSpace(path)) return;

    var full = Path.GetFullPath(path);
    if (!File.Exists(full)) return;

    var sources = configuration.Sources.ToList();
    var insertAt = sources.FindIndex(s => s.GetType().Name.Contains("EnvironmentVariables", StringComparison.Ordinal));

    var source = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
    {
        Path = full,
        Optional = true,
        ReloadOnChange = false,
        FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(full)!),
    };
    source.ResolveFileProvider();

    if (insertAt >= 0) configuration.Sources.Insert(insertAt, source);
    else configuration.Sources.Add(source);
}

/// <summary>
/// Where settings.json and secrets.json live. HEARTHSENSE_DATA_DIR wins; otherwise it is the folder the
/// database was configured into before the UI had any say.
/// </summary>
static string DataDirectoryOf(IConfiguration configuration)
{
    var explicitly = Environment.GetEnvironmentVariable("HEARTHSENSE_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(explicitly)) return Path.GetFullPath(explicitly);

    var database = configuration["HearthSense:Storage:Path"];
    if (string.IsNullOrWhiteSpace(database)) database = new StorageOptions().Path;

    var directory = Path.GetDirectoryName(Path.GetFullPath(database));
    return string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
}

/// <summary>The values of the restart-only settings as the process started, for spotting drift later.</summary>
static Dictionary<string, string> RestartValuesOf(HearthSenseOptions options) =>
    SettingsCatalog.Fields
        .Where(field => field.RestartRequired)
        .ToDictionary(field => field.Key, field => SettingsCatalog.Display(field, options)?.ToJsonString() ?? "");

/// <summary>Exposed so the test host can boot the real application.</summary>
public partial class Program;
