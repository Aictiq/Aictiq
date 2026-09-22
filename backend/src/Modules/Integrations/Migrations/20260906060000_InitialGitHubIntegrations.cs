using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Integrations.Migrations;

/// <inheritdoc />
[DbContext(typeof(IntegrationsDbContext))]
[Migration("20260906060000_InitialGitHubIntegrations")]
public partial class InitialGitHubIntegrations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "integrations");

        migrationBuilder.CreateTable(
            name: "github_deliveries",
            schema: "integrations",
            columns: table => new
            {
                delivery_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                installation_id = table.Column<long>(type: "bigint", nullable: true),
                organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_github_deliveries", x => x.delivery_id));

        migrationBuilder.CreateTable(
            name: "github_installations",
            schema: "integrations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                installation_id = table.Column<long>(type: "bigint", nullable: false),
                account_login = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                account_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                status = table.Column<short>(type: "smallint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_github_installations", x => x.id));

        migrationBuilder.CreateTable(
            name: "repo_bindings",
            schema: "integrations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                installation_id = table.Column<long>(type: "bigint", nullable: false),
                repo_id = table.Column<long>(type: "bigint", nullable: false),
                full_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_repo_bindings", x => x.id));

        migrationBuilder.CreateIndex(name: "ix_github_deliveries_installation_id", schema: "integrations", table: "github_deliveries", column: "installation_id");
        migrationBuilder.CreateIndex(name: "ix_github_deliveries_organization_id", schema: "integrations", table: "github_deliveries", column: "organization_id");
        migrationBuilder.CreateIndex(name: "ix_github_deliveries_received_at", schema: "integrations", table: "github_deliveries", column: "received_at");
        migrationBuilder.CreateIndex(name: "ix_github_installations_installation_id", schema: "integrations", table: "github_installations", column: "installation_id", unique: true);
        migrationBuilder.CreateIndex(name: "ix_github_installations_organization_id_status", schema: "integrations", table: "github_installations", columns: new[] { "organization_id", "status" });
        migrationBuilder.CreateIndex(name: "ix_repo_bindings_installation_id", schema: "integrations", table: "repo_bindings", column: "installation_id");
        migrationBuilder.CreateIndex(name: "ix_repo_bindings_organization_id_project_id", schema: "integrations", table: "repo_bindings", columns: new[] { "organization_id", "project_id" });
        migrationBuilder.CreateIndex(name: "ux_repo_bindings_repo_project", schema: "integrations", table: "repo_bindings", columns: new[] { "repo_id", "project_id" }, unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "github_deliveries", schema: "integrations");
        migrationBuilder.DropTable(name: "github_installations", schema: "integrations");
        migrationBuilder.DropTable(name: "repo_bindings", schema: "integrations");
    }
}
