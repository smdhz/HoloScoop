using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HoloScoop.Data;

public enum QueueTaskResult
{
    Queued,
    NotFound,
    NotSelectable,
    Expired,
    ConcurrencyConflict,
    MissingScheduleMember
}

public interface ITaskCommands
{
    Task<QueueTaskResult> QueueAsync(
        long taskId,
        DownloadMode downloadMode,
        int speakerCount,
        string speakerNamesJson,
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
        int speakerCount,
        string speakerNamesJson,
        byte[] expectedRowVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (speakerCount is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(speakerCount), "Speaker count must be between 1 and 20.");
        }

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
        task.SpeakerCount = speakerCount;
        if (speakerCount == 1)
        {
            if (string.IsNullOrWhiteSpace(task.ScheduledMemberName))
            {
                return QueueTaskResult.MissingScheduleMember;
            }
            task.SpeakerNamesJson = JsonSerializer.Serialize(new[] { task.ScheduledMemberName.Trim() });
        }
        else
        {
            task.SpeakerNamesJson = speakerNamesJson;
        }
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
