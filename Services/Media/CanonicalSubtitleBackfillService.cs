using System.Diagnostics;
using System.Text.Json;
using HoloScoop.Data;
using HoloScoop.Data.Entities;
using HoloScoop.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public enum CanonicalBackfillResult
{
    Completed,
    AlreadyExists,
    NoRawSubtitles,
    NoCanonicalCues
}

public sealed class CanonicalSubtitleBackfillService(
    HoloScoopDbContext dbContext,
    ILocalMediaLibrary mediaLibrary,
    SubtitleCanonicalizer canonicalizer,
    CanonicalSubtitleBuilder canonicalBuilder,
    ISubtitleSearchService searchService,
    IOptions<MediaProcessingOptions> options,
    IOptions<SpeakerDiarizationOptions> diarizationOptions,
    ILogger<CanonicalSubtitleBackfillService> logger)
{
    private const int ErrorLimit = 4 * 1024;
    private readonly MediaProcessingOptions _options = options.Value;
    private readonly SpeakerDiarizationOptions _diarizationOptions = diarizationOptions.Value;

    public async Task<CanonicalBackfillResult> ProcessAsync(
        long streamId,
        CancellationToken cancellationToken)
    {
        var stream = await dbContext.Streams
            .AsNoTracking()
            .SingleAsync(item => item.Id == streamId, cancellationToken);
        if (await dbContext.SubtitleSegments.AnyAsync(
                segment => segment.StreamId == streamId && segment.Source == "canonical",
                cancellationToken))
        {
            return CanonicalBackfillResult.AlreadyExists;
        }

        var rawSegments = await dbContext.SubtitleSegments
            .AsNoTracking()
            .Where(segment => segment.StreamId == streamId && segment.Source != "canonical")
            .OrderBy(segment => segment.Language)
            .ThenBy(segment => segment.Source)
            .ThenBy(segment => segment.Sequence)
            .ToListAsync(cancellationToken);
        if (rawSegments.Count == 0)
        {
            return CanonicalBackfillResult.NoRawSubtitles;
        }

        var tracks = rawSegments
            .GroupBy(segment => new { segment.Language, segment.Source })
            .Select(group => new ParsedSubtitleTrack(
                new DownloadedSubtitle(string.Empty, group.Key.Language, group.Key.Source),
                group.Select((segment, sequence) => new ParsedSubtitleCue(
                    sequence,
                    segment.StartMs,
                    segment.EndMs,
                    segment.Text)).ToArray()))
            .ToArray();
        var originalLanguage = ReadOriginalLanguage(stream.ExternalId);
        var canonical = canonicalizer.CreateDraft(
            tracks,
            originalLanguage,
            _options.CanonicalRepairPaddingSeconds);
        if (canonical is null || canonical.Cues.Count == 0)
        {
            return CanonicalBackfillResult.NoCanonicalCues;
        }

        var needsAudioRepair = canonical.Quality < _options.CanonicalMinimumQuality ||
            canonical.RepairRanges.Count > 0;
        if (needsAudioRepair)
        {
            var mediaPath = mediaLibrary.FindAudio(stream.ExternalId)?.Path ??
                mediaLibrary.FindVideo(stream.ExternalId)?.Path;
            if (mediaPath is null)
            {
                logger.LogWarning(
                    "No local media exists for stream {StreamId}; canonical subtitles will retain marked source cues",
                    streamId);
            }
            else
            {
                var workDirectory = SafeMediaPath.UnderRoot(
                    _options.WorkRoot,
                    $"canonical-backfill-{streamId}");
                if (Directory.Exists(workDirectory))
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
                Directory.CreateDirectory(workDirectory);
                try
                {
                    var audioPath = SafeMediaPath.UnderRoot(workDirectory, "audio.wav");
                    await PrepareAudioAsync(mediaPath, audioPath, cancellationToken);
                    canonical = await canonicalBuilder.BuildAsync(
                        stream.ExternalId,
                        audioPath,
                        tracks,
                        originalLanguage,
                        cancellationToken) ?? canonical;
                }
                finally
                {
                    TryDeleteWorkDirectory(workDirectory);
                }
            }
        }

        return await PersistCanonicalAsync(stream, canonical, cancellationToken);
    }

    private async Task<CanonicalBackfillResult> PersistCanonicalAsync(
        Data.Entities.Stream stream,
        CanonicalTrackDraft canonical,
        CancellationToken cancellationToken)
    {
        var speakerTurns = await dbContext.SpeakerTurns
            .AsNoTracking()
            .Where(turn => turn.StreamId == stream.Id)
            .ToListAsync(cancellationToken);
        var entities = canonical.Cues
            .OrderBy(cue => cue.StartMs)
            .ThenBy(cue => cue.EndMs)
            .Select((cue, sequence) => new SubtitleSegment
            {
                StreamId = stream.Id,
                Language = canonical.Language,
                Source = "canonical",
                Sequence = sequence,
                StartMs = cue.StartMs,
                EndMs = cue.EndMs,
                Text = cue.Text,
                Memo = SubtitleMemo.CreateCanonical(canonical, cue)
            })
            .ToArray();
        MediaTaskProcessor.AssignSpeakerLabels(
            entities,
            speakerTurns.Select(turn => new DiarizedSpeakerTurn(
                turn.SpeakerLabel,
                turn.StartMs,
                turn.EndMs)).ToArray(),
            _diarizationOptions.MinimumSubtitleOverlapRatio);
        ApplyKnownSpeakerNames(entities, speakerTurns);

        dbContext.SubtitleSegments.AddRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            var documents = entities.Select(segment => new SubtitleSearchDocument(
                segment.Id,
                segment.StreamId,
                stream.Platform,
                stream.ExternalId,
                stream.Title,
                stream.ChannelName,
                stream.SourceUrl,
                segment.Language,
                segment.Source,
                segment.Sequence,
                segment.StartMs,
                segment.EndMs,
                segment.Text,
                segment.SpeakerLabel,
                segment.SpeakerName));
            await searchService.ReplaceStreamAsync(stream.Id, documents, cancellationToken);
        }
        catch
        {
            await dbContext.SubtitleSegments
                .Where(segment => segment.StreamId == stream.Id && segment.Source == "canonical")
                .ExecuteDeleteAsync(cancellationToken);
            throw;
        }

        return CanonicalBackfillResult.Completed;
    }

    private void TryDeleteWorkDirectory(string workDirectory)
    {
        try
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not remove canonical backfill work directory {WorkDirectory}",
                workDirectory);
        }
    }

    private async Task PrepareAudioAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            WorkingDirectory = Path.GetDirectoryName(destinationPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArguments(
            startInfo,
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", sourcePath,
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-c:a", "pcm_s16le",
            destinationPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new MediaDownloadException("ffmpeg 无法启动 canonical 回填音频准备。");
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new MediaDownloadException(
                    $"ffmpeg 在准备 canonical 回填音频时退出（{process.ExitCode}）。",
                    process.ExitCode,
                    Truncate(error));
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MediaDownloadException(
                $"无法启动 ffmpeg（{_options.FfmpegPath}）：{exception.Message}");
        }
    }

    private string? ReadOriginalLanguage(string externalId)
    {
        var directory = SafeMediaPath.UnderRoot(
            _options.LibraryRoot,
            "youtube",
            SafeMediaPath.ValidateExternalId(externalId),
            "metadata");
        if (!Directory.Exists(directory)) return null;
        var path = Directory.EnumerateFiles(directory, "*.info.json").FirstOrDefault();
        if (path is null) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("language", out var language) &&
                   language.ValueKind == JsonValueKind.String
                ? language.GetString()
                : null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Could not read original language from {MetadataPath}", path);
            return null;
        }
    }

    private static void ApplyKnownSpeakerNames(
        IEnumerable<SubtitleSegment> segments,
        IReadOnlyCollection<SpeakerTurn> turns)
    {
        var names = turns
            .Where(turn => !string.IsNullOrWhiteSpace(turn.SpeakerName))
            .GroupBy(turn => turn.SpeakerLabel)
            .ToDictionary(
                group => group.Key,
                group => group.Select(turn => turn.SpeakerName).Distinct().Count() == 1
                    ? group.First().SpeakerName
                    : null,
                StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (segment.SpeakerLabel is not null &&
                names.TryGetValue(segment.SpeakerLabel, out var name))
            {
                segment.SpeakerName = name;
            }
        }
    }

    private static void AddArguments(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string Truncate(string value) =>
        value.Length <= ErrorLimit ? value.Trim() : value[^ErrorLimit..].Trim();
}
