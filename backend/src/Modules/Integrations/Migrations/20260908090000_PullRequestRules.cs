using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Integrations.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20260908090000_PullRequestRules")]
public partial class PullRequestRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "on_pull_request_opened_state_id", schema: "integrations", table: "repo_bindings", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "on_pull_request_merged_state_id", schema: "integrations", table: "repo_bindings", type: "uuid", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "on_pull_request_opened_state_id", schema: "integrations", table: "repo_bindings");
        migrationBuilder.DropColumn(name: "on_pull_request_merged_state_id", schema: "integrations", table: "repo_bindings");
    }
}
