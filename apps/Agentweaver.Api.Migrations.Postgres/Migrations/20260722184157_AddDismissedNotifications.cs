using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260722184157_AddDismissedNotifications")]
public sealed class AddDismissedNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "dismissed_notifications",
            columns: table => new
            {
                user = table.Column<string>(nullable: false),
                notification_id = table.Column<string>(nullable: false),
                dismissed_at = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_dismissed_notifications", x => new { x.user, x.notification_id });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "dismissed_notifications");
    }
}
