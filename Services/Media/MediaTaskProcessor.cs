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
    IAudioTranscriber audioTranscriber,
    MediaProcessingGate processingGate,
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
        using var processingLease = await processingGate.EnterAsync(cancellationToken);
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
        await ApplyDownloadedMetadataAsync(task.Stream, result.MetadataRelativePath, cancellationToken);

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

        var transcriptionLanguage = NormalizeLanguage(
            result.OriginalLanguage ?? _options.WhisperLanguage);
        logger.LogInformation(
            "Transcribing {ExternalId} locally with Whisper in {Language}",
            task.Stream.ExternalId,
            transcriptionLanguage);
        var transcribedCues = await audioTranscriber.TranscribeAsync(
            new AudioTranscriptionRequest(
                task.Stream.ExternalId,
                result.DiarizationAudioPath,
                transcriptionLanguage,
                Persist: true),
            cancellationToken);
        var usableCues = transcribedCues
            .Where(cue =>
                cue.StartMs >= 0 &&
                cue.EndMs > cue.StartMs &&
                cue.EndMs - cue.StartMs <= (long)_options.WhisperMaxSubtitleDurationSeconds * 1000 &&
                !string.IsNullOrWhiteSpace(cue.Text))
            .OrderBy(cue => cue.StartMs)
            .ThenBy(cue => cue.EndMs)
            .ToArray();
        var rejectedCueCount = transcribedCues.Count - usableCues.Length;
        if (rejectedCueCount > 0)
        {
            logger.LogWarning(
                "Discarded {RejectedCueCount} invalid or overlong Whisper cue(s) for {ExternalId}",
                rejectedCueCount,
                task.Stream.ExternalId);
        }
        if (usableCues.Length == 0)
        {
            throw new MediaDownloadException(
                $"本地 Whisper 未生成有效字幕（{task.Stream.ExternalId}）。");
        }
        await dbContext.SubtitleSegments
            .Where(segment => segment.StreamId == task.StreamId)
            .ExecuteDeleteAsync(cancellationToken);

        var sequence = 0;
        foreach (var cue in usableCues)
        {
            dbContext.SubtitleSegments.Add(new SubtitleSegment
            {
                StreamId = task.StreamId,
                Language = transcriptionLanguage,
                ModelVersion = Path.GetFileName(_options.WhisperModelPath),
                Sequence = sequence++,
                StartMs = cue.StartMs,
                EndMs = cue.EndMs,
                Text = cue.Text
            });
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
                    SpeakerNameSource = confirmedSpeakerCount == 1 ? "confirmed-single" : null,
                    SpeakerNameScore = confirmedSpeakerCount == 1 ? 1d : null,
                    SuggestedSpeakerName = suggestion?.MemberName,
                    SuggestedSpeakerScore = suggestion?.Score,
                    StartMs = turn.StartMs,
                    EndMs = turn.EndMs
                });
            }

            var segments = await dbContext.SubtitleSegments
                .Where(segment => segment.StreamId == task.StreamId)
                .ToListAsync(cancellationToken);
            AssignSpeakerLabels(
                segments,
                turns,
                _diarizationOptions.MinimumSpeakerDominanceRatio,
                _diarizationOptions.MinimumDetectedSpeechMs,
                _diarizationOptions.MinimumSpeakerLeadMs);
            if (confirmedSpeakerCount == 1)
            {
                foreach (var segment in segments.Where(segment => segment.SpeakerLabel != null))
                {
                    segment.SpeakerName = speakerNames[0];
                    segment.SpeakerNameSource = "confirmed-single";
                    segment.SpeakerNameScore = 1d;
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

    private async Task ApplyDownloadedMetadataAsync(
        Data.Entities.Stream stream,
        string? metadataRelativePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadataRelativePath))
        {
            return;
        }

        var path = ResolveLibraryPath(metadataRelativePath);
        try
        {
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken);
            var root = document.RootElement;

            stream.ChannelId = GetString(root, "channel_id") ?? stream.ChannelId;
            stream.ChannelName = GetString(root, "channel") ?? GetString(root, "uploader") ?? stream.ChannelName;
            stream.Title = GetString(root, "title") ?? stream.Title;
            stream.Description = GetString(root, "description") ?? stream.Description;
            stream.ThumbnailUrl = GetString(root, "thumbnail") ?? stream.ThumbnailUrl;
            stream.SourceUrl = GetString(root, "webpage_url") ?? stream.SourceUrl;
            stream.ScheduledAt = GetUnixTimestamp(root, "release_timestamp") ?? stream.ScheduledAt;
            stream.StartedAt = GetUnixTimestamp(root, "timestamp") ?? stream.StartedAt;
            if (root.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds))
            {
                stream.DurationMs = checked((long)Math.Round(seconds * 1000));
            }
            if (stream.StartedAt is not null && stream.DurationMs is not null)
            {
                stream.EndedAt = stream.StartedAt.Value.AddMilliseconds(stream.DurationMs.Value);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or OverflowException)
        {
            logger.LogWarning(exception, "Could not import metadata for {ExternalId}", stream.ExternalId);
        }
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? GetUnixTimestamp(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

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
        double minimumDominanceRatio,
        long minimumDetectedSpeechMs,
        long minimumSpeakerLeadMs)
    {
        foreach (var segment in segments)
        {
            segment.SpeakerLabel = null;
            segment.SpeakerName = null;
            segment.SpeakerNameSource = null;
            segment.SpeakerNameScore = null;
            if (segment.Text.Split(">>", StringSplitOptions.None).Length > 2)
            {
                continue;
            }
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
            var detectedSpeechDuration = overlaps.Sum(item => item.Duration);
            if (overlaps.Length == 0 ||
                detectedSpeechDuration < minimumDetectedSpeechMs ||
                overlaps[0].Duration / (double)detectedSpeechDuration < minimumDominanceRatio)
            {
                continue;
            }

            if (overlaps.Length > 1 &&
                overlaps[0].Duration - overlaps[1].Duration < minimumSpeakerLeadMs)
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

    private static string NormalizeLanguage(string language)
    {
        const string originalSuffix = "-orig";
        return language.EndsWith(originalSuffix, StringComparison.OrdinalIgnoreCase)
            ? language[..^originalSuffix.Length]
            : language;
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
