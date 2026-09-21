using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>
/// Who can see a project without being named on it.
///
/// <see cref="Private"/> is 0 so that a row with an unset visibility is the closed one:
/// the accident should hide a project, never expose one. New projects are created
/// <see cref="Organization"/> because that is what a team of ten actually wants, but that
/// is a decision the endpoint makes out loud rather than one the type default smuggles in.
/// </summary>
public enum ProjectVisibility
{
    /// <summary>Only people explicitly added — plus the organization's Owners and Admins.</summary>
    Private = 0,

    /// <summary>Every organization member is implicitly in it; Guests implicitly as Guests.</summary>
    Organization = 1,
}

/// <summary>
/// An isolated workspace: its own key, workflow, labels, wiki and boards.
///
/// The <see cref="Key"/> is the part that matters and the part that cannot change. It is
/// the prefix of every item identifier the team will ever quote — in commit messages, in
/// chat, in an agent's configuration, in a bug report filed by a customer — so renaming it
/// would silently break every reference already written down. The display name is what
/// changes when a team renames a project.
/// </summary>
public sealed class Project : TenantEntity, IAudited
{
    public const int MinKeyLength = KeyFormat.MinLength;
    public const int MaxKeyLength = KeyFormat.MaxLength;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 2000;

    /// <summary>Immutable. See the type remarks — this is in every link and every item id.</summary>
    public required string Key { get; init; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public ProjectVisibility Visibility { get; set; }

    /// <summary>A single emoji, or null. Rendered beside the name in every list.</summary>
    public string? Icon { get; set; }

    /// <summary>A hex colour (<c>#rrggbb</c>), or null for the theme's default.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Archived, not deleted: the team's history stays readable and searchable, and every
    /// write path is closed by <c>RequireProjectWritable</c>. Un-archiving restores it.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public required string CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public static Project Create(
        Guid organizationId, string key, string name, string? description,
        ProjectVisibility visibility, string? icon, string? color,
        string createdBy, DateTimeOffset now)
    {
        var project = new Project
        {
            OrganizationId = organizationId,
            Key = key,
            Name = name,
            Description = description,
            Visibility = visibility,
            Icon = icon,
            Color = color,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        project.Raise(new ProjectCreated(organizationId, project.Id, key, name, createdBy) { OccurredAt = now });
        return project;
    }

    public void Update(
        string name, string? description, ProjectVisibility visibility,
        string? icon, string? color, DateTimeOffset now)
    {
        Name = name;
        Description = description;
        Visibility = visibility;
        Icon = icon;
        Color = color;
        UpdatedAt = now;
    }

    public void Archive(string archivedBy, DateTimeOffset now)
    {
        if (IsArchived)
        {
            return;
        }

        ArchivedAt = now;
        UpdatedAt = now;
        Raise(new ProjectArchived(OrganizationId, Id, Key, archivedBy) { OccurredAt = now });
    }

    public void Unarchive(string unarchivedBy, DateTimeOffset now)
    {
        if (!IsArchived)
        {
            return;
        }

        ArchivedAt = null;
        UpdatedAt = now;
        // A separate event rather than ProjectArchived carrying a flag: the modules that
        // consume it (Wiki, WorkItems) act on it, and "archived: false" is not something a
        // handler name can describe.
        Raise(new ProjectUnarchived(OrganizationId, Id, Key, unarchivedBy) { OccurredAt = now });
    }
}
