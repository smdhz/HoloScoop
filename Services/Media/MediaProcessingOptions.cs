namespace HoloScoop.Services.Media;

public sealed class MediaProcessingOptions
{
    public const string SectionName = "Media";

    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string LibraryRoot { get; set; } = "/data/library";
    public string WorkRoot { get; set; } = "/data/work";
    public string LibraryPath { get => LibraryRoot; set => LibraryRoot = value; }
    public string WorkPath { get => WorkRoot; set => WorkRoot = value; }
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromHours(2);
    public string[] SubtitleLanguages { get; set; } = ["en", "ja"];
    public string WhisperExecutablePath { get; set; } = "/usr/local/bin/whisper-cli";
    public string WhisperModelPath { get; set; } = "/opt/whisper/models/ggml-small.bin";
    public string WhisperLanguage { get; set; } = "ja";
    public int WhisperThreads { get; set; } = 12;
    public TimeSpan TranscriptionTimeout { get; set; } = TimeSpan.FromHours(12);
}
