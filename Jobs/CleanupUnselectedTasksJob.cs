using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;

namespace HoloScoop.Jobs;

[DisallowConcurrentExecution]
public sealed class CleanupUnselectedTasksJob(
    HoloScoopDbContext dbContext,
    IOptions<MaintenanceOptions> options,
    ILogger<CleanupUnselectedTasksJob> logger) : IJob
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task Execute(IJobExecutionContext context)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.UnselectedRetentionDays);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            context.CancellationToken);

        var deletedTasks = await dbContext.Tasks
            .Where(task => task.DownloadMode == null && task.CreatedAt < cutoff)
            .ExecuteDeleteAsync(context.CancellationToken);

        var deletedStreams = await dbContext.Streams
            .Where(stream => stream.CreatedAt < cutoff &&
                             !stream.Tasks.Any() &&
                             !stream.SubtitleSegments.Any())
            .ExecuteDeleteAsync(context.CancellationToken);

        await transaction.CommitAsync(context.CancellationToken);

        if (deletedTasks > 0 || deletedStreams > 0)
        {
            logger.LogInformation(
                "Deleted {TaskCount} unselected tasks older than {RetentionDays} days and {StreamCount} orphan streams.",
                deletedTasks,
                _options.UnselectedRetentionDays,
                deletedStreams);
        }
    }
}
