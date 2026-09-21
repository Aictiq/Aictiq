using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Notifications.Domain;

/// <summary>The terminal and non-terminal states of a queued message.</summary>
public static class EmailStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";

    /// <summary>Exhausted its attempts against a relay that is configured but not working.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// There is no relay on this instance. Terminal without ever having been attempted:
    /// retrying cannot help, and the row is kept so an operator can see what would have
    /// been sent.
    /// </summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// One rendered message waiting for a relay. Not a tenant table on purpose: it is drained
/// by a background sweep that spans every organization, and a recipient's address is not
/// a tenant-scoped fact at the moment of sending.
/// </summary>
/// <remarks>
/// The id is the id of the <see cref="SharedKernel.Email.SendEmailRequested"/> event that
/// produced it. Outbox delivery is at least once, so the handler can run twice for one
/// event; the primary key — the database, not a check in the handler — is what makes the
/// second run a no-op instead of a second email.
/// </remarks>
public sealed class EmailOutboxMessage : EntityBase
{
    public required string ToAddress { get; init; }
    public required string Subject { get; init; }
    public required string BodyHtml { get; init; }
    public required string BodyText { get; init; }

    /// <summary>Which template rendered it — for operators reading the table, not for sending.</summary>
    public required string Template { get; init; }

    public string Status { get; set; } = EmailStatus.Pending;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>Not before this instant. Moved forward by the delivery service's backoff.</summary>
    public DateTimeOffset SendAfter { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
