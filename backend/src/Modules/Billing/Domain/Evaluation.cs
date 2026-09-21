using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Billing.Domain;

/// <summary>
/// The one evaluation a hosted organization is born with: thirty days of the
/// Hosted entitlement, no card. Created exactly once, when the organization is created —
/// the handler answers <c>OrganizationCreated</c> and the unique index on the organization
/// is what makes "once" a database fact, so a redelivered event, a restart, or two
/// deliveries racing cannot mint a second window.
///
/// Expiry never charges anybody and never deletes anything. It flips the same read-only
/// switch a lapsed payment uses — reads, downloads and exports keep working, ordinary
/// writes and new agent dispatches stop — until an explicit checkout says otherwise.
/// Inviting people, restarting Workers, or asking for a plan cannot move
/// <see cref="EndsAt"/>: nothing writes to this row after the day it is born.
/// </summary>
public sealed class Evaluation : TenantEntity
{
    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public uint Version { get; private set; }

    public bool Expired(DateTimeOffset now) => EndsAt <= now;
}
