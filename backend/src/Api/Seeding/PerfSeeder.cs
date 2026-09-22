using System.Diagnostics;
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
/// Builds a performance-scale workspace under the "perf" organization: 20 projects,
/// 100k work items, 400k history rows and 50k comments, inserted in plain EF batches
/// rather than through the API. Like the demo seed it is all-or-nothing - an existing
/// perf organization is user data and is never topped up on a later start.
/// </summary>
public static class PerfSeeder
{
    private const string PerfSlug = "perf";
    private const string Password = "PerfPassword123!";
    private const int ProjectCount = 20;
    private const int ItemsPerProject = 5_000;
    private const int ItemBatch = 2_000;
    private const int HistoryBatch = 20_000;
    private const int CommentBatch = 10_000;

    public static async Task SeedAsync(IServiceProvider services, string? ownerUserId, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await tenancy.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Slug == PerfSlug, cancellationToken))
        {
            return;
        }

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PerfSeeder");
        if (string.IsNullOrEmpty(ownerUserId))
        {
            logger.LogWarning("Perf seeding needs an owner - set Seed:AdminEmail and Seed:AdminPassword. Skipping.");
            return;
        }

        logger.LogInformation("PerfSeeder: creating {Projects} projects with {Items} items each under /{Slug}",
            ProjectCount, ItemsPerProject, PerfSlug);
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(104729);
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var users = await CreateUsersAsync(scope.ServiceProvider, cancellationToken);
        var organization = Organization.Create(PerfSlug, "Perf", ownerUserId, OrganizationSettings.Default, now);
        var currentTenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();
        var roster = new List<(string UserId, bool IsAgent)> { (ownerUserId, false) };
        roster.AddRange(users.Select(user => (user.Id, user.IsAgent)));

        using (currentTenant.Use(organization.Id))
        {
            tenancy.Organizations.Add(organization);
            tenancy.Members.Add(OrganizationMember.Create(organization.Id, ownerUserId, OrgRole.Owner, now));
            tenancy.Members.AddRange(users.Select(user =>
                OrganizationMember.Create(organization.Id, user.Id, OrgRole.Member, now)));

            var projects = new List<(Project Project, Team Team)>();
            for (var index = 1; index <= ProjectCount; index++)
            {
                var project = Project.Create(organization.Id, $"P{index:D2}", $"Perf Project {index:D2}",
                    $"Performance-scale data, project {index}.", ProjectVisibility.Organization,
                    "⚡", index % 2 == 0 ? "#0284c7" : "#7c3aed", ownerUserId, now);
                var team = Team.Create(organization.Id, project.Id, $"Team {index:D2}", $"T{index:D2}", true, now);
                tenancy.Projects.Add(project);
                tenancy.Teams.Add(team);
                tenancy.ProjectMembers.AddRange(roster.Select(member =>
                    ProjectMember.Create(organization.Id, project.Id, member.UserId, ProjectRole.Member, ownerUserId, now)));
                tenancy.TeamMembers.AddRange(roster.Select(member =>
                    TeamMember.Create(organization.Id, team.Id, member.UserId, member.UserId == ownerUserId, member.IsAgent ? 0 : 6, now)));
                projects.Add((project, team));
            }

            await tenancy.SaveChangesAsync(cancellationToken);

            var work = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>();
            work.ChangeTracker.AutoDetectChangesEnabled = false;
            var totalItems = 0;
            var totalHistory = 0;
            var totalComments = 0;

            for (var index = 0; index < projects.Count; index++)
            {
                var (project, team) = projects[index];
                await WorkflowEndpoints.EnsureDefaultAsync(work, organization.Id, project.Id, cancellationToken);
                var states = await StatesAsync(work, project.Id, cancellationToken);

                var labels = CreateLabels(organization.Id, project.Id);
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                var completed = new Sprint { OrganizationId = organization.Id, TeamId = team.Id, Name = $"Sprint {index * 3 + 1}", Goal = "Clear the backlog", StartsOn = today.AddDays(-42), EndsOn = today.AddDays(-29), State = SprintState.Completed, CreatedAt = now.AddDays(-43), StartedAt = now.AddDays(-42), CompletedAt = now.AddDays(-29) };
                var active = new Sprint { OrganizationId = organization.Id, TeamId = team.Id, Name = $"Sprint {index * 3 + 2}", Goal = "Keep the board moving", StartsOn = today.AddDays(-7), EndsOn = today.AddDays(7), State = SprintState.Active, AutoCreateNext = true, CreatedAt = now.AddDays(-12), StartedAt = now.AddDays(-7) };
                var planned = new Sprint { OrganizationId = organization.Id, TeamId = team.Id, Name = $"Sprint {index * 3 + 3}", Goal = "Scale the dataset", StartsOn = today.AddDays(8), EndsOn = today.AddDays(22), State = SprintState.Planned, AutoCreateNext = true, CreatedAt = now.AddDays(-2) };
                work.Labels.AddRange(labels);
                work.Sprints.AddRange(completed, active, planned);
                work.ProjectSequences.Add(new ProjectSequence { OrganizationId = organization.Id, ProjectId = project.Id, NextNumber = ItemsPerProject + 1 });
                await work.SaveChangesAsync(cancellationToken);
                work.ChangeTracker.Clear();

                var items = BuildItems(organization.Id, project, team, completed, active, planned, users, states, now, random);
                await SaveBatchesAsync(work, items, ItemBatch, cancellationToken);

                var history = BuildHistory(organization.Id, items, users, random);
                await SaveBatchesAsync(work, history, HistoryBatch, cancellationToken);

                var comments = items.Where(item => item.Number % 2 == 0).Select((item, i) => new Comment
                {
                    OrganizationId = organization.Id, ItemId = item.Id, AuthorId = users[i % users.Count].Id,
                    BodyMarkdown = $"Perf discussion for **{item.Key}**: keeping the dataset feeling lived in.",
                    BodyHtml = $"<p>Perf discussion for <strong>{item.Key}</strong>: keeping the dataset feeling lived in.</p>",
                    CreatedAt = item.CreatedAt.AddDays(1),
                }).ToList();
                await SaveBatchesAsync(work, comments, CommentBatch, cancellationToken);

                await SaveBatchesAsync(work, items.Where(item => item.Number % 5 == 0).Select((item, i) => new ItemLabel
                {
                    OrganizationId = organization.Id, ItemId = item.Id, LabelId = labels[i % labels.Count].Id,
                    AddedAt = item.CreatedAt, AddedBy = item.CreatedBy,
                }), ItemBatch, cancellationToken);

                await SaveBatchesAsync(work, items.Where(item => item.SprintId is not null).Select(item => new SprintScopeLog
                {
                    OrganizationId = organization.Id, SprintId = item.SprintId!.Value, ItemId = item.Id,
                    Change = SprintScopeChange.Added, Points = item.Points, RemainingHours = item.RemainingHours, At = item.CreatedAt,
                }), CommentBatch, cancellationToken);

                totalItems += items.Count;
                totalHistory += history.Count;
                totalComments += comments.Count;
                logger.LogInformation("PerfSeeder: project {Index}/{Count}, items {TotalItems}", index + 1, ProjectCount, totalItems);
            }

            logger.LogInformation(
                "PerfSeeder: seeded {Projects} projects, {Items} items, {History} history rows and {Comments} comments in {Seconds:F0}s",
                ProjectCount, totalItems, totalHistory, totalComments, stopwatch.Elapsed.TotalSeconds);
        }
    }

    private static async Task SaveBatchesAsync<T>(WorkItemsDbContext work, IEnumerable<T> rows, int batchSize, CancellationToken ct)
        where T : class
    {
        foreach (var batch in rows.Chunk(batchSize))
        {
            work.Set<T>().AddRange(batch);
            await work.SaveChangesAsync(ct);
            work.ChangeTracker.Clear();
        }
    }

    private static async Task<List<ApplicationUser>> CreateUsersAsync(IServiceProvider services, CancellationToken ct)
    {
        var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var people = new[] { ("Avery", "Chen"), ("Blair", "Singh"), ("Casey", "Morgan"), ("Devon", "Ibrahim"), ("Emery", "Novak"), ("Finley", "Haddad") };
        var result = new List<ApplicationUser>();
        foreach (var (first, last) in people)
        {
            var email = $"{first.ToLowerInvariant()}@perf.test";
            var user = await manager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FirstName = first, LastName = last, CreatedAt = DateTimeOffset.UnixEpoch };
                var created = await manager.CreateAsync(user, Password);
                if (!created.Succeeded) throw new InvalidOperationException($"Unable to create perf user {email}: {string.Join("; ", created.Errors.Select(x => x.Description))}");
            }
            result.Add(user);
        }
        foreach (var name in new[] { "Build Bot", "Triage Bot" })
        {
            var email = $"{name.Replace(" ", ".").ToLowerInvariant()}@perf.test";
            var agent = await manager.FindByEmailAsync(email);
            if (agent is null)
            {
                agent = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FirstName = name.Split(' ')[0], LastName = name.Split(' ')[1], IsAgent = true, AgentOwnerUserId = result[0].Id, CreatedAt = DateTimeOffset.UnixEpoch };
                var created = await manager.CreateAsync(agent);
                if (!created.Succeeded) throw new InvalidOperationException($"Unable to create perf agent {email}: {string.Join("; ", created.Errors.Select(x => x.Description))}");
            }
            result.Add(agent);
        }
        return result;
    }

    /// <summary>
    /// One representative state per category - the first by position, so Active resolves to
    /// "Active" rather than "In Review". A category is <em>not</em> unique among a project's
    /// states (the default workflow has two Active ones), so keying a dictionary on it
    /// directly throws, and the seed cannot run at all.
    /// </summary>
    private static async Task<Dictionary<WorkflowStateCategory, Guid>> StatesAsync(WorkItemsDbContext db, Guid projectId, CancellationToken ct)
    {
        var states = await db.WorkflowStates
            .Where(x => db.Workflows.Any(w => w.Id == x.WorkflowId && w.ProjectId == projectId))
            .OrderBy(x => x.Position).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return states.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.First().Id);
    }

    private static List<Label> CreateLabels(Guid orgId, Guid projectId) =>
    [
        new() { OrganizationId = orgId, ProjectId = projectId, Name = "backend", Color = "#7c3aed", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = projectId, Name = "frontend", Color = "#0284c7", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = projectId, Name = "agent", Color = "#059669", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = projectId, Name = "infra", Color = "#ea580c", Group = "area" },
        new() { OrganizationId = orgId, ProjectId = projectId, Name = "perf", Color = "#dc2626", Group = "type" },
    ];

    private static List<WorkItem> BuildItems(Guid orgId, Project project, Team team, Sprint completed, Sprint active,
        Sprint planned, IReadOnlyList<ApplicationUser> users, IReadOnlyDictionary<WorkflowStateCategory, Guid> states,
        DateTimeOffset now, Random random)
    {
        Guid? SprintFor(int number, WorkItemType type) =>
            number % 3 == 0 ? completed.Id : number % 3 == 1 ? active.Id
            : type is WorkItemType.Task or WorkItemType.Bug ? null : planned.Id;

        // Parents first so children can name them; the board and list endpoints mostly
        // query the stories and tasks, so epics and features stay a thin top of the tree.
        var epics = Enumerable.Range(1, 5).Select(number =>
            New(orgId, project, number, WorkItemType.Epic, $"Perf epic {number}", null, SprintFor(number, WorkItemType.Epic),
                team, states, users, now, random)).ToList();
        var features = Enumerable.Range(6, 25).Select(number =>
            New(orgId, project, number, WorkItemType.Feature, $"Perf feature {number}", epics[(number - 6) % epics.Count].Id,
                SprintFor(number, WorkItemType.Feature), team, states, users, now, random)).ToList();
        var bugs = Enumerable.Range(31, 300).Select(number =>
            New(orgId, project, number, WorkItemType.Bug, $"Perf bug {number}", null, SprintFor(number, WorkItemType.Bug),
                team, states, users, now, random)).ToList();
        var stories = Enumerable.Range(331, 3170).Select(number =>
            New(orgId, project, number, WorkItemType.Story, $"Perf story {number}", features[(number - 331) % features.Count].Id,
                SprintFor(number, WorkItemType.Story), team, states, users, now, random)).ToList();
        var tasks = Enumerable.Range(3501, 1500).Select(number =>
            New(orgId, project, number, WorkItemType.Task, $"Perf task {number}", stories[(number - 3501) % stories.Count].Id,
                SprintFor(number, WorkItemType.Task), team, states, users, now, random)).ToList();

        var items = new List<WorkItem>(ItemsPerProject);
        items.AddRange(epics);
        items.AddRange(features);
        items.AddRange(bugs);
        items.AddRange(stories);
        items.AddRange(tasks);
        return items;
    }

    private static WorkItem New(Guid orgId, Project project, int number, WorkItemType type, string title, Guid? parentId,
        Guid? sprintId, Team team, IReadOnlyDictionary<WorkflowStateCategory, Guid> states, IReadOnlyList<ApplicationUser> users,
        DateTimeOffset now, Random random)
    {
        var age = random.Next(0, 181);
        var category = age > 135 ? WorkflowStateCategory.Completed : age > 90 ? WorkflowStateCategory.Resolved
            : age > 45 ? WorkflowStateCategory.Active : WorkflowStateCategory.Proposed;
        var created = now.AddDays(-age).AddHours(-random.Next(0, 24));
        var isTask = type is WorkItemType.Task or WorkItemType.Bug;
        return new WorkItem
        {
            OrganizationId = orgId, ProjectId = project.Id, ProjectKey = project.Key, Number = number, Type = type, Title = title,
            DescriptionMarkdown = $"Performance data for **{title}**.", DescriptionHtml = $"<p>Performance data for <strong>{title}</strong>.</p>",
            StateId = states[category], Priority = (WorkItemPriority)random.Next(0, 5),
            // A third of the proposed work stays unassigned on purpose: `list_ready_work`
            // needs rows to hand a newly connected agent.
            AssigneeId = category == WorkflowStateCategory.Proposed && number % 3 == 0 ? null : users[random.Next(users.Count)].Id,
            TeamId = team.Id, SprintId = sprintId, ParentId = parentId, Rank = $"0|{number:D6}",
            Points = type == WorkItemType.Story ? random.Next(1, 9) : null,
            // `ck_items_hours_task_or_bug` wants all three hour columns NULL on anything
            // that is not a Task or a Bug - a zero is a value, and the constraint refuses it.
            EstimateHours = isTask ? random.Next(2, 17) : null,
            RemainingHours = !isTask ? null : category != WorkflowStateCategory.Completed ? random.Next(1, 9) : 0,
            CompletedHours = !isTask ? null : category == WorkflowStateCategory.Completed ? random.Next(2, 17) : 0,
            CreatedBy = users[number % users.Count].Id, CreatedAt = created, UpdatedAt = created.AddDays(random.Next(0, Math.Max(1, age))),
            CompletedAt = category == WorkflowStateCategory.Completed ? created.AddDays(Math.Max(1, age / 2)) : null,
        };
    }

    private static List<ItemHistory> BuildHistory(Guid orgId, IReadOnlyList<WorkItem> items,
        IReadOnlyList<ApplicationUser> users, Random random)
    {
        var history = new List<ItemHistory>(items.Count * 4);
        foreach (var item in items)
        {
            var actor = users[random.Next(users.Count)].Id;
            history.Add(new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actor, At = item.CreatedAt, Field = "created", NewValue = item.Title, EventId = Guid.CreateVersion7() });
            history.Add(new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actor, At = item.UpdatedAt, Field = "state", OldValue = "New", NewValue = "Active", EventId = Guid.CreateVersion7() });
            history.Add(new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actor, At = item.UpdatedAt, Field = "assignee", NewValue = item.AssigneeId ?? actor, EventId = Guid.CreateVersion7() });
            history.Add(item.Points is { } points
                ? new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actor, At = item.UpdatedAt, Field = "points", NewValue = points.ToString(), EventId = Guid.CreateVersion7() }
                : new ItemHistory { OrganizationId = orgId, ItemId = item.Id, ActorId = actor, At = item.UpdatedAt, Field = "title", OldValue = item.Title, NewValue = $"{item.Title} (revised)", EventId = Guid.CreateVersion7() });
        }
        return history;
    }
}
