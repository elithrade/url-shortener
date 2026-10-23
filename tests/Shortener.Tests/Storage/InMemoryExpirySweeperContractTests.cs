namespace Shortener.Tests;

public class InMemoryExpirySweeperContractTests : ExpirySweeperContractTests
{
    protected override (IUrlStore Store, IExpirySweeper Sweeper) Create(TimeProvider clock)
    {
        var store = new InMemoryUrlStore(clock);

        return (store, store);
    }
}
