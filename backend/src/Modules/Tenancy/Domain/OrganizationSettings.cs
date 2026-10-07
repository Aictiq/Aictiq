using System.Text.Json.Serialization;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// Per-organization preferences, stored as one <c>jsonb</c> column rather than a widening
/// row of scalar columns: settings are read as a unit, never queried across, and every
/// new preference would otherwise be a migration.
/// </summary>
/// <param name="TimeZone">
/// IANA identifier ("Europe/Sarajevo"). Dates that mean a *day* - a sprint boundary, a due
/// date - need an organization-wide answer to "when does the day end", and the browser's
/// guess differs per member.
/// </param>
/// <param name="WeekStart">First day of the week in calendars and velocity charts.</param>
/// <remarks>
/// The JSON names are spelled out because they are also SQL: the column is written with
/// web (camelCase) naming, but Npgsql translates a LINQ read of a member, such as
/// <c>org.Settings.TimeZone</c>, to <c>settings -&gt;&gt; 'TimeZone'</c> unless the member
/// says otherwise, and that key is never there.
/// </remarks>
public sealed record OrganizationSettings(
    [property: JsonPropertyName("timeZone")] string TimeZone,
    [property: JsonPropertyName("weekStart")] DayOfWeek WeekStart)
{
    /// <summary>
    /// Whether ordinary members may create projects, or only Admins and Owners.
    ///
    /// A property with an initializer rather than a fourth positional parameter: these
    /// records are deserialized from jsonb written before this setting existed, and an
    /// absent property must land on <c>true</c> - the behaviour organizations already
    /// have - instead of silently taking away a permission on the next deployment.
    /// </summary>
    [JsonPropertyName("membersCanCreateProjects")]
    public bool MembersCanCreateProjects { get; init; } = true;

    public static OrganizationSettings Default { get; } = new("UTC", DayOfWeek.Monday);

    /// <summary>
    /// Validation lives with the value, so the endpoint and the seeder cannot disagree.
    /// The time zone is checked against the machine's database rather than a regex: an
    /// identifier that does not resolve here would fail later, at conversion time, in
    /// whatever request happened to need it.
    /// </summary>
    public static bool IsKnownTimeZone(string timeZone) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);
}
