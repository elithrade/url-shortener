using System.ComponentModel.DataAnnotations;

namespace Shortener;

// bound from the Clicks section
public sealed class ClickOptions
{
    public const string Section = "Clicks";

    // clicks the channel holds before Record drops new ones
    [Range(1, int.MaxValue)]
    public int Capacity { get; init; }

    // distinct (code, day) entries collected before a flush
    [Range(1, int.MaxValue)]
    public int MaxBatchSize { get; init; }

    // longest time collected clicks wait before a flush
    [Range(1, int.MaxValue)]
    public int FlushIntervalSeconds { get; init; }
}
