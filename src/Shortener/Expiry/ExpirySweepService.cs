namespace Shortener;

public sealed partial class ExpirySweepService(
    IExpirySweeper sweeper,
    TimeProvider clock,
    ILogger<ExpirySweepService> logger
) : BackgroundService
{
    private readonly IExpirySweeper _sweeper = sweeper;
    private readonly TimeProvider _clock = clock;

    const int DefaultIntervalSeconds = 60;

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Count} entries")]
    private partial void LogSwept(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred when sweeping")]
    private partial void LogSweepFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancel signalled, exiting...")]
    private partial void LogStopping();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(DefaultIntervalSeconds), _clock);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var swept = await _sweeper.SweepAsync(stoppingToken);
                    if (swept > 0)
                    {
                        LogSwept(swept);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSweepFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopping();
            return;
        }
    }
}
