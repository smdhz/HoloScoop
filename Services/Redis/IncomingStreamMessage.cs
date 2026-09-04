namespace HoloScoop.Services.Redis;

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
}
