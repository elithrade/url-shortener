using Microsoft.Extensions.Options;

namespace Shortener;

// Makes WarmUpOptions.Calls store lookups at startup, at most Parallelism at once, so the app
// reports ready with its code warmed up and pool connections open. Reports ready anyway after
// DeadlineSeconds.
// No exception may leave ExecuteAsync: a BackgroundService that throws stops the host.
// Registered as a singleton that the host runs, so WarmUpHealthCheck reads the same instance.
public sealed partial class WarmUpService(
    IUrlStore store,
    TimeProvider clock,
    IOptions<WarmUpOptions> options,
    ILogger<WarmUpService> logger
) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    // completes once every call has finished or the deadline has passed; the readiness check
    // reads Ready.IsCompleted
    public Task Ready => _ready.Task;

    // a code no link can have: CodeGenerator only uses letters and digits
    public const string Code = "~warmup";

    // the wait before trying a failed lookup again
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(50);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var deadline = TimeSpan.FromSeconds(opts.DeadlineSeconds);
        using var deadlineSource = new CancellationTokenSource(deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            deadlineSource.Token
        );
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, opts.Calls),
                new ParallelOptions()
                {
                    MaxDegreeOfParallelism = opts.Parallelism,
                    CancellationToken = linked.Token,
                },
                async (_, callToken) =>
                {
                    var success = false;
                    while (!success)
                    {
                        try
                        {
                            await store.GetAsync(Code, callToken);
                            success = true;
                        }
                        catch (Exception) when (!callToken.IsCancellationRequested)
                        {
                            // lookup failed, keep trying
                            await Task.Delay(RetryPause, clock, callToken);
                        }
                    }
                }
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown, not error
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            logger.LogDeadlinePassed(deadline);
        }
        catch (Exception ex)
        {
            logger.LogWarmUpFailed(ex);
        }
        finally
        {
            _ready.SetResult();
        }
    }
}

internal static partial class WarmUpServiceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Warm-up deadline of {Deadline} passed before all calls finished"
    )]
    public static partial void LogDeadlinePassed(
        this ILogger<WarmUpService> logger,
        TimeSpan deadline
    );

    [LoggerMessage(Level = LogLevel.Warning, Message = "Warm-up calls failed")]
    public static partial void LogWarmUpFailed(this ILogger<WarmUpService> logger, Exception ex);
}
