namespace HoloScoop.Jobs;

/// <summary>
/// Runs the media-specific stages after a task has atomically entered Downloading.
/// Implementations own the Downloading -> ParsingSubtitles -> Diarizing -> Indexing -> Completed
/// transitions. Exceptions are converted to Failed by <see cref="QueuedTasksJob"/>.
/// </summary>
public interface IMediaTaskProcessor
{
    Task ProcessAsync(long taskId, CancellationToken cancellationToken);
}
