namespace Aictiq.Modules.Identity.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "aictiq-api";
    public string Audience { get; set; } = "aictiq-clients";
    /// <summary>HS256 signing key; ≥32 chars enforced at startup. Never in the repo.</summary>
    public string Key { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    /// <summary>Refresh lifetime. Rotation means a browser session survives this long idle.</summary>
    public int RefreshTokenDays { get; set; } = 30;
}
