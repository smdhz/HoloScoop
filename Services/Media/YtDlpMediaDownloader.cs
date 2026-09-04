using System.Diagnostics;
using System.Text.Json;
using HoloScoop.Data.Entities;
using Microsoft.Extensions.Options;

namespace HoloScoop.Services.Media;

public sealed class YtDlpMediaDownloader(
    IOptions<MediaProcessingOptions> options,
    ILogger<YtDlpMediaDownloader> logger) : IMediaDownloader
{
    private const int ErrorLimit = 16 * 1024;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".m4v"
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".flac", ".m4a", ".mka", ".mp3", ".mp4", ".mkv", ".mov", ".ogg",
        ".opus", ".wav", ".webm"
    };

    private readonly MediaProcessingOptions _options = options.Value;

    public async Task<MediaDownloadResult> DownloadAsync(
        MediaDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TaskId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Task ID must be positive.");
        }

        var externalId = SafeMediaPath.ValidateExternalId(request.ExternalId);
        SafeMediaPath.ValidateYouTubeUrl(request.SourceUrl);

        var workDirectory = SafeMediaPath.UnderRoot(_options.WorkRoot, request.TaskId.ToString());
        var libraryDirectory = SafeMediaPath.UnderRoot(_options.LibraryRoot, "youtube", externalId);
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }
        Directory.CreateDirectory(workDirectory);

        var existingVideoPath = FindExistingVideo(libraryDirectory);
        if (existingVideoPath is not null)
        {
            logger.LogInformation(
                "Reusing existing library video for {ExternalId}; yt-dlp will skip media download",
                externalId);
        }

        var startInfo = BuildStartInfo(request, workDirectory, existingVideoPath is not null);
        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);

        logger.LogInformation("Starting yt-dlp for task {TaskId} in {Mode} mode", request.TaskId, request.Mode);

        try
        {
            if (!process.Start())
            {
                throw new MediaDownloadException("yt-dlp could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await stdoutTask;
            var standardError = await stderrTask;

            logger.LogDebug("yt-dlp output for task {TaskId}: {Output}", request.TaskId, Truncate(standardOutput));

            if (process.ExitCode != 0)
            {
                var truncatedError = Truncate(standardError);
                logger.LogError(
                    "yt-dlp failed for task {TaskId} with exit code {ExitCode}: {StandardError}",
                    request.TaskId,
                    process.ExitCode,
                    truncatedError);
                throw new MediaDownloadException(
                    $"yt-dlp exited with code {process.ExitCode}.",
                    process.ExitCode,
                    truncatedError);
            }
            else if (!string.IsNullOrWhiteSpace(standardError))
            {
                logger.LogWarning(
                    "yt-dlp completed task {TaskId} with warnings: {StandardError}",
                    request.TaskId,
                    Truncate(standardError));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            throw new TimeoutException($"yt-dlp exceeded the configured timeout of {_options.DownloadTimeout}.");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MediaDownloadException($"Unable to launch yt-dlp at '{_options.YtDlpPath}': {exception.Message}");
        }

        var preparedAudio = request.Mode == DownloadMode.VideoOnly
            ? new PreparedAudio(string.Empty, null)
            : await PrepareDiarizationAudioAsync(
                workDirectory,
                request,
                existingVideoPath,
                timeout.Token);
        var result = await PromoteArtifactsAsync(
            workDirectory,
            libraryDirectory,
            externalId,
            request.TaskId,
            preparedAudio.PlaybackSourcePath,
            cancellationToken);
        var storedSubtitles = FindExistingSubtitles(libraryDirectory, externalId);
        result = result with
        {
            Subtitles = result.Subtitles
                .Concat(storedSubtitles)
                .DistinctBy(subtitle => subtitle.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        if (existingVideoPath is not null)
        {
            var existingRelativePath = Path.Combine(
                "youtube", externalId, "video", Path.GetFileName(existingVideoPath)).Replace('\\', '/');
            result = result with { VideoRelativePaths = [existingRelativePath] };
        }
        return result with { DiarizationAudioPath = preparedAudio.DiarizationPath };
    }

    private ProcessStartInfo BuildStartInfo(
        MediaDownloadRequest request,
        string workDirectory,
        bool skipMediaDownload)
    {
        var info = new ProcessStartInfo
        {
            FileName = _options.YtDlpPath,
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddArguments(info,
            "--no-playlist",
            "--no-progress",
            "--ignore-errors",
            "--retry-sleep", "http:exp=1:20",
            "--output", $"{request.ExternalId}.%(ext)s");

        if (request.Mode != DownloadMode.VideoOnly)
        {
            AddArguments(info,
                "--write-info-json",
                "--write-thumbnail",
                "--write-subs",
                "--write-auto-subs",
                "--sub-format", "vtt",
                "--sub-langs", string.Join(',', _options.SubtitleLanguages),
                "--sleep-subtitles", "5");
        }

        if (skipMediaDownload)
        {
            AddArguments(info, "--skip-download");
        }
        else if (request.Mode == DownloadMode.SubtitlesOnly)
        {
            AddArguments(info, "--format", "ba/b");
        }
        else if (request.Mode is DownloadMode.VideoAndSubtitles or DownloadMode.VideoOnly)
        {
            AddArguments(info, "--format", "bv*+ba/b");
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unsupported download mode.");
        }

        info.ArgumentList.Add(request.SourceUrl.AbsoluteUri);
        return info;
    }

    private async Task<PreparedAudio> PrepareDiarizationAudioAsync(
        string workDirectory,
        MediaDownloadRequest request,
        string? existingVideoPath,
        CancellationToken cancellationToken)
    {
        var inputPath = existingVideoPath ?? Directory
            .EnumerateFiles(workDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault()
            ?? throw new MediaDownloadException(
                $"yt-dlp returned no audio-bearing media for {request.ExternalId}.");
        var outputPath = SafeMediaPath.UnderRoot(workDirectory, "diarization.wav");
        var info = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArguments(
            info,
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", inputPath,
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-c:a", "pcm_s16le",
            outputPath);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                throw new MediaDownloadException("ffmpeg could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new MediaDownloadException(
                    $"ffmpeg exited with code {process.ExitCode} while preparing diarization audio.",
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
                $"Unable to launch ffmpeg at '{_options.FfmpegPath}': {exception.Message}");
        }

        // Subtitle-only tasks still need durable playback media for speaker samples.
        // Keep the compressed source and promote it to the library; the much larger
        // PCM WAV remains work-only and is removed after processing completes.
        var playbackSourcePath = existingVideoPath is null && request.Mode == DownloadMode.SubtitlesOnly
            ? inputPath
            : null;
        return new PreparedAudio(outputPath, playbackSourcePath);
    }

    private static string? FindExistingVideo(string libraryDirectory)
    {
        var directory = SafeMediaPath.UnderRoot(libraryDirectory, "video");
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IReadOnlyList<DownloadedSubtitle> FindExistingSubtitles(
        string libraryDirectory,
        string externalId)
    {
        var directory = SafeMediaPath.UnderRoot(libraryDirectory, "subtitles");
        if (!Directory.Exists(directory)) return [];
        var metadataDirectory = SafeMediaPath.UnderRoot(libraryDirectory, "metadata");
        var captionMetadata = ReadCaptionMetadata(metadataDirectory);
        return Directory.EnumerateFiles(directory, "*.vtt", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var (language, source) = InferSubtitleDetails(
                    fileName,
                    externalId,
                    captionMetadata.OfficialLanguages,
                    captionMetadata.AutomaticLanguages);
                var relative = Path.Combine(
                    "youtube", externalId, "subtitles", fileName).Replace('\\', '/');
                return new DownloadedSubtitle(relative, language, source);
            })
            .ToArray();
    }

    private static void AddArguments(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
    }

    private async Task<MediaDownloadResult> PromoteArtifactsAsync(
        string workDirectory,
        string libraryDirectory,
        string externalId,
        long taskId,
        string? playbackAudioPath,
        CancellationToken cancellationToken)
    {
        var subtitles = new List<DownloadedSubtitle>();
        var videos = new List<string>();
        var thumbnails = new List<string>();
        var audioCount = 0;
        string? metadata = null;
        var captionMetadata = ReadCaptionMetadata(workDirectory);

        foreach (var sourcePath in Directory.EnumerateFiles(workDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(sourcePath);
            string category;
            if (playbackAudioPath is not null &&
                Path.GetFullPath(sourcePath).Equals(
                    Path.GetFullPath(playbackAudioPath),
                    StringComparison.Ordinal))
            {
                category = "audio";
            }
            else if (fileName.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase))
            {
                category = "metadata";
            }
            else if (Path.GetExtension(fileName).Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                category = "subtitles";
            }
            else if (VideoExtensions.Contains(Path.GetExtension(fileName)))
            {
                category = "video";
            }
            else if (ImageExtensions.Contains(Path.GetExtension(fileName)))
            {
                category = "thumbnails";
            }
            else
            {
                continue;
            }

            var destinationDirectory = SafeMediaPath.UnderRoot(libraryDirectory, category);
            Directory.CreateDirectory(destinationDirectory);
            var destination = SafeMediaPath.UnderRoot(destinationDirectory, fileName);
            var fileSize = new FileInfo(sourcePath).Length;
            logger.LogInformation(
                "Promoting {Category} artifact {FileName} for task {TaskId} to NFS ({FileSizeBytes} bytes)",
                category,
                fileName,
                taskId,
                fileSize);
            await CopyWithProgressAsync(
                sourcePath,
                destination,
                taskId,
                fileName,
                fileSize,
                cancellationToken);
            File.Delete(sourcePath);
            logger.LogInformation(
                "Promoted {Category} artifact {FileName} for task {TaskId} to {Destination}",
                category,
                fileName,
                taskId,
                destination);

            var relative = Path.Combine("youtube", externalId, category, fileName).Replace('\\', '/');
            switch (category)
            {
                case "audio":
                    audioCount++;
                    break;
                case "metadata":
                    metadata = relative;
                    break;
                case "subtitles":
                    var (language, source) = InferSubtitleDetails(
                        fileName,
                        externalId,
                        captionMetadata.OfficialLanguages,
                        captionMetadata.AutomaticLanguages);
                    subtitles.Add(new DownloadedSubtitle(relative, language, source));
                    break;
                case "video":
                    videos.Add(relative);
                    break;
                case "thumbnails":
                    thumbnails.Add(relative);
                    break;
            }
        }

        logger.LogInformation(
            "Finished promoting artifacts for task {TaskId}: {VideoCount} video(s), {AudioCount} audio file(s), {SubtitleCount} subtitle(s), {ThumbnailCount} thumbnail(s)",
            taskId,
            videos.Count,
            audioCount,
            subtitles.Count,
            thumbnails.Count);
        return new MediaDownloadResult(
            externalId,
            metadata,
            captionMetadata.OriginalLanguage,
            subtitles,
            videos,
            thumbnails,
            string.Empty);
    }

    private async Task CopyWithProgressAsync(
        string sourcePath,
        string destinationPath,
        long taskId,
        string fileName,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[bufferSize];
        long copiedBytes = 0;
        var lastProgress = Stopwatch.GetTimestamp();
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            copiedBytes += bytesRead;

            if (Stopwatch.GetElapsedTime(lastProgress) < TimeSpan.FromSeconds(10))
            {
                continue;
            }

            var percent = totalBytes == 0 ? 100d : copiedBytes * 100d / totalBytes;
            logger.LogInformation(
                "Promoting artifact {FileName} for task {TaskId}: {CopiedBytes}/{TotalBytes} bytes ({Percent:F1}%)",
                fileName,
                taskId,
                copiedBytes,
                totalBytes,
                percent);
            lastProgress = Stopwatch.GetTimestamp();
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static (string Language, string Source) InferSubtitleDetails(
        string fileName,
        string externalId,
        IReadOnlySet<string> officialLanguages,
        IReadOnlySet<string> automaticLanguages)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var language = stem.StartsWith(externalId + '.', StringComparison.Ordinal)
            ? stem[(externalId.Length + 1)..]
            : stem;
        if (language.EndsWith(".whisper", StringComparison.OrdinalIgnoreCase))
        {
            return (language[..^".whisper".Length], "whisper");
        }
        var source = officialLanguages.Contains(language)
            ? "official"
            : automaticLanguages.Contains(language) ? "auto" : "yt-dlp";
        return (language, source);
    }

    private static CaptionMetadata ReadCaptionMetadata(
        string workDirectory)
    {
        var official = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var automatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(workDirectory))
        {
            return new CaptionMetadata(null, official, automatic);
        }
        var metadataPath = Directory.EnumerateFiles(workDirectory, "*.info.json").FirstOrDefault();
        if (metadataPath is null)
        {
            return new CaptionMetadata(null, official, automatic);
        }

        string? originalLanguage = null;
        try
        {
            using var stream = File.OpenRead(metadataPath);
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.TryGetProperty("language", out var languageNode) &&
                languageNode.ValueKind == JsonValueKind.String)
            {
                originalLanguage = languageNode.GetString();
            }
            AddPropertyNames(json.RootElement, "subtitles", official);
            AddPropertyNames(json.RootElement, "automatic_captions", automatic);
        }
        catch (JsonException)
        {
            // Preserve the downloaded metadata for diagnostics and use a neutral source label.
        }

        return new CaptionMetadata(originalLanguage, official, automatic);
    }

    private static void AddPropertyNames(JsonElement root, string property, HashSet<string> destination)
    {
        if (!root.TryGetProperty(property, out var captions) || captions.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var caption in captions.EnumerateObject())
        {
            destination.Add(caption.Name);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= ErrorLimit ? value : value[^ErrorLimit..];

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

    private sealed record PreparedAudio(string DiarizationPath, string? PlaybackSourcePath);

    private sealed record CaptionMetadata(
        string? OriginalLanguage,
        HashSet<string> OfficialLanguages,
        HashSet<string> AutomaticLanguages);
}
