namespace Shortener.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

public sealed class RemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(
                async (context, nextMiddleware) =>
                {
                    var testClientIp = context
                        .Request.Headers["X-Test-Client-Ip"]
                        .SingleOrDefault();

                    if (!string.IsNullOrEmpty(testClientIp))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(testClientIp);
                    }

                    await nextMiddleware();
                }
            );
            next(app);
        };
    }
}
