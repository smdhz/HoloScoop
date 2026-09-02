namespace HoloScoop.Data.Entities;

public sealed class DownloadedVideo
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public DateTimeOffset DownloadedAt { get; set; }

    public Stream Stream { get; set; } = null!;
}
