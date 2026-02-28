namespace Companion.Core;

public sealed class LogEvent
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required string Level { get; init; }

    public required string EventName { get; init; }

    public Dictionary<string, object?> Data { get; init; } = new();
}
