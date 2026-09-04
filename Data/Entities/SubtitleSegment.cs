namespace HoloScoop.Data.Entities;

public sealed class SubtitleSegment
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public required string Language { get; set; }
    public required string Source { get; set; }
    public required string TrackRole { get; set; }
    public required string DeclaredLanguage { get; set; }
    public string? OriginDeclaredLanguage { get; set; }
    public required string OriginSource { get; set; }
    public int GenerationVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool NeedsReview { get; set; }
    public double? BaseTrackQuality { get; set; }
    public string? BuildStatus { get; set; }
    public string? DetectedLanguage { get; set; }
    public string? LanguageDetectionMethod { get; set; }
    public string? ModelVersion { get; set; }
    public int Sequence { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public required string Text { get; set; }
    public string? SpeakerLabel { get; set; }
    public string? SpeakerName { get; set; }
    public string? SpeakerNameSource { get; set; }
    public double? SpeakerNameScore { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Stream Stream { get; set; } = null!;
}
