using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

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
        var now = DateTimeOffset.UtcNow;
        var unselectedCutoff = now.AddDays(-_options.UnselectedRetentionDays);
        var completedCutoff = now.AddDays(-_options.CompletedRetentionDays);
        var strategy = dbContext.Database.CreateExecutionStrategy();
        var (deletedUnselectedTasks, deletedCompletedTasks, deletedStreams) =
            await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                context.CancellationToken);

            var unselectedTaskCount = await dbContext.Tasks
                .Where(task => task.DownloadMode == null && task.CreatedAt < unselectedCutoff)
                .ExecuteDeleteAsync(context.CancellationToken);

            var completedTaskCount = await dbContext.Tasks
                .Where(task => task.Status == MediaTaskStatus.Completed &&
                               task.UpdatedAt < completedCutoff)
                .ExecuteDeleteAsync(context.CancellationToken);

            var streamCount = await dbContext.Streams
                .Where(stream => stream.CreatedAt < unselectedCutoff &&
                                 !stream.Tasks.Any() &&
                                 !stream.DownloadedVideos.Any() &&
                                 !stream.SubtitleSegments.Any())
                .ExecuteDeleteAsync(context.CancellationToken);

            await transaction.CommitAsync(context.CancellationToken);
            return (unselectedTaskCount, completedTaskCount, streamCount);
        });

        if (deletedUnselectedTasks > 0 || deletedCompletedTasks > 0 || deletedStreams > 0)
        {
            logger.LogInformation(
                "Deleted {UnselectedTaskCount} unselected tasks older than {UnselectedRetentionDays} days, " +
                "{CompletedTaskCount} completed tasks older than {CompletedRetentionDays} days, and " +
                "{StreamCount} orphan streams.",
                deletedUnselectedTasks,
                _options.UnselectedRetentionDays,
                deletedCompletedTasks,
                _options.CompletedRetentionDays,
                deletedStreams);
        }
    }
}
