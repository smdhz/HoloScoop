using HoloScoop.Data;
using HoloScoop.Services.Redis;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Services.Note;

public interface INoteScheduleLookup
{
    Task<IncomingStreamMessage?> FindAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default);

    Task<IncomingStreamMessage?> FindActiveAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NoteScheduleSearchResult>> SearchActiveByMemberAsync(
        string memberName,
        int limit = 50,
        CancellationToken cancellationToken = default);
}

public sealed record NoteScheduleSearchResult(
    Guid Id,
    DateTime StartDt,
    string MemberName,
    string StreamTitle,
    string StreamUrl,
    string? StreamImage);

public sealed class NoteScheduleLookup(NoteScheduleDbContext dbContext) : INoteScheduleLookup
{
    public async Task<IncomingStreamMessage?> FindAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.HololiveSchedule
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == scheduleId, cancellationToken);

        return row is null ? null : Map(row);
    }

    public async Task<IncomingStreamMessage?> FindActiveAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.HololiveSchedule
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == scheduleId && !item.IsArchive,
                cancellationToken);

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<NoteScheduleSearchResult>> SearchActiveByMemberAsync(
        string memberName,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = memberName.Trim();
        if (query.Length == 0)
            return [];

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await dbContext.HololiveSchedule
            .AsNoTracking()
            .Where(item => !item.IsArchive && item.MemberName.Contains(query))
            .OrderBy(item => item.StartDt)
            .Take(limit)
            .Select(item => new NoteScheduleSearchResult(
                item.Id,
                item.StartDt,
                item.MemberName,
                item.StreamTitle,
                item.StreamUrl,
                item.StreamImage))
            .ToListAsync(cancellationToken);
    }

    private static IncomingStreamMessage Map(NoteScheduleRow row)
    {
        var externalId = IncomingStreamMessage.TryGetYouTubeId(row.StreamUrl);
        if (string.IsNullOrWhiteSpace(externalId))
            throw new InvalidOperationException(
                $"Note.dbo.HololiveSchedule row {row.Id} has an unsupported StreamUrl.");

        return new IncomingStreamMessage(
            "youtube",
            externalId,
            row.StreamUrl,
            row.StreamTitle,
            ChannelId: null,
            ChannelName: row.MemberName,
            Description: null,
            ThumbnailUrl: row.StreamImage,
            ScheduledAt: new DateTimeOffset(row.StartDt),
            StartedAt: null,
            EndedAt: null,
            DurationMs: null);
    }
}
