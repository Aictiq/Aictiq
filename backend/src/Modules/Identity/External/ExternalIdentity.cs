using System.Security.Claims;

namespace Aictiq.Modules.Identity.External;

/// <summary>
/// What a provider told us about the person, normalised.
///
/// <paramref name="EmailVerified"/> is the field the whole flow turns on: an address the
/// provider has not verified proves nothing about who is holding it, so it can never be
/// used to find - let alone create - an account. Google states it directly;
/// GitHub is asked for the primary <em>verified</em> address and given nothing else.
/// </summary>
public sealed record ExternalIdentity(
    string Provider, string ProviderKey, string? Email, bool EmailVerified,
    string? FirstName, string? LastName)
{
    /// <summary>Claim types are the short JWT-style names, as everywhere else in Aictiq.</summary>
    public const string ProviderKeyClaim = "sub";
    public const string EmailClaim = "email";
    public const string EmailVerifiedClaim = "email_verified";
    public const string FirstNameClaim = "given_name";
    public const string LastNameClaim = "family_name";

    public static ExternalIdentity? FromPrincipal(string provider, ClaimsPrincipal principal)
    {
        var key = principal.FindFirstValue(ProviderKeyClaim);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return new ExternalIdentity(
            provider,
            key,
            principal.FindFirstValue(EmailClaim),
            string.Equals(principal.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase),
            principal.FindFirstValue(FirstNameClaim),
            principal.FindFirstValue(LastNameClaim));
    }

    /// <summary>
    /// Splits whatever the provider calls a display name into the two fields Aictiq stores.
    /// Never blank: an account with no name renders as an empty row everywhere.
    /// </summary>
    public static (string First, string Last) SplitName(string? displayName, string? email)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length == 0)
        {
            var local = email?.Split('@')[0] ?? "";
            return (local.Length > 0 ? local : "Someone", "");
        }

        var space = name.LastIndexOf(' ');
        return space <= 0 ? (name, "") : (name[..space].Trim(), name[(space + 1)..].Trim());
    }
}
