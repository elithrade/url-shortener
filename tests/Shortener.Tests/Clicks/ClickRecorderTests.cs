using Microsoft.Extensions.Time.Testing;

namespace Shortener.Tests;

public class ClickRecorderTests
{
    [Fact]
    public void Record_with_room_left_puts_item_in_the_channel()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ClickRecorder recorder = CreateRecorder(1, now);

        recorder.Record("abc");

        var read = recorder.Reader.TryRead(out var click);

        Assert.True(read);
        Assert.Equal(0, recorder.Dropped);
        Assert.NotNull(click);
        Assert.Equal(now, click.At);
        Assert.Equal("abc", click.Code);
    }

    [Fact]
    public void Record_when_full_drops_the_new_click()
    {
        ClickRecorder recorder = CreateRecorder(2);

        recorder.Record("abc");
        recorder.Record("def");

        Assert.Equal(0, recorder.Dropped);

        recorder.Record("ghi");
        Assert.Equal(1, recorder.Dropped);

        var codes = new List<string>();
        while (recorder.Reader.TryRead(out var c))
        {
            codes.Add(c.Code);
        }

        Assert.Equal(["abc", "def"], codes);
    }

    [Fact]
    public void Record_accepts_again_once_room_is_freed()
    {
        ClickRecorder recorder = CreateRecorder(2);

        recorder.Record("abc");
        recorder.Record("def");
        recorder.Record("ghi"); // this should be dropped

        Assert.Equal(1, recorder.Dropped);

        recorder.Reader.TryRead(out var first);
        Assert.Equal("abc", first?.Code);

        recorder.Record("jkl");
        Assert.Equal(1, recorder.Dropped);

        var codes = new List<string>();
        while (recorder.Reader.TryRead(out var c))
        {
            codes.Add(c.Code);
        }

        Assert.Equal(["def", "jkl"], codes);
    }

    [Fact]
    public async Task Concurrent_drops_are_all_counted()
    {
        ClickRecorder recorder = CreateRecorder(1);

        await Concurrently.RunSyncAsync(10_000, _ => recorder.Record("abc"));
        Assert.Equal(9_999, recorder.Dropped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_invalid_capacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRecorder(capacity));
    }

    private static ClickRecorder CreateRecorder(int capacity, DateTimeOffset? now = null)
    {
        var clock = new FakeTimeProvider(now ?? DateTimeOffset.UtcNow);

        return new ClickRecorder(capacity: capacity, clock);
    }
}
