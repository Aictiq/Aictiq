namespace Aictiq.Modules.WorkItems.Workers;

public sealed class ClaimsOptions
{
    public const string SectionName = "Claims";
    public int StaleAfterMinutes { get; set; } = 30;
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(2);
}
