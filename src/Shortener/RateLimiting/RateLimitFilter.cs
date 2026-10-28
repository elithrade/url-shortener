using System.Globalization;

namespace Shortener;

public sealed class RateLimitFilter(IRateLimiter rateLimiter, ILogger<RateLimitFilter> logger)
    : IEndpointFilter
{
    private readonly IRateLimiter _rateLimiter = rateLimiter;
    private readonly ILogger<RateLimitFilter> _logger = logger;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var address = context.HttpContext.Connection.RemoteIpAddress;

        if (address is null)
        {
            RateLimitLog.MissingClientAddress(_logger, RateLimitKey.Unknown);
        }

        var key = RateLimitKey.For(address);
        var result = await _rateLimiter.AcquireAsync(key, context.HttpContext.RequestAborted);

        if (result.IsAllowed)
        {
            return await next(context);
        }

        var seconds = (int)Math.Ceiling(result.RetryAfter.TotalSeconds);

        context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(
            CultureInfo.InvariantCulture
        );

        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}

internal static partial class RateLimitLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Client address is missing, so the request shares the {Key} rate limit with all other requests without one"
    )]
    public static partial void MissingClientAddress(ILogger logger, string key);
}
