using HoloScoop.Data;
using HoloScoop.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Pages.Tasks;

public sealed class IndexModel(HoloScoopDbContext dbContext, ITaskCommands taskCommands) : PageModel
{
    public IReadOnlyList<MediaTask> Candidates { get; private set; } = [];
    public IReadOnlyList<MediaTask> RecentTasks { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        Candidates = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Stream)
            .Where(task => task.Status == MediaTaskStatus.PendingSelection
                && (task.ExpiresAt == null || task.ExpiresAt > now))
            .OrderBy(task => task.ExpiresAt)
            .ThenBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);

        RecentTasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Stream)
            .Where(task => task.Status != MediaTaskStatus.PendingSelection)
            .OrderByDescending(task => task.UpdatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSelectAsync(
        long id,
        DownloadMode mode,
        string rowVersion,
        CancellationToken cancellationToken)
    {
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
            id, mode, expectedVersion, DateTimeOffset.UtcNow, cancellationToken);
        StatusMessage = result switch
        {
            QueueTaskResult.Queued when mode == DownloadMode.SubtitlesOnly => "已加入仅字幕下载队列。",
            QueueTaskResult.Queued => "已加入视频和字幕下载队列。",
            QueueTaskResult.Expired => "该候选任务已经过期。",
            QueueTaskResult.NotFound => "没有找到该任务。",
            QueueTaskResult.NotSelectable => "该任务已经被处理，不能重复选择。",
            QueueTaskResult.ConcurrencyConflict => "任务已被其他操作更新，请刷新后重试。",
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
