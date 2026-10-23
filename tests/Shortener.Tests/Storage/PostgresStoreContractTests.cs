namespace Shortener.Tests;

[Collection("postgres")]
public class PostgresStoreContractTests(PostgresFixture fixture)
    : UrlStoreContractTests,
        IAsyncLifetime
{
    public Task DisposeAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
    }

    protected override IUrlStore CreateStore(TimeProvider clock)
    {
        return new PostgresUrlStore(fixture.Factory, clock);
        ;
    }
}
