namespace PhoneLinkPC.Core.Models;

public sealed record CallHistoryEntry
{
    public required Guid Id { get; init; }
    public string? PhoneNumber { get; init; }
    public string? ContactName { get; init; }
    public CallDirection Direction { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public CallHistoryStatus Status { get; init; }
}
