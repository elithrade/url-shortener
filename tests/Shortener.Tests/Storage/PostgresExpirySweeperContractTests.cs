namespace Shortener.Tests;

[Collection("postgres")]
public class PostgresExpirySweeperContractTests(PostgresFixture fixture)
    : ExpirySweeperContractTests,
        IAsyncLifetime
{
    public Task DisposeAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
    }

    protected override (IUrlStore Store, IExpirySweeper Sweeper) Create(TimeProvider clock)
    {
        var store = new PostgresUrlStore(fixture.Factory, clock);

        return (store, store);
    }
}
