using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

public static class InterruptedTaskRecovery
{
    public static Task<int> RequeueAsync(
        HoloScoopDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return dbContext.Tasks
            .Where(task =>
                task.Status == MediaTaskStatus.Downloading ||
                task.Status == MediaTaskStatus.ParsingSubtitles ||
                task.Status == MediaTaskStatus.Diarizing ||
                task.Status == MediaTaskStatus.Indexing)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.Status, MediaTaskStatus.Queued)
                .SetProperty(task => task.LastError, (string?)null)
                .SetProperty(task => task.UpdatedAt, now),
                cancellationToken);
    }
}
