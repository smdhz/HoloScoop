using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace HoloScoop.Services.Media;

public interface ISpeakerEmbeddingService
{
    Task<IReadOnlyDictionary<string, float[]>> ExtractAsync(
        string wavePath,
        IReadOnlyCollection<DiarizedSpeakerTurn> turns,
        CancellationToken cancellationToken = default);
}

public sealed class SpeakerEmbeddingService(IOptions<SpeakerDiarizationOptions> options)
    : ISpeakerEmbeddingService
{
    private const int MaximumSecondsPerSpeaker = 120;
    private readonly SpeakerDiarizationOptions _options = options.Value;

    public Task<IReadOnlyDictionary<string, float[]>> ExtractAsync(
        string wavePath,
        IReadOnlyCollection<DiarizedSpeakerTurn> turns,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyDictionary<string, float[]>>(
            () => Extract(wavePath, turns, cancellationToken), cancellationToken);
    }

    private IReadOnlyDictionary<string, float[]> Extract(
        string wavePath,
        IReadOnlyCollection<DiarizedSpeakerTurn> turns,
        CancellationToken cancellationToken)
    {
        var wave = PcmWaveFile.Read(wavePath);
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = _options.EmbeddingModelPath,
            NumThreads = _options.NumThreads,
            Provider = "cpu"
        };
        using var extractor = new SpeakerEmbeddingExtractor(config);
        var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var group in turns.GroupBy(turn => turn.SpeakerLabel))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = CollectSamples(wave, group, MaximumSecondsPerSpeaker * wave.SampleRate);
            if (samples.Length < wave.SampleRate * 2)
            {
                continue;
            }

            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(wave.SampleRate, samples);
            stream.InputFinished();
            if (!extractor.IsReady(stream))
            {
                continue;
            }

            result[group.Key] = Normalize(extractor.Compute(stream));
        }
        return result;
    }

    private static float[] CollectSamples(
        PcmWaveData wave,
        IEnumerable<DiarizedSpeakerTurn> turns,
        int maximumSamples)
    {
        var samples = new List<float>(Math.Min(maximumSamples, wave.Samples.Length));
        foreach (var turn in turns.OrderByDescending(turn => turn.EndMs - turn.StartMs))
        {
            var start = (int)Math.Clamp(turn.StartMs * wave.SampleRate / 1000, 0, wave.Samples.Length);
            var end = (int)Math.Clamp(turn.EndMs * wave.SampleRate / 1000, start, wave.Samples.Length);
            var count = Math.Min(end - start, maximumSamples - samples.Count);
            if (count <= 0) break;
            samples.AddRange(wave.Samples.AsSpan(start, count).ToArray());
        }
        return samples.ToArray();
    }

    internal static float[] Normalize(float[] embedding)
    {
        var norm = Math.Sqrt(embedding.Sum(value => value * value));
        if (norm <= 0) return embedding;
        for (var index = 0; index < embedding.Length; index++)
        {
            embedding[index] = (float)(embedding[index] / norm);
        }
        return embedding;
    }
}
