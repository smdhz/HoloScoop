namespace HoloScoop.Data.Entities;

public sealed class SpeakerClusterEmbedding
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public required string SpeakerLabel { get; set; }
    public required byte[] Embedding { get; set; }
    public int Dimension { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Stream Stream { get; set; } = null!;
}
