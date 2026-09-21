namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// How hard the Workers process tries to get a queued message out, bound from
/// <c>Email:Delivery</c>.
/// </summary>
public sealed class EmailDeliveryOptions
{
    public const string SectionName = "Email:Delivery";

    /// <summary>How often the queue is swept. Email is not interactive; seconds are fine.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Messages claimed per sweep. One SMTP connection each, so kept modest.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Attempts before a message is given up on as <c>failed</c>. Fewer than the outbox's
    /// ten: by the time we are here the event is safely recorded, and a relay that has
    /// refused a message five times over an hour is not going to accept the sixth.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry delay; doubles with each attempt up to <see cref="MaxRetrySeconds"/>.</summary>
    public int BaseRetrySeconds { get; set; } = 60;

    public int MaxRetrySeconds { get; set; } = 3600;

    /// <summary>
    /// Settled rows (<c>sent</c>, <c>skipped</c>) older than this are deleted — the queue
    /// is unbounded otherwise. <c>failed</c> rows are never collected: like a dead-lettered
    /// outbox message, a message nobody could deliver is an open incident, not history.
    /// 0 keeps everything.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}
