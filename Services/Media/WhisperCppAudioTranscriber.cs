using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public interface IAudioTranscriber
{
    Task<IReadOnlyList<ParsedSubtitleCue>> TranscribeAsync(
        string externalId,
        string audioPath,
        CancellationToken cancellationToken = default);
}

public sealed class WhisperCppAudioTranscriber(
    IOptions<MediaProcessingOptions> options,
    ISubtitleParser subtitleParser,
    ILogger<WhisperCppAudioTranscriber> logger) : IAudioTranscriber
{
    private const int ErrorLimit = 4 * 1024;
    private readonly MediaProcessingOptions _options = options.Value;

    public async Task<IReadOnlyList<ParsedSubtitleCue>> TranscribeAsync(
        string externalId,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            throw new MediaDownloadException("本地转写所需的音频文件不存在。");
        }

        externalId = SafeMediaPath.ValidateExternalId(externalId);
        var workDirectory = Path.GetDirectoryName(audioPath)
            ?? throw new MediaDownloadException("无法确定本地转写输出目录。");
        var outputPrefix = Path.Combine(workDirectory, "whisper");
        var outputPath = $"{outputPrefix}.vtt";
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
            "-f", audioPath,
            "-l", _options.WhisperLanguage,
            "-t", _options.WhisperThreads.ToString(CultureInfo.InvariantCulture),
            "-ovtt",
            "-of", outputPrefix);

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TranscriptionTimeout);

        logger.LogInformation("No usable remote subtitles; starting local whisper.cpp transcription for {AudioPath}", audioPath);
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
        if (cues.Count > 0)
        {
            await PersistSubtitleAsync(externalId, outputPath, cancellationToken);
        }
        return cues;
    }

    private async Task PersistSubtitleAsync(
        string externalId,
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
            $"{externalId}.{_options.WhisperLanguage}.whisper.vtt");
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
