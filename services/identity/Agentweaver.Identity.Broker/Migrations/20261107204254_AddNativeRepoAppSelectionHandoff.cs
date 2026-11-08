using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeRepoAppSelectionHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NativeRepoAppSelectionReview",
                schema: "identity_broker",
                table: "pending_authorizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedWorkspaceSelectionPayload",
                schema: "identity_broker",
                table: "pending_authorizations",
                type: "character varying(16384)",
                maxLength: 16384,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceSelectionRequest",
                schema: "identity_broker",
                table: "pending_authorizations",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NativeRepoAppSelectionReview",
                schema: "identity_broker",
                table: "pending_authorizations");

            migrationBuilder.DropColumn(
                name: "ProtectedWorkspaceSelectionPayload",
                schema: "identity_broker",
                table: "pending_authorizations");

            migrationBuilder.DropColumn(
                name: "WorkspaceSelectionRequest",
                schema: "identity_broker",
                table: "pending_authorizations");
        }
    }
}
