using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations;

/// <summary>Restore unteamed portfolio items to team backlogs, parents before children.</summary>
[DbContext(typeof(WorkItemsDbContext))]
[Migration("20261116131500_BackfillPlanningTeams")]
public partial class BackfillPlanningTeams : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Migrations run administratively across tenants. Match both organization and
        // project explicitly for these one-off reads of Tenancy's team data.
        migrationBuilder.Sql("""
            UPDATE work.items AS item
            SET team_id = team.id, sprint_id = NULL
            FROM tenancy.teams AS team
            WHERE item.type = 0 AND item.team_id IS NULL
              AND team.organization_id = item.organization_id
              AND team.project_id = item.project_id AND team.is_default;

            WITH assignments AS (
                SELECT item.id, COALESCE(
                    (SELECT team.id
                     FROM work.items AS parent
                     JOIN tenancy.teams AS team ON team.id = parent.team_id
                       AND team.organization_id = parent.organization_id
                       AND team.project_id = parent.project_id
                     WHERE parent.id = item.parent_id
                       AND parent.organization_id = item.organization_id
                       AND parent.project_id = item.project_id),
                    (SELECT team.id FROM tenancy.teams AS team
                     WHERE team.organization_id = item.organization_id
                       AND team.project_id = item.project_id AND team.is_default)) AS team_id
                FROM work.items AS item
                WHERE item.type = 1 AND item.team_id IS NULL
            )
            UPDATE work.items AS item
            SET team_id = assignments.team_id, sprint_id = NULL
            FROM assignments
            WHERE item.id = assignments.id AND assignments.team_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data repair: assigned teams may have been edited since, so retain them.
    }
}
