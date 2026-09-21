namespace Aictiq.SharedKernel.Http;

/// <summary>
/// Endpoint metadata: this route sets its own <c>Cache-Control</c> and means it.
///
/// The API's security headers put <c>no-store</c> on every backend route, which is right
/// for the private JSON that is nearly all of them. The exception is a response that is
/// deliberately cacheable — the avatar redirect, whose whole point is that a browser may
/// keep it rather than re-ask on every screen.
///
/// A marker rather than "whatever the endpoint already set wins": ASP.NET's own health
/// check middleware writes <c>no-store, no-cache</c>, so a permissive rule would silently
/// change routes nobody was thinking about. Opting out has to be something a person typed.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class CacheableResponseAttribute : Attribute;
