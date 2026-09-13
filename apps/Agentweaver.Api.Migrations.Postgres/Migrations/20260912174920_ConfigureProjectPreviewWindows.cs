using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ConfigureProjectPreviewWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "preview_approval_timeout_minutes",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 1440,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "preview_lifetime_minutes",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 1440);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "preview_lifetime_minutes",
                table: "projects");

            migrationBuilder.AlterColumn<int>(
                name: "preview_approval_timeout_minutes",
                table: "projects",
                type: "integer",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1440);
        }
    }
}
