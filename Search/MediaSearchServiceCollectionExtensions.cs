using HoloScoop.Jobs;
using HoloScoop.Services.Media;

namespace HoloScoop.Search;

public static class MediaSearchServiceCollectionExtensions
{
    public static IServiceCollection AddMediaAndSubtitleSearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MediaProcessingOptions>()
            .Bind(configuration.GetSection(MediaProcessingOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.YtDlpPath), "Media:YtDlpPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.LibraryRoot), "Media:LibraryRoot is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkRoot), "Media:WorkRoot is required.")
            .Validate(options => options.DownloadTimeout > TimeSpan.Zero, "Media:DownloadTimeout must be positive.")
            .ValidateOnStart();

        services.AddOptions<MeilisearchOptions>()
            .Bind(configuration.GetSection(MeilisearchOptions.SectionName))
            .Validate(
                options => Uri.TryCreate(options.Url, UriKind.Absolute, out _),
                "Meilisearch:Url must be an absolute URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.IndexName), "Meilisearch:IndexName is required.")
            .Validate(options => options.BatchSize > 0, "Meilisearch:BatchSize must be positive.")
            .ValidateOnStart();

        services.AddScoped<IMediaDownloader, YtDlpMediaDownloader>();
        services.AddSingleton<ILocalMediaLibrary, LocalMediaLibrary>();
        services.AddSingleton<ISubtitleParser, WebVttParser>();
        services.AddScoped<IMediaTaskProcessor, MediaTaskProcessor>();

        services.AddHttpClient<MeilisearchSubtitleSearchService>();
        services.AddScoped<ISubtitleSearchService>(provider =>
            provider.GetRequiredService<MeilisearchSubtitleSearchService>());
        services.AddScoped<ISearchIndexRebuilder, SearchIndexRebuilder>();

        return services;
    }
}
