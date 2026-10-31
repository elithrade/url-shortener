using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shortener;

const int MaxAttempts = 5;
const int CodeLength = 7;
const string ReadyTag = "ready";
const int DbHealthCheckTimeoutSeconds = 2;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);

bool usePostgres = RegisterStorage(builder);
RegisterRateLimiter(builder);
RegisterShortenerOptions(builder);
RegisterHealthChecks(builder, usePostgres);

var app = builder.Build();
StartupLog.UsingStorage(app.Logger, usePostgres ? "Postgres" : "InMemory");

if (args.Length > 0 && args[0] == "migrate")
{
    return await RunMigrationAsync(app, usePostgres);
}

app.MapPost("/shorten", HandlePost).AddEndpointFilter<RateLimitFilter>();
app.MapGet("/{code}", HandleGet);

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
            o.UseNpgsql(connectionString)
        );
        builder.Services.AddSingleton<PostgresUrlStore>();
        // register multiple interfaces with the same implementation
        builder.Services.AddSingleton<IUrlStore>(sp => sp.GetRequiredService<PostgresUrlStore>());
        builder.Services.AddSingleton<IExpirySweeper>(sp =>
            sp.GetRequiredService<PostgresUrlStore>()
        );
    }
    else
    {
        // register multiple interfaces with the same implementation
        builder.Services.AddSingleton<InMemoryUrlStore>();
        builder.Services.AddSingleton<IUrlStore>(sp => sp.GetRequiredService<InMemoryUrlStore>());
        builder.Services.AddSingleton<IExpirySweeper>(sp =>
            sp.GetRequiredService<InMemoryUrlStore>()
        );
    }

    builder.Services.AddHostedService<ExpirySweepService>();

    return usePostgres;
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
    ILogger<Program> logger,
    CancellationToken ct
)
{
    string? url = null;
    try
    {
        url = await store.GetAsync(code, ct);
    }
    catch (StorageUnavailableException ex)
    {
        HandlerLog.StorageUnavailable(logger, ex);
        return Results.StatusCode(503);
    }

    if (url != null)
    {
        return Results.Redirect(url);
    }

    return Results.NotFound();
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
