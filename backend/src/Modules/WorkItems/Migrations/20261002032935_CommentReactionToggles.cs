using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class CommentReactionToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                schema: "work",
                table: "comment_reactions",
                type: "timestamp with time zone",
                nullable: true);

            // The previous API accepted arbitrary strings. Retire unsupported legacy
            // reactions and reactions on tombstones before enforcing the fixed set.
            migrationBuilder.Sql("""
                DELETE FROM work.comment_reactions
                WHERE emoji NOT IN ('👍', '👎', '❤️', '🎉', '👀', '✅')
                   OR comment_id IN (SELECT id FROM work.comments WHERE deleted_at IS NOT NULL);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_comment_reactions_emoji",
                schema: "work",
                table: "comment_reactions",
                sql: "emoji IN ('👍', '👎', '❤️', '🎉', '👀', '✅')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_comment_reactions_emoji",
                schema: "work",
                table: "comment_reactions");

            migrationBuilder.DropColumn(
                name: "removed_at",
                schema: "work",
                table: "comment_reactions");
        }
    }
}
