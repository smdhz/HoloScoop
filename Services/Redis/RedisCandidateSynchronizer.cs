using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Services.Redis;

public interface IRedisCandidateSynchronizer
{
    Task<RedisCandidateSyncResult> SynchronizeAllAsync(
        CancellationToken cancellationToken = default);

    Task<bool> SynchronizeOneAsync(
        long taskId,
        CancellationToken cancellationToken = default);
}

public sealed record RedisCandidateSyncResult(int Activated, int Expired);

public sealed class RedisCandidateSynchronizer(
    HoloScoopDbContext dbContext,
    IConnectionMultiplexer redis,
    IOptions<RedisStreamOptions> options) : IRedisCandidateSynchronizer
{
    private readonly RedisStreamOptions _options = options.Value;

    public async Task<RedisCandidateSyncResult> SynchronizeAllAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = await dbContext.Tasks
            .Where(task => task.RedisStream == _options.StreamName &&
                           task.DownloadMode == null &&
                           (task.Status == MediaTaskStatus.PendingSelection ||
                            task.Status == MediaTaskStatus.Expired))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new RedisCandidateSyncResult(0, 0);

        var database = redis.GetDatabase();
        var existenceChecks = candidates.Select(task =>
            database.StreamRangeAsync(
                task.RedisStream,
                task.RedisMessageId,
                task.RedisMessageId,
                count: 1)).ToArray();
        var entries = await Task.WhenAll(existenceChecks).ConfigureAwait(false);

        var activated = 0;
        var expired = 0;
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < candidates.Count; index++)
        {
            var task = candidates[index];
            var existsInRedis = entries[index].Length > 0;

            if (existsInRedis && task.Status == MediaTaskStatus.Expired)
            {
                task.Status = MediaTaskStatus.PendingSelection;
                task.UpdatedAt = now;
                activated++;
            }
            else if (!existsInRedis && task.Status == MediaTaskStatus.PendingSelection)
            {
                task.Status = MediaTaskStatus.Expired;
                task.UpdatedAt = now;
                expired++;
            }
        }

        if (activated > 0 || expired > 0)
            await dbContext.SaveChangesAsync(cancellationToken);

        return new RedisCandidateSyncResult(activated, expired);
    }

    public async Task<bool> SynchronizeOneAsync(
        long taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks.SingleOrDefaultAsync(
            item => item.Id == taskId,
            cancellationToken);
        if (task is null)
            return false;

        // Manually added tasks do not have a Redis lifecycle.
        if (task.RedisStream != _options.StreamName)
            return task.Status == MediaTaskStatus.PendingSelection;

        var entries = await redis.GetDatabase().StreamRangeAsync(
            task.RedisStream,
            task.RedisMessageId,
            task.RedisMessageId,
            count: 1).ConfigureAwait(false);
        var existsInRedis = entries.Length > 0;

        if (!existsInRedis && task.Status == MediaTaskStatus.PendingSelection)
        {
            task.Status = MediaTaskStatus.Expired;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (existsInRedis && task.Status == MediaTaskStatus.Expired && task.DownloadMode is null)
        {
            task.Status = MediaTaskStatus.PendingSelection;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return existsInRedis && task.Status == MediaTaskStatus.PendingSelection;
    }
}
