using System.Text.RegularExpressions;

namespace HoloScoop.Services.Media;

internal sealed record ParsedSubtitleTrack(
    DownloadedSubtitle Subtitle,
    IReadOnlyList<ParsedSubtitleCue> Cues);

internal sealed record SubtitleRepairRange(long StartMs, long EndMs);

internal sealed record CanonicalSubtitleCue(
    long StartMs,
    long EndMs,
    string Text,
    string OriginSource,
    string DeclaredLanguage,
    string? DetectedLanguage,
    bool NeedsReview,
    string? ModelVersion = null);

internal sealed record CanonicalTrackDraft(
    string Language,
    IReadOnlyList<CanonicalSubtitleCue> Cues,
    double Quality,
    IReadOnlyList<SubtitleRepairRange> RepairRanges,
    string BuildStatus = "normalized",
    bool IsActive = true);

public sealed partial class SubtitleCanonicalizer
{
    private const long DuplicateWindowMs = 2_000;
    private const long RepetitionWindowMs = 30_000;
    private const long RepairMergeGapMs = 3_000;
    private const long MaximumRepairRangeMs = 120_000;
    private const long TransientCueMaximumMs = 100;

    [GeneratedRegex("\\[[^\\]]+\\]", RegexOptions.CultureInvariant)]
    private static partial Regex AnnotationPattern();

