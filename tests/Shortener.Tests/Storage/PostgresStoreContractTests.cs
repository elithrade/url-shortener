namespace Shortener.Tests;

public class PostgresStoreContractTests(PostgresFixture fixture)
    : UrlStoreContractTests,
        IClassFixture<PostgresFixture>,
        IAsyncLifetime
{
    public async Task DisposeAsync() { }

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
