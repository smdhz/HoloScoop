namespace HoloScoop.Data.Entities;

public enum TaskStatus
{
    PendingSelection,
    Queued,
    Downloading,
    ParsingSubtitles,
    Diarizing,
    Indexing,
    Completed,
    Failed,
    Expired
}
