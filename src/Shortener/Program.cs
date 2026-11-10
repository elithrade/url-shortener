using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Shortener;

const int MaxAttempts = 5;
const int CodeLength = 7;
const string ReadyTag = "ready";
const int DbHealthCheckTimeoutSeconds = 2;

// deadline budget, each layer gives up before the one calling it:
// DB connect 3 s, DB command 3 s (measured: a hung command fails after the timeout + about 6 s) < host shutdown 12 s < container grace period (Compose: 15 s)
const int DbConnectTimeoutSeconds = 3;
const int DbCommandTimeoutSeconds = 3;
const int ShutdownTimeoutSeconds = 12;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = TimeSpan.FromSeconds(ShutdownTimeoutSeconds)
);

bool usePostgres = RegisterStorage(builder);
RegisterClicks(builder);
RegisterRateLimiter(builder);
RegisterShortenerOptions(builder);
RegisterForwardedHeaders(builder);
RegisterHealthChecks(builder, usePostgres);
RegisterRequestLogging(builder);

var app = builder.Build();
StartupLog.UsingStorage(app.Logger, usePostgres ? "Postgres" : "InMemory");

if (args.Length > 0 && args[0] == "migrate")
{
    return await RunMigrationAsync(app, usePostgres);
}

// run every ValidateOnStart check now, before Run: a bad setting fails here while the host is
// still alive, instead of inside Run, which disposes the host as it fails (that raced with
// WebApplicationFactory and surfaced as ObjectDisposedException in the startup tests)
app.Services.GetRequiredService<IStartupValidator>().Validate();

// first in the pipeline, so everything after it (the rate limiter) sees the client's address
if (app.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value.Enabled)
{
    app.UseForwardedHeaders();
}

// one log line per request (method, path, status, duration); health probes would only add noise
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseHttpLogging()
);

app.MapPost("/shorten", HandlePost).AddEndpointFilter<RateLimitFilter>();
app.MapGet("/{code}", HandleGet);
app.MapGet("/{code}/stats", HandleGetStats);

// liveness runs no checks: it only proves the process can answer
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// readiness runs the dependency checks tagged "ready" (none with the in-memory store)
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }
);

app.Run();

return 0;

static bool RegisterStorage(WebApplicationBuilder builder)
{
    var storage = builder.Configuration["Storage"]?.Trim() ?? "InMemory";
    var allowed = new HashSet<string>(["InMemory", "Postgres"], StringComparer.OrdinalIgnoreCase);

    if (!allowed.Contains(storage))
    {
        throw new InvalidOperationException($"Only {string.Join(", ", allowed)} are allowed");
    }

    var usePostgres = storage.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
    if (usePostgres)
    {
        var connectionString = builder.Configuration.GetConnectionString("Shortener");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Shortener is missing");
        }

        builder.Services.AddDbContextFactory<ShortenerDbContext>(o =>
            o.UseNpgsql(
                // the app enforces its connect budget, whatever the connection string says
                new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Timeout = DbConnectTimeoutSeconds,
                }.ConnectionString,
                npgsql => npgsql.CommandTimeout(DbCommandTimeoutSeconds)
            )
        );
        builder.Services.AddSingleton<PostgresUrlStore>();
        // register multiple interfaces with the same implementation
        builder.Services.AddSingleton<IUrlStore>(sp => sp.GetRequiredService<PostgresUrlStore>());
        builder.Services.AddSingleton<IExpirySweeper>(sp =>
            sp.GetRequiredService<PostgresUrlStore>()
        );
        builder.Services.AddSingleton<IClickStore, PostgresClickStore>();
    }
    else
    {
        // register multiple interfaces with the same implementation
        builder.Services.AddSingleton<InMemoryUrlStore>();
        builder.Services.AddSingleton<IUrlStore>(sp => sp.GetRequiredService<InMemoryUrlStore>());
        builder.Services.AddSingleton<IExpirySweeper>(sp =>
            sp.GetRequiredService<InMemoryUrlStore>()
        );
        builder.Services.AddSingleton<IClickStore, InMemoryClickStore>();
    }

    builder.Services.AddHostedService<ExpirySweepService>();

    return usePostgres;
}

