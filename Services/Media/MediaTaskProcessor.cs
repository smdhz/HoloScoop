using HoloScoop.Data;
using HoloScoop.Data.Entities;
using HoloScoop.Jobs;
using HoloScoop.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Services.Media;

public sealed class MediaTaskProcessor(
    HoloScoopDbContext dbContext,
    IMediaDownloader downloader,
    ISubtitleParser subtitleParser,
    IAudioTranscriber audioTranscriber,
    ISpeakerDiarizer speakerDiarizer,
    ISpeakerEmbeddingService speakerEmbeddingService,
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
        var speakerCount = task.SpeakerCount;
        var speakerNames = ParseSpeakerNames(task.SpeakerNamesJson);
        if (mode != DownloadMode.VideoOnly && speakerCount is null)
        {
            throw new InvalidOperationException($"Media task {taskId} has no confirmed speaker count.");
        }
        if (mode != DownloadMode.VideoOnly && speakerCount == 1 && speakerNames.Count != 1)
        {
            throw new InvalidOperationException($"Single-speaker task {taskId} has no confirmed member name.");
        }
        if (!Uri.TryCreate(task.Stream.SourceUrl, UriKind.Absolute, out var sourceUrl))
        {
            throw new InvalidOperationException($"Media task {taskId} has an invalid source URL.");
        }

        var result = await downloader.DownloadAsync(
            new MediaDownloadRequest(task.Id, task.Stream.ExternalId, sourceUrl, mode),
            cancellationToken);

        if ((mode is DownloadMode.VideoAndSubtitles or DownloadMode.VideoOnly) &&
            result.VideoRelativePaths.Count == 0)
        {
            throw new MediaDownloadException(
                $"yt-dlp returned no video file for media {task.Stream.ExternalId}.");
        }

        if ((mode is DownloadMode.VideoAndSubtitles or DownloadMode.VideoOnly) &&
            result.VideoRelativePaths.Count > 0)
        {
            await RecordDownloadedVideoAsync(task.StreamId, cancellationToken);
        }

        if (mode == DownloadMode.VideoOnly)
        {
            task.Status = TaskStatus.Completed;
            task.LastError = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            CleanupWorkDirectory(task.Id, logger);
            return;
        }

        var confirmedSpeakerCount = speakerCount!.Value;

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
            logger.LogWarning(
                "yt-dlp returned no parseable VTT subtitles for {ExternalId}; falling back to local Whisper",
                task.Stream.ExternalId);
            var cues = await audioTranscriber.TranscribeAsync(
                task.Stream.ExternalId,
                result.DiarizationAudioPath,
                cancellationToken);
            if (cues.Count > 0)
            {
                parsedGroups.Add((
                    new DownloadedSubtitle(
                        string.Empty,
                        _options.WhisperLanguage,
                        "whisper"),
                    cues));
            }
        }

        if (parsedGroups.Count == 0)
        {
            throw new MediaDownloadException(
                $"远端字幕不可用，且本地 Whisper 未识别出语音（{task.Stream.ExternalId}）。");
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
        await dbContext.SpeakerClusterEmbeddings
            .Where(embedding => embedding.StreamId == task.StreamId)
            .ExecuteDeleteAsync(cancellationToken);
        if (_diarizationOptions.Enabled)
        {
            var turns = await speakerDiarizer.DiarizeAsync(
                result.DiarizationAudioPath,
                confirmedSpeakerCount,
                cancellationToken);
            if (turns.Count == 0)
            {
                throw new MediaDownloadException(
                    $"Speaker diarization found no speech for media {task.Stream.ExternalId}.");
            }

            var embeddings = await speakerEmbeddingService.ExtractAsync(
                result.DiarizationAudioPath, turns, cancellationToken);
            foreach (var (label, embedding) in embeddings)
            {
                dbContext.SpeakerClusterEmbeddings.Add(new SpeakerClusterEmbedding
                {
                    StreamId = task.StreamId,
                    SpeakerLabel = label,
                    Embedding = ToBytes(embedding),
                    Dimension = embedding.Length
                });
            }
            var suggestions = confirmedSpeakerCount > 1
                ? await MatchVoiceProfilesAsync(embeddings, speakerNames, cancellationToken)
                : new Dictionary<string, VoiceSuggestion>(StringComparer.Ordinal);

            foreach (var turn in turns)
            {
                suggestions.TryGetValue(turn.SpeakerLabel, out var suggestion);
                dbContext.SpeakerTurns.Add(new SpeakerTurn
                {
                    StreamId = task.StreamId,
                    SpeakerLabel = turn.SpeakerLabel,
                    SpeakerName = confirmedSpeakerCount == 1 ? speakerNames[0] : null,
                    SuggestedSpeakerName = suggestion?.MemberName,
                    SuggestedSpeakerScore = suggestion?.Score,
                    StartMs = turn.StartMs,
                    EndMs = turn.EndMs
                });
            }

            var segments = await dbContext.SubtitleSegments
                .Where(segment => segment.StreamId == task.StreamId)
                .ToListAsync(cancellationToken);
            AssignSpeakerLabels(segments, turns, _diarizationOptions.MinimumSubtitleOverlapRatio);
            if (confirmedSpeakerCount == 1)
            {
                foreach (var segment in segments.Where(segment => segment.SpeakerLabel != null))
                {
                    segment.SpeakerName = speakerNames[0];
                }

                var embedding = embeddings.Values.FirstOrDefault();
                if (embedding is not null)
                {
                    await UpdateVoiceProfileAsync(
                        speakerNames[0], embedding, task.StreamId, cancellationToken);
                }
            }
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

    private async Task RecordDownloadedVideoAsync(
        long streamId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.DownloadedVideos
            .AnyAsync(video => video.StreamId == streamId, cancellationToken);
        if (!exists)
        {
            dbContext.DownloadedVideos.Add(new DownloadedVideo
            {
                StreamId = streamId,
                DownloadedAt = DateTimeOffset.UtcNow
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, VoiceSuggestion>> MatchVoiceProfilesAsync(
        IReadOnlyDictionary<string, float[]> embeddings,
        IReadOnlyList<string> candidateNames,
        CancellationToken cancellationToken)
    {
        var query = dbContext.VoiceProfiles.AsNoTracking();
        if (candidateNames.Count > 0)
        {
            query = query.Where(profile => candidateNames.Contains(profile.MemberName));
        }
        var profiles = await query.ToListAsync(cancellationToken);
        var matches = new List<(string Label, string Name, double Score, double Margin)>();
        foreach (var (label, embedding) in embeddings)
        {
            var scores = profiles
                .Where(profile => profile.Dimension == embedding.Length)
                .Select(profile => (profile.MemberName, Score: Cosine(embedding, FromBytes(profile.Embedding))))
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (scores.Length == 0) continue;
            var margin = scores.Length == 1 ? 1d : scores[0].Score - scores[1].Score;
            if (scores[0].Score >= _diarizationOptions.VoiceMatchThreshold &&
                margin >= _diarizationOptions.VoiceMatchMinimumMargin)
            {
                matches.Add((label, scores[0].MemberName, scores[0].Score, margin));
            }
        }

        var result = new Dictionary<string, VoiceSuggestion>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches.OrderByDescending(match => match.Score))
        {
            if (usedNames.Add(match.Name))
            {
                result[match.Label] = new VoiceSuggestion(match.Name, match.Score);
            }
        }
        return result;
    }

    private async Task UpdateVoiceProfileAsync(
        string memberName,
        float[] embedding,
        long streamId,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.VoiceProfiles
            .SingleOrDefaultAsync(item => item.MemberName == memberName, cancellationToken);
        if (profile is null)
        {
            dbContext.VoiceProfiles.Add(new VoiceProfile
            {
                MemberName = memberName,
                Embedding = ToBytes(embedding),
                Dimension = embedding.Length,
                SampleCount = 1,
                SourceStreamId = streamId
            });
            return;
        }

        if (profile.Dimension != embedding.Length)
        {
            profile.Embedding = ToBytes(embedding);
            profile.Dimension = embedding.Length;
            profile.SampleCount = 1;
            profile.SourceStreamId = streamId;
            return;
        }

        var average = FromBytes(profile.Embedding);
        for (var index = 0; index < average.Length; index++)
        {
            average[index] = (average[index] * profile.SampleCount + embedding[index]) /
                (profile.SampleCount + 1);
        }
        profile.Embedding = ToBytes(SpeakerEmbeddingService.Normalize(average));
        profile.SampleCount++;
        profile.SourceStreamId = streamId;
    }

    private static IReadOnlyList<string> ParseSpeakerNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double Cosine(float[] left, float[] right)
    {
        double sum = 0;
        for (var index = 0; index < left.Length; index++) sum += left[index] * right[index];
        return sum;
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private sealed record VoiceSuggestion(string MemberName, double Score);

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
