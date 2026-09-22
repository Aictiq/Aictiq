using Aictiq.Modules.Notifications;
using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aictiq.Modules.Notifications.Migrations;

[DbContext(typeof(NotificationsDbContext))]
[Migration("20260914100003_RlsTenantIsolation")]
public partial class RlsTenantIsolation : Migration
{
    private static readonly string[] Tables = ["notifications"];
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        TenantRls.Enable(migrationBuilder, "notify", Tables);
        // The personal inbox intentionally has no organization route. A user may read
        // and mark only their own rows even while no tenant is selected.
        migrationBuilder.Sql("""
            CREATE POLICY aictiq_notification_owner ON notify.notifications
                FOR ALL TO aictiq_app
                USING (user_id = NULLIF(current_setting('app.user_id', true), ''))
                WITH CHECK (user_id = NULLIF(current_setting('app.user_id', true), ''));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP POLICY IF EXISTS aictiq_notification_owner ON notify.notifications;");
        TenantRls.Disable(migrationBuilder, "notify", Tables);
    }
}
