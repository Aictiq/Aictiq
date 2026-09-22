namespace Aictiq.Modules.Analytics.Workers;

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";
    /// <summary>Daily samples older than this are purged.  Plans may lower this later.</summary>
    public int RetentionDays { get; set; } = 730;
}
