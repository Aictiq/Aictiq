namespace Aictiq.Modules.Identity.Workers;

/// <summary>
/// Retention windows for the append-only tables Identity owns. Every value is in days;
/// set one to 0 to disable that purge (e.g. when a compliance regime says audit rows
/// must be kept forever - export them elsewhere rather than letting the table grow).
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>How often the cleanup sweep runs. Cheap enough to run hourly.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Rows deleted per statement, per table. Bounded so a first run against years of
    /// backlog can't take a long lock or blow up the WAL - the sweep simply loops.
    /// </summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>
    /// Consumed, revoked or expired refresh tokens older than this are deleted. Keep it
    /// comfortably longer than Jwt:RefreshTokenDays so a live rotation chain - the
    /// evidence reuse detection relies on - is never truncated under an active session.
    /// </summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// Revoked or long-expired personal access tokens older than this are deleted. Live
    /// ones are never touched, whatever their age - a token in daily use for two years is
    /// still a working credential, not a stale row.
    /// </summary>
    public int RevokedAccessTokenDays { get; set; } = 90;

    /// <summary>Audit rows older than this are deleted. 0 keeps them forever.</summary>
    public int AuditLogDays { get; set; } = 365;

    /// <summary>
    /// Successfully processed outbox rows older than this are deleted. Dead-lettered rows
    /// are NEVER purged here - they are unresolved incidents, not history.
    /// </summary>
    public int ProcessedOutboxDays { get; set; } = 7;
}
