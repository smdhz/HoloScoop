using HoloScoop.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HoloScoop.Pages.Search;

public sealed class IndexModel(
    ISubtitleSearchService searchService,
    ISearchIndexRebuilder indexRebuilder) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string Query { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Language { get; set; }

    public SubtitleSearchResult? Result { get; private set; }
    public string? ErrorMessage { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Query = Query.Trim();
        if (Query.Length == 0)
            return;

        try
        {
            Result = await searchService.SearchAsync(Query, Language, cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "搜索服务暂时不可用，请稍后重试。";
        }
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
}
