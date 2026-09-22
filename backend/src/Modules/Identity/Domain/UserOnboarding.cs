namespace Aictiq.Modules.Identity.Domain;

/// <summary>
/// Where a person stands in the first-login product tour - account state, not
/// tenant data: it belongs to the human, and joining or switching organizations must not
/// repeat it. A missing row reads as <see cref="TourStatuses.NotStarted"/>; GET never
/// creates one, so a person who has never been asked simply has no row yet. Agents are
/// refused on write - a tour is for people, and an agent's account is managed by its
/// owner. Existing people were seeded as dismissed by the rollout migration, so release
/// day interrupts nobody who was already here.
/// </summary>
public sealed class UserOnboarding
{
    /// <summary>
    /// Bumped only when the tour's content changes meaningfully. A bump alone never
    /// replays anything; it exists so a future tour can tell an old "completed" from a
    /// new one.
    /// </summary>
    public const int CurrentTourVersion = 1;

    /// <summary>The tour's lifecycle; the database constrains the set.</summary>
    public static class TourStatuses
    {
        public const string NotStarted = "not_started";
        public const string InProgress = "in_progress";
        public const string Deferred = "deferred";
        public const string Dismissed = "dismissed";
        public const string Completed = "completed";

        public static readonly string[] All = [NotStarted, InProgress, Deferred, Dismissed, Completed];

        public static bool IsKnown(string? status) =>
            status is NotStarted or InProgress or Deferred or Dismissed or Completed;
    }

    /// <summary>
    /// Stable step ids shared with the SPA's declarative tour registry. Validated at the
    /// API boundary: the frontend owns the tour's content, and the API only refuses to
    /// persist a step id no version of the tour will ever send.
    /// </summary>
    public static class TourSteps
    {
        public const string Navigation = "navigation";
        public const string Organization = "organization";
        public const string ProjectTeam = "project-team";
        public const string Board = "board";
        public const string PrepareItem = "prepare-item";
        public const string ProjectKnowledge = "project-knowledge";
        public const string FactoryConcepts = "factory-concepts";
        public const string RunnerSetup = "runner-setup";
        public const string Handoff = "handoff";
        public const string FollowUp = "follow-up";

        public static bool IsKnown(string? stepId) =>
            stepId is Navigation or Organization or ProjectTeam or Board or PrepareItem
                or ProjectKnowledge or FactoryConcepts or RunnerSetup or Handoff or FollowUp;
    }

    /// <summary>The person this state belongs to - the primary key, one row per account.</summary>
    public required string UserId { get; init; }

    public int TourVersion { get; set; } = CurrentTourVersion;

    public string Status { get; set; } = TourStatuses.NotStarted;

    /// <summary>Where a paused tour resumes from; cleared once the tour ends for good.</summary>
    public string? LastStepId { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>xmin. Echoed back by the client or the write answers 409.</summary>
    public uint Version { get; private set; }
}
