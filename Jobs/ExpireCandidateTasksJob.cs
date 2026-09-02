using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

[DisallowConcurrentExecution]
public sealed class ExpireCandidateTasksJob(
    HoloScoopDbContext dbContext,
    ILogger<ExpireCandidateTasksJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var count = await dbContext.Tasks
            .Where(task => task.Status == MediaTaskStatus.PendingSelection &&
                           task.ExpiresAt != null && task.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.Status, MediaTaskStatus.Expired)
                .SetProperty(task => task.UpdatedAt, now),
                context.CancellationToken);

        if (count > 0)
            logger.LogInformation("Expired {Count} unselected candidate tasks.", count);
    }
}
