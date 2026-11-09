namespace Shortener;

// the range is echoed back so a client can see which defaults were applied; both ends inclusive
public record StatsResponse(string Code, DateOnly Start, DateOnly End, long Clicks);
