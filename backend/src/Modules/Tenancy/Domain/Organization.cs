using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// The tenant boundary itself.
///
/// Note what this is <b>not</b>: a <see cref="TenantEntity"/>. Every other tenant table
/// carries an <c>organization_id</c> and is filtered by it; the organization row is the
/// thing being pointed at, so filtering it the same way would make it invisible before a
/// tenant is established - and resolving the tenant is precisely what reads it.
/// </summary>
public sealed class Organization : EntityBase, IAudited
{
    /// <summary>
    /// The URL segment. Immutable after creation: a slug is in every link a team has
    /// bookmarked, pasted into chat, or written into an agent's configuration, and a
    /// rename that silently breaks all of them is not a feature. Renaming the
    /// organization changes <see cref="Name"/> only.
    /// </summary>
    public required string Slug { get; init; }

    public required string Name { get; set; }

    /// <summary>Billing plan. Self-hosted installations stay on the default.</summary>
    public string Plan { get; set; } = OrganizationPlans.SelfHosted;

    public OrganizationSettings Settings { get; set; } = OrganizationSettings.Default;

    /// <summary>The user who created it - kept even after they leave, for support.</summary>
    public required string CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; private set; }

    public static Organization Create(
        string slug, string name, string createdBy, OrganizationSettings settings, DateTimeOffset now)
    {
        var organization = new Organization
        {
            Slug = slug,
            Name = name,
            Settings = settings,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        organization.Raise(new OrganizationCreated(organization.Id, slug, name, createdBy) { OccurredAt = now });
        return organization;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = name;
        UpdatedAt = now;
    }

    /// <summary>
    /// Billing decides the plan; Tenancy stores it because every limit check
    /// reads it here. Only <c>OrganizationPlanHandler</c> calls this.
    /// </summary>
    public void ChangePlan(string plan, DateTimeOffset now)
    {
        Plan = plan;
        UpdatedAt = now;
    }

    public void UpdateSettings(OrganizationSettings settings, DateTimeOffset now)
    {
        Settings = settings;
        UpdatedAt = now;
    }
}

/// <summary>
/// The address of an organization that was deleted for good - the slug and nothing else.
/// Everything the organization held is gone, but the slug is in bookmarks, pasted links and
/// agent configuration, and releasing it would hand all of those to whoever registered the
/// name next. <c>tenancy.reject_retired_slug</c> is what refuses a new organization one.
/// </summary>
public sealed class RetiredSlug
{
    public required string Slug { get; init; }
}

public static class OrganizationPlans
{
    public const string SelfHosted = "self_hosted";
}
