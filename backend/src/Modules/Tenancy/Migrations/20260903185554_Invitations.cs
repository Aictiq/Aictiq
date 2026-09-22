using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations
{
    /// <inheritdoc />
    public partial class Invitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_role = table.Column<short>(type: "smallint", nullable: true),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    invited_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invitations", x => x.id);
                    table.CheckConstraint("ck_invitations_accepted_consistency", "(accepted_at IS NULL) = (accepted_by IS NULL)");
                    table.CheckConstraint("ck_invitations_email_normalized", "email = lower(email) AND length(btrim(email)) > 0");
                    table.CheckConstraint("ck_invitations_project_role_paired", "(project_id IS NULL) = (project_role IS NULL)");
                    table.CheckConstraint("ck_invitations_role_not_owner", "role > 0");
                    table.ForeignKey(
                        name: "fk_invitations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_organization_id_created_at",
                schema: "tenancy",
                table: "invitations",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_token_hash",
                schema: "tenancy",
                table: "invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invitations_open_per_email",
                schema: "tenancy",
                table: "invitations",
                columns: new[] { "organization_id", "email" },
                unique: true,
                filter: "accepted_at IS NULL AND revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations",
                schema: "tenancy");
        }
    }
}
