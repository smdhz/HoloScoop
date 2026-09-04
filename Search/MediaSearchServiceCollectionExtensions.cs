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
            .Validate(options => !string.IsNullOrWhiteSpace(options.FfmpegPath), "Media:FfmpegPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.LibraryRoot), "Media:LibraryRoot is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkRoot), "Media:WorkRoot is required.")
            .Validate(options => options.DownloadTimeout > TimeSpan.Zero, "Media:DownloadTimeout must be positive.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WhisperExecutablePath), "Media:WhisperExecutablePath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WhisperModelPath), "Media:WhisperModelPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WhisperLanguage), "Media:WhisperLanguage is required.")
            .Validate(options => options.WhisperThreads > 0, "Media:WhisperThreads must be positive.")
            .Validate(options => options.TranscriptionTimeout > TimeSpan.Zero, "Media:TranscriptionTimeout must be positive.")
            .ValidateOnStart();

        services.AddOptions<SpeakerDiarizationOptions>()
            .Bind(configuration.GetSection(SpeakerDiarizationOptions.SectionName))
            .Validate(options => options.NumThreads > 0, "SpeakerDiarization:NumThreads must be positive.")
            .Validate(options => options.MinimumSubtitleOverlapRatio is > 0 and <= 1,
                "SpeakerDiarization:MinimumSubtitleOverlapRatio must be between zero and one.")
            .Validate(options => options.VoiceMatchThreshold is > 0 and <= 1,
                "SpeakerDiarization:VoiceMatchThreshold must be between zero and one.")
            .Validate(options => options.VoiceMatchMinimumMargin is >= 0 and < 1,
                "SpeakerDiarization:VoiceMatchMinimumMargin must be between zero and one.")
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
        services.AddSingleton<ISpeakerDiarizer, SherpaOnnxSpeakerDiarizer>();
        services.AddSingleton<ISpeakerEmbeddingService, SpeakerEmbeddingService>();
        services.AddSingleton<ILocalMediaLibrary, LocalMediaLibrary>();
        services.AddSingleton<ISubtitleParser, WebVttParser>();
        services.AddSingleton<MediaProcessingGate>();
        services.AddSingleton<IAudioTranscriber, WhisperCppAudioTranscriber>();
        services.AddScoped<IMediaTaskProcessor, MediaTaskProcessor>();

        services.AddHttpClient<MeilisearchSubtitleSearchService>();
        services.AddScoped<ISubtitleSearchService>(provider =>
            provider.GetRequiredService<MeilisearchSubtitleSearchService>());
        services.AddScoped<ISearchIndexRebuilder, SearchIndexRebuilder>();

        return services;
    }
}
