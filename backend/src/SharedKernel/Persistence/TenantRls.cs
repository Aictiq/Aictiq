using Microsoft.EntityFrameworkCore.Migrations;

namespace Aictiq.SharedKernel.Persistence;

/// <summary>SQL shared by the per-module RLS migrations.</summary>
public static class TenantRls
{
    /// <summary>
    /// The setting is intentionally transaction-local when a transaction exists. A missing
    /// setting is NULL, and therefore matches no organization. Keep the policy expression
    /// in one place: a permissive policy in one module would defeat the point of RLS.
    /// </summary>
    private const string PolicyExpression =
        "organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid";

    public static void Enable(MigrationBuilder migrationBuilder, string schema, params string[] tables)
    {
        migrationBuilder.Sql($"""
            GRANT USAGE ON SCHEMA {Quote(schema)} TO aictiq_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {Quote(schema)} TO aictiq_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Quote(schema)} TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA {Quote(schema)} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aictiq_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA {Quote(schema)} GRANT USAGE, SELECT ON SEQUENCES TO aictiq_app;
            """);
        foreach (var table in tables)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE {Quote(schema)}.{Quote(table)} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE {Quote(schema)}.{Quote(table)} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)};
                CREATE POLICY aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)}
                    FOR ALL TO aictiq_app
                    USING ({PolicyExpression})
                    WITH CHECK ({PolicyExpression});
                GRANT SELECT, INSERT, UPDATE, DELETE ON {Quote(schema)}.{Quote(table)} TO aictiq_app;
                """);
        }
    }

    public static void Disable(MigrationBuilder migrationBuilder, string schema, params string[] tables)
    {
        foreach (var table in tables)
        {
            migrationBuilder.Sql($"""
                DROP POLICY IF EXISTS aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)};
                ALTER TABLE {Quote(schema)}.{Quote(table)} NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE {Quote(schema)}.{Quote(table)} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }

    /// <summary>
    /// Some dependent tables predate the TenantEntity convention: their tenant is
    /// carried by a same-schema parent. They still need RLS, otherwise an unfiltered raw
    /// query of (for example) comment revisions bypasses the parent's protection.
    /// </summary>
    public static void EnableViaParent(MigrationBuilder migrationBuilder, string schema,
        string table, string foreignKey, string parentTable)
    {
        var parent = $"{Quote(schema)}.{Quote(parentTable)}";
        var predicate = $"EXISTS (SELECT 1 FROM {parent} parent WHERE parent.\"id\" = {Quote(foreignKey)} AND parent.organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid)";
        migrationBuilder.Sql($"""
            GRANT USAGE ON SCHEMA {Quote(schema)} TO aictiq_app;
            ALTER TABLE {Quote(schema)}.{Quote(table)} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {Quote(schema)}.{Quote(table)} FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)};
            CREATE POLICY aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)}
                FOR ALL TO aictiq_app USING ({predicate}) WITH CHECK ({predicate});
            GRANT SELECT, INSERT, UPDATE, DELETE ON {Quote(schema)}.{Quote(table)} TO aictiq_app;
            """);
    }

    /// <summary>Protect assigned rows while retaining an explicitly global inbox.</summary>
    public static void EnableNullableTenant(MigrationBuilder migrationBuilder, string schema, string table)
    {
        var predicate = $"organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid OR organization_id IS NULL";
        migrationBuilder.Sql($"""
            GRANT USAGE ON SCHEMA {Quote(schema)} TO aictiq_app;
            ALTER TABLE {Quote(schema)}.{Quote(table)} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {Quote(schema)}.{Quote(table)} FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)};
            CREATE POLICY aictiq_tenant_isolation ON {Quote(schema)}.{Quote(table)}
                FOR ALL TO aictiq_app USING ({predicate}) WITH CHECK ({predicate});
            GRANT SELECT, INSERT, UPDATE, DELETE ON {Quote(schema)}.{Quote(table)} TO aictiq_app;
            """);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
