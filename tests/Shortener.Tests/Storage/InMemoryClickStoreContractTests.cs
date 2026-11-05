namespace Shortener.Tests;

public class InMemoryClickStoreContractTests : ClickStoreContractTests
{
    protected override IClickStore CreateStore()
    {
        return new InMemoryClickStore();
    }

    // the in-memory store doesn't know about links, so there is nothing to create
    protected override Task AddLinkAsync(string code)
    {
        return Task.CompletedTask;
    }
}
