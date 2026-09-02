namespace HoloScoop.Data.Entities;

public enum TaskStatus
{
    PendingSelection,
    Queued,
    Downloading,
    ParsingSubtitles,
    Indexing,
    Completed,
    Failed,
    Expired
}
