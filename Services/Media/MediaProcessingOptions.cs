namespace HoloScoop.Services.Media;

public sealed class MediaProcessingOptions
{
    public const string SectionName = "Media";

    public string YtDlpPath { get; set; } = "yt-dlp";
    public string LibraryRoot { get; set; } = "/data/library";
    public string WorkRoot { get; set; } = "/data/work";
    public string LibraryPath { get => LibraryRoot; set => LibraryRoot = value; }
    public string WorkPath { get => WorkRoot; set => WorkRoot = value; }
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromHours(2);
    public string[] SubtitleLanguages { get; set; } = ["ja", "en", "zh-Hans", "zh-Hant"];
}
