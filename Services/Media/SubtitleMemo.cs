using System.Text.Json;

namespace HoloScoop.Services.Media;

internal static class SubtitleMemo
{
    public static string CreateRaw(string language, string source, string whisperModelPath)
    {
        var memo = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["trackRole"] = "raw",
            ["declaredLanguage"] = language,
            ["originSource"] = source
        };
        if (source.Equals("whisper", StringComparison.OrdinalIgnoreCase))
        {
            memo["modelVersion"] = Path.GetFileName(whisperModelPath);
        }
        return JsonSerializer.Serialize(memo);
    }

    public static string CreateCanonical(CanonicalTrackDraft track, CanonicalSubtitleCue cue)
    {
        var memo = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1,
            ["trackRole"] = "canonical",
            ["declaredLanguage"] = track.Language,
            ["originDeclaredLanguage"] = cue.DeclaredLanguage,
            ["originSource"] = cue.OriginSource,
            ["generationVersion"] = 1,
            ["isActive"] = true,
            ["needsReview"] = cue.NeedsReview,
            ["baseTrackQuality"] = Math.Round(track.Quality, 4)
        };
        if (cue.DetectedLanguage is not null)
        {
            memo["detectedLanguage"] = cue.DetectedLanguage;
            memo["languageDetectionMethod"] = "script";
        }
        if (cue.ModelVersion is not null)
        {
            memo["modelVersion"] = cue.ModelVersion;
        }
        return JsonSerializer.Serialize(memo);
    }
}
