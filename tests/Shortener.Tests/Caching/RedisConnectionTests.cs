using StackExchange.Redis;

namespace Shortener.Tests;

// The settings that keep a Redis outage from slowing redirects. Checked directly, because in a
// timing test fail-fast and the short timeouts hide each other (removing either alone still
// passes).
public class RedisConnectionTests
{
    private static ConfigurationOptions Configure(int timeoutMilliseconds = 150) =>
        RedisConnection.Configure(
            "localhost:6379",
            new CacheOptions { TimeoutMilliseconds = timeoutMilliseconds }
        );

    [Fact]
    public void The_app_starts_without_redis()
    {
        Assert.False(Configure().AbortOnConnectFail);
    }

    [Fact]
    public void Every_timeout_comes_from_the_cache_options()
    {
        var config = Configure(timeoutMilliseconds: 150);

        Assert.Equal(150, config.ConnectTimeout);
        Assert.Equal(150, config.SyncTimeout);
        Assert.Equal(150, config.AsyncTimeout);
    }

    [Fact]
    public void Commands_fail_fast_when_there_is_no_connection()
    {
        Assert.Same(BacklogPolicy.FailFast, Configure().BacklogPolicy);
    }

    [Fact]
    public void The_endpoint_comes_from_the_connection_string()
    {
        var endpoint = Assert.Single(Configure().EndPoints);

        Assert.Equal("localhost:6379", endpoint.ToString()?.Replace("Unspecified/", ""));
    }
}
