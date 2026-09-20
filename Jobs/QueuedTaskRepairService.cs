using HoloScoop.Data;
using HoloScoop.Services.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

/// <summary>
/// Repairs the database/Redis dual-write gap. Redis drives normal dispatch; this scan is only a safety net.
/// </summary>
public sealed class QueuedTaskRepairService(
    IServiceScopeFactory scopeFactory,
    IOptions<RedisStreamOptions> options,
    ILogger<QueuedTaskRepairService> logger) : BackgroundService
{
    private readonly RedisStreamOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.QueueRepairIntervalMinutes));
        do
        {
            try
            {
                await PublishQueuedTasksAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not repair missing Redis media task notifications.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task PublishQueuedTasksAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HoloScoopDbContext>();
        var taskQueue = scope.ServiceProvider.GetRequiredService<IMediaTaskQueue>();
        var ids = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.Status == MediaTaskStatus.Queued)
            .OrderBy(task => task.CreatedAt)
            .Select(task => task.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var id in ids)
            await taskQueue.PublishAsync(id, cancellationToken).ConfigureAwait(false);

        if (ids.Count > 0)
            logger.LogInformation("Republished {TaskCount} queued media task notification(s).", ids.Count);
    }
}
