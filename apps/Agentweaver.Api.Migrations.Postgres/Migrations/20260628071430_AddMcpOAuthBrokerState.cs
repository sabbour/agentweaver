using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds the replica-safe OAuth broker state tables: pending authorizations (keyed by the GitHub
    /// CSRF state) and single-use authorization codes (keyed by the opaque code). Persisting these in
    /// Postgres lets any API replica complete the PKCE flow regardless of which replica served
    /// /oauth/authorize, the GitHub callback, or /oauth/token.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260628071430_AddMcpOAuthBrokerState")]
    public partial class AddMcpOAuthBrokerState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpPendingAuthorizations",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    State = table.Column<string>(nullable: false),
                    ClientId = table.Column<string>(nullable: false),
                    RedirectUri = table.Column<string>(nullable: false),
                    CodeChallenge = table.Column<string>(nullable: false),
                    ClientState = table.Column<string>(nullable: true),
                    Scope = table.Column<string>(nullable: false),
                    Resource = table.Column<string>(nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_McpPendingAuthorizations", x => x.Id));

            migrationBuilder.CreateIndex("IX_McpPendingAuthorizations_State", "McpPendingAuthorizations", "State", unique: true);
            migrationBuilder.CreateIndex("IX_McpPendingAuthorizations_ExpiresAt", "McpPendingAuthorizations", "ExpiresAt");

            migrationBuilder.CreateTable(
                name: "McpAuthorizationCodes",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(nullable: false),
                    Subject = table.Column<string>(nullable: false),
                    GithubLogin = table.Column<string>(nullable: false),
                    CodeChallenge = table.Column<string>(nullable: false),
                    RedirectUri = table.Column<string>(nullable: false),
                    ClientId = table.Column<string>(nullable: false),
                    Scope = table.Column<string>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_McpAuthorizationCodes", x => x.Id));

            migrationBuilder.CreateIndex("IX_McpAuthorizationCodes_Code", "McpAuthorizationCodes", "Code", unique: true);
            migrationBuilder.CreateIndex("IX_McpAuthorizationCodes_ExpiresAt", "McpAuthorizationCodes", "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("McpAuthorizationCodes");
            migrationBuilder.DropTable("McpPendingAuthorizations");
        }
    }
}
