using HoloScoop.Data;
using HoloScoop.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Pages;

public sealed record SpeakerMappingRow(string Label, string? Name, int TurnCount, int SubtitleCount);

public sealed class SpeakersModel(
    HoloScoopDbContext dbContext,
    ISubtitleSearchService searchService) : PageModel
{
    public long StreamId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public IReadOnlyList<SpeakerMappingRow> Speakers { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(long streamId, CancellationToken cancellationToken)
    {
        var stream = await dbContext.Streams
            .AsNoTracking()
            .Where(item => item.Id == streamId)
            .Select(item => new { item.Id, item.Title })
            .SingleOrDefaultAsync(cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        StreamId = stream.Id;
        Title = stream.Title;
        var turns = await dbContext.SpeakerTurns
            .AsNoTracking()
            .Where(turn => turn.StreamId == streamId)
            .GroupBy(turn => new { turn.SpeakerLabel, turn.SpeakerName })
            .Select(group => new
            {
                Label = group.Key.SpeakerLabel,
                Name = group.Key.SpeakerName,
                Count = group.Count()
            })
            .OrderBy(item => item.Label)
            .ToListAsync(cancellationToken);
        var subtitleCounts = await dbContext.SubtitleSegments
            .AsNoTracking()
            .Where(segment => segment.StreamId == streamId && segment.SpeakerLabel != null)
            .GroupBy(segment => segment.SpeakerLabel!)
            .Select(group => new { Label = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Label, item => item.Count, cancellationToken);
        Speakers = turns
            .Select(turn => new SpeakerMappingRow(
                turn.Label,
                turn.Name,
                turn.Count,
                subtitleCounts.GetValueOrDefault(turn.Label)))
            .ToArray();
        return Page();
    }

    public async Task<IActionResult> OnPostMapAsync(
        long streamId,
        string label,
        string? name,
        CancellationToken cancellationToken)
    {
        label = label.Trim();
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (label.Length == 0 || label.Length > 64 || name?.Length > 256)
        {
            return BadRequest();
        }

        var exists = await dbContext.Streams.AnyAsync(item => item.Id == streamId, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }

        var changedTurns = await dbContext.SpeakerTurns
            .Where(turn => turn.StreamId == streamId && turn.SpeakerLabel == label)
            .ExecuteUpdateAsync(update => update.SetProperty(turn => turn.SpeakerName, name), cancellationToken);
        if (changedTurns == 0)
        {
            return BadRequest();
        }

        await dbContext.SubtitleSegments
            .Where(segment => segment.StreamId == streamId && segment.SpeakerLabel == label)
            .ExecuteUpdateAsync(update => update.SetProperty(segment => segment.SpeakerName, name), cancellationToken);
        await ReindexStreamAsync(streamId, cancellationToken);
        StatusMessage = name is null
            ? $"已清除 {label} 的姓名映射。"
            : $"已将 {label} 映射为 {name}。";
        return RedirectToPage(new { streamId });
    }

    private async Task ReindexStreamAsync(long streamId, CancellationToken cancellationToken)
    {
        var documents = await dbContext.SubtitleSegments
            .AsNoTracking()
            .Where(segment => segment.StreamId == streamId)
            .Select(segment => new SubtitleSearchDocument(
                segment.Id,
                segment.StreamId,
                segment.Stream.Platform,
                segment.Stream.ExternalId,
                segment.Stream.Title,
                segment.Stream.ChannelName,
                segment.Stream.SourceUrl,
                segment.Language,
                segment.Source,
                segment.Sequence,
                segment.StartMs,
                segment.EndMs,
                segment.Text,
                segment.SpeakerLabel,
                segment.SpeakerName))
            .ToListAsync(cancellationToken);
        await searchService.ReplaceStreamAsync(streamId, documents, cancellationToken);
    }
}
