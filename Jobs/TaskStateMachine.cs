using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

public interface ITaskStateMachine
{
    Task<bool> TryTransitionAsync(
        long taskId,
        MediaTaskStatus expected,
        MediaTaskStatus next,
        string? error = null,
        CancellationToken cancellationToken = default);
}

public sealed class TaskStateMachine(HoloScoopDbContext dbContext) : ITaskStateMachine
{
    private static readonly HashSet<(MediaTaskStatus From, MediaTaskStatus To)> AllowedTransitions =
    [
        (MediaTaskStatus.PendingSelection, MediaTaskStatus.Queued),
        (MediaTaskStatus.PendingSelection, MediaTaskStatus.Expired),
        (MediaTaskStatus.Queued, MediaTaskStatus.Downloading),
        (MediaTaskStatus.Downloading, MediaTaskStatus.ParsingSubtitles),
        (MediaTaskStatus.ParsingSubtitles, MediaTaskStatus.Indexing),
        (MediaTaskStatus.Indexing, MediaTaskStatus.Completed),
        (MediaTaskStatus.Queued, MediaTaskStatus.Failed),
        (MediaTaskStatus.Downloading, MediaTaskStatus.Failed),
        (MediaTaskStatus.ParsingSubtitles, MediaTaskStatus.Failed),
        (MediaTaskStatus.Indexing, MediaTaskStatus.Failed),
        (MediaTaskStatus.Failed, MediaTaskStatus.Queued)
    ];

    public async Task<bool> TryTransitionAsync(
        long taskId,
        MediaTaskStatus expected,
        MediaTaskStatus next,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedTransitions.Contains((expected, next)))
            throw new InvalidOperationException($"Task transition {expected} -> {next} is not allowed.");

        var now = DateTimeOffset.UtcNow;
        var count = await dbContext.Tasks
            .Where(task => task.Id == taskId && task.Status == expected)
            .ExecuteUpdateAsync(update => update
                .SetProperty(task => task.Status, next)
                .SetProperty(task => task.LastError, error)
                .SetProperty(task => task.UpdatedAt, now),
                cancellationToken);
        return count == 1;
    }
}
