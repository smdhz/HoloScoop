using HoloScoop.Data.Entities;

namespace HoloScoop.Services.Media;

public sealed record MediaDownloadRequest(
    long TaskId,
    string ExternalId,
    Uri SourceUrl,
    DownloadMode Mode);

public sealed record DownloadedSubtitle(
    string RelativePath,
    string Language,
    string Source);

public sealed record MediaDownloadResult(
    string ExternalId,
    string? MetadataRelativePath,
    IReadOnlyList<DownloadedSubtitle> Subtitles,
    IReadOnlyList<string> VideoRelativePaths,
    IReadOnlyList<string> ThumbnailRelativePaths);

public interface IMediaDownloader
{
    Task<MediaDownloadResult> DownloadAsync(
        MediaDownloadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MediaDownloadException : Exception
{
    public MediaDownloadException(string message, int? exitCode = null, string? standardError = null)
        : base(IncludeStandardError(message, standardError))
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int? ExitCode { get; }
    public string? StandardError { get; }

    private static string IncludeStandardError(string message, string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return message;
        }

        return $"{message}{Environment.NewLine}{Environment.NewLine}yt-dlp stderr:{Environment.NewLine}{standardError.Trim()}";
    }
}
