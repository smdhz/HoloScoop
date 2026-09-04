using HoloScoop.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HoloScoop.Search;

public sealed class SearchIndexRebuilder(
    HoloScoopDbContext dbContext,
    MeilisearchSubtitleSearchService searchService,
    IOptions<MeilisearchOptions> options,
    ILogger<SearchIndexRebuilder> logger) : ISearchIndexRebuilder
{
    private readonly MeilisearchOptions _options = options.Value;

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(_options.BatchSize, 1, 5_000);
        long cursor = 0;
        long indexed = 0;

        logger.LogInformation("Starting a full rebuild of Meilisearch index {IndexName}", _options.IndexName);
        await searchService.ResetAsync(cancellationToken);

        while (true)
        {
            var documents = await dbContext.SubtitleSegments
                .AsNoTracking()
                .Where(segment => segment.Id > cursor &&
                    (segment.Source == "canonical" ||
                     !dbContext.SubtitleSegments.Any(candidate =>
                         candidate.StreamId == segment.StreamId &&
                         candidate.Source == "canonical")))
                .OrderBy(segment => segment.Id)
                .Select(segment => new SubtitleSearchDocument(
                    segment.Id,
                    segment.StreamId,
                    segment.Stream.Platform,
                    segment.Stream.ExternalId,
                    segment.Stream.Title,
                    segment.Stream.ChannelName,
                    segment.Stream.SourceUrl,
                    segment.Language,
                    segment.Source,
                    segment.Sequence,
                    segment.StartMs,
                    segment.EndMs,
                    segment.Text,
                    segment.SpeakerLabel,
                    segment.SpeakerName))
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (documents.Count == 0)
            {
                break;
            }

            await searchService.IndexAsync(documents, cancellationToken);
            cursor = documents[^1].Id;
            indexed += documents.Count;
        }

        logger.LogInformation(
            "Completed rebuild of Meilisearch index {IndexName} with {DocumentCount} documents",
            _options.IndexName,
            indexed);
    }
}
