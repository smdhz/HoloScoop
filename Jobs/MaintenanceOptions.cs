namespace HoloScoop.Jobs;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public int UnselectedRetentionDays { get; set; } = 14;
    public int CompletedRetentionDays { get; set; } = 100;
}
