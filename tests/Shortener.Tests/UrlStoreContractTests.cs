using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public abstract class UrlStoreContractTests
{
    protected abstract IUrlStore CreateStore(TimeProvider clock);

    [Theory]
    [InlineData(-1)]
    [InlineData(-3600 * 1000 * 24)] // a day
    [InlineData(0)]
    public async Task Throws_exception_when_expiresAt_is_in_the_past(int milliseconds)
    {
        var (store, now) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await store.TryAddAsync(
                "abc",
                new LinkEntry(
                    "https://example.com/",
                    now + TimeSpan.FromMilliseconds(milliseconds)
                ),
                CancellationToken.None
            );
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600 * 1000 * 24)]
    public async Task ExpiresAt_in_the_future_is_accepted(int milliseconds)
    {
        var (store, now) = Create();

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now + TimeSpan.FromMilliseconds(milliseconds)),
            CancellationToken.None
        );

        Assert.True(added);
    }

    [Fact]
    public async Task Null_expiresAt_is_accepted()
    {
        var (store, _) = Create();

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", null),
            CancellationToken.None
        );

        Assert.True(added);
    }

    [Fact]
    public async Task Existing_code_with_past_expiry_throws_instead_of_returning_false()
    {
        var (store, now) = Create();

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now + TimeSpan.FromMilliseconds(1)),
            CancellationToken.None
        );

        Assert.True(added);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", now + TimeSpan.FromMilliseconds(-1)),
                CancellationToken.None
            );
        });
    }

    [Fact]
    public async Task Code_can_be_added_after_rejected_past_expiry()
    {
        var (store, now) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await store.TryAddAsync(
                "abc",
                new LinkEntry("https://example.com/", now + TimeSpan.FromSeconds(-1)),
                CancellationToken.None
            );
        });

        var added = await store.TryAddAsync(
            "abc",
            new LinkEntry("https://example.com/", now + TimeSpan.FromSeconds(1)),
            CancellationToken.None
        );

        Assert.True(added);
    }

    private (IUrlStore Store, DateTimeOffset Now) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var store = CreateStore(clock);
        var now = clock.GetUtcNow();

        return (store, now);
    }
}
