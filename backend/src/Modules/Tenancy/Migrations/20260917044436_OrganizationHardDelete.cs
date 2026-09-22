using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Tenancy.Migrations
{
    /// <summary>
    /// Organizations are deleted for good instead of being marked. What survives is the slug
    /// alone, in tenancy.retired_slugs, and a trigger keeps any new organization off it - the
    /// database, not the create endpoint, is what guarantees a stale link never resolves to a
    /// stranger.
    ///
    /// Organizations soft-deleted before this migration are deleted for real: their slugs are
    /// retired and an OrganizationDeleted is queued for each, so every other module purges
    /// what it holds exactly as it would for a deletion made today.
    /// </summary>
    public partial class OrganizationHardDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retired_slugs",
                schema: "tenancy",
                columns: table => new
                {
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retired_slugs", x => x.slug);
                });

            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON tenancy.retired_slugs TO aictiq_app;

                -- Only an existing instance has anything to purge, and only there do the
                -- outbox and account tables this reads necessarily exist.
                DO $purge$
                BEGIN
                    IF EXISTS (SELECT 1 FROM tenancy.organizations WHERE deleted_at IS NOT NULL) THEN
                        INSERT INTO tenancy.retired_slugs (slug)
                        SELECT slug FROM tenancy.organizations WHERE deleted_at IS NOT NULL
                        ON CONFLICT DO NOTHING;

                        -- The payload is what OutboxMessage.From would have written: the record's
                        -- property names as System.Text.Json spells them by default.
                        INSERT INTO shared.outbox_messages (id, type, payload, occurred_at, attempts)
                        SELECT event_id, 'Aictiq.SharedKernel.Contracts.OrganizationDeleted',
                               jsonb_build_object(
                                   'OrganizationId', o.id,
                                   'Slug', o.slug,
                                   'AgentIds', COALESCE((
                                       SELECT jsonb_agg(m.user_id)
                                       FROM tenancy.organization_members m
                                       JOIN identity."AspNetUsers" u ON u.id = m.user_id AND u.is_agent
                                       WHERE m.organization_id = o.id), '[]'::jsonb),
                                   'ActorId', o.created_by,
                                   'EventId', event_id,
                                   'OccurredAt', now()),
                               now(), 0
                        FROM (SELECT gen_random_uuid() AS event_id, * FROM tenancy.organizations WHERE deleted_at IS NOT NULL) o;

                        DELETE FROM tenancy.organizations WHERE deleted_at IS NOT NULL;
                    END IF;
                END $purge$;

                CREATE FUNCTION tenancy.reject_retired_slug()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM tenancy.retired_slugs WHERE slug = NEW.slug) THEN
                        RAISE EXCEPTION 'slug % belonged to a deleted organization', NEW.slug
                            USING ERRCODE = 'unique_violation', CONSTRAINT = 'ux_organizations_retired_slug';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER trg_organizations_retired_slug
                BEFORE INSERT ON tenancy.organizations
                FOR EACH ROW EXECUTE FUNCTION tenancy.reject_retired_slug();
                """);

            migrationBuilder.DropIndex(
                name: "ix_organizations_deleted_at",
                schema: "tenancy",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "tenancy",
                table: "organizations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted organizations are gone; this restores the shape, not the rows.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_organizations_retired_slug ON tenancy.organizations;
                DROP FUNCTION IF EXISTS tenancy.reject_retired_slug();
                """);

            migrationBuilder.DropTable(
                name: "retired_slugs",
                schema: "tenancy");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                schema: "tenancy",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_organizations_deleted_at",
                schema: "tenancy",
                table: "organizations",
                column: "deleted_at",
                filter: "deleted_at IS NULL");
        }
    }
}
