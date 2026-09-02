using HoloScoop.Data;
using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MediaStream = HoloScoop.Data.Entities.Stream;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Services.Redis;

public sealed class EfIncomingTaskStore(HoloScoopDbContext dbContext) : IIncomingTaskStore
{
    public async Task<CandidateSaveResult> SaveCandidateAsync(
        string redisStream,
        string redisMessageId,
        IncomingStreamMessage message,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        if (await TaskExistsAsync(redisStream, redisMessageId, cancellationToken).ConfigureAwait(false))
            return CandidateSaveResult.AlreadyExists;

        var stream = await dbContext.Streams.SingleOrDefaultAsync(
            item => item.Platform == message.Platform && item.ExternalId == message.ExternalId,
            cancellationToken).ConfigureAwait(false);

        if (stream is null)
        {
            stream = MapStream(message);
            dbContext.Streams.Add(stream);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Another consumer may have discovered the same media concurrently.
                dbContext.Entry(stream).State = EntityState.Detached;
                stream = await dbContext.Streams.SingleAsync(
                    item => item.Platform == message.Platform && item.ExternalId == message.ExternalId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            ApplyMetadata(stream, message);
        }

        dbContext.Tasks.Add(new MediaTask
        {
            RedisStream = redisStream,
            RedisMessageId = redisMessageId,
            Stream = stream,
            Status = MediaTaskStatus.PendingSelection,
            ExpiresAt = expiresAt
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return CandidateSaveResult.Created;
        }
        catch (DbUpdateException)
        {
            // (RedisStream, RedisMessageId) is the authoritative idempotency key.
            dbContext.ChangeTracker.Clear();
            if (await TaskExistsAsync(redisStream, redisMessageId, cancellationToken).ConfigureAwait(false))
                return CandidateSaveResult.AlreadyExists;
            throw;
        }
    }

    private Task<bool> TaskExistsAsync(string stream, string messageId, CancellationToken cancellationToken) =>
        dbContext.Tasks.AsNoTracking().AnyAsync(
            task => task.RedisStream == stream && task.RedisMessageId == messageId,
            cancellationToken);

    private static MediaStream MapStream(IncomingStreamMessage message) => new()
    {
        Platform = message.Platform,
        ExternalId = message.ExternalId,
        SourceUrl = message.SourceUrl,
        Title = message.Title,
        ChannelId = message.ChannelId,
        ChannelName = message.ChannelName,
        Description = message.Description,
        ThumbnailUrl = message.ThumbnailUrl,
        ScheduledAt = message.ScheduledAt,
        StartedAt = message.StartedAt,
        EndedAt = message.EndedAt,
        DurationMs = message.DurationMs
    };

    private static void ApplyMetadata(MediaStream stream, IncomingStreamMessage message)
    {
        stream.SourceUrl = message.SourceUrl;
        stream.Title = message.Title;
        stream.ChannelId = message.ChannelId ?? stream.ChannelId;
        stream.ChannelName = message.ChannelName ?? stream.ChannelName;
        stream.Description = message.Description ?? stream.Description;
        stream.ThumbnailUrl = message.ThumbnailUrl ?? stream.ThumbnailUrl;
        stream.ScheduledAt = message.ScheduledAt ?? stream.ScheduledAt;
        stream.StartedAt = message.StartedAt ?? stream.StartedAt;
        stream.EndedAt = message.EndedAt ?? stream.EndedAt;
        stream.DurationMs = message.DurationMs ?? stream.DurationMs;
    }
}
