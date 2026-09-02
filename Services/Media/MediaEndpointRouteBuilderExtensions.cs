using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Services.Media;

public static class MediaEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/streams/{streamId:long}/video", GetVideoAsync);
        return endpoints;
    }

    private static async Task<IResult> GetVideoAsync(
        long streamId,
        HoloScoopDbContext dbContext,
        ILocalMediaLibrary mediaLibrary,
        CancellationToken cancellationToken)
    {
        var externalId = await dbContext.Streams
            .AsNoTracking()
            .Where(stream => stream.Id == streamId)
            .Select(stream => stream.ExternalId)
            .SingleOrDefaultAsync(cancellationToken);
        if (externalId is null)
        {
            return Results.NotFound();
        }

        var video = mediaLibrary.FindVideo(externalId);
        return video is null
            ? Results.NotFound()
            : Results.File(
                video.Path,
                video.ContentType,
                enableRangeProcessing: true,
                lastModified: video.LastModified);
    }
}
