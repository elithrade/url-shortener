using Microsoft.EntityFrameworkCore;
using Shortener;

var builder = WebApplication.CreateBuilder(args);

var storage = builder.Configuration["Storage"]?.Trim() ?? "InMemory";
var allowed = new HashSet<string>(["InMemory", "Postgres"], StringComparer.OrdinalIgnoreCase);

if (!allowed.Contains(storage))
    throw new InvalidOperationException($"Only {string.Join(", ", allowed)} are allowed");

var usePostgres = storage.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
if (usePostgres)
{
    var connectionString = builder.Configuration.GetConnectionString("Shortener");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("ConnectionStrings:Shortener is missing");

    builder.Services.AddDbContextFactory<ShortenerDbContext>(o => o.UseNpgsql(connectionString));
    builder.Services.AddSingleton<PostgresUrlStore>();
    // register multiple interfaces with the same implementation
    builder.Services.AddSingleton<IUrlStore>(sp => sp.GetRequiredService<PostgresUrlStore>());
    builder.Services.AddSingleton<IExpirySweeper>(sp => sp.GetRequiredService<PostgresUrlStore>());
    builder.Services.AddSingleton(TimeProvider.System);
}
else
{
    // register multiple interfaces with the same implementation
    builder.Services.AddSingleton<InMemoryUrlStore>();
    builder.Services.AddSingleton<IUrlStore>(sp => sp.GetRequiredService<InMemoryUrlStore>());
    builder.Services.AddSingleton<IExpirySweeper>(sp => sp.GetRequiredService<InMemoryUrlStore>());
    builder.Services.AddSingleton(TimeProvider.System);
}

builder.Services.AddHostedService<ExpirySweepService>();

var app = builder.Build();
StartupLog.UsingStorage(app.Logger, usePostgres ? "Postgres" : "InMemory");

if (args.Length > 0 && args[0] == "migrate")
{
    if (usePostgres)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ShortenerDbContext>>();
        var exitCode = await SchemaMigrator.ExecuteAsync(factory, app.Logger);

        return exitCode;
    }

    StartupLog.UnableToMigrate(app.Logger);

    return 1;
}

const int MaxAttempts = 5;
const int CodeLength = 7;

app.MapPost(
    "/shorten",
    async (
        ShortenedUrlRequest urlRequest,
        TimeProvider clock,
        IUrlStore store,
        HttpContext ctx,
        CancellationToken ct
    ) =>
    {
        if (
            !UrlValidator.TryValidate(
                urlRequest.Url,
                ctx.Request.Host.Host,
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
                HandlerLog.StorageUnavailable(app.Logger, ex);
                return Results.StatusCode(503);
            }

            if (added)
            {
                var request = ctx.Request;
                var shortUrl = $"{request.Scheme}://{request.Host}/{code}";

                return Results.Created(shortUrl, new ShortenResponse(code, shortUrl));
            }
        }

        return Results.StatusCode(503);
    }
);

app.MapGet(
    "/{code}",
    async (string code, IUrlStore store, CancellationToken ct) =>
    {
        string? url = null;
        try
        {
            url = await store.GetAsync(code, ct);
        }
        catch (StorageUnavailableException ex)
        {
            HandlerLog.StorageUnavailable(app.Logger, ex);
            return Results.StatusCode(503);
        }

        if (url != null)
        {
            return Results.Redirect(url);
        }

        return Results.NotFound();
    }
);

app.Run();

return 0;

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
