using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public interface IAudioTranscriber
{
    Task<IReadOnlyList<ParsedSubtitleCue>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AudioTranscriptionRequest(
    string ExternalId,
    string AudioPath,
    string Language,
    long StartMs = 0,
    long? EndMs = null,
    bool Persist = false);

public sealed class WhisperCppAudioTranscriber(
    IOptions<MediaProcessingOptions> options,
    ISubtitleParser subtitleParser,
    ILogger<WhisperCppAudioTranscriber> logger) : IAudioTranscriber
{
    private const int ErrorLimit = 4 * 1024;
    private readonly MediaProcessingOptions _options = options.Value;

    public async Task<IReadOnlyList<ParsedSubtitleCue>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var audioPath = request.AudioPath;
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            throw new MediaDownloadException("本地转写所需的音频文件不存在。");
        }

        var externalId = SafeMediaPath.ValidateExternalId(request.ExternalId);
        if (request.StartMs < 0 || request.EndMs <= request.StartMs)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "转录时间范围无效。");
        }
        var workDirectory = Path.GetDirectoryName(audioPath)
            ?? throw new MediaDownloadException("无法确定本地转写输出目录。");
        var rangeSuffix = request.StartMs == 0 && request.EndMs is null
            ? "full"
            : $"{request.StartMs}-{request.EndMs?.ToString(CultureInfo.InvariantCulture) ?? "end"}";
        var outputPrefix = Path.Combine(workDirectory, $"whisper-{rangeSuffix}");
        var outputPath = $"{outputPrefix}.vtt";
        var transcriptionInput = audioPath;
        if (request.StartMs > 0 || request.EndMs is not null)
        {
            transcriptionInput = $"{outputPrefix}.wav";
            await ExtractRangeAsync(
                audioPath,
                transcriptionInput,
                request.StartMs,
                request.EndMs,
                cancellationToken);
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.WhisperExecutablePath,
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArguments(
            startInfo,
            "-m", _options.WhisperModelPath,
            "-f", transcriptionInput,
            "-l", request.Language,
            "-t", _options.WhisperThreads.ToString(CultureInfo.InvariantCulture),
            "-ovtt",
            "-of", outputPrefix);

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TranscriptionTimeout);

        logger.LogInformation(
            "Starting local whisper.cpp transcription for {AudioPath} at {StartMs}-{EndMs} in {Language}",
            audioPath,
            request.StartMs,
            request.EndMs,
            request.Language);
        try
        {
            if (!process.Start())
            {
                throw new MediaDownloadException("本地 whisper.cpp 转写进程无法启动。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0)
            {
                logger.LogError(
                    "whisper.cpp exited with code {ExitCode}: {Error}",
                    process.ExitCode,
                    Truncate(error));
                throw new MediaDownloadException(
                    $"本地 whisper.cpp 转写失败（退出码 {process.ExitCode}）。",
                    process.ExitCode,
                    Truncate(error));
            }

            logger.LogInformation("Local whisper.cpp transcription completed: {Output}", Truncate(output));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            throw new TimeoutException($"本地 whisper.cpp 转写超过时限 {_options.TranscriptionTimeout}。");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MediaDownloadException(
                $"无法启动本地 whisper.cpp（{_options.WhisperExecutablePath}）：{exception.Message}");
        }

        if (!File.Exists(outputPath))
        {
            throw new MediaDownloadException("本地 whisper.cpp 没有生成字幕文件。");
        }

        await using var stream = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var cues = await subtitleParser.ParseAsync(stream, cancellationToken);
        if (request.StartMs > 0)
        {
            cues = cues.Select((cue, index) => cue with
            {
                Sequence = index,
                StartMs = cue.StartMs + request.StartMs,
                EndMs = cue.EndMs + request.StartMs
            }).ToArray();
        }
        if (cues.Count > 0 && request.Persist)
        {
            await PersistSubtitleAsync(
                externalId,
                request.Language,
                outputPath,
                cancellationToken);
        }
        return cues;
    }

    private async Task ExtractRangeAsync(
        string sourcePath,
        string destinationPath,
        long startMs,
        long? endMs,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            WorkingDirectory = Path.GetDirectoryName(destinationPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArguments(
            startInfo,
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-ss", FormatSeconds(startMs),
            "-i", sourcePath);
        if (endMs is not null)
        {
            AddArguments(startInfo, "-t", FormatSeconds(endMs.Value - startMs));
        }
        AddArguments(
            startInfo,
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-c:a", "pcm_s16le",
            destinationPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new MediaDownloadException("ffmpeg 无法启动局部转录音频切片。");
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new MediaDownloadException(
                    $"ffmpeg 在准备局部转录音频时退出（{process.ExitCode}）。",
                    process.ExitCode,
                    Truncate(error));
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MediaDownloadException(
                $"无法启动 ffmpeg（{_options.FfmpegPath}）：{exception.Message}");
        }
    }

    private async Task PersistSubtitleAsync(
        string externalId,
        string language,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var directory = SafeMediaPath.UnderRoot(
            _options.LibraryRoot,
            "youtube",
            externalId,
            "subtitles");
        Directory.CreateDirectory(directory);
        var destination = SafeMediaPath.UnderRoot(
            directory,
            $"{externalId}.{language}.whisper.vtt");
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static void AddArguments(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= ErrorLimit ? value.Trim() : value[^ErrorLimit..].Trim();

    private static string FormatSeconds(long milliseconds) =>
        (milliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and kill call.
        }
    }
}
