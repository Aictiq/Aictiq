using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AuditOrganizationModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "audit",
                table: "audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_organization_id_at",
                schema: "audit",
                table: "audit_log",
                columns: new[] { "organization_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_organization_id_at",
                schema: "audit",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "audit",
                table: "audit_log");
        }
    }
}
