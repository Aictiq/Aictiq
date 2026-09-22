using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class TimeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "remaining_hours",
                schema: "work",
                table: "items",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "estimate_hours",
                schema: "work",
                table: "items",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "completed_hours",
                schema: "work",
                table: "items",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_hours_task_or_bug",
                schema: "work",
                table: "items",
                sql: "type IN (3, 4) OR (estimate_hours IS NULL AND remaining_hours IS NULL AND completed_hours IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_hours_task_or_bug",
                schema: "work",
                table: "items");

            migrationBuilder.AlterColumn<decimal>(
                name: "remaining_hours",
                schema: "work",
                table: "items",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,2)",
                oldPrecision: 6,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "estimate_hours",
                schema: "work",
                table: "items",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,2)",
                oldPrecision: 6,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "completed_hours",
                schema: "work",
                table: "items",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(6,2)",
                oldPrecision: 6,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
