using HoloScoop.Data;
using HoloScoop.Services.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Pages;

public sealed class WatchModel(
    HoloScoopDbContext dbContext,
    ILocalMediaLibrary mediaLibrary) : PageModel
{
    public long StreamId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? ChannelName { get; private set; }
    public string SourceUrl { get; private set; } = string.Empty;
    public string VideoContentType { get; private set; } = string.Empty;
    public long StartSeconds { get; private set; }
    public bool HasLocalVideo { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long streamId,
        long t = 0,
        CancellationToken cancellationToken = default)
    {
        var stream = await dbContext.Streams
            .AsNoTracking()
            .Where(item => item.Id == streamId)
            .Select(item => new
            {
                item.Id,
                item.ExternalId,
                item.Title,
                item.ChannelName,
                item.SourceUrl
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        var video = mediaLibrary.FindVideo(stream.ExternalId);
        StreamId = stream.Id;
        Title = stream.Title;
        ChannelName = stream.ChannelName;
        SourceUrl = stream.SourceUrl;
        StartSeconds = Math.Max(0, t);
        HasLocalVideo = video is not null;
        VideoContentType = video?.ContentType ?? string.Empty;
        return Page();
    }
}
