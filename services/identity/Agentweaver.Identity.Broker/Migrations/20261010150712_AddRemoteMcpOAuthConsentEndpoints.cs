using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteMcpOAuthConsentEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "authorization_endpoint_uri",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "token_endpoint_uri",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "authorization_endpoint_uri",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents");

            migrationBuilder.DropColumn(
                name: "token_endpoint_uri",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents");
        }
    }
}
