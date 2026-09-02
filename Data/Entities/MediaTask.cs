namespace HoloScoop.Data.Entities;

public sealed class MediaTask
{
    public long Id { get; set; }
    public required string RedisStream { get; set; }
    public required string RedisMessageId { get; set; }
    public long StreamId { get; set; }
    public DownloadMode? DownloadMode { get; set; }
    public int? SpeakerCount { get; set; }
    public string? SpeakerNamesJson { get; set; }
    public string? ScheduledMemberName { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.PendingSelection;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public Stream Stream { get; set; } = null!;
}