    [GeneratedRegex("[^\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchKeyPattern();

    internal CanonicalTrackDraft? CreateDraft(
        IReadOnlyList<ParsedSubtitleTrack> tracks,
        string? originalLanguage,
        int repairPaddingSeconds)
    {
        var candidates = tracks
            .Select(track => new Candidate(track, Normalize(track.Cues)))
            .Where(candidate => candidate.Cues.Count > 0)
            .Select(candidate => candidate with
            {
                Quality = Analyze(candidate.Cues, repairPaddingSeconds)
            })
            .OrderByDescending(candidate => CandidateScore(candidate, originalLanguage))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var selected = candidates[0];
        var language = BaseLanguage(selected.Track.Subtitle.Language);
        var suspicious = selected.Quality!.SuspiciousCueIndexes.ToHashSet();
        var cues = selected.Cues.Select((cue, index) => new CanonicalSubtitleCue(
            cue.StartMs,
            cue.EndMs,
            cue.Text,
            selected.Track.Subtitle.Source,
            selected.Track.Subtitle.Language,
            DetectWrittenLanguage(cue.Text),
            suspicious.Contains(index))).ToArray();
        return new CanonicalTrackDraft(
            language,
            cues,
            selected.Quality.Score,
            selected.Quality.RepairRanges);
    }

    internal IReadOnlyList<ParsedSubtitleCue> Normalize(IReadOnlyList<ParsedSubtitleCue> cues)
    {
        var normalized = new List<ParsedSubtitleCue>(cues.Count);
        foreach (var cue in ExpandSpeakerBoundaries(cues)
                     .OrderBy(cue => cue.StartMs)
                     .ThenBy(cue => cue.EndMs))
        {
            var text = cue.Text.Trim();
            var key = SearchKey(text);
            if (key.Length == 0)
            {
                continue;
            }

            if (normalized.Count > 0)
            {
                var previous = normalized[^1];
                var previousKey = SearchKey(previous.Text);
                if (previousKey.Equals(key, StringComparison.Ordinal) &&
                    cue.StartMs <= previous.EndMs + DuplicateWindowMs)
                {
                    normalized[^1] = previous with { EndMs = Math.Max(previous.EndMs, cue.EndMs) };
                    continue;
                }

                if (cue.EndMs - cue.StartMs <= TransientCueMaximumMs &&
                    cue.StartMs <= previous.EndMs + TransientCueMaximumMs)
                {
                    var mergedText = previousKey.EndsWith(key, StringComparison.Ordinal)
                        ? previous.Text
                        : key.EndsWith(previousKey, StringComparison.Ordinal)
                            ? text
                            : $"{previous.Text} {text}";
                    normalized[^1] = previous with
                    {
                        EndMs = Math.Max(previous.EndMs, cue.EndMs),
                        Text = mergedText
                    };
                    continue;
                }
            }

            normalized.Add(new ParsedSubtitleCue(
                normalized.Count,
                cue.StartMs,
                cue.EndMs,
                text));
        }

        return normalized;
    }

    private static IEnumerable<ParsedSubtitleCue> ExpandSpeakerBoundaries(
        IEnumerable<ParsedSubtitleCue> cues)
    {
        foreach (var cue in cues)
        {
            var parts = cue.Text.Split(">>", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length <= 1 || cue.EndMs - cue.StartMs < parts.Length)
            {
                yield return cue;
                continue;
            }

            var duration = cue.EndMs - cue.StartMs;
            var totalWeight = parts.Sum(part => Math.Max(1, SearchKey(part).Length));
            var start = cue.StartMs;
            for (var index = 0; index < parts.Length; index++)
            {
                var end = index == parts.Length - 1
                    ? cue.EndMs
                    : Math.Min(
                        cue.EndMs - (parts.Length - index - 1),
                        start + Math.Max(1, duration * Math.Max(1, SearchKey(parts[index]).Length) / totalWeight));
                yield return new ParsedSubtitleCue(cue.Sequence, start, end, parts[index]);
                start = end;
            }
        }
    }

    public static string BaseLanguage(string language)
    {
        const string originalSuffix = "-orig";
        return language.EndsWith(originalSuffix, StringComparison.OrdinalIgnoreCase)
            ? language[..^originalSuffix.Length]
            : language;
    }

    public static string? DetectWrittenLanguage(string text)
    {
        var japanese = 0;
        var latin = 0;
        foreach (var character in text)
        {
            if (character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff')
            {
                japanese++;
            }
            else if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latin++;
            }
        }

        if (japanese > 0 && latin > 0) return "mixed";
        if (japanese > 0) return "ja";
        if (latin > 0) return "en";
        return null;
    }

    private static double CandidateScore(Candidate candidate, string? originalLanguage)
    {
        var language = candidate.Track.Subtitle.Language;
        var baseLanguage = BaseLanguage(language);
        double score = candidate.Quality!.Score * 25;
        score += candidate.Track.Subtitle.Source.ToLowerInvariant() switch
        {
            "official" => 100,
            "auto" => 25,
            "yt-dlp" => 10,
            "whisper" => 5,
            _ => 0
        };
        if (language.EndsWith("-orig", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }
        if (!string.IsNullOrWhiteSpace(originalLanguage) &&
            baseLanguage.Equals(BaseLanguage(originalLanguage), StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return score + Math.Min(10, Math.Log10(candidate.Cues.Count + 1) * 3);
    }

    private static QualityAnalysis Analyze(
        IReadOnlyList<ParsedSubtitleCue> cues,
        int repairPaddingSeconds)
    {
        var suspicious = new HashSet<int>();
        var recent = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var key = SearchKey(cue.Text);
            var duration = Math.Max(1, cue.EndMs - cue.StartMs);
            var wordLikeUnits = CountWordLikeUnits(cue.Text);
            var suspiciousDuration = duration >= 7_000 && wordLikeUnits <= 2;
            var suspiciousRate = key.Length * 1000d / duration is < 0.2 or > 35;

            if (!recent.TryGetValue(key, out var occurrences))
            {
                occurrences = new Queue<long>();
                recent[key] = occurrences;
            }
            while (occurrences.TryPeek(out var start) && cue.StartMs - start > RepetitionWindowMs)
            {
                occurrences.Dequeue();
            }
            occurrences.Enqueue(cue.StartMs);
            var suspiciousRepetition = occurrences.Count >= 3 && key.Length <= 40;
            if (suspiciousDuration || suspiciousRate || suspiciousRepetition)
            {
                suspicious.Add(index);
            }
        }

        var score = cues.Count == 0 ? 0 : 1d - suspicious.Count / (double)cues.Count;
        var paddingMs = Math.Max(0, repairPaddingSeconds) * 1000L;
        var ranges = MergeRepairRanges(
            suspicious.Order().Select(index => new SubtitleRepairRange(
                Math.Max(0, cues[index].StartMs - paddingMs),
                cues[index].EndMs + paddingMs)));
        return new QualityAnalysis(score, suspicious.ToArray(), ranges);
    }

    private static IReadOnlyList<SubtitleRepairRange> MergeRepairRanges(
        IEnumerable<SubtitleRepairRange> ranges)
    {
        var merged = new List<SubtitleRepairRange>();
        foreach (var range in ranges.OrderBy(range => range.StartMs))
        {
            if (merged.Count == 0 || range.StartMs > merged[^1].EndMs + RepairMergeGapMs ||
                range.EndMs - merged[^1].StartMs > MaximumRepairRangeMs)
            {
                merged.Add(range);
                continue;
            }

            merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, range.EndMs) };
        }
        return merged;
    }

    private static int CountWordLikeUnits(string text)
    {
        var withoutAnnotations = AnnotationPattern().Replace(text, " ");
        var whitespaceWords = withoutAnnotations.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        var japaneseCharacters = withoutAnnotations.Count(character =>
            character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');
        return Math.Max(whitespaceWords, japaneseCharacters / 2);
    }

    private static string SearchKey(string text)
    {
        var withoutAnnotations = AnnotationPattern().Replace(text, " ");
        return SearchKeyPattern().Replace(withoutAnnotations, string.Empty).ToLowerInvariant();
    }

    private sealed record Candidate(
        ParsedSubtitleTrack Track,
        IReadOnlyList<ParsedSubtitleCue> Cues,
        QualityAnalysis? Quality = null);

    private sealed record QualityAnalysis(
        double Score,
        IReadOnlyList<int> SuspiciousCueIndexes,
        IReadOnlyList<SubtitleRepairRange> RepairRanges);
}
