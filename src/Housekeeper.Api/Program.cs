using System.Security.Cryptography;
using System.Text;
using Housekeeper.Api;
using Housekeeper.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

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

// Except for how the add-on is reached. Those values are a contract with the Supervisor, which opens ingress
// on the port config.yaml declares; a stored override would leave it knocking at a port nothing is listening
// on, and the settings page needed to put it right is itself behind that ingress. Applied after settings.json
// so nothing stored can win, and refused by the settings API so nobody is told a change worked when it did not.
//
// Read from `inherited`, which is the configuration WITHOUT the settings.json layer. Reading them back out of
// builder.Configuration would have pinned whatever settings.json already said - re-asserting the stored
// override on top of itself, which is the exact value this exists to overrule. An add-on upgraded from a
// build where the port was an ordinary setting would have carried its stored port straight through the pin.
if (Managed.IsAddOn)
    builder.Configuration.AddInMemoryCollection(Managed.PinnedFrom(inherited));

// Bound eagerly only to choose the listen address; everything else resolves the live provider below.
var listen = new HousekeeperOptions();
builder.Configuration.GetSection(HousekeeperOptions.SectionName).Bind(listen);
builder.WebHost.UseUrls($"http://{listen.Api.BindAddress}:{listen.Api.Port}");

builder.Services.AddSingleton(new InheritedConfiguration(inherited));
builder.Services.AddSingleton(sp => new SettingsStore(settingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));
builder.Services.AddSingleton(sp => new SecretStore(secretsFile, sp.GetRequiredService<ILogger<SecretStore>>()));
builder.Services.AddSingleton(sp => new SettingsProvider(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<SettingsStore>()));
builder.Services.AddSingleton<ISettingsProvider>(sp => sp.GetRequiredService<SettingsProvider>());
builder.Services.AddSingleton(new BootSnapshot(RestartValuesOf(listen), dataDirectory));
builder.Services.AddSingleton<SettingsContext>();
builder.Services.AddSingleton<IAdapters, Adapters>();

builder.Services.AddSingleton(TimeProvider.System);

// The Logs page reads from an in-memory ring of every line the app writes. Our own categories are kept at
// Debug so the page can show the request-by-request detail the console is spared; the host's are not.
var logBuffer = new LogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new LogBufferProvider(logBuffer, TimeProvider.System));
builder.Logging.AddFilter<LogBufferProvider>((category, level) =>
    category is not null && category.StartsWith("Housekeeper", StringComparison.Ordinal) ? level >= LogLevel.Debug : level >= LogLevel.Warning);

// The clients read their address, timeout and credentials per request, so these are plain pooled handlers.
// HttpClient's own 100-second default is switched off: each request sets its own deadline from the configured
// timeout, and leaving the default in place silently capped any value above 100s while the failure message
// still quoted the configured one. A slow local model on a CPU regularly needs longer than that.
builder.Services.AddHttpClient(HomeAssistantClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    // A home install serving HTTPS with its own certificate is ordinary, and refusing it outright would be
    // unhelpful -- but so would not checking, because every request here carries an admin token. The settings
    // are read inside the callback rather than captured, so pinning a fingerprint takes effect on the next
    // request instead of on the next restart.
    .ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            var ha = sp.GetRequiredService<ISettingsProvider>().Current.HomeAssistant;
            return Certificates.Accept(certificate, errors, ha.CertificateFingerprint, ha.AcceptAnyCertificate);
        },
    });
builder.Services.AddHttpClient(LlmClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

// Every entity read passes through the name book on its way past, so a proposal can be read back in the
// names the user knows without asking Home Assistant again for each page load.
builder.Services.AddSingleton<NameBook>();
builder.Services.AddSingleton<IHomeAssistant>(sp => new NamingHomeAssistant(
    sp.GetRequiredService<IAdapters>().HomeAssistant(sp.GetRequiredService<ISettingsProvider>(), sp.GetRequiredService<SecretStore>()),
    sp.GetRequiredService<NameBook>()));

builder.Services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<IAdapters>()
    .Llm(sp.GetRequiredService<ISettingsProvider>(), sp.GetRequiredService<SecretStore>()));

builder.Services.AddSingleton<SqliteStore>(sp => new SqliteStore(
    SqliteStore.ConnectionStringFor(sp.GetRequiredService<ISettingsProvider>().Current.Storage.Path)));
builder.Services.AddSingleton<IStore>(sp => sp.GetRequiredService<SqliteStore>());

