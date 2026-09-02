namespace HoloScoop.Search;

public sealed record SubtitleSearchDocument(
    long Id,
    long StreamId,
    string Platform,
    string ExternalId,
    string Title,
    string? ChannelName,
    string SourceUrl,
    string Language,
    string Source,
    int Sequence,
    long StartMs,
    long EndMs,
    string Text);

public sealed record SubtitleSearchHit(
    long SegmentId,
    long StreamId,
    string Title,
    string? ChannelName,
    string Language,
    string Source,
    long StartMs,
    long EndMs,
    string Text,
    string TimestampUrl);

public sealed record SubtitleSearchResult(
    IReadOnlyList<SubtitleSearchHit> Hits,
    int Offset,
    int Limit,
    long? EstimatedTotalHits);

public interface ISubtitleSearchService
{
    Task IndexAsync(
        IEnumerable<SubtitleSearchDocument> documents,
        CancellationToken cancellationToken = default);

    Task ReplaceStreamAsync(
        long streamId,
        IEnumerable<SubtitleSearchDocument> documents,
        CancellationToken cancellationToken = default);

    Task<SubtitleSearchResult> SearchAsync(
        string query,
        string? language = null,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default);
}

public interface ISearchIndexRebuilder
{
    Task RebuildAsync(CancellationToken cancellationToken = default);
}
