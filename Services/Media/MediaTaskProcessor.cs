using HoloScoop.Data;
using HoloScoop.Data.Entities;
using HoloScoop.Jobs;
using HoloScoop.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Services.Media;

public sealed class MediaTaskProcessor(
    HoloScoopDbContext dbContext,
    IMediaDownloader downloader,
    ISubtitleParser subtitleParser,
    ISpeakerDiarizer speakerDiarizer,
    ISubtitleSearchService searchService,
    IOptions<MediaProcessingOptions> options,
    IOptions<SpeakerDiarizationOptions> diarizationOptions,
    ILogger<MediaTaskProcessor> logger) : IMediaTaskProcessor
{
    private readonly MediaProcessingOptions _options = options.Value;
    private readonly SpeakerDiarizationOptions _diarizationOptions = diarizationOptions.Value;

    public async Task ProcessAsync(long taskId, CancellationToken cancellationToken)
    {
        var task = await dbContext.Tasks
            .Include(item => item.Stream)
            .SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Media task {taskId} was not found.");

        if (task.Status != TaskStatus.Downloading)
        {
            throw new InvalidOperationException(
                $"Media task {taskId} must be Downloading, but is {task.Status}.");
        }

        var mode = task.DownloadMode
            ?? throw new InvalidOperationException($"Media task {taskId} has no download mode.");
        if (!Uri.TryCreate(task.Stream.SourceUrl, UriKind.Absolute, out var sourceUrl))
        {
            throw new InvalidOperationException($"Media task {taskId} has an invalid source URL.");
        }

        var result = await downloader.DownloadAsync(
            new MediaDownloadRequest(task.Id, task.Stream.ExternalId, sourceUrl, mode),
            cancellationToken);

        if (mode == DownloadMode.VideoAndSubtitles && result.VideoRelativePaths.Count == 0)
        {
            throw new MediaDownloadException(
                $"yt-dlp returned no video file for media {task.Stream.ExternalId}.");
        }

        task.Status = TaskStatus.ParsingSubtitles;
        await dbContext.SaveChangesAsync(cancellationToken);

        var parsedGroups = new List<(DownloadedSubtitle Subtitle, IReadOnlyList<ParsedSubtitleCue> Cues)>();
        foreach (var subtitle in result.Subtitles)
        {
            var path = ResolveLibraryPath(subtitle.RelativePath);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            var cues = await subtitleParser.ParseAsync(stream, cancellationToken);
            if (cues.Count > 0)
            {
                parsedGroups.Add((subtitle, cues));
            }
        }

        if (parsedGroups.Count == 0)
        {
            throw new MediaDownloadException(
                $"yt-dlp returned no parseable VTT subtitles for media {task.Stream.ExternalId}.");
        }

        await dbContext.SubtitleSegments
            .Where(segment => segment.StreamId == task.StreamId)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var group in parsedGroups.GroupBy(item =>
                     new { item.Subtitle.Language, item.Subtitle.Source }))
        {
            var sequence = 0;
            foreach (var (_, cues) in group)
            {
                foreach (var cue in cues)
                {
                    dbContext.SubtitleSegments.Add(new SubtitleSegment
                    {
                        StreamId = task.StreamId,
                        Language = group.Key.Language,
                        Source = group.Key.Source,
                        Sequence = sequence++,
                        StartMs = cue.StartMs,
                        EndMs = cue.EndMs,
                        Text = cue.Text
                    });
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        task.Status = TaskStatus.Diarizing;
        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.SpeakerTurns
            .Where(turn => turn.StreamId == task.StreamId)
            .ExecuteDeleteAsync(cancellationToken);
        if (_diarizationOptions.Enabled)
        {
            var turns = await speakerDiarizer.DiarizeAsync(
                result.DiarizationAudioPath,
                cancellationToken);
            if (turns.Count == 0)
            {
                throw new MediaDownloadException(
                    $"Speaker diarization found no speech for media {task.Stream.ExternalId}.");
            }

            foreach (var turn in turns)
            {
                dbContext.SpeakerTurns.Add(new SpeakerTurn
                {
                    StreamId = task.StreamId,
                    SpeakerLabel = turn.SpeakerLabel,
                    StartMs = turn.StartMs,
                    EndMs = turn.EndMs
                });
            }

            var segments = await dbContext.SubtitleSegments
                .Where(segment => segment.StreamId == task.StreamId)
                .ToListAsync(cancellationToken);
            AssignSpeakerLabels(segments, turns, _diarizationOptions.MinimumSubtitleOverlapRatio);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        task.Status = TaskStatus.Indexing;
        await dbContext.SaveChangesAsync(cancellationToken);

        var documents = await dbContext.SubtitleSegments
            .AsNoTracking()
            .Where(segment => segment.StreamId == task.StreamId)
            .Select(segment => new SubtitleSearchDocument(
                segment.Id,
                segment.StreamId,
                task.Stream.Platform,
                task.Stream.ExternalId,
                task.Stream.Title,
                task.Stream.ChannelName,
                task.Stream.SourceUrl,
                segment.Language,
                segment.Source,
                segment.Sequence,
                segment.StartMs,
                segment.EndMs,
                segment.Text,
                segment.SpeakerLabel,
                segment.SpeakerName))
            .ToListAsync(cancellationToken);
        await searchService.ReplaceStreamAsync(task.StreamId, documents, cancellationToken);

        task.Status = TaskStatus.Completed;
        task.LastError = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        CleanupWorkDirectory(task.Id, logger);
    }

    internal static void AssignSpeakerLabels(
        IReadOnlyCollection<SubtitleSegment> segments,
        IReadOnlyCollection<DiarizedSpeakerTurn> turns,
        double minimumOverlapRatio)
    {
        foreach (var segment in segments)
        {
            segment.SpeakerLabel = null;
            segment.SpeakerName = null;
            var duration = Math.Max(1, segment.EndMs - segment.StartMs);
            var overlaps = turns
                .Select(turn => new
                {
                    turn.SpeakerLabel,
                    Duration = Math.Max(
                        0,
                        Math.Min(segment.EndMs, turn.EndMs) -
                        Math.Max(segment.StartMs, turn.StartMs))
                })
                .Where(item => item.Duration > 0)
                .GroupBy(item => item.SpeakerLabel)
                .Select(group => new
                {
                    SpeakerLabel = group.Key,
                    Duration = group.Sum(item => item.Duration)
                })
                .OrderByDescending(item => item.Duration)
                .ToArray();
            if (overlaps.Length == 0 || overlaps[0].Duration / (double)duration < minimumOverlapRatio)
            {
                continue;
            }

            if (overlaps.Length > 1 && overlaps[0].Duration <= overlaps[1].Duration)
            {
                continue;
            }

            segment.SpeakerLabel = overlaps[0].SpeakerLabel;
        }
    }

    private void CleanupWorkDirectory(long taskId, ILogger logger)
    {
        var workDirectory = SafeMediaPath.UnderRoot(_options.WorkRoot, taskId.ToString());
        try
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                exception,
                "Could not remove completed task work directory {WorkDirectory}",
                workDirectory);
        }
    }

    private string ResolveLibraryPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Stored media paths must be relative.");
        }

        var components = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return SafeMediaPath.UnderRoot(_options.LibraryRoot, components);
    }
}
