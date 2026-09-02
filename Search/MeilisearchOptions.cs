namespace HoloScoop.Search;

public sealed class MeilisearchOptions
{
    public const string SectionName = "Meilisearch";

    public string Url { get; set; } = "http://meilisearch:7700";
    public string ApiKey { get; set; } = string.Empty;
    public string MasterKey { get => ApiKey; set => ApiKey = value; }
    public string IndexName { get; set; } = "subtitle_segments";
    public int BatchSize { get; set; } = 500;
    public TimeSpan TaskTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
