namespace Shortener.Tests;

/// <summary>
/// Starts <c>count</c> actions on the thread pool, holds them all at a gate, then releases them
/// together so they race, and waits for every one.
/// </summary>
internal static class Concurrently
{
    public static async Task<T[]> RunAsync<T>(int count, Func<int, Task<T>> action)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable
            .Range(0, count)
            .Select(i =>
                Task.Run(async () =>
                {
                    await gate.Task;
                    return await action(i);
                })
            )
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public static Task RunAsync(int count, Func<int, Task> action) =>
        RunAsync(
            count,
            async i =>
            {
                await action(i);
                return true;
            }
        );

    // A separate name on purpose: as an overload of RunAsync, a lambda returning a ValueTask
    // would bind to Action<int> and its result would be dropped without being awaited.
    public static Task RunSyncAsync(int count, Action<int> action) =>
        RunAsync(
            count,
            i =>
            {
                action(i);
                return Task.CompletedTask;
            }
        );
}
