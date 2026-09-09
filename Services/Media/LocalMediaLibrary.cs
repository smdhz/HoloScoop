using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public sealed record LocalVideoFile(
    string Path,
    string ContentType,
    DateTimeOffset LastModified);

public sealed record LocalAudioFile(
    string Path,
    string ContentType,
    DateTimeOffset LastModified);

public sealed record LocalSubtitleFile(
    string FileName,
    string Path,
    string ContentType,
    DateTimeOffset LastModified);

public interface ILocalMediaLibrary
{
    LocalVideoFile? FindVideo(string externalId);
    LocalAudioFile? FindAudio(string externalId);
    IReadOnlyList<LocalSubtitleFile> FindSubtitles(string externalId);
    void DeleteMedia(string externalId);
}

public sealed class LocalMediaLibrary(IOptions<MediaProcessingOptions> options) : ILocalMediaLibrary
{
    private static readonly IReadOnlyDictionary<string, string> VideoContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".m4v"] = "video/mp4",
            [".webm"] = "video/webm",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime"
        };
    private static readonly IReadOnlyDictionary<string, string> AudioContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".aac"] = "audio/aac",
            [".flac"] = "audio/flac",
            [".m4a"] = "audio/mp4",
            [".mka"] = "audio/x-matroska",
            [".mp3"] = "audio/mpeg",
            [".ogg"] = "audio/ogg",
            [".opus"] = "audio/ogg",
            [".wav"] = "audio/wav",
            [".webm"] = "audio/webm"
        };
    private static readonly IReadOnlyDictionary<string, string> SubtitleContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".ass"] = "text/plain; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".lrc"] = "text/plain; charset=utf-8",
            [".srt"] = "application/x-subrip; charset=utf-8",
            [".ssa"] = "text/plain; charset=utf-8",
            [".tsv"] = "text/tab-separated-values; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
            [".vtt"] = "text/vtt; charset=utf-8"
        };

    private readonly MediaProcessingOptions _options = options.Value;

    public LocalVideoFile? FindVideo(string externalId)
    {
        var safeExternalId = SafeMediaPath.ValidateExternalId(externalId);
        var directory = SafeMediaPath.UnderRoot(
            _options.LibraryRoot,
            "youtube",
            safeExternalId,
            "video");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var path = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(candidate => VideoContentTypes.ContainsKey(Path.GetExtension(candidate)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null)
        {
            return null;
        }

        return new LocalVideoFile(
            path,
            VideoContentTypes[Path.GetExtension(path)],
            File.GetLastWriteTimeUtc(path));
    }

    public LocalAudioFile? FindAudio(string externalId)
    {
        var safeExternalId = SafeMediaPath.ValidateExternalId(externalId);
        var directory = SafeMediaPath.UnderRoot(
            _options.LibraryRoot,
            "youtube",
            safeExternalId,
            "audio");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var path = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(candidate => AudioContentTypes.ContainsKey(Path.GetExtension(candidate)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null)
        {
            return null;
        }

        return new LocalAudioFile(
            path,
            AudioContentTypes[Path.GetExtension(path)],
            File.GetLastWriteTimeUtc(path));
    }

    public IReadOnlyList<LocalSubtitleFile> FindSubtitles(string externalId)
    {
        var safeExternalId = SafeMediaPath.ValidateExternalId(externalId);
        var directory = SafeMediaPath.UnderRoot(
            _options.LibraryRoot,
            "youtube",
            safeExternalId,
            "subtitles");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new LocalSubtitleFile(
                Path.GetFileName(path),
                path,
                SubtitleContentTypes.GetValueOrDefault(
                    Path.GetExtension(path),
                    "application/octet-stream"),
                File.GetLastWriteTimeUtc(path)))
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void DeleteMedia(string externalId)
    {
        var safeExternalId = SafeMediaPath.ValidateExternalId(externalId);
        var directory = SafeMediaPath.UnderRoot(
            _options.LibraryRoot,
            "youtube",
            safeExternalId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
