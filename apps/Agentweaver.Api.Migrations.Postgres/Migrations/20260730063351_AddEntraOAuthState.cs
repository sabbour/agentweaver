using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds the replica-safe Microsoft Entra web sign-in CSRF/PKCE state table
    /// (<c>EntraOAuthStates</c>), keyed by the opaque OAuth <c>state</c> token and carrying the PKCE
    /// <c>CodeVerifier</c> the callback must replay at Microsoft's token endpoint. Persisting this in
    /// Postgres lets the browser callback validate the CSRF state and redeem the code on ANY API
    /// replica, not just the pod that served /auth/entra/authorize, fixing the ~50% "Invalid or
    /// expired OAuth state" failures at replicas:2. String primary key (no identity), with an
    /// ExpiresAt index for opportunistic purge of expired rows. Mirrors the GitHub <c>OAuthStates</c>
    /// table, with the added verifier column that PKCE requires.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260730063351_AddEntraOAuthState")]
    public partial class AddEntraOAuthState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntraOAuthStates",
                columns: table => new
                {
                    State = table.Column<string>(nullable: false),
                    CodeVerifier = table.Column<string>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_EntraOAuthStates", x => x.State));

            migrationBuilder.CreateIndex("IX_EntraOAuthStates_ExpiresAt", "EntraOAuthStates", "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("EntraOAuthStates");
        }
    }
}
