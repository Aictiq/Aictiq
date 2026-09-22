using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Email;

/// <summary>
/// "Send this email." Raised by whichever module has something to say - Tenancy for an
/// invitation, WorkItems for a mention - and consumed by the Notifications module in the
/// Workers process.
///
/// It lives in SharedKernel rather than in Notifications because a module may not
/// reference another module's types: this is the shared vocabulary that lets Tenancy ask
/// for an email without knowing that a Notifications module exists, let alone how it
/// renders or sends one.
/// </summary>
/// <param name="Template">
/// A template name (<c>invitation</c>, <c>mention</c>, …), not a rendered body. The
/// wording of an email is the notification module's business, and a payload that
/// travelled through the outbox as finished HTML could never be corrected after the fact.
/// </param>
/// <param name="Variables">
/// Everything the template needs, flat and already stringified. Flat because the payload
/// is stored as jsonb and read back by a process that may be a version ahead: a missing
/// key renders empty, where a shape change would dead-letter the message.
/// </param>
public sealed record SendEmailRequested(
    string To,
    string Template,
    IReadOnlyDictionary<string, string> Variables) : DomainEvent, IIntegrationEvent;
