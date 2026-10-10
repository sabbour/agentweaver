using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteMcpOAuthClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_id",
                schema: "identity_broker",
                table: "remote_mcp_oauth_connections",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "client_id",
                schema: "identity_broker",
                table: "remote_mcp_oauth_connections");
        }
    }
}
