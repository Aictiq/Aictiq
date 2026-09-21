using Aictiq.Modules.Wiki;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Wiki.Migrations;

/// <summary>
/// A deleted project or organization takes its wiki with it. Both functions hold the same
/// transaction-local flag as wiki.delete_page_subtree for exactly their own DELETE, so page
/// revisions stay append-only to everything else.
/// </summary>
[DbContext(typeof(WikiDbContext))]
[Migration("20260916200200_WikiPurge")]
public partial class WikiPurge : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION wiki.purge_project(project uuid)
            RETURNS SETOF uuid LANGUAGE plpgsql AS $$
            BEGIN
                RETURN QUERY SELECT id FROM wiki.pages WHERE project_id = project;
                SET LOCAL app.wiki_page_purge = 'on';
                DELETE FROM wiki.pages WHERE project_id = project;
                SET LOCAL app.wiki_page_purge = 'off';
            END;
            $$;

            CREATE FUNCTION wiki.purge_organization(org uuid)
            RETURNS void LANGUAGE plpgsql AS $$
            BEGIN
                SET LOCAL app.wiki_page_purge = 'on';
                DELETE FROM wiki.pages WHERE organization_id = org;
                DELETE FROM wiki.page_item_links WHERE organization_id = org;
                DELETE FROM wiki.page_permissions WHERE organization_id = org;
                SET LOCAL app.wiki_page_purge = 'off';
            END;
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS wiki.purge_organization(uuid);
            DROP FUNCTION IF EXISTS wiki.purge_project(uuid);
            """);
    }
}
