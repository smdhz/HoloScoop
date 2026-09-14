using HoloScoop.Data;
using HoloScoop.Services.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Pages.Videos;

public sealed class IndexModel(
    HoloScoopDbContext dbContext,
    ILocalMediaLibrary mediaLibrary) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string Query { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<DownloadedItem> VideoDownloads { get; private set; } = [];
    public IReadOnlyList<DownloadedItem> AudioDownloads { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    private const int PageSize = 24;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Query = Query.Trim();
        var downloads = dbContext.Streams
            .AsNoTracking()
            .Where(stream =>
                stream.DownloadedVideos.Any() ||
                stream.SubtitleSegments.Any())
            .AsQueryable();

        if (Query.Length > 0)
        {
            downloads = downloads.Where(stream =>
                stream.Title.Contains(Query) ||
                (stream.ChannelName != null && stream.ChannelName.Contains(Query)) ||
                stream.ExternalId.Contains(Query));
        }

        var rows = await downloads
            .OrderByDescending(stream => stream.UpdatedAt)
            .Select(stream => new
            {
                StreamId = stream.Id,
                stream.Title,
                stream.ChannelName,
                stream.ExternalId,
                stream.SourceUrl,
                stream.ThumbnailUrl,
                SavedAt = stream.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var availableDownloads = rows
            .Select(row => new DownloadedItem(
                row.StreamId,
                row.Title,
                row.ChannelName,
                row.ExternalId,
                row.SourceUrl,
                row.ThumbnailUrl,
                row.SavedAt,
                mediaLibrary.FindVideo(row.ExternalId) is not null,
                mediaLibrary.FindAudio(row.ExternalId) is not null,
                mediaLibrary.FindSubtitles(row.ExternalId)
                    .Select(file => new SubtitleFileItem(file.FileName))
                    .ToArray()))
            .Where(item => item.HasVideo || item.HasAudio)
            .ToList();

        TotalCount = availableDownloads.Count;
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        var page = availableDownloads
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        VideoDownloads = page.Where(item => item.HasVideo).ToList();
        AudioDownloads = page.Where(item => !item.HasVideo && item.HasAudio).ToList();
    }

    public sealed record DownloadedItem(
        long StreamId,
        string Title,
        string? ChannelName,
        string ExternalId,
        string SourceUrl,
        string? ThumbnailUrl,
        DateTimeOffset SavedAt,
        bool HasVideo,
        bool HasAudio,
        IReadOnlyList<SubtitleFileItem> SubtitleFiles);

    public sealed record SubtitleFileItem(string FileName);
}
