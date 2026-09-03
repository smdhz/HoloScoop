namespace HoloScoop.Services.Redis;

public sealed class RedisStreamOptions
{
    public const string SectionName = "Redis";

    public bool Enabled { get; set; } = true;
    public string ConnectionString { get; set; } = "localhost:6379";
    public string StreamKey { get; set; } = "holoscoop:incoming";
    // Compatibility with the existing Redis__StreamName deployment variable.
    public string StreamName { get => StreamKey; set => StreamKey = value; }
    public string ConsumerGroup { get; set; } = "holoscoop";
    public string ConsumerName { get; set; } = Environment.MachineName;
    public string GroupStartPosition { get; set; } = "0-0";
    public int BatchSize { get; set; } = 10;
    public int PollDelayMilliseconds { get; set; } = 1_000;
    public int PendingIdleMilliseconds { get; set; } = 60_000;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(GroupStartPosition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(PollDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(PendingIdleMilliseconds);
    }
}