static void RegisterClicks(WebApplicationBuilder builder)
{
    builder
        .Services.AddOptions<ClickOptions>()
        .BindConfiguration(ClickOptions.Section)
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // one recorder for the whole app: the redirect writes to its channel, the flush service
    // reads from the same channel. A second instance would be a second, unread channel.
    builder.Services.AddSingleton(sp => new ClickRecorder(
        sp.GetRequiredService<IOptions<ClickOptions>>().Value.Capacity,
        sp.GetRequiredService<TimeProvider>()
    ));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<ClickRecorder>().Reader);
    builder.Services.AddHostedService<ClickFlushService>();
}

static void RegisterRateLimiter(WebApplicationBuilder builder)
{
    builder
        .Services.AddOptions<RateLimitOptions>()
        .BindConfiguration(RateLimitOptions.Section)
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IOptions<RateLimitOptions>>().Value.ToTokenBucketOptions()
    );
    builder.Services.AddSingleton<IRateLimiter, TokenBucketLimiter>();
}

static void RegisterShortenerOptions(WebApplicationBuilder builder)
{
    builder.Services.AddSingleton<IValidateOptions<ShortenerOptions>, ShortenerOptionsValidator>();
    builder
        .Services.AddOptions<ShortenerOptions>()
        .BindConfiguration(ShortenerOptions.Section)
        .ValidateOnStart();
}

static void RegisterForwardedHeaders(WebApplicationBuilder builder)
{
    builder.Services.AddSingleton<
        IValidateOptions<TrustedProxyOptions>,
        TrustedProxyOptionsValidator
    >();
    builder
        .Services.AddOptions<TrustedProxyOptions>()
        .BindConfiguration(TrustedProxyOptions.Section)
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // only X-Forwarded-For: Host comes from Shortener:BaseUrl, and the scheme isn't used
    builder
        .Services.AddOptions<ForwardedHeadersOptions>()
        .Configure<IOptions<TrustedProxyOptions>>(
            (forwarded, trusted) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                forwarded.ForwardLimit = trusted.Value.ForwardLimit;

                // drop the loopback defaults: trust only what config names
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                if (trusted.Value.TrustAnyProxy)
                {
                    return; // empty lists mean "trust every peer"
                }
                foreach (var network in trusted.Value.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            }
        );
}

static void RegisterRequestLogging(WebApplicationBuilder builder)
{
    // no query string, headers or bodies: the URLs users shorten can carry tokens or personal data
    builder.Services.AddHttpLogging(o =>
    {
        o.LoggingFields =
            HttpLoggingFields.RequestMethod
            | HttpLoggingFields.RequestPath
            | HttpLoggingFields.ResponseStatusCode
            | HttpLoggingFields.Duration;
        o.CombineLogs = true;
    });
}

static void RegisterHealthChecks(WebApplicationBuilder builder, bool usePostgres)
{
    var checks = builder.Services.AddHealthChecks();

    if (usePostgres)
    {
        checks.AddCheck<PostgresHealthCheck>(
            "postgres",
            tags: [ReadyTag],
            timeout: TimeSpan.FromSeconds(DbHealthCheckTimeoutSeconds)
        );
    }
}

static async Task<int> RunMigrationAsync(WebApplication app, bool usePostgres)
{
    if (!usePostgres)
    {
        StartupLog.UnableToMigrate(app.Logger);
        return 1;
    }

    var factory = app.Services.GetRequiredService<IDbContextFactory<ShortenerDbContext>>();

    return await SchemaMigrator.ExecuteAsync(factory, app.Logger);
}

