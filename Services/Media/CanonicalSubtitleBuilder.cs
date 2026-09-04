using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public sealed class CanonicalSubtitleBuilder(
    SubtitleCanonicalizer canonicalizer,
    IAudioTranscriber audioTranscriber,
    IOptions<MediaProcessingOptions> options,
    ILogger<CanonicalSubtitleBuilder> logger)
{
    private readonly MediaProcessingOptions _options = options.Value;

    internal async Task<CanonicalTrackDraft?> BuildAsync(
        string externalId,
        string audioPath,
        IReadOnlyList<ParsedSubtitleTrack> tracks,
        string? originalLanguage,
        CancellationToken cancellationToken)
    {
        var draft = canonicalizer.CreateDraft(
            tracks,
            originalLanguage,
            _options.CanonicalRepairPaddingSeconds);
        if (draft is null || draft.Cues.All(cue => cue.OriginSource.Equals(
                "whisper", StringComparison.OrdinalIgnoreCase)))
        {
            return draft;
        }

        var transcribeWholeTrack = draft.Quality < _options.CanonicalMinimumQuality ||
            draft.RepairRanges.Count > _options.CanonicalMaximumRepairRanges;
        if (transcribeWholeTrack)
        {
            logger.LogWarning(
                "Canonical subtitle base for {ExternalId} scored {Quality:F3}; retranscribing the full audio",
                externalId,
                draft.Quality);
            try
            {
                var transcribed = await audioTranscriber.TranscribeAsync(
                    new AudioTranscriptionRequest(
                        externalId,
                        audioPath,
                        draft.Language,
                        Persist: true),
                    cancellationToken);
                var normalized = canonicalizer.Normalize(transcribed);
                if (normalized.Count > 0)
                {
                    return draft with
                    {
                        Cues = normalized.Select(cue => ToWhisperCue(cue, draft.Language)).ToArray(),
                        RepairRanges = [],
                        BuildStatus = "retranscribed",
                        IsActive = true
                    };
                }
            }
            catch (Exception exception) when (exception is MediaDownloadException or TimeoutException)
            {
                logger.LogWarning(
                    exception,
                    "Full canonical retranscription failed for {ExternalId}; retaining the marked source track",
                    externalId);
            }
            return draft with { BuildStatus = "failed", IsActive = false };
        }

        var cues = draft.Cues.ToList();
        var repairFailed = false;
        var anyRepairSucceeded = false;
        foreach (var range in draft.RepairRanges)
        {
            try
            {
                var repairedCues = await audioTranscriber.TranscribeAsync(
                    new AudioTranscriptionRequest(
                        externalId,
                        audioPath,
                        draft.Language,
                        range.StartMs,
                        range.EndMs),
                    cancellationToken);
                var normalized = canonicalizer.Normalize(repairedCues);
                if (normalized.Count == 0)
                {
                    repairFailed = true;
                    continue;
                }

                cues.RemoveAll(cue => Overlaps(cue.StartMs, cue.EndMs, range.StartMs, range.EndMs));
                cues.AddRange(normalized.Select(cue => ToWhisperCue(cue, draft.Language)));
                anyRepairSucceeded = true;
            }
            catch (Exception exception) when (exception is MediaDownloadException or TimeoutException)
            {
                repairFailed = true;
                logger.LogWarning(
                    exception,
                    "Canonical subtitle repair failed for {ExternalId} at {StartMs}-{EndMs}; retaining source cues",
                    externalId,
                    range.StartMs,
                    range.EndMs);
            }
        }

        return draft with
        {
            Cues = cues
                .OrderBy(cue => cue.StartMs)
                .ThenBy(cue => cue.EndMs)
                .ToArray(),
            BuildStatus = repairFailed ? "repair-failed" : anyRepairSucceeded ? "repaired" : "normalized",
            IsActive = !repairFailed && cues.All(cue => !cue.NeedsReview)
        };
    }

    private CanonicalSubtitleCue ToWhisperCue(ParsedSubtitleCue cue, string language) =>
        new(
            cue.StartMs,
            cue.EndMs,
            cue.Text,
            "whisper",
            language,
            SubtitleCanonicalizer.DetectWrittenLanguage(cue.Text),
            NeedsReview: false,
            Path.GetFileName(_options.WhisperModelPath));

    private static bool Overlaps(long leftStart, long leftEnd, long rightStart, long rightEnd) =>
        Math.Min(leftEnd, rightEnd) > Math.Max(leftStart, rightStart);
}
