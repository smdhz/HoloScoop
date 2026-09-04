namespace HoloScoop.Data.Entities;

public sealed class SpeakerTurn
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public required string SpeakerLabel { get; set; }
    public string? SpeakerName { get; set; }
    public string? SpeakerNameSource { get; set; }
    public double? SpeakerNameScore { get; set; }
    public string? SuggestedSpeakerName { get; set; }
    public double? SuggestedSpeakerScore { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Stream Stream { get; set; } = null!;
}
