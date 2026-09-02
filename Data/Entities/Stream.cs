namespace HoloScoop.Data.Entities;

public sealed class Stream
{
    public long Id { get; set; }
    public required string Platform { get; set; }
    public required string ExternalId { get; set; }
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public required string SourceUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MediaTask> Tasks { get; } = new List<MediaTask>();
    public ICollection<SubtitleSegment> SubtitleSegments { get; } = new List<SubtitleSegment>();
    public ICollection<SpeakerTurn> SpeakerTurns { get; } = new List<SpeakerTurn>();
}