builder.Services.AddSingleton<ProposalService>();
builder.Services.AddSingleton<ConcernService>();
builder.Services.AddSingleton<AnomalyScanner>();
builder.Services.AddHostedService<ScanWorker>();

// The live state feed runs beside the scan rather than instead of it: it records the transitions that fall
// between two polls, and the scan remains the thing that reads every entity and closes any gap it leaves.
builder.Services.AddSingleton<IStateFeed>(sp => sp.GetRequiredService<IAdapters>()
    .StateFeed(sp.GetRequiredService<ISettingsProvider>(), sp.GetRequiredService<SecretStore>()));
builder.Services.AddHostedService<StateFeedWorker>();

builder.Services.AddOpenApi();

var app = builder.Build();

var settings = app.Services.GetRequiredService<SettingsProvider>();
var secrets = app.Services.GetRequiredService<SecretStore>();
var options = settings.Current;
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Housekeeper");

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
        _ => (StatusCodes.Status500InternalServerError, "Something went wrong; see the Housekeeper log for details."),
    };

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = message });
}));

// --- Loopback is trusted; anything else needs a bearer token or we refuse to start ---
var requiresAuth = !Addresses.IsLoopback(options.Api.BindAddress);

// --- Nothing another site opened in the same browser may reach this, by any route ---
//
// On the default loopback binding there is no token and no authentication, which is normal for a local tool
// and is what makes all of this necessary. Two different attacks have to be stopped, and neither is stopped
// by the other:
//
//   A page the user has open posts straight at 127.0.0.1. Every state-changing endpoint is a POST, which a
//   browser sends cross-origin without asking permission, so a draft could be confirmed into someone's house
//   by a page they merely visited. Sec-Fetch-Site and Origin give this away; a browser sets both itself and a
//   page cannot forge either, while a non-browser client (curl, a script) sends neither and is unaffected.
//
//   Or that page is served from a domain whose DNS is flipped to 127.0.0.1 mid-visit. Then it IS same-origin
//   as far as the browser is concerned - it reports Sec-Fetch-Site: same-origin, and it can read every reply
//   as well as write - so the signals above say yes and mean it. What still gives it away is the Host header,
//   which carries the attacker's name rather than a name this instance is served under. That check has to
//   cover reads too, because the point of rebinding is to exfiltrate what it can read.
app.Use(async (context, next) =>
{
    // Only where no token is required. With one, a rebound page cannot authenticate anyway, and the check
    // would refuse the ordinary case of reaching a network-bound instance by its host name.
    if (!requiresAuth && !Addresses.IsAllowedHost(context.Request.Host.Host, settings.Current.Api))
    {
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = $"Housekeeper is not served at '{context.Request.Host.Host}'. Reach it at localhost, or add "
                + "that name to Api.AllowedHosts if it is your own reverse proxy.",
        });
        return;
    }

    if (HttpMethods.IsGet(context.Request.Method) ||
        HttpMethods.IsHead(context.Request.Method) ||
        HttpMethods.IsOptions(context.Request.Method))
    {
        await next();
        return;
    }

    // Sec-Fetch-Site is set by every current browser and cannot be assigned from script, so where it is
    // present it is the answer. Accepted as an allowlist rather than refusing "cross-site" alone: "same-site"
    // means only that the registrable domain matches, and for a host with no registrable domain - which is
    // every IP literal and localhost - that is satisfied by nothing more than an equal host. So any OTHER
    // service on 127.0.0.1, a dev server or a local model UI, would count as same-site and be let in.
    // Behind Home Assistant ingress the page and this API genuinely share an origin, so "same-origin" covers
    // the add-on without needing to loosen this.
    var site = context.Request.Headers["Sec-Fetch-Site"].ToString();

    if (site.Length > 0)
    {
        if (site.Equals("same-origin", StringComparison.OrdinalIgnoreCase) ||
            site.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }
    }
    else
    {
        // No Sec-Fetch-Site: an older browser - including the web view in the Home Assistant companion app on
        // older iOS, which is exactly this add-on's audience - or not a browser at all. Origin is the older
        // signal, and is present on exactly the cross-site posts that matter; its absence means nothing to go
        // on. It is compared against the host the browser was actually addressing, which behind any proxy is
        // X-Forwarded-Host rather than the address that proxy dialled us on.
        var origin = context.Request.Headers.Origin.ToString();
        var forwarded = context.Request.Headers["X-Forwarded-Host"].ToString();

        var addressed = new[] { forwarded, context.Request.Host.Value }
            .Where(host => !string.IsNullOrWhiteSpace(host));

        if (origin.Length == 0 ||
            (Uri.TryCreate(origin, UriKind.Absolute, out var from) &&
             addressed.Any(host => string.Equals(from.Authority, host, StringComparison.OrdinalIgnoreCase))))
        {
            await next();
            return;
        }
    }

    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "Cross-site requests are refused. Open the Housekeeper dashboard directly.",
    });
});

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
             path.Equals("/settings", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/logs", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/concerns", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/noticed", StringComparison.OrdinalIgnoreCase)))
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
app.MapGet("/logs", () => Results.Content(LogsPage.Html, "text/html")).ExcludeFromDescription();
app.MapGet("/concerns", () => Results.Content(ConcernsPage.Html, "text/html")).ExcludeFromDescription();
app.MapGet("/noticed", () => Results.Content(NoticedPage.Html, "text/html")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Json(new { status = "ok" })).ExcludeFromDescription();

app.MapGet("/ready", async (IHomeAssistant homeAssistant, CancellationToken cancellationToken) =>
{
    var connected = await homeAssistant.PingAsync(cancellationToken);
    return Results.Json(
        new { status = connected ? "ready" : "not-ready", homeAssistant = connected ? "connected" : "unreachable" },
        statusCode: connected ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).ExcludeFromDescription();

app.MapHousekeeper();
app.MapSettings();
app.MapOpenApi();

await app.Services.GetRequiredService<SqliteStore>().InitialiseAsync(CancellationToken.None);

logger.LogInformation(
    "Housekeeper listening on http://{Bind}:{Port} (auth: {Auth}), data in {DataDirectory}, Home Assistant at {Ha}, model {Model}.",
    options.Api.BindAddress, options.Api.Port, requiresAuth ? "token" : "loopback only",
    dataDirectory, options.HomeAssistant.BaseUrl, options.Llm.Model);

await app.RunAsync();
return 0;

/// <summary>Layers in an optional JSON file, kept below environment variables so those still win.</summary>
static void AddConfigFile(IConfigurationBuilder configuration)
{
    var path = Environment.GetEnvironmentVariable("HOUSEKEEPER_CONFIG_FILE");
    if (string.IsNullOrWhiteSpace(path)) return;

    var full = Path.GetFullPath(path);
    if (!File.Exists(full)) return;

    var sources = configuration.Sources.ToList();
    var insertAt = sources.FindLastIndex(s => s.GetType().Name.Contains("EnvironmentVariables", StringComparison.Ordinal));

    // The provider is deliberately left null so ResolveFileProvider can split the absolute path into a
    // provider rooted at the directory plus a bare file name. Setting both meant the provider was asked for
    // a rooted sub-path, which PhysicalFileProvider re-combines under its own root and never finds - and
    // because the source is optional it loaded nothing, logged nothing, and the whole documented
    // HOUSEKEEPER_CONFIG_FILE layer was discarded in silence. The file's existence is checked above, so a
    // failure to read it from here on is worth hearing about.
    var source = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
    {
        Path = full,
        Optional = false,
        ReloadOnChange = false,
    };
    source.ResolveFileProvider();

    // Below real environment variables and above appsettings.json, which is the documented precedence.
    if (insertAt >= 0) configuration.Sources.Insert(insertAt, source);
    else configuration.Sources.Add(source);
}

/// <summary>
/// Where settings.json and secrets.json live. HOUSEKEEPER_DATA_DIR wins; otherwise it is the folder the
/// database was configured into before the UI had any say.
/// </summary>
static string DataDirectoryOf(IConfiguration configuration)
{
    var explicitly = Environment.GetEnvironmentVariable("HOUSEKEEPER_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(explicitly)) return Path.GetFullPath(explicitly);

    var database = configuration["Housekeeper:Storage:Path"];
    if (string.IsNullOrWhiteSpace(database)) database = new StorageOptions().Path;

    var directory = Path.GetDirectoryName(Path.GetFullPath(database));
    return string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
}

/// <summary>The values of the restart-only settings as the process started, for spotting drift later.</summary>
static Dictionary<string, string> RestartValuesOf(HousekeeperOptions options) =>
    SettingsCatalog.Fields
        .Where(field => field.RestartRequired)
        .ToDictionary(field => field.Key, field => SettingsCatalog.Display(field, options)?.ToJsonString() ?? "");

/// <summary>Exposed so the test host can boot the real application.</summary>
public partial class Program;
