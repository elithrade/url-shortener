using Shortener;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IUrlStore, InMemoryUrlStore>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

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

            var added = await store.TryAddAsync(code, linkEntry, ct);
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
        var url = await store.GetAsync(code, ct);
        if (url != null)
        {
            return Results.Redirect(url);
        }

        return Results.NotFound();
    }
);

app.Run();
