namespace Shortener;

// One row per code per UTC day; Count is the running total that the upsert adds to.
public sealed class ClickStatRecord
{
    public required string Code { get; set; }
    public DateOnly Day { get; set; }
    public long Count { get; set; }
}
