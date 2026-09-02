namespace HoloScoop.Search;

public static class YouTubeTimestampUrl
{
    public static string Create(string sourceUrl, long startMs)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
        {
            throw new ArgumentException("Source URL must be absolute.", nameof(sourceUrl));
        }

        var seconds = Math.Max(0, startMs / 1000);
        var queryParts = source.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(static part =>
            {
                var key = part.Split('=', 2)[0];
                return !key.Equals("t", StringComparison.OrdinalIgnoreCase) &&
                       !key.Equals("start", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        queryParts.Add($"t={seconds}s");

        var builder = new UriBuilder(source)
        {
            Query = string.Join('&', queryParts)
        };
        return builder.Uri.AbsoluteUri;
    }
}

