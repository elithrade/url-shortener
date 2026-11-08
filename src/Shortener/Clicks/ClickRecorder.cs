using System.Threading.Channels;

namespace Shortener;

public sealed class ClickRecorder
{
    private readonly Channel<Click> _channel;
    private readonly TimeProvider _clock;

    private long _dropped;

    public ClickRecorder(int capacity, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0, nameof(capacity));

        _clock = clock;
        _channel = Channel.CreateBounded<Click>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );
    }

    public void Record(string code)
    {
        if (!_channel.Writer.TryWrite(new Click(code, _clock.GetUtcNow())))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public ChannelReader<Click> Reader => _channel.Reader;
}
