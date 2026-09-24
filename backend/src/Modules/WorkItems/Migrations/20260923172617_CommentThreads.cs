using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class CommentThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_comment_id",
                schema: "work",
                table: "comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_comments_parent_comment_id",
                schema: "work",
                table: "comments",
                column: "parent_comment_id");

            migrationBuilder.AddForeignKey(
                name: "fk_comments_comments_parent_comment_id",
                schema: "work",
                table: "comments",
                column: "parent_comment_id",
                principalSchema: "work",
                principalTable: "comments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_comments_comments_parent_comment_id",
                schema: "work",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_comments_parent_comment_id",
                schema: "work",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "parent_comment_id",
                schema: "work",
                table: "comments");
        }
    }
}
