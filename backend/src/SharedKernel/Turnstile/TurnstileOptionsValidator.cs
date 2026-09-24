using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Turnstile;

/// <summary>
/// Both keys or neither. Compose binds an unset variable to the empty string, so "neither"
/// arrives as two blanks and is the ordinary development and self-hosted configuration.
/// </summary>
public sealed class TurnstileOptionsValidator : IValidateOptions<TurnstileOptions>
{
    public ValidateOptionsResult Validate(string? name, TurnstileOptions options)
    {
        var hasSite = !string.IsNullOrWhiteSpace(options.SiteKey);
        var hasSecret = !string.IsNullOrWhiteSpace(options.SecretKey);
        if (hasSite != hasSecret)
        {
            return ValidateOptionsResult.Fail(
                "Turnstile needs both Turnstile:SiteKey and Turnstile:SecretKey, or neither.");
        }

        if (!Uri.TryCreate(options.VerifyUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail("Turnstile:VerifyUrl must be an absolute URL.");
        }

        return options.TimeoutSeconds is > 0 and <= 60
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Turnstile:TimeoutSeconds must be between 1 and 60.");
    }
}
