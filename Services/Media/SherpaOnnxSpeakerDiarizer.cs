using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace HoloScoop.Services.Media;

public sealed record DiarizedSpeakerTurn(string SpeakerLabel, long StartMs, long EndMs);

public interface ISpeakerDiarizer
{
    Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        string wavePath,
        int speakerCount,
        CancellationToken cancellationToken = default);
}

public sealed class SherpaOnnxSpeakerDiarizer(
    IOptions<SpeakerDiarizationOptions> options,
    ILogger<SherpaOnnxSpeakerDiarizer> logger) : ISpeakerDiarizer
{
    private readonly SpeakerDiarizationOptions _options = options.Value;

    public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        string wavePath,
        int speakerCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(speakerCount, 1);
        return Task.Run<IReadOnlyList<DiarizedSpeakerTurn>>(
            () => Diarize(wavePath, speakerCount, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<DiarizedSpeakerTurn> Diarize(
        string wavePath,
        int speakerCount,
        CancellationToken cancellationToken)
    {
        EnsureFileExists(wavePath, "diarization audio");
        EnsureFileExists(_options.SegmentationModelPath, "speaker segmentation model");
        EnsureFileExists(_options.EmbeddingModelPath, "speaker embedding model");

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _options.SegmentationModelPath;
        config.Segmentation.NumThreads = _options.NumThreads;
        config.Embedding.Model = _options.EmbeddingModelPath;
        config.Embedding.NumThreads = _options.NumThreads;
        config.Clustering.NumClusters = speakerCount;

        using var diarizer = new OfflineSpeakerDiarization(config);
        var wave = PcmWaveFile.Read(wavePath);
        if (wave.SampleRate != diarizer.SampleRate)
        {
            throw new InvalidOperationException(
                $"Diarization audio must be {diarizer.SampleRate} Hz, but was {wave.SampleRate} Hz.");
        }

        var callback = new OfflineSpeakerDiarizationProgressCallback(
            (processed, total, _) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return 1;
                }

                if (processed == total || processed % 25 == 0)
                {
                    logger.LogInformation(
                        "Speaker diarization progress: {Processed}/{Total} chunks",
                        processed,
                        total);
                }
                return 0;
            });
        var segments = diarizer.ProcessWithCallback(wave.Samples, callback, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        return segments
            .Where(segment => segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .Select(segment => new DiarizedSpeakerTurn(
                $"SPEAKER_{segment.Speaker:00}",
                checked((long)Math.Round(segment.Start * 1000d)),
                checked((long)Math.Round(segment.End * 1000d))))
            .ToArray();
    }

    private static void EnsureFileExists(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The {description} was not found at '{path}'.", path);
        }
    }
}
