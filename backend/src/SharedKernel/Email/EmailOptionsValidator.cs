using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Email;

/// <summary>
/// Validates <see cref="EmailOptions"/> where DataAnnotations cannot: an instance with
/// no relay is a supported deployment, and compose deployments reach one by leaving
/// <c>Email:FromAddress</c> empty — an environment variable binds as an empty string,
/// never as null, and <see cref="EmailAddressAttribute"/> refuses the empty string.
/// Only an address that was actually set is checked for shape.
/// </summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FromAddress)) return ValidateOptionsResult.Success;
        if (new EmailAddressAttribute().IsValid(options.FromAddress)) return ValidateOptionsResult.Success;
        return ValidateOptionsResult.Fail(
            $"Email:FromAddress '{options.FromAddress}' is not a valid e-mail address.");
    }
}
