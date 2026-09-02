using StackExchange.Redis;

namespace HoloScoop.Services.Redis;

/// <summary>
/// Contract for an item in the incoming Redis stream. Canonical fields are
/// platform, externalId, sourceUrl and title. See <see cref="TryParse"/> for
/// accepted compatibility aliases.
/// </summary>
public sealed record IncomingStreamMessage(
    string Platform,
    string ExternalId,
    string SourceUrl,
    string Title,
    string? ChannelId,
    string? ChannelName,
    string? Description,
    string? ThumbnailUrl,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs)
{
    public static bool TryParse(NameValueEntry[] values, out IncomingStreamMessage? message, out string? error)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in values)
        {
            if (!item.Value.IsNull)
                fields[item.Name.ToString()] = item.Value.ToString();
        }

        string? Get(params string[] names)
        {
            foreach (var name in names)
                if (fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            return null;
        }

        var externalId = Get("externalId", "external_id", "videoId", "video_id", "id");
        var sourceUrl = Get("sourceUrl", "source_url", "url", "watchUrl", "watch_url");
        var platform = Get("platform") ??
            (externalId is not null && sourceUrl is null ? "youtube" : InferPlatform(sourceUrl));

        if (externalId is null && sourceUrl is not null)
            externalId = TryGetYouTubeId(sourceUrl);
        if (sourceUrl is null && externalId is not null && platform.Equals("youtube", StringComparison.OrdinalIgnoreCase))
            sourceUrl = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(externalId)}";

        var title = Get("title", "streamTitle", "stream_title") ?? externalId;
        if (externalId is null || sourceUrl is null || title is null)
        {
            message = null;
            error = "Redis message must identify a video using externalId/videoId and sourceUrl/url (one may be inferred for YouTube), and should include title.";
            return false;
        }

        message = new IncomingStreamMessage(
            platform,
            externalId,
            sourceUrl,
            title,
            Get("channelId", "channel_id"),
            Get("channelName", "channel_name", "memberName", "member_name"),
            Get("description"),
            Get("thumbnailUrl", "thumbnail_url", "thumbnail"),
            ParseDate(Get("scheduledAt", "scheduled_at", "startTime", "start_time")),
            ParseDate(Get("startedAt", "started_at", "actualStartTime", "actual_start_time")),
            ParseDate(Get("endedAt", "ended_at", "endTime", "end_time")),
            ParseLong(Get("durationMs", "duration_ms")));
        error = null;
        return true;
    }

    private static string InferPlatform(string? url) =>
        url?.Contains("youtu", StringComparison.OrdinalIgnoreCase) == true ? "youtube" : "unknown";

    public static string? TryGetYouTubeId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) return null;
        var queryId = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Equals("v", StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();
        if (queryId is not null) return queryId;
        var path = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length >= 2 && path[0] is "live" or "shorts" or "embed" ? path[1] : null;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (DateTimeOffset.TryParse(value, out var result)) return result;
        if (!long.TryParse(value, out var unix)) return null;
        try
        {
            return Math.Abs(unix) >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                : DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long? ParseLong(string? value) => long.TryParse(value, out var result) ? result : null;
}
