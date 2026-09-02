using HoloScoop.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Pages.Videos;

public sealed class IndexModel(HoloScoopDbContext dbContext) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string Query { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<DownloadedVideoItem> Videos { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    private const int PageSize = 24;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Query = Query.Trim();
        var videos = dbContext.DownloadedVideos
            .AsNoTracking()
            .Include(video => video.Stream)
            .AsQueryable();

        if (Query.Length > 0)
        {
            videos = videos.Where(video =>
                video.Stream.Title.Contains(Query) ||
                (video.Stream.ChannelName != null && video.Stream.ChannelName.Contains(Query)) ||
                video.Stream.ExternalId.Contains(Query));
        }

        TotalCount = await videos.CountAsync(cancellationToken);
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Videos = await videos
            .OrderByDescending(video => video.DownloadedAt)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(video => new DownloadedVideoItem(
                video.StreamId,
                video.Stream.Title,
                video.Stream.ChannelName,
                video.Stream.ExternalId,
                video.Stream.SourceUrl,
                video.Stream.ThumbnailUrl,
                video.DownloadedAt))
            .ToListAsync(cancellationToken);
    }

    public sealed record DownloadedVideoItem(
        long StreamId,
        string Title,
        string? ChannelName,
        string ExternalId,
        string SourceUrl,
        string? ThumbnailUrl,
        DateTimeOffset DownloadedAt);
}
