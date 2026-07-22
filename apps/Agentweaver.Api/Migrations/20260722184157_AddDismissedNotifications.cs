using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDismissedNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dismissed_notifications",
                columns: table => new
                {
                    user = table.Column<string>(type: "TEXT", nullable: false),
                    notification_id = table.Column<string>(type: "TEXT", nullable: false),
                    dismissed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
