namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// Messages a trigger raises with <c>RAISE EXCEPTION ... USING ERRCODE = 'P0001'</c> to
/// name an invariant the database refused to break.
///
/// <c>P0001</c> (raise_exception) is the same SQLSTATE for every such raise, so the
/// message text is what distinguishes them. Both sides — the migration that writes the
/// trigger and the handler that turns the failure into a status code — read the name
/// from here, so a rename cannot leave one of them behind.
/// </summary>
public static class DatabaseSignals
{
    /// <summary>tenancy.ensure_org_has_owner(): the last Owner would have disappeared.</summary>
    public const string LastOwner = "last_owner";
}
