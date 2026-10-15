using Shortener;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IUrlStore, InMemoryUrlStore>();

var app = builder.Build();

const int MAX_RETRY = 5;
const int CODE_LENGTH = 7;

app.MapPost(
    "/shorten",
    async (
        ShortenedUrlRequest urlRequest,
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

        for (var i = 0; i < MAX_RETRY; i++)
        {
            var code = CodeGenerator.GenerateCode(CODE_LENGTH);

            var added = await store.TryAddAsync(code, validatedUrl.AbsoluteUri, ct);
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
