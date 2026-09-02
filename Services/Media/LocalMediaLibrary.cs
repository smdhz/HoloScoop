using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public sealed record LocalVideoFile(
    string Path,
    string ContentType,
    DateTimeOffset LastModified);

public interface ILocalMediaLibrary
{
    LocalVideoFile? FindVideo(string externalId);
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
}
