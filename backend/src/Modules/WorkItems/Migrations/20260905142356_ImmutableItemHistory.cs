using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.WorkItems.Migrations
{
    /// <inheritdoc />
    public partial class ImmutableItemHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "event_id",
                schema: "work",
                table: "item_history",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "work",
                table: "item_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE work.item_history AS history
                SET event_id = history.id,
                    organization_id = item.organization_id
                FROM work.items AS item
                WHERE item.id = history.item_id;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                schema: "work",
                table: "item_history",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_history_event_id",
                schema: "work",
                table: "item_history",
                column: "event_id");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION work.reject_item_history_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'item history is append-only';
                END;
                $$;
                CREATE TRIGGER trg_item_history_append_only
                BEFORE UPDATE OR DELETE ON work.item_history
                FOR EACH ROW EXECUTE FUNCTION work.reject_item_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_item_history_append_only ON work.item_history; DROP FUNCTION IF EXISTS work.reject_item_history_mutation();");

            migrationBuilder.DropIndex(
                name: "ix_item_history_event_id",
                schema: "work",
                table: "item_history");

            migrationBuilder.DropColumn(
                name: "event_id",
                schema: "work",
                table: "item_history");

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "work",
                table: "item_history");
        }
    }
}
