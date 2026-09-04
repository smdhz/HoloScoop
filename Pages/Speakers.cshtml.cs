using HoloScoop.Data;
using HoloScoop.Search;
using HoloScoop.Services.Media;
using HoloScoop.Services.Note;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace HoloScoop.Pages;

public sealed record SpeakerMappingRow(
    string Label,
    string? Name,
    string? SuggestedName,
    double? SuggestedScore,
    int TurnCount,
    int SubtitleCount,
    IReadOnlyList<SpeakerSample> Samples);

public sealed record SpeakerSample(long StartMs, long EndMs, string? Text);

public sealed class SpeakersModel(
    HoloScoopDbContext dbContext,
    ISubtitleSearchService searchService,
    ILocalMediaLibrary mediaLibrary,
    SpeakerNameCatalog speakerNameCatalog) : PageModel
{
    public long StreamId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public bool HasLocalVideo { get; private set; }
    public bool HasLocalAudio { get; private set; }
    public bool HasPlayableMedia => HasLocalVideo || HasLocalAudio;
    public bool CanRestorePlaybackAudio { get; private set; }
    public IReadOnlyList<SpeakerMappingRow> Speakers { get; private set; } = [];
    public IReadOnlyList<string> SpeakerNames => speakerNameCatalog.Names;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(long streamId, CancellationToken cancellationToken)
    {
        var stream = await dbContext.Streams
            .AsNoTracking()
            .Where(item => item.Id == streamId)
            .Select(item => new { item.Id, item.ExternalId, item.Title })
            .SingleOrDefaultAsync(cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        StreamId = stream.Id;
        Title = stream.Title;
        HasLocalVideo = mediaLibrary.FindVideo(stream.ExternalId) is not null;
        HasLocalAudio = mediaLibrary.FindAudio(stream.ExternalId) is not null;
        CanRestorePlaybackAudio = !HasPlayableMedia && await dbContext.Tasks
            .AsNoTracking()
            .AnyAsync(task =>
                task.StreamId == streamId &&
                task.Status == Data.Entities.TaskStatus.Completed &&
                task.DownloadMode != Data.Entities.DownloadMode.VideoOnly,
                cancellationToken);
        var turns = await dbContext.SpeakerTurns
            .AsNoTracking()
            .Where(turn => turn.StreamId == streamId)
            .Select(turn => new
            {
                turn.SpeakerLabel,
                turn.SpeakerName,
                turn.SuggestedSpeakerName,
                turn.SuggestedSpeakerScore,
                turn.StartMs,
                turn.EndMs
            })
            .ToListAsync(cancellationToken);
        var subtitles = await dbContext.SubtitleSegments
            .AsNoTracking()
            .Where(segment =>
                segment.StreamId == streamId &&
                segment.SpeakerLabel != null)
            .Select(segment => new SubtitleSample(
                segment.SpeakerLabel!, segment.StartMs, segment.EndMs, segment.Text))
            .ToListAsync(cancellationToken);
        var subtitlesByLabel = subtitles
            .GroupBy(segment => segment.Label)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Speakers = turns
            .GroupBy(turn => turn.SpeakerLabel)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var first = group.First();
                var labelSubtitles = subtitlesByLabel.GetValueOrDefault(group.Key) ?? [];
                var samples = PickSamples(group
                    .Select(turn => (turn.StartMs, turn.EndMs))
                    .ToArray())
                    .Select(turn => new SpeakerSample(
                        turn.StartMs,
                        turn.EndMs,
                        FindSampleText(turn.StartMs, turn.EndMs, labelSubtitles)))
                    .ToArray();
                return new SpeakerMappingRow(
                    group.Key,
                    first.SpeakerName,
                    first.SuggestedSpeakerName,
                    first.SuggestedSpeakerScore,
                    group.Count(),
                    labelSubtitles.Length,
                    samples);
            })
            .ToArray();
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAudioAsync(
        long streamId,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks
            .Include(item => item.Stream)
            .Where(item =>
                item.StreamId == streamId &&
                item.Status == Data.Entities.TaskStatus.Completed &&
                item.DownloadMode != Data.Entities.DownloadMode.VideoOnly)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (task is null)
        {
            return NotFound();
        }

        if (mediaLibrary.FindVideo(task.Stream.ExternalId) is not null ||
            mediaLibrary.FindAudio(task.Stream.ExternalId) is not null)
        {
            StatusMessage = "本地已经存在可试听媒体，无需重新下载。";
            return RedirectToPage(new { streamId });
        }

        task.Status = Data.Entities.TaskStatus.Queued;
        task.LastError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = "已重新加入队列；任务会重新处理字幕和发言人结果，并在完成后保留试听音频。";
        return RedirectToPage(new { streamId });
    }

    private static IReadOnlyList<(long StartMs, long EndMs)> PickSamples(
        IReadOnlyCollection<(long StartMs, long EndMs)> turns)
    {
        var candidates = turns
            .Where(turn => turn.EndMs - turn.StartMs is >= 2_000 and <= 15_000)
            .OrderByDescending(turn => turn.EndMs - turn.StartMs)
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = turns.OrderByDescending(turn => turn.EndMs - turn.StartMs).ToList();
        }

        var selected = new List<(long StartMs, long EndMs)>();
        foreach (var candidate in candidates)
        {
            if (selected.All(sample => Math.Abs(sample.StartMs - candidate.StartMs) >= 30_000))
            {
                selected.Add(candidate);
                if (selected.Count == 3) break;
            }
        }
        return selected.OrderBy(turn => turn.StartMs).ToArray();
    }

    private static string? FindSampleText(
        long startMs,
        long endMs,
        IEnumerable<SubtitleSample> subtitles)
    {
        return subtitles
            .Select(subtitle => new
            {
                subtitle.Text,
                Overlap = Math.Max(0, Math.Min(endMs, subtitle.EndMs) - Math.Max(startMs, subtitle.StartMs))
            })
            .Where(item => item.Overlap > 0)
            .OrderByDescending(item => item.Overlap)
            .ThenByDescending(item => item.Text.Length)
            .Select(item => item.Text)
            .FirstOrDefault();
    }

    public static string FormatTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss");

    private sealed record SubtitleSample(string Label, long StartMs, long EndMs, string Text);

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

        if (name is not null)
        {
            if (!speakerNameCatalog.TryGetCanonicalName(name, out var canonicalName))
            {
                ErrorMessage = $"未保存：主播名“{name}”不在数据库名单中，请从提示列表中选择。";
                return RedirectToPage(new { streamId });
            }

            name = canonicalName;
        }

        var exists = await dbContext.Streams.AnyAsync(item => item.Id == streamId, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }

        var changedTurns = await dbContext.SpeakerTurns
            .Where(turn => turn.StreamId == streamId && turn.SpeakerLabel == label)
            .ExecuteUpdateAsync(update => update
                .SetProperty(turn => turn.SpeakerName, name)
                .SetProperty(turn => turn.SpeakerNameSource, name == null ? null : "manual")
                .SetProperty(turn => turn.SpeakerNameScore, (double?)null), cancellationToken);
        if (changedTurns == 0)
        {
            return BadRequest();
        }

        await dbContext.SubtitleSegments
            .Where(segment => segment.StreamId == streamId && segment.SpeakerLabel == label)
            .ExecuteUpdateAsync(update => update
                .SetProperty(segment => segment.SpeakerName, name)
                .SetProperty(segment => segment.SpeakerNameSource, name == null ? null : "manual")
                .SetProperty(segment => segment.SpeakerNameScore, (double?)null), cancellationToken);
        if (name is not null)
        {
            await CreateVoiceProfileIfMissingAsync(streamId, label, name, cancellationToken);
        }
        await ReindexStreamAsync(streamId, cancellationToken);
        StatusMessage = name is null
            ? $"已清除 {label} 的姓名映射。"
            : $"已将 {label} 映射为 {name}。";
        return RedirectToPage(new { streamId });
    }

    private async Task CreateVoiceProfileIfMissingAsync(
        long streamId,
        string label,
        string memberName,
        CancellationToken cancellationToken)
    {
        if (await dbContext.VoiceProfiles.AnyAsync(
                profile => profile.MemberName == memberName, cancellationToken))
        {
            return;
        }

        var cluster = await dbContext.SpeakerClusterEmbeddings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                embedding => embedding.StreamId == streamId && embedding.SpeakerLabel == label,
                cancellationToken);
        if (cluster is null)
        {
            return;
        }

        dbContext.VoiceProfiles.Add(new Data.Entities.VoiceProfile
        {
            MemberName = memberName,
            Embedding = cluster.Embedding,
            Dimension = cluster.Dimension,
            SampleCount = 1,
            SourceStreamId = streamId
        });
        await dbContext.SaveChangesAsync(cancellationToken);
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
