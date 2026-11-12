using StackExchange.Redis;

namespace Shortener;

public static class RedisConnection
{
    // The settings every cache connection uses, so a Redis outage costs a redirect almost
    // nothing. The tests build their multiplexers through this too.
    public static ConfigurationOptions Configure(string connectionString, CacheOptions cache)
    {
        var config = ConfigurationOptions.Parse(connectionString);

        // start even when Redis is down, and reconnect in the background
        config.AbortOnConnectFail = false;

        // the client's defaults are 5 s
        config.ConnectTimeout = cache.TimeoutMilliseconds;
        config.SyncTimeout = cache.TimeoutMilliseconds;
        config.AsyncTimeout = cache.TimeoutMilliseconds;

        // with no connection, fail a command at once instead of queueing it until one comes
        // back (measured: a queued command waited 5 s although asyncTimeout was 200 ms)
        config.BacklogPolicy = BacklogPolicy.FailFast;

        return config;
    }
}
