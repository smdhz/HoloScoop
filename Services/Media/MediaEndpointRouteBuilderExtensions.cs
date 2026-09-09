using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Services.Media;

public static class MediaEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/streams/{streamId:long}/video", GetVideoAsync);
        endpoints.MapGet("/api/streams/{streamId:long}/audio", GetAudioAsync);
        endpoints.MapGet(
            "/api/streams/{streamId:long}/subtitles/{fileName}",
            GetSubtitleAsync);
        return endpoints;
    }

    private static async Task<IResult> GetVideoAsync(
        long streamId,
        HoloScoopDbContext dbContext,
        ILocalMediaLibrary mediaLibrary,
        CancellationToken cancellationToken,
        bool download = false)
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
                fileDownloadName: download ? Path.GetFileName(video.Path) : null,
                enableRangeProcessing: true,
                lastModified: video.LastModified);
    }

    private static async Task<IResult> GetAudioAsync(
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

        var audio = mediaLibrary.FindAudio(externalId);
        return audio is null
            ? Results.NotFound()
            : Results.File(
                audio.Path,
                audio.ContentType,
                enableRangeProcessing: true,
                lastModified: audio.LastModified);
    }

    private static async Task<IResult> GetSubtitleAsync(
        long streamId,
        string fileName,
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

        var subtitle = mediaLibrary.FindSubtitles(externalId)
            .SingleOrDefault(candidate =>
                string.Equals(candidate.FileName, fileName, StringComparison.Ordinal));
        return subtitle is null
            ? Results.NotFound()
            : Results.File(
                subtitle.Path,
                subtitle.ContentType,
                fileDownloadName: subtitle.FileName,
                lastModified: subtitle.LastModified);
    }
}
