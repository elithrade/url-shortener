namespace Shortener.Tests;

// The health endpoints while the real app warms up. Hold warm-up open with a store whose
// GetAsync waits until the test releases it, substituted through WithWebHostBuilder (seed,
// call and read through that same derived factory). Settings go in with UseSetting, for
// example "WarmUp:Calls".
//
// Before writing these, check the existing tests that expect /health/ready to return 200
// (HealthEndpointTests, PostgresHealthEndpointTests): with warm-up on they may race it.
public class WarmUpEndpointTests
{
    [Fact(Skip = "todo")]
    public async Task Ready_is_503_while_warm_up_runs() { }

    [Fact(Skip = "todo")]
    public async Task Ready_is_200_after_warm_up_finishes() { }

    // the process is fine while warming, so liveness must not fail (it would get it restarted)
    [Fact(Skip = "todo")]
    public async Task Live_is_200_while_warm_up_runs() { }

    [Fact(Skip = "todo")]
    public async Task Ready_is_200_at_once_when_warm_up_is_off() { }

    // warm-up only delays readiness; a request that arrives early is still answered
    [Fact(Skip = "todo")]
    public async Task Redirects_work_while_warm_up_runs() { }
}
