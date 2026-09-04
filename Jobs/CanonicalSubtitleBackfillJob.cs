using System.Collections.Concurrent;
using HoloScoop.Data;
using HoloScoop.Services.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using MediaTaskStatus = HoloScoop.Data.Entities.TaskStatus;

namespace HoloScoop.Jobs;

/// <summary>
/// TEMPORARY DATA-MIGRATION JOB.
/// Delete this job, CanonicalSubtitleBackfillOptions, its registration, and the
/// CanonicalSubtitleBackfill appsettings section after every eligible legacy stream has a
/// canonical subtitle track. CanonicalSubtitleBackfillService may then be deleted too unless
/// an operator-triggered backfill command still needs it.
/// </summary>
[DisallowConcurrentExecution]
public sealed class CanonicalSubtitleBackfillJob(
    HoloScoopDbContext dbContext,
    CanonicalSubtitleBackfillService backfillService,
    MediaProcessingGate processingGate,
    IOptions<CanonicalSubtitleBackfillOptions> options,
    ILogger<CanonicalSubtitleBackfillJob> logger) : IJob
{
    private static readonly ConcurrentDictionary<long, byte> SkippedForThisProcess = new();
    private readonly CanonicalSubtitleBackfillOptions _options = options.Value;

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var cancellationToken = context.CancellationToken;
        var normalMediaWorkPending = await dbContext.Tasks.AsNoTracking().AnyAsync(task =>
            task.Status == MediaTaskStatus.Queued ||
            task.Status == MediaTaskStatus.Downloading ||
            task.Status == MediaTaskStatus.ParsingSubtitles ||
            task.Status == MediaTaskStatus.Diarizing ||
            task.Status == MediaTaskStatus.Indexing,
            cancellationToken);
        if (normalMediaWorkPending)
        {
            logger.LogDebug("Canonical subtitle backfill yielded to queued or active media work.");
            return;
        }

        var candidates = await dbContext.Streams
            .AsNoTracking()
            .Where(stream =>
                stream.SubtitleSegments.Any(segment => segment.Source != "canonical") &&
                !stream.SubtitleSegments.Any(segment => segment.Source == "canonical") &&
                !stream.Tasks.Any(task =>
                    task.Status == MediaTaskStatus.Queued ||
                    task.Status == MediaTaskStatus.Downloading ||
                    task.Status == MediaTaskStatus.ParsingSubtitles ||
                    task.Status == MediaTaskStatus.Diarizing ||
                    task.Status == MediaTaskStatus.Indexing))
            .OrderBy(stream => stream.Id)
            .Select(stream => stream.Id)
            .Take(_options.CandidateScanLimit)
            .ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var streamId in candidates.Where(id => !SkippedForThisProcess.ContainsKey(id)))
        {
            if (processed >= _options.BatchSize)
            {
                break;
            }

            using var processingLease = processingGate.TryEnter();
            if (processingLease is null)
            {
                logger.LogDebug("Canonical subtitle backfill yielded to an active media task.");
                return;
            }

            try
            {
                var result = await backfillService.ProcessAsync(streamId, cancellationToken);
                if (result == CanonicalBackfillResult.Completed)
                {
                    processed++;
                    logger.LogInformation(
                        "Backfilled canonical subtitles for stream {StreamId}",
                        streamId);
                }
                else
                {
                    SkippedForThisProcess.TryAdd(streamId, 0);
                    logger.LogWarning(
                        "Skipped canonical subtitle backfill for stream {StreamId}: {Result}",
                        streamId,
                        result);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                SkippedForThisProcess.TryAdd(streamId, 0);
                logger.LogError(
                    exception,
                    "Canonical subtitle backfill failed for stream {StreamId}; it will be skipped until the process restarts",
                    streamId);
            }
        }
    }
}
