using Microsoft.AspNetCore.DataProtection;

namespace Aictiq.Modules.WorkItems.Jira;

/// <summary>
/// The Jira API token between the request that queued an import and the worker that runs it.
/// The API and Workers share one key ring, so either can read what the other wrote; the job
/// clears it when it ends.
/// </summary>
public sealed class JiraTokenProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Aictiq.WorkItems.JiraImportToken.v1");
    public string Protect(string token) => _protector.Protect(token);
    public string Unprotect(string protectedToken) => _protector.Unprotect(protectedToken);
}
