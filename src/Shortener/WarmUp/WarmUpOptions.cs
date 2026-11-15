using System.ComponentModel.DataAnnotations;

namespace Shortener;

// bound from the WarmUp section
public sealed class WarmUpOptions
{
    public const string Section = "WarmUp";

    // store lookups made before the app reports ready; 0 turns warm-up off
    [Range(0, int.MaxValue)]
    public int Calls { get; init; }

    // lookups in flight at once, which is also roughly how many connections the pool opens
    [Range(1, int.MaxValue)]
    public int Parallelism { get; init; }

    // after this long the app reports ready even if warm-up has not finished
    [Range(1, int.MaxValue)]
    public int DeadlineSeconds { get; init; }
}
