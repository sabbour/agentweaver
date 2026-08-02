using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Postgres counterpart of the SQLite-side <c>AddDismissedNotifications</c> migration
    /// (apps/Agentweaver.Api/Migrations/20260722184157_AddDismissedNotifications.cs). That migration
    /// was only ever added to the SQLite dev-migrations project; the Postgres production provider
    /// resolves migrations from <c>Agentweaver.Api.Migrations.Postgres</c> (see
    /// MigrationsAssembly("Agentweaver.Api.Migrations.Postgres") in Program.cs), so the table was
    /// never created live even though efbundle reported "already up to date". Adds the
    /// dismissed_notifications table (per-user notification dismissal state), keyed by (user,
    /// notification_id).
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260731000000_AddDismissedNotifications")]
    public partial class AddDismissedNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dismissed_notifications",
                columns: table => new
                {
                    user = table.Column<string>(nullable: false),
                    notification_id = table.Column<string>(nullable: false),
                    dismissed_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dismissed_notifications", x => new { x.user, x.notification_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dismissed_notifications");
        }
    }
}
