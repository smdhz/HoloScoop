using HoloScoop.Data;
using HoloScoop.Search;
using HoloScoop.Services.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace HoloScoop.Pages.Search;

public sealed record PopularTopic(string Query, int StreamCount);

public sealed class IndexModel(
    ISubtitleSearchService searchService,
    ISearchIndexRebuilder indexRebuilder,
    HoloScoopDbContext dbContext) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string Query { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Language { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Speaker { get; set; }

    public SubtitleSearchResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }
    public IReadOnlySet<long> LocalVideoStreamIds { get; private set; } = new HashSet<long>();
    public IReadOnlyList<PopularTopic> PopularTopics { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        PopularTopics = await LoadPopularTopicsAsync(cancellationToken);
        Query = Query.Trim();
        if (Query.Length == 0)
            return;

        try
        {
            Result = await searchService.SearchAsync(
                Query,
                Language,
                Speaker,
                cancellationToken: cancellationToken);
            var streamIds = Result.Hits.Select(hit => hit.StreamId).Distinct().ToArray();
            LocalVideoStreamIds = (await dbContext.DownloadedVideos
                .AsNoTracking()
                .Where(video => streamIds.Contains(video.StreamId))
                .Select(video => video.StreamId)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "搜索服务暂时不可用，请稍后重试。";
        }
    }

    public bool HasLocalVideo(long streamId) => LocalVideoStreamIds.Contains(streamId);

    private async Task<IReadOnlyList<PopularTopic>> LoadPopularTopicsAsync(
        CancellationToken cancellationToken)
    {
        var recentTitles = await dbContext.Streams
            .AsNoTracking()
            .Where(stream => stream.SubtitleSegments.Any())
            .OrderByDescending(stream => stream.UpdatedAt)
            .Select(stream => stream.Title)
            .Take(100)
            .ToListAsync(cancellationToken);

        return recentTitles
            .SelectMany(title => HashtagPattern.Matches(title)
                .Cast<Match>()
                .Select(match => $"#{match.Groups["topic"].Value}")
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(topic => topic, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PopularTopic(group.Key, group.Count()))
            .OrderByDescending(topic => topic.StreamCount)
            .ThenBy(topic => topic.Query, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    public async Task<IActionResult> OnPostRebuildAsync(CancellationToken cancellationToken)
    {
        try
        {
            await indexRebuilder.RebuildAsync(cancellationToken);
            StatusMessage = "搜索索引已从数据库重建。";
        }
        catch (HttpRequestException)
        {
            StatusMessage = "索引重建失败：搜索服务不可用。";
        }

        return RedirectToPage();
    }

    public static string FormatTimestamp(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private static readonly Regex HashtagPattern = new(
        "[#＃](?<topic>[\\p{L}\\p{M}\\p{N}_ー-]{2,50})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
