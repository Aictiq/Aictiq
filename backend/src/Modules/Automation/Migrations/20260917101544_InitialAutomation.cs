using System;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Automation.Migrations
{
    /// <summary>
    /// The factory's first table. Two things EF cannot say are raw SQL here: a runner's name is
    /// unique per organization case-insensitively among runners that still exist (a deleted
    /// runner frees its name), and row-level security — including the one pre-tenant read, the
    /// secret lookup, which RLS admits only for the row whose hash is in the session.
    /// </summary>
    public partial class InitialAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "automation");

            migrationBuilder.CreateTable(
                name: "runners",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    registered_by = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    capabilities = table.Column<string>(type: "jsonb", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runners", x => x.id);
                    table.CheckConstraint("ck_runners_deleted_is_disabled", "deleted_at IS NULL OR disabled_at IS NOT NULL");
                    table.CheckConstraint("ck_runners_name", "length(btrim(name)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_runners_token_hash", "token_hash ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_runners_token_prefix", "length(token_prefix) = 8");
                });

            migrationBuilder.CreateIndex(
                name: "ix_runners_organization_id",
                schema: "automation",
                table: "runners",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_runners_token_hash",
                schema: "automation",
                table: "runners",
                column: "token_hash",
                unique: true);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_runners_organization_name
                    ON automation.runners (organization_id, lower(name))
                    WHERE deleted_at IS NULL;
                """);

            TenantRls.Enable(migrationBuilder, "automation", "runners");
            migrationBuilder.Sql("""
                CREATE POLICY aictiq_runner_token_lookup ON automation.runners
                    FOR SELECT TO aictiq_app
                    USING (token_hash = NULLIF(current_setting('app.runner_token_hash', true), ''));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS aictiq_runner_token_lookup ON automation.runners;");
            TenantRls.Disable(migrationBuilder, "automation", "runners");
            migrationBuilder.DropTable(
                name: "runners",
                schema: "automation");
        }
    }
}
