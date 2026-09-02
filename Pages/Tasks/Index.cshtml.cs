using HoloScoop.Data;
using HoloScoop.Data.Entities;
using HoloScoop.Services.Media;
using HoloScoop.Services.Note;
using HoloScoop.Services.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Pages.Tasks;

public sealed class IndexModel(
    HoloScoopDbContext dbContext,
    ITaskCommands taskCommands,
    INoteScheduleLookup noteScheduleLookup,
    IIncomingTaskStore incomingTaskStore,
    IOptions<RedisStreamOptions> redisOptions) : PageModel
{
    public IReadOnlyList<MediaTask> Candidates { get; private set; } = [];
    public IReadOnlyList<MediaTask> RecentTasks { get; private set; } = [];
    public IReadOnlyList<NoteScheduleSearchResult> ScheduleResults { get; private set; } = [];
    public IReadOnlySet<long> LocalVideoStreamIds { get; private set; } = new HashSet<long>();
    public string? MemberQuery { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(string? member, CancellationToken cancellationToken)
    {
        MemberQuery = member?.Trim();
        if (!string.IsNullOrWhiteSpace(MemberQuery))
        {
            ScheduleResults = await noteScheduleLookup.SearchActiveByMemberAsync(
                MemberQuery,
                cancellationToken: cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var selectionCutoff = now.AddHours(-1);
        Candidates = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Stream)
            .Where(task => task.Status == MediaTaskStatus.PendingSelection
                && (task.ExpiresAt == null || task.ExpiresAt > now)
                && (task.Stream.StartedAt ?? task.Stream.ScheduledAt) <= selectionCutoff)
            .OrderBy(task => task.ExpiresAt)
            .ThenBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);

        var recentIncompleteTasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Stream)
            .Where(task => task.Status != MediaTaskStatus.PendingSelection &&
                           task.Status != MediaTaskStatus.Completed)
            .OrderByDescending(task => task.UpdatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        var recentCompletedTasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Stream)
            .Where(task => task.Status == MediaTaskStatus.Completed)
            .OrderByDescending(task => task.UpdatedAt)
            .Take(3)
            .ToListAsync(cancellationToken);
        RecentTasks = recentIncompleteTasks
            .Concat(recentCompletedTasks)
            .OrderByDescending(task => task.UpdatedAt)
            .ToList();
        var recentStreamIds = RecentTasks.Select(task => task.StreamId).Distinct().ToArray();
        LocalVideoStreamIds = (await dbContext.DownloadedVideos
            .AsNoTracking()
            .Where(video => recentStreamIds.Contains(video.StreamId))
            .Select(video => video.StreamId)
            .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    public bool HasLocalVideo(long streamId) => LocalVideoStreamIds.Contains(streamId);

    public async Task<IActionResult> OnPostAddManualAsync(
        Guid scheduleId,
        string? member,
        CancellationToken cancellationToken)
    {
        var message = await noteScheduleLookup.FindActiveAsync(scheduleId, cancellationToken);
        if (message is null)
        {
            StatusMessage = "该日程不存在或已经不再 active。";
            return RedirectToPage(new { member });
        }

        var lifetimeHours = redisOptions.Value.CandidateLifetimeHours;
        var result = await incomingTaskStore.SaveCandidateAsync(
            "manual:note",
            scheduleId.ToString("D"),
            message,
            DateTimeOffset.UtcNow.AddHours(lifetimeHours),
            cancellationToken);

        StatusMessage = result == CandidateSaveResult.Created
            ? "已添加到候选任务。"
            : "该日程已经手动添加过。";
        return RedirectToPage(new { member });
    }

    public async Task<IActionResult> OnPostSelectAsync(
        long id,
        DownloadMode mode,
        string? speakerChoice,
        int? multipleSpeakerCount,
        string rowVersion,
        CancellationToken cancellationToken)
    {
        int? speakerCount = mode == DownloadMode.VideoOnly
            ? null
            : speakerChoice switch
        {
            "single" => 1,
            "multiple" when multipleSpeakerCount is >= 2 and <= 20 => multipleSpeakerCount.Value,
            _ => 0
        };
        if (mode != DownloadMode.VideoOnly && speakerCount == 0)
        {
            StatusMessage = "请选择单人直播，或输入 2 到 20 的实际说话人数。";
            return RedirectToPage();
        }

        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(rowVersion);
        }
        catch (FormatException)
        {
            return BadRequest();
        }

        var result = await taskCommands.QueueAsync(
            id, mode, speakerCount, expectedVersion, DateTimeOffset.UtcNow, cancellationToken);
        StatusMessage = result switch
        {
            QueueTaskResult.Queued when mode == DownloadMode.VideoOnly => "已加入仅视频下载队列。",
            QueueTaskResult.Queued when mode == DownloadMode.SubtitlesOnly => "已加入仅字幕下载队列。",
            QueueTaskResult.Queued => "已加入视频和字幕下载队列。",
            QueueTaskResult.Expired => "该候选任务已经过期。",
            QueueTaskResult.NotFound => "没有找到该任务。",
            QueueTaskResult.NotSelectable => "该任务已经被处理，不能重复选择。",
            QueueTaskResult.ConcurrencyConflict => "任务已被其他操作更新，请刷新后重试。",
            QueueTaskResult.MissingScheduleMember => "该日程没有成员姓名，无法建立单人声纹。",
            _ => "任务状态未改变。"
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(long id, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (task is null)
            return NotFound();

        if (task.Status != MediaTaskStatus.Failed)
        {
            StatusMessage = "只有失败任务可以重试。";
            return RedirectToPage();
        }

        task.Status = MediaTaskStatus.Queued;
        task.LastError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = "任务已重新加入队列。";
        return RedirectToPage();
    }
}
