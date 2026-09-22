namespace Aictiq.Modules.Identity.Domain;

/// <summary>
/// The rules for a profile picture, in one place because three endpoints and one commit
/// check all have to agree on them.
///
/// Bytes never pass through the API (see <c>IBlobStorage</c>): the browser PUTs to a
/// presigned URL and then asks the API to record the key. That split is what makes the
/// commit check load-bearing - the signature fixes the content type but cannot bound the
/// size, so the object is HEADed after the fact and deleted if it came back too big.
/// </summary>
public static class Avatar
{
    /// <summary>
    /// Two megabytes. An avatar is displayed at 36 pixels; anything larger is a photo
    /// somebody dragged in by accident, and storing it would only make every page slower.
    /// </summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Raster formats a browser will render inline. SVG is deliberately absent: it is a
    /// document that can carry script, and an avatar is served from the object store
    /// where a CSP does not reach it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Extensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",
        };

    /// <summary>
    /// How long a presigned avatar GET stays valid. Longer than the store's default,
    /// because this URL is not a download link a person pastes anywhere - it is fetched
    /// by an <c>&lt;img&gt;</c> on every screen - and it must outlive the redirect the
    /// browser cached to reach it.
    /// </summary>
    public static readonly TimeSpan DownloadTtl = TimeSpan.FromHours(2);

    /// <summary>
    /// How long the browser may cache the redirect. Half the signature's life, so a
    /// cached redirect can never be followed to an expired URL. Safe to be this long
    /// because a new avatar gets a new key and therefore a new URL.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null && Extensions.ContainsKey(contentType.Trim());

    /// <summary>
    /// Every one of a person's avatars lives under this prefix, and the commit endpoint
    /// refuses a key outside it. Without that check a presigned URL for one's own avatar
    /// would be a way to claim any object in the bucket as one's picture.
    /// </summary>
    public static string PrefixFor(string userId) => $"users/{userId}/avatar/";

    /// <summary>
    /// A fresh key per upload rather than one stable <c>avatar.png</c>: the key is what
    /// the browser caches against, so replacing a picture in place would leave every
    /// colleague looking at the old one until their cache expired.
    /// </summary>
    public static string NewKey(string userId, string contentType) =>
        PrefixFor(userId) + Guid.CreateVersion7().ToString("n") + Extensions[contentType.Trim()];

    public static bool BelongsTo(string key, string userId) =>
        key.StartsWith(PrefixFor(userId), StringComparison.Ordinal);

    /// <summary>
    /// A short, stable token for the current avatar - the last path segment of its key.
    /// The frontend hangs it on the avatar URL so a changed picture is a changed URL,
    /// which is what lets the redirect be cached for an hour.
    /// </summary>
    public static string? VersionOf(string? key) =>
        key is null ? null : key[(key.LastIndexOf('/') + 1)..];
}
