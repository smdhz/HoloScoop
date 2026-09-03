using HoloScoop.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Data;

public interface ITaskQueries
{
    Task<IReadOnlyList<MediaTask>> GetCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<MediaTask?> FindAsync(long id, CancellationToken cancellationToken = default);
}

public sealed class TaskQueries(HoloScoopDbContext dbContext) : ITaskQueries
{
    public async Task<IReadOnlyList<MediaTask>> GetCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var selectionCutoff = now.AddHours(-1);
        return await dbContext.Tasks
            .AsNoTracking()
            .Include(x => x.Stream)
            .Where(x => x.Status == MediaTaskStatus.PendingSelection
                && (x.Stream.StartedAt ?? x.Stream.ScheduledAt) <= selectionCutoff)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<MediaTask?> FindAsync(long id, CancellationToken cancellationToken = default)
    {
        return dbContext.Tasks
            .AsNoTracking()
            .Include(x => x.Stream)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }
}
