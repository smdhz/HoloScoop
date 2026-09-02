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

        var startInfo = BuildStartInfo(request, workDirectory);
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

        var result = await PromoteArtifactsAsync(
            workDirectory,
            libraryDirectory,
            externalId,
            request.TaskId,
            cancellationToken);
        Directory.Delete(workDirectory, recursive: true);
        return result;
    }

    private ProcessStartInfo BuildStartInfo(MediaDownloadRequest request, string workDirectory)
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
            "--write-info-json",
            "--write-thumbnail",
            "--write-subs",
            "--write-auto-subs",
            "--sub-format", "vtt",
            "--sub-langs", string.Join(',', _options.SubtitleLanguages),
            "--sleep-subtitles", "5",
            "--retry-sleep", "http:exp=1:20",
            "--output", $"{request.ExternalId}.%(ext)s");

        if (request.Mode == DownloadMode.SubtitlesOnly)
        {
            info.ArgumentList.Add("--skip-download");
        }
        else if (request.Mode == DownloadMode.VideoAndSubtitles)
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
        CancellationToken cancellationToken)
    {
        var subtitles = new List<DownloadedSubtitle>();
        var videos = new List<string>();
        var thumbnails = new List<string>();
        string? metadata = null;
        var (officialLanguages, automaticLanguages) = ReadCaptionLanguages(workDirectory);

        foreach (var sourcePath in Directory.EnumerateFiles(workDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(sourcePath);
            string category;
            if (fileName.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase))
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
                case "metadata":
                    metadata = relative;
                    break;
                case "subtitles":
                    var (language, source) = InferSubtitleDetails(
                        fileName,
                        externalId,
                        officialLanguages,
                        automaticLanguages);
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
            "Finished promoting artifacts for task {TaskId}: {VideoCount} video(s), {SubtitleCount} subtitle(s), {ThumbnailCount} thumbnail(s)",
            taskId,
            videos.Count,
            subtitles.Count,
            thumbnails.Count);
        return new MediaDownloadResult(externalId, metadata, subtitles, videos, thumbnails);
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
        var source = officialLanguages.Contains(language)
            ? "official"
            : automaticLanguages.Contains(language) ? "auto" : "yt-dlp";
        return (language, source);
    }

    private static (HashSet<string> Official, HashSet<string> Automatic) ReadCaptionLanguages(
        string workDirectory)
    {
        var official = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var automatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataPath = Directory.EnumerateFiles(workDirectory, "*.info.json").FirstOrDefault();
        if (metadataPath is null)
        {
            return (official, automatic);
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            using var json = JsonDocument.Parse(stream);
            AddPropertyNames(json.RootElement, "subtitles", official);
            AddPropertyNames(json.RootElement, "automatic_captions", automatic);
        }
        catch (JsonException)
        {
            // Preserve the downloaded metadata for diagnostics and use a neutral source label.
        }

        return (official, automatic);
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
}
