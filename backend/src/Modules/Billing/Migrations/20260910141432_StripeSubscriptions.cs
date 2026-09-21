using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Billing.Migrations
{
    /// <inheritdoc />
    public partial class StripeSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "included_agents_per_human",
                schema: "billing",
                table: "plans",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "stripe_events",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stripe_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    stripe_customer_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    stripe_subscription_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    current_period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    seats_human = table.Column<int>(type: "integer", nullable: false),
                    seats_agent = table.Column<int>(type: "integer", nullable: false),
                    seats_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    grace_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.CheckConstraint("ck_subscriptions_billing_has_subscription", "status NOT IN (3, 4, 5, 7) OR stripe_subscription_id IS NOT NULL");
                    table.CheckConstraint("ck_subscriptions_grace", "(payment_failed_at IS NULL) = (grace_ends_at IS NULL) AND (grace_ends_at IS NULL OR grace_ends_at > payment_failed_at)");
                    table.CheckConstraint("ck_subscriptions_seats", "seats_human >= 0 AND seats_agent >= 0");
                    table.CheckConstraint("ck_subscriptions_status", "status BETWEEN 0 AND 8");
                    table.CheckConstraint("ck_subscriptions_subscription_has_customer", "stripe_subscription_id IS NULL OR stripe_customer_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_subscriptions_plans_plan",
                        column: x => x.plan,
                        principalSchema: "billing",
                        principalTable: "plans",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "enterprise",
                column: "included_agents_per_human",
                value: null);

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "free",
                column: "included_agents_per_human",
                value: null);

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "self_hosted",
                column: "included_agents_per_human",
                value: null);

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "starter",
                column: "included_agents_per_human",
                value: 3);

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "plans",
                keyColumn: "code",
                keyValue: "team",
                column: "included_agents_per_human",
                value: null);

            migrationBuilder.AddCheckConstraint(
                name: "ck_plans_included_agents",
                schema: "billing",
                table: "plans",
                sql: "included_agents_per_human IS NULL OR included_agents_per_human >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_stripe_events_received_at",
                schema: "billing",
                table: "stripe_events",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_grace_ends_at",
                schema: "billing",
                table: "subscriptions",
                column: "grace_ends_at",
                filter: "grace_ends_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_plan",
                schema: "billing",
                table: "subscriptions",
                column: "plan");

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_organization_id",
                schema: "billing",
                table: "subscriptions",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_stripe_customer_id",
                schema: "billing",
                table: "subscriptions",
                column: "stripe_customer_id",
                unique: true,
                filter: "stripe_customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_stripe_subscription_id",
                schema: "billing",
                table: "subscriptions",
                column: "stripe_subscription_id",
                unique: true,
                filter: "stripe_subscription_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stripe_events",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "billing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plans_included_agents",
                schema: "billing",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "included_agents_per_human",
                schema: "billing",
                table: "plans");
        }
    }
}
