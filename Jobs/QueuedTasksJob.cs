using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

/// <summary>
/// Minimal in-process dispatcher. DisallowConcurrentExecution prevents overlap on one
/// scheduler; the conditional UPDATE remains the cross-process claim if the web app is scaled.
/// </summary>
[DisallowConcurrentExecution]
public sealed class QueuedTasksJob(
    HoloScoopDbContext dbContext,
    IMediaTaskProcessor processor,
    ILogger<QueuedTasksJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var ids = await dbContext.Tasks.AsNoTracking()
            .Where(task => task.Status == MediaTaskStatus.Queued)
            .OrderBy(task => task.CreatedAt)
            .Select(task => task.Id)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var now = DateTimeOffset.UtcNow;
            var claimed = await dbContext.Tasks
                .Where(task => task.Id == id && task.Status == MediaTaskStatus.Queued)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(task => task.Status, MediaTaskStatus.Downloading)
                    .SetProperty(task => task.AttemptCount, task => task.AttemptCount + 1)
                    .SetProperty(task => task.LastError, (string?)null)
                    .SetProperty(task => task.UpdatedAt, now),
                    cancellationToken);

            if (claimed == 0) continue;

            try
            {
                await processor.ProcessAsync(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Media task {TaskId} failed.", id);
                await MarkFailedAsync(id, exception, cancellationToken);
            }
        }
    }

    private Task<int> MarkFailedAsync(long id, Exception exception, CancellationToken cancellationToken)
    {
        var error = exception.ToString();
        if (error.Length > 16_000) error = error[..16_000];
        var now = DateTimeOffset.UtcNow;
        return dbContext.Tasks
            .Where(task => task.Id == id && task.Status != MediaTaskStatus.Completed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.Status, MediaTaskStatus.Failed)
                .SetProperty(task => task.LastError, error)
                .SetProperty(task => task.UpdatedAt, now),
                cancellationToken);
    }
}
