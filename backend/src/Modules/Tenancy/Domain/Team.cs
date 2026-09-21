using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>What a team's estimates are denominated in. Fixed per team, not per item.</summary>
public enum EstimationUnit
{
    /// <summary>Fibonacci story points, relative and unitless.</summary>
    Points = 0,

    /// <summary>Hours: original, remaining, completed.</summary>
    Hours = 1,
}

/// <summary>
/// A slice of a project with its own backlog, board and sprints.
///
/// Teams exist because a project of any size is worked by more than one group of people
/// and a single backlog stops being anybody's. The planning settings live here rather
/// than on the project for the same reason: two teams in one project routinely run
/// different sprint lengths and estimate in different units.
///
/// Exactly one team per project is the <b>default</b> — where work lands when nobody says
/// otherwise — and a partial unique index is what guarantees it.
/// </summary>
public sealed class Team : TenantEntity, IAudited
{
    public const int MaxNameLength = 60;
    public const int MinSprintLengthDays = 1;
    public const int MaxSprintLengthDays = 28;

    /// <summary>Monday to Friday. The overwhelming default, and a sane one to fail back to.</summary>
    public static readonly int[] DefaultWorkingDays = [1, 2, 3, 4, 5];

    public Guid ProjectId { get; init; }

    public required string Name { get; set; }

    /// <summary>A short code for chips and sprint names. Unique within the project.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// One to twenty-eight days. The lower bound is a day because a team running daily
    /// timeboxes is unusual but not wrong; the upper is four weeks, past which a sprint
    /// has stopped being one.
    /// </summary>
    public int SprintLengthDays { get; set; } = 14;

    /// <summary>
    /// <see cref="DayOfWeek"/> numbers. Capacity and burndown are counted in these, so a
    /// team that does not work Fridays does not get a burndown that says it should have.
    /// </summary>
    public int[] WorkingDays { get; set; } = DefaultWorkingDays;

    public EstimationUnit EstimationUnit { get; set; }

    /// <summary>
    /// Overrides the organization's time zone for this team's day boundaries, or null to
    /// follow it. A distributed team plans in one zone; a second team in another office
    /// should not have to.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Where a project's work lands when nobody names a team. Guaranteed unique per
    /// project by <c>ux_teams_default_per_project</c> — and always present, because a
    /// project creates one with itself and the default cannot be deleted.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; private set; }

    public static Team Create(
        Guid organizationId, Guid projectId, string name, string key, bool isDefault, DateTimeOffset now) =>
        new()
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = name,
            Key = key,
            IsDefault = isDefault,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void Update(
        string name, int sprintLengthDays, int[] workingDays, EstimationUnit estimationUnit,
        string? timeZone, DateTimeOffset now)
    {
        Name = name;
        SprintLengthDays = sprintLengthDays;
        WorkingDays = workingDays;
        EstimationUnit = estimationUnit;
        TimeZone = timeZone;
        UpdatedAt = now;
    }

    /// <summary>Validation lives with the value so the endpoint and the seeder cannot disagree.</summary>
    public static bool IsValidSprintLength(int days) =>
        days is >= MinSprintLengthDays and <= MaxSprintLengthDays;

    public static bool IsValidWorkingDays(IReadOnlyCollection<int> days) =>
        days.Count is > 0 and <= 7
        && days.All(day => day is >= 0 and <= 6)
        && days.Distinct().Count() == days.Count;
}

/// <summary>
/// Someone on a team, and what they are worth to a sprint.
///
/// A team member must already be a member of the team's project — checked in the endpoint
/// rather than by a foreign key, because the person themselves lives in Identity's schema
/// and modules do not FK across schemas. Recorded as a known gap for the RLS pass
///.
/// </summary>
public sealed class TeamMember : TenantEntity, IAudited
{
    public Guid TeamId { get; init; }

    public required string UserId { get; init; }

    /// <summary>
    /// A lead runs the team without needing to run the project: they manage its roster and
    /// its planning settings. It is not a *role* — a lead's permissions on the work itself
    /// are still their project role — which is why it is a flag rather than a rank.
    /// </summary>
    public bool IsLead { get; set; }

    /// <summary>
    /// Hours per working day this person is available for, used to size a sprint. Null
    /// means "the team's default", which is a decision sprint planning makes
    /// rather than one stored here as a guess.
    /// </summary>
    public decimal? CapacityHoursPerDay { get; set; }

    public DateTimeOffset AddedAt { get; init; }

    public static TeamMember Create(
        Guid organizationId, Guid teamId, string userId, bool isLead,
        decimal? capacityHoursPerDay, DateTimeOffset now) =>
        new()
        {
            OrganizationId = organizationId,
            TeamId = teamId,
            UserId = userId,
            IsLead = isLead,
            CapacityHoursPerDay = capacityHoursPerDay,
            AddedAt = now,
        };
}
