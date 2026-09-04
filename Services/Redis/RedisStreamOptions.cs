namespace HoloScoop.Services.Redis;

public sealed class RedisStreamOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public string StreamName { get; set; } = "holoscoop:incoming";
    public string ConsumerGroup { get; set; } = "holoscoop";
    public string ConsumerName { get; set; } = Environment.MachineName;
    public string GroupStartPosition { get; set; } = "0-0";
    public int BatchSize { get; set; } = 10;
    public int PollDelayMilliseconds { get; set; } = 1_000;
    public int PendingIdleMilliseconds { get; set; } = 60_000;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(GroupStartPosition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(PollDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(PendingIdleMilliseconds);
    }
}
