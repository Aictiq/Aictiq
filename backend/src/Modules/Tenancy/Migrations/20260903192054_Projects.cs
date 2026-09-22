using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations
{
    /// <summary>
    /// Projects and their explicit memberships, plus one index EF cannot express.
    ///
    /// Two projects called "Website" and "website" in one organization would be
    /// indistinguishable in every list a person reads, so the name is unique
    /// case-insensitively - which means an index on <c>lower(name)</c>, and EF's
    /// <c>HasIndex</c> only takes columns. It is created here in SQL; the endpoint's own
    /// check exists for the error message rather than as the guarantee.
    /// </summary>
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    visibility = table.Column<short>(type: "smallint", nullable: false),
                    icon = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.CheckConstraint("ck_projects_color_format", "color IS NULL OR color ~ '^#[0-9a-fA-F]{6}$'");
                    table.CheckConstraint("ck_projects_key_format", "key ~ '^[A-Z][A-Z0-9]{1,9}$'");
                    table.CheckConstraint("ck_projects_name_not_blank", "length(btrim(name)) > 0");
                    table.ForeignKey(
                        name: "fk_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                schema: "tenancy",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_members", x => new { x.project_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "tenancy",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_members_organization_id_user_id",
                schema: "tenancy",
                table: "project_members",
                columns: new[] { "organization_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id_key",
                schema: "tenancy",
                table: "projects",
                columns: new[] { "organization_id", "key" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_projects_organization_id_name_lower
                ON tenancy.projects (organization_id, lower(name));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS tenancy.ux_projects_organization_id_name_lower;");

            migrationBuilder.DropTable(
                name: "project_members",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "projects",
                schema: "tenancy");
        }
    }
}
