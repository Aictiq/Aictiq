using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class RunRefinedChannelMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A finished refinement (RunRefined = 14) used to arrive as RunSucceeded. Connected
            // shared channels hear it the way they hear a refine run's questions (RunNeedsInput = 13).
            migrationBuilder.Sql("""
                UPDATE notify.org_channels
                SET modes = modes || jsonb_build_object('14', modes -> '13')
                WHERE modes ? '13' AND NOT modes ? '14';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE notify.org_channels SET modes = modes - '14' WHERE modes ? '14';");
        }
    }
}
