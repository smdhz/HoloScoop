namespace HoloScoop.Jobs;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public int UnselectedRetentionDays { get; set; } = 14;
}
