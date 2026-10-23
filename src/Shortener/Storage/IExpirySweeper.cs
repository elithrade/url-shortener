namespace Shortener;

public interface IExpirySweeper
{
    ValueTask<int> SweepAsync(CancellationToken ct);
}