static async Task<IResult> HandlePost(
    ShortenedUrlRequest urlRequest,
    TimeProvider clock,
    IUrlStore store,
    IOptions<ShortenerOptions> options,
    ILogger<Program> logger,
    CancellationToken ct
)
{
    var baseUrl = new Uri(options.Value.BaseUrl!);

    if (
        !UrlValidator.TryValidate(
            urlRequest.Url,
            baseUrl.Host,
            out Uri? validatedUrl,
            out string? error
        )
    )
    {
        return Results.BadRequest(error);
    }

    if (
        !ExpiryPolicy.TryResolve(
            urlRequest.ExpiresInSeconds,
            clock.GetUtcNow(),
            out DateTimeOffset? expiryAt,
            out error
        )
    )
    {
        return Results.BadRequest(error);
    }

    for (var i = 0; i < MaxAttempts; i++)
    {
        var code = CodeGenerator.GenerateCode(CodeLength);

        var linkEntry = new LinkEntry(validatedUrl.AbsoluteUri, expiryAt);

        bool added;
        try
        {
            added = await store.TryAddAsync(code, linkEntry, ct);
        }
        catch (StorageUnavailableException ex)
        {
            HandlerLog.StorageUnavailable(logger, ex);

            return Results.StatusCode(503);
        }

        if (added)
        {
            var shortUrl = new Uri(baseUrl, code).AbsoluteUri;

            return Results.Created(shortUrl, new ShortenResponse(code, shortUrl));
        }
    }

    return Results.StatusCode(503);
}

static async Task<IResult> HandleGet(
    string code,
    IUrlStore store,
    ClickRecorder recorder,
    ILogger<Program> logger,
    CancellationToken ct
)
{
    LinkEntry? link = null;
    try
    {
        link = await store.GetAsync(code, ct);
    }
    catch (StorageUnavailableException ex)
    {
        HandlerLog.StorageUnavailable(logger, ex);
        return Results.StatusCode(503);
    }

    if (link != null)
    {
        recorder.Record(code);
        return Results.Redirect(link.Url);
    }

    return Results.NotFound();
}

static async Task<IResult> HandleGetStats(
    string code,
    string? start,
    string? end,
    IClickStore store,
    TimeProvider clock,
    ILogger<Program> logger,
    CancellationToken ct
)
{
    // parsed here, not bound as DateOnly?: binding fails with a framework message (the developer
    // page in Development) and accepts other formats such as 10/01/2026
    if (!TryParseDay(start, out var startDay) || !TryParseDay(end, out var endDay))
    {
        return Results.BadRequest("start and end must be dates in the format yyyy-MM-dd");
    }

    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    endDay ??= today;
    startDay ??= endDay.Value.AddDays(-29);

    if (startDay > endDay)
    {
        return Results.BadRequest("Start must not be after end");
    }

    if (endDay.Value.AddYears(-1) > startDay)
    {
        return Results.BadRequest("The range must be at most one year");
    }

    try
    {
        var count = await store.GetAsync(code, startDay.Value, endDay.Value, ct);
        return Results.Ok(new StatsResponse(code, startDay.Value, endDay.Value, count));
    }
    catch (StorageUnavailableException ex)
    {
        HandlerLog.StorageUnavailable(logger, ex);
        return Results.StatusCode(503);
    }
}

// a missing or empty value is valid (it gets a default, as with ?start=); any other must be
// exactly yyyy-MM-dd
static bool TryParseDay(string? value, out DateOnly? day)
{
    day = null;
    if (string.IsNullOrEmpty(value))
    {
        return true;
    }

    if (
        !DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed
        )
    )
    {
        return false;
    }

    day = parsed;
    return true;
}

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Using {Storage} storage")]
    public static partial void UsingStorage(ILogger logger, string storage);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Migration can only run on Postgres storage type"
    )]
    public static partial void UnableToMigrate(ILogger logger);
}

internal static partial class HandlerLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Storage unavailable")]
    public static partial void StorageUnavailable(ILogger logger, Exception exception);
}
