using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Modules.Identity.Auth;

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

public interface ITokenService
{
    Task<TokenPair> IssueAsync(ApplicationUser user, CancellationToken cancellationToken);

    /// <summary>Rotates the refresh token. Returns null when the token is invalid; reuse of a consumed token revokes its whole family.</summary>
    Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken);

    Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Ends one session - one rotation family - belonging to one person. Scoped by user
    /// id as well as family id on purpose: a family id is a guessable-shaped uuid that
    /// arrives in a URL, and "revoke session X" must never be able to reach someone
    /// else's. Returns the number of tokens revoked, so a caller can tell "done" from
    /// "there was no such session".
    /// </summary>
    Task<int> RevokeFamilyForUserAsync(string userId, Guid familyId, CancellationToken cancellationToken);

    /// <summary>
    /// Signs the person out everywhere except <paramref name="keepFamilyId"/>. Null keeps
    /// nothing, which is what a password reset wants.
    /// </summary>
    Task RevokeAllForUserExceptAsync(string userId, Guid? keepFamilyId, CancellationToken cancellationToken);
}

public sealed class TokenService(
    IdentityDbContext db,
    UserManager<ApplicationUser> userManager,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    ILogger<TokenService> logger,
    IHttpContextAccessor? httpContextAccessor = null) : ITokenService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<TokenPair> IssueAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (refreshToken, entity) = NewRefreshToken(
            user.Id, familyId: Guid.CreateVersion7(), now, CurrentUserAgent());
        db.RefreshTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        var (accessToken, expiresAt) = await CreateAccessTokenAsync(user, now, entity.FamilyId);
        return new TokenPair(accessToken, expiresAt, refreshToken);
    }

    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = RefreshToken.Hash(refreshToken);

        // Read only for the family/user identity (both immutable). The usability check
        // is NOT made here: a read-check-write would let two concurrent refreshes of the
        // same token both pass and fork the family, which silently disables the reuse
        // detection this whole design exists for.
        var stored = await db.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Compare-and-swap: the database decides the winner. Exactly one caller can
        // move used_at from NULL to a value, so a racing second refresh consumes 0 rows
        // and takes the reuse path below.
        var consumed = await db.RefreshTokens
            .Where(t => t.TokenHash == hash
                && t.UsedAt == null
                && t.RevokedAt == null
                && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken);

        if (consumed == 0)
        {
            // Already consumed, revoked, expired, or it lost the race - the token leaked
            // (to an attacker, or the legitimate client double-refreshed). Either way the
            // safe move is the same: kill the whole family.
            logger.LogWarning("Refresh token reuse for user {UserId}; revoking the entire token family",
                stored.UserId);
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var user = await userManager.FindByIdAsync(stored.UserId);
        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // Consuming the old token and issuing its successor is one transaction: a
        // failure here must not strand the caller with a token that is already spent.
        // The user agent travels with the family rather than being re-read from this
        // request: a rotation is the same session as the sign-in that started it, and the
        // browser that refreshes at 3am from a service worker would otherwise rename it.
        var (newToken, newEntity) = NewRefreshToken(stored.UserId, stored.FamilyId, now, stored.UserAgent);
        db.RefreshTokens.Add(newEntity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var (accessToken, expiresAt) = await CreateAccessTokenAsync(user, now, stored.FamilyId);
        return new TokenPair(accessToken, expiresAt, newToken);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = RefreshToken.Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
        }
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    public async Task<int> RevokeFamilyForUserAsync(
        string userId, Guid familyId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await db.RefreshTokens
            .Where(t => t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    public async Task RevokeAllForUserExceptAsync(
        string userId, Guid? keepFamilyId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null
                && (keepFamilyId == null || t.FamilyId != keepFamilyId))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAccessTokenAsync(
        ApplicationUser user, DateTimeOffset now, Guid familyId)
    {
        var expiresAt = now.AddMinutes(_jwt.AccessTokenMinutes);
        // Short JWT claim names ("sub", "email", "name", "role") - the bearer handler is
        // configured with MapInboundClaims = false so these arrive unchanged, and all
        // clients (Nuxt BFF, Expo) read the same names.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            // Which session this token belongs to. The refresh cookie is scoped to the
            // refresh endpoint and so is absent everywhere else, which would leave
            // /me/sessions unable to say which row is "this browser" - the one thing that
            // list has to get right.
            new(PrincipalClaims.SessionId, familyId.ToString()),
        };
        claims.AddRange((await userManager.GetRolesAsync(user)).Select(r => new Claim("role", r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        var handler = new JwtSecurityTokenHandler();
        handler.OutboundClaimTypeMap.Clear();
        return (handler.WriteToken(token), expiresAt);
    }

    private (string Token, RefreshToken Entity) NewRefreshToken(
        string userId, Guid familyId, DateTimeOffset now, string? userAgent)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = RefreshToken.Hash(token),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_jwt.RefreshTokenDays),
            UserAgent = userAgent,
        };
        return (token, entity);
    }

    /// <summary>
    /// What the sessions list shows. Read here rather than passed in by every caller so
    /// that adding a new way to sign in cannot quietly produce an unlabelled session.
    /// Absent outside a request (a background sign-in has no browser), which is fine -
    /// the column is nullable and the UI says "unknown device".
    /// </summary>
    private string? CurrentUserAgent()
    {
        var value = httpContextAccessor?.HttpContext?.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value[..Math.Min(value.Length, RefreshToken.MaxUserAgentLength)];
    }
}
