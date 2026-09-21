using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aictiq.Modules.Identity.Domain;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Api.Seeding;

/// <summary>
/// Creates the small, deliberately predictable Acme workspace used by local demos.
/// It is intentionally an all-or-nothing seed: an existing Acme organization is treated
/// as user data and is never topped up on a later start.
/// </summary>
public static class DemoSeeder
{
    private const string AcmeSlug = "acme";
    private const int TicketCount = 87;

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await tenancy.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Slug == AcmeSlug, cancellationToken))
        {
            return;
        }

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DemoSeeder");
        var users = await CreateUsersAsync(scope.ServiceProvider, cancellationToken);
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var organization = Organization.Create(AcmeSlug, "Acme", users[0].Id,
            OrganizationSettings.Default with { TimeZone = "Europe/Sarajevo" }, now);
        var currentTenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();

        using (currentTenant.Use(organization.Id))
        {
            tenancy.Organizations.Add(organization);
            tenancy.Members.AddRange(users.Select((user, index) =>
                OrganizationMember.Create(organization.Id, user.Id, index == 0 ? OrgRole.Owner : OrgRole.Member, now)));

            var aictiq = Project.Create(organization.Id, "ACME", "Aictiq", "Aictiq's dogfooding roadmap", ProjectVisibility.Organization,
                "⚡", "#7c3aed", users[0].Id, now);
            var web = Project.Create(organization.Id, "WEB", "Acme Web", "Customer-facing web work", ProjectVisibility.Organization,
                "🌐", "#0284c7", users[0].Id, now);
            tenancy.Projects.AddRange(aictiq, web);
            tenancy.ProjectMembers.AddRange(users.SelectMany(user => new[]
            {
                ProjectMember.Create(organization.Id, aictiq.Id, user.Id, ProjectRole.Member, users[0].Id, now),
                ProjectMember.Create(organization.Id, web.Id, user.Id, ProjectRole.Member, users[0].Id, now),
            }));

            var core = Team.Create(organization.Id, aictiq.Id, "Core", "CORE", true, now);
            var webTeam = Team.Create(organization.Id, web.Id, "Web", "WEB", true, now);
            tenancy.Teams.AddRange(core, webTeam);
            tenancy.TeamMembers.AddRange(users.SelectMany((user, index) => new[]
            {
                TeamMember.Create(organization.Id, core.Id, user.Id, index == 0, user.IsAgent ? 0 : 6, now),
                TeamMember.Create(organization.Id, webTeam.Id, user.Id, index == 1, user.IsAgent ? 0 : 6, now),
            }));
            await tenancy.SaveChangesAsync(cancellationToken);

            var work = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>();
            await WorkflowEndpoints.EnsureDefaultAsync(work, organization.Id, aictiq.Id, cancellationToken);
            await WorkflowEndpoints.EnsureDefaultAsync(work, organization.Id, web.Id, cancellationToken);
            var aictiqStates = await StatesAsync(work, aictiq.Id, cancellationToken);
            var webStates = await StatesAsync(work, web.Id, cancellationToken);

            var labels = CreateLabels(organization.Id, aictiq.Id, web.Id);
            work.Labels.AddRange(labels);
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var completed = new Sprint { OrganizationId = organization.Id, TeamId = core.Id, Name = "Sprint 23", Goal = "Ship the board", StartsOn = today.AddDays(-42), EndsOn = today.AddDays(-29), State = SprintState.Completed, CreatedAt = now.AddDays(-43), StartedAt = now.AddDays(-42), CompletedAt = now.AddDays(-29) };
            var active = new Sprint { OrganizationId = organization.Id, TeamId = core.Id, Name = "Sprint 24", Goal = "Make planning fast", StartsOn = today.AddDays(-7), EndsOn = today.AddDays(7), State = SprintState.Active, AutoCreateNext = true, CreatedAt = now.AddDays(-12), StartedAt = now.AddDays(-7) };
            var planned = new Sprint { OrganizationId = organization.Id, TeamId = core.Id, Name = "Sprint 25", Goal = "Polish the agent experience", StartsOn = today.AddDays(8), EndsOn = today.AddDays(22), State = SprintState.Planned, AutoCreateNext = true, CreatedAt = now.AddDays(-2) };
            work.Sprints.AddRange(completed, active, planned);
            await work.SaveChangesAsync(cancellationToken);

            var random = new Random(104729);
            var items = BuildItems(organization.Id, aictiq, web, core, webTeam, users, aictiqStates, webStates,
                completed, active, planned, now, random);
            work.Items.AddRange(items);
            work.ProjectSequences.AddRange(
                new ProjectSequence { OrganizationId = organization.Id, ProjectId = aictiq.Id, NextNumber = items.Count(x => x.ProjectId == aictiq.Id) + 1 },
                new ProjectSequence { OrganizationId = organization.Id, ProjectId = web.Id, NextNumber = items.Count(x => x.ProjectId == web.Id) + 1 });
            work.ItemLabels.AddRange(items.Where((_, index) => index % 3 == 0).Select((item, index) => new ItemLabel
            {
                OrganizationId = organization.Id, ItemId = item.Id, LabelId = labels[index % labels.Count].Id,
                AddedAt = item.CreatedAt, AddedBy = item.CreatedBy,
            }));
            work.ItemHistory.AddRange(items.SelectMany((item, index) => History(organization.Id, item, users[index % users.Count].Id, index)));
            work.Comments.AddRange(items.Where((_, index) => index % 4 == 0).Select((item, index) => new Comment
            {
                OrganizationId = organization.Id, ItemId = item.Id, AuthorId = users[index % users.Count].Id,
                BodyMarkdown = "Demo discussion: this keeps the workspace feeling lived in.",
                BodyHtml = "<p>Demo discussion: this keeps the workspace feeling lived in.</p>",
                CreatedAt = item.CreatedAt.AddDays(1),
            }));
            work.SprintScopeLog.AddRange(items.Where(x => x.SprintId is not null).Select(item => new SprintScopeLog
            {
                OrganizationId = organization.Id, SprintId = item.SprintId!.Value, ItemId = item.Id,
                Change = SprintScopeChange.Added, Points = item.Points, RemainingHours = item.RemainingHours, At = item.CreatedAt,
            }));
            await work.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Seeded deterministic Acme demo workspace with roughly 300 work items");
    }

    private static async Task<List<ApplicationUser>> CreateUsersAsync(IServiceProvider services, CancellationToken ct)
    {
        var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var people = new[] { ("Avery", "Chen"), ("Blair", "Singh"), ("Casey", "Morgan"), ("Devon", "Ibrahim"), ("Emery", "Novak"), ("Finley", "Haddad") };
        var result = new List<ApplicationUser>();
        foreach (var (first, last) in people)
        {
            var email = $"{first.ToLowerInvariant()}@acme.test";
            var user = await manager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FirstName = first, LastName = last, CreatedAt = DateTimeOffset.UnixEpoch };
                var created = await manager.CreateAsync(user, "DemoPassword123!");
                if (!created.Succeeded) throw new InvalidOperationException($"Unable to create demo user {email}: {string.Join("; ", created.Errors.Select(x => x.Description))}");
            }
            result.Add(user);
        }
        foreach (var name in new[] { "Build Bot", "Triage Bot" })
        {
            var email = $"{name.Replace(" ", ".").ToLowerInvariant()}@acme.test";
            var agent = await manager.FindByEmailAsync(email);
            if (agent is null)
            {
                agent = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FirstName = name.Split(' ')[0], LastName = name.Split(' ')[1], IsAgent = true, AgentOwnerUserId = result[0].Id, CreatedAt = DateTimeOffset.UnixEpoch };
                var created = await manager.CreateAsync(agent);
                if (!created.Succeeded) throw new InvalidOperationException($"Unable to create demo agent {email}: {string.Join("; ", created.Errors.Select(x => x.Description))}");
            }
            result.Add(agent);
        }
        return result;
    }

    /// <summary>
    /// One representative state per category — the first by position, so Active resolves to
    /// "Active" rather than "In Review". A category is <em>not</em> unique among a project's
    /// states (the default workflow has two Active ones), so keying a dictionary on it
    /// directly throws, and the demo data cannot be seeded at all.
    /// </summary>
    private static async Task<Dictionary<WorkflowStateCategory, Guid>> StatesAsync(WorkItemsDbContext db, Guid projectId, CancellationToken ct)
    {
        var states = await db.WorkflowStates
            .Where(x => db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == projectId))
            .OrderBy(x => x.Position).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return states.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.First().Id);
    }

    private static List<Label> CreateLabels(Guid orgId, Guid aictiqId, Guid webId) =>
    [
        new() { OrganizationId = orgId, ProjectId = aictiqId, Name = "backend", Color = "#7c3aed", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = aictiqId, Name = "frontend", Color = "#0284c7", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = aictiqId, Name = "agent", Color = "#059669", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = webId, Name = "customer", Color = "#ea580c", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = webId, Name = "bug", Color = "#dc2626", Group = "type" },
    ];

    private static List<WorkItem> BuildItems(Guid orgId, Project aictiq, Project web, Team core, Team webTeam,
        IReadOnlyList<ApplicationUser> users, IReadOnlyDictionary<WorkflowStateCategory, Guid> aictiqStates,
        IReadOnlyDictionary<WorkflowStateCategory, Guid> webStates, Sprint completed, Sprint active, Sprint planned,
        DateTimeOffset now, Random random)
    {
        var items = new List<WorkItem>();
        var epics = Enumerable.Range(1, 3).Select(number => New(orgId, aictiq, number, WorkItemType.Epic, $"Aictiq roadmap {number}", null, core, null, aictiqStates, users, now, random)).ToList();
        items.AddRange(epics);
        var features = Enumerable.Range(1, 9).Select(number => New(orgId, aictiq, number + 3, WorkItemType.Feature, $"Roadmap capability {number}", epics[(number - 1) / 3].Id, core, null, aictiqStates, users, now, random)).ToList();
        items.AddRange(features);
        var stories = Enumerable.Range(1, TicketCount).Select(number => New(orgId, aictiq, number + 12, WorkItemType.Story,
            $"ACME-{number:000}: roadmap ticket", features[(number - 1) % features.Count].Id, core,
            number % 3 == 0 ? completed.Id : number % 3 == 1 ? active.Id : planned.Id, aictiqStates, users, now, random)).ToList();
        items.AddRange(stories);
        items.AddRange(Enumerable.Range(1, 160).Select(number => New(orgId, aictiq, number + 99, WorkItemType.Task,
            $"Implementation task {number}", stories[(number - 1) % stories.Count].Id, core,
            number % 3 == 0 ? completed.Id : number % 3 == 1 ? active.Id : null, aictiqStates, users, now, random)));
        items.AddRange(Enumerable.Range(1, 41).Select(number => New(orgId, web, number, WorkItemType.Bug,
            $"Customer issue {number}", null, webTeam, null, webStates, users, now, random)));
        return items;
    }

    private static WorkItem New(Guid orgId, Project project, int number, WorkItemType type, string title, Guid? parentId,
        Team team, Guid? sprintId, IReadOnlyDictionary<WorkflowStateCategory, Guid> states, IReadOnlyList<ApplicationUser> users,
        DateTimeOffset now, Random random)
    {
        var age = random.Next(0, 61);
        var category = age > 40 ? WorkflowStateCategory.Completed : age > 20 ? WorkflowStateCategory.Active : WorkflowStateCategory.Proposed;
        var created = now.AddDays(-age).AddHours(-random.Next(0, 12));
        var isTask = type is WorkItemType.Task or WorkItemType.Bug;
        return new WorkItem
        {
            OrganizationId = orgId, ProjectId = project.Id, ProjectKey = project.Key, Number = number, Type = type, Title = title,
            DescriptionMarkdown = $"Demo data for **{title}**.", DescriptionHtml = $"<p>Demo data for <strong>{title}</strong>.</p>",
            StateId = states[category], Priority = (WorkItemPriority)random.Next(0, 5),
            // A share of the proposed work is left unassigned on purpose: a workspace in
            // which every item already has an owner has nothing for a newly connected agent
            // to pick up, and `list_ready_work` would greet it with an empty list.
            AssigneeId = category == WorkflowStateCategory.Proposed && number % 3 == 0 ? null : users[random.Next(users.Count)].Id,
            TeamId = team.Id, SprintId = sprintId, ParentId = parentId, Rank = $"0|{number:D6}",
            Points = type == WorkItemType.Story ? random.Next(1, 9) : null,
            // `ck_items_hours_task_or_bug` wants all three hour columns NULL on anything
            // that is not a Task or a Bug — a zero is a value, and the constraint refuses it.
            EstimateHours = isTask ? random.Next(2, 17) : null,
            RemainingHours = !isTask ? null : category != WorkflowStateCategory.Completed ? random.Next(1, 9) : 0,
            CompletedHours = !isTask ? null : category == WorkflowStateCategory.Completed ? random.Next(2, 17) : 0,
            CreatedBy = users[number % users.Count].Id, CreatedAt = created, UpdatedAt = created.AddDays(random.Next(0, Math.Max(1, age))),
            CompletedAt = category == WorkflowStateCategory.Completed ? created.AddDays(Math.Max(1, age / 2)) : null,
        };
    }

    private static IEnumerable<ItemHistory> History(Guid orgId, WorkItem item, string actorId, int index)
    {
        var eventId = Guid.CreateVersion7();
        yield return new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actorId, At = item.CreatedAt, Field = "created", NewValue = item.Title, EventId = eventId };
        if (index % 2 == 0)
            yield return new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actorId, At = item.UpdatedAt, Field = "state", OldValue = "New", NewValue = "Active", EventId = Guid.CreateVersion7() };
    }
}
