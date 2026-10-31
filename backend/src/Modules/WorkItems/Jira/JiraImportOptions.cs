namespace Aictiq.Modules.WorkItems.Jira;

/// <summary>
/// Which Jira sites the server may be sent to. A site URL is user input that makes the server
/// open a connection, so by default only Jira Cloud names are accepted, over HTTPS, and every
/// connection is re-checked as public when the socket opens.
/// </summary>
public sealed class JiraImportOptions
{
    public const string SectionName = "JiraImport";

    /// <summary>Host suffixes a site must end with. Empty accepts any public host.</summary>
    public string[] AllowedSiteSuffixes { get; set; } = [".atlassian.net"];

    /// <summary>
    /// Development and test only: allows <c>http://</c> and private or loopback addresses, so a
    /// local Jira stub can stand in for Jira Cloud.
    /// </summary>
    public bool AllowInsecureSites { get; set; }

    /// <summary>How long a running job may go without progress before another worker resumes it.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);
}
