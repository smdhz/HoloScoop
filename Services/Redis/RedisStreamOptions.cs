namespace HoloScoop.Services.Redis;

public sealed class RedisStreamOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public string StreamName { get; set; } = "holoscoop:incoming";
    public string ConsumerGroup { get; set; } = "holoscoop";
    public string ConsumerName { get; set; } = Environment.MachineName;
    public string GroupStartPosition { get; set; } = "0-0";
    public string ProcessingStreamName { get; set; } = "holoscoop:media-tasks";
    public string ProcessingConsumerGroup { get; set; } = "holoscoop-media-workers";
    public int BatchSize { get; set; } = 10;
    public int PollDelayMilliseconds { get; set; } = 1_000;
    public int PendingIdleMilliseconds { get; set; } = 60_000;
    public int PendingRecoveryIntervalMilliseconds { get; set; } = 60_000;
    public int ProcessingPendingIdleMilliseconds { get; set; } = 86_400_000;
    public int ProcessingStreamMaxLength { get; set; } = 10_000;
    public int QueueRepairIntervalMinutes { get; set; } = 5;
    public int CandidateSyncIntervalMinutes { get; set; } = 1;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(GroupStartPosition);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProcessingStreamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProcessingConsumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PollDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(PendingIdleMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PendingRecoveryIntervalMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(ProcessingPendingIdleMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProcessingStreamMaxLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(QueueRepairIntervalMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CandidateSyncIntervalMinutes);
    }
}
