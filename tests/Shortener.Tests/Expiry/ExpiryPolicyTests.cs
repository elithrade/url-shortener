namespace Shortener.Tests;

public class ExpiryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, 2_592_000)] // omitted -> 30 days (30 x 86,400)
    [InlineData(3600, 3600)] // 1 hour
    [InlineData(60, 60)] // exactly the minimum
    [InlineData(31_536_000, 31_536_000)] // exactly the maximum
    public void Resolves_the_expiry(int? expiresInSeconds, int expectedSeconds)
    {
        var ok = ExpiryPolicy.TryResolve(expiresInSeconds, Now, out var expiresAt, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(Now.AddSeconds(expectedSeconds), expiresAt);
    }

    [Theory]
    [InlineData(59)]
    [InlineData(31_536_001)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Rejects_out_of_range_values(int expireInSeconds)
    {
        Assert.False(
            ExpiryPolicy.TryResolve(expireInSeconds, Now, out var expiresAt, out var error)
        );
        Assert.Null(expiresAt);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
