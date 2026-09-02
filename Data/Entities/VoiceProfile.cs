namespace HoloScoop.Data.Entities;

public sealed class VoiceProfile
{
    public long Id { get; set; }
    public required string MemberName { get; set; }
    public required byte[] Embedding { get; set; }
    public int Dimension { get; set; }
    public int SampleCount { get; set; }
    public long SourceStreamId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
