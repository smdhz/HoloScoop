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
    public string WhisperExecutablePath { get; set; } = "/usr/local/bin/whisper-cli";
    public string WhisperModelPath { get; set; } = "/opt/whisper/models/ggml-small.bin";
    public string WhisperLanguage { get; set; } = "ja";
    public int WhisperMaxContext { get; set; } = 64;
    public bool WhisperUseVad { get; set; } = true;
    public string WhisperVadModelPath { get; set; } = "/opt/whisper/models/ggml-silero-v6.2.0.bin";
    public double WhisperVadThreshold { get; set; } = 0.5;
    public int WhisperVadMinSpeechDurationMs { get; set; } = 250;
    public int WhisperVadMinSilenceDurationMs { get; set; } = 500;
    public double WhisperVadMaxSpeechDurationSeconds { get; set; } = 30;
    public int WhisperVadSpeechPadMs { get; set; } = 200;
    public double WhisperVadSamplesOverlapSeconds { get; set; } = 0.1;
    public bool WhisperSuppressNonSpeechTokens { get; set; } = true;
    // whisper.cpp generally scales best around the number of physical cores.
    // Environment.ProcessorCount reports the logical processors available to
    // this process (including container CPU limits), so assume two-way SMT for
    // the automatic default. Media__WhisperThreads can still override it.
    public int WhisperThreads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public TimeSpan TranscriptionTimeout { get; set; } = TimeSpan.FromHours(12);
}
