namespace Shortener.Tests;

public class InMemoryStoreContractTests : UrlStoreContractTests
{
    protected override IUrlStore CreateStore(TimeProvider clock)
    {
        return new InMemoryUrlStore(clock);
    }
}
