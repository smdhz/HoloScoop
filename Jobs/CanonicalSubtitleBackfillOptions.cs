namespace HoloScoop.Jobs;

public sealed class CanonicalSubtitleBackfillOptions
{
    public const string SectionName = "CanonicalSubtitleBackfill";

    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 1;
    public int CandidateScanLimit { get; set; } = 100;
    public int IntervalMinutes { get; set; } = 10;
}
