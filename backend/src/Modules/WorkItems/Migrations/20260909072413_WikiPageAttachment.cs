using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class WikiPageAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_owner_and_status",
                schema: "work",
                table: "attachments");

            migrationBuilder.AddColumn<Guid>(
                name: "wiki_page_id",
                schema: "work",
                table: "attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_project_id_wiki_page_id",
                schema: "work",
                table: "attachments",
                columns: new[] { "project_id", "wiki_page_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_owner_and_status",
                schema: "work",
                table: "attachments",
                sql: "(status = 0 AND item_id IS NULL AND comment_id IS NULL AND wiki_page_id IS NULL) OR (status = 1 AND ((item_id IS NOT NULL)::integer + (comment_id IS NOT NULL)::integer + (wiki_page_id IS NOT NULL)::integer = 1))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_attachments_project_id_wiki_page_id",
                schema: "work",
                table: "attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_owner_and_status",
                schema: "work",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "wiki_page_id",
                schema: "work",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_owner_and_status",
                schema: "work",
                table: "attachments",
                sql: "(status = 0 AND item_id IS NULL AND comment_id IS NULL) OR (status = 1 AND ((item_id IS NOT NULL)::integer + (comment_id IS NOT NULL)::integer = 1))");
        }
    }
}
