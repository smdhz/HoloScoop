using HoloScoop.Data;
using HoloScoop.Services.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Pages;

public sealed class IndexModel(
    HoloScoopDbContext dbContext,
    IOptions<MediaProcessingOptions> mediaOptions,
    ILocalMediaLibrary mediaLibrary) : PageModel
{
    private static readonly IReadOnlyDictionary<string, string> ThumbnailContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

    private readonly MediaProcessingOptions _mediaOptions = mediaOptions.Value;

    public IReadOnlyList<RecentProject> RecentProjects { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var completedTasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Stream)
            .Where(task => task.Status == MediaTaskStatus.Completed)
            .OrderByDescending(task => task.UpdatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        RecentProjects = completedTasks
            .DistinctBy(task => task.StreamId)
            .Take(12)
            .Select(task => new RecentProject(
                task.StreamId,
                task.Stream.Title,
                task.Stream.ChannelName,
                task.Stream.SourceUrl,
                task.Stream.ThumbnailUrl,
                task.UpdatedAt,
                HasLocalThumbnail(task.Stream.ExternalId),
                mediaLibrary.FindVideo(task.Stream.ExternalId) is not null))
            .ToList();
    }

    public async Task<IActionResult> OnGetThumbnailAsync(
        long streamId,
        CancellationToken cancellationToken)
    {
        var externalId = await dbContext.Streams
            .AsNoTracking()
            .Where(stream => stream.Id == streamId)
            .Select(stream => stream.ExternalId)
            .SingleOrDefaultAsync(cancellationToken);
        if (externalId is null)
        {
            return NotFound();
        }

        var thumbnailPath = FindLocalThumbnail(externalId);
        if (thumbnailPath is null ||
            !ThumbnailContentTypes.TryGetValue(Path.GetExtension(thumbnailPath), out var contentType))
        {
            return NotFound();
        }

        return PhysicalFile(thumbnailPath, contentType);
    }

    private bool HasLocalThumbnail(string externalId) => FindLocalThumbnail(externalId) is not null;

    private string? FindLocalThumbnail(string externalId)
    {
        var safeExternalId = SafeMediaPath.ValidateExternalId(externalId);
        var directory = SafeMediaPath.UnderRoot(
            _mediaOptions.LibraryRoot,
            "youtube",
            safeExternalId,
            "thumbnails");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ThumbnailContentTypes.ContainsKey(Path.GetExtension(path)))
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public sealed record RecentProject(
        long StreamId,
        string Title,
        string? ChannelName,
        string SourceUrl,
        string? RemoteThumbnailUrl,
        DateTimeOffset CompletedAt,
        bool HasLocalThumbnail,
        bool HasLocalVideo);
}
