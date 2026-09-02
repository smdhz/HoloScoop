using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Data;

public enum QueueTaskResult
{
    Queued,
    NotFound,
    NotSelectable,
    Expired,
    ConcurrencyConflict
}

public interface ITaskCommands
{
    Task<QueueTaskResult> QueueAsync(
        long taskId,
        DownloadMode downloadMode,
        byte[] expectedRowVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<int> ExpireCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class TaskCommands(HoloScoopDbContext dbContext) : ITaskCommands
{
    public async Task<QueueTaskResult> QueueAsync(
        long taskId,
        DownloadMode downloadMode,
        byte[] expectedRowVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken);
        if (task is null)
        {
            return QueueTaskResult.NotFound;
        }

        if (!task.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return QueueTaskResult.ConcurrencyConflict;
        }

        if (task.Status != Entities.TaskStatus.PendingSelection)
        {
            return QueueTaskResult.NotSelectable;
        }

        if (task.ExpiresAt is not null && task.ExpiresAt <= now)
        {
            task.Status = Entities.TaskStatus.Expired;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return QueueTaskResult.ConcurrencyConflict;
            }

            return QueueTaskResult.Expired;
        }

        task.DownloadMode = downloadMode;
        task.Status = Entities.TaskStatus.Queued;
        task.LastError = null;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return QueueTaskResult.Queued;
        }
        catch (DbUpdateConcurrencyException)
        {
            return QueueTaskResult.ConcurrencyConflict;
        }
    }

    public Task<int> ExpireCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Tasks
            .Where(x => x.Status == Entities.TaskStatus.PendingSelection
                && x.ExpiresAt != null
                && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, Entities.TaskStatus.Expired)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }
}
