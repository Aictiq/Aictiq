using Microsoft.Extensions.Configuration;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Identity.Auth;

/// <summary>
/// Whether a new password account must confirm its address before it can sign in.
///
/// On by default, and only ever in force on an instance that can send mail: without a
/// relay there is no way to deliver the link, and a supported "no email" deployment must
/// still be able to take its first sign-up. <c>Features:EmailConfirmation=false</c> turns
/// it off for an operator who vets accounts some other way.
///
/// Accounts that arrive already proven are never asked: an invitation accepted from the
/// address it was sent to, a provider that verified the address, a seeded administrator.
/// </summary>
public sealed class EmailConfirmationPolicy(IConfiguration configuration, IEmailCapabilities email)
{
    public const string FeatureKey = "Features:EmailConfirmation";

    public bool Required => email.IsConfigured && configuration.GetValue(FeatureKey, true);
}
