using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RetireLegacyOAuthAndBroker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_mode_epochs");

            migrationBuilder.DropTable(
                name: "github_account_link_states");

            migrationBuilder.DropTable(
                name: "McpAuthorizationCodes");

            migrationBuilder.DropTable(
                name: "McpClientRegistrations");

            migrationBuilder.DropTable(
                name: "McpPendingAuthorizations");

            migrationBuilder.DropTable(
                name: "McpRefreshTokens");

            migrationBuilder.DropTable(
                name: "McpRevokedJtis");

            migrationBuilder.DropTable(
                name: "OAuthStates");

            migrationBuilder.DropTable(
                name: "project_github_identity_overrides");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
