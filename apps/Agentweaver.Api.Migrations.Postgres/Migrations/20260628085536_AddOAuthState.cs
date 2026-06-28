using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds the replica-safe web sign-in CSRF state table (<c>OAuthStates</c>), keyed by the opaque
    /// GitHub OAuth <c>state</c> token. Persisting this in Postgres lets the browser callback validate
    /// the CSRF state on ANY API replica, not just the pod that served /auth/github/authorize, fixing
    /// the ~50% "Invalid or expired OAuth state" failures at replicas:2. String primary key (no
    /// identity), with an ExpiresAt index for opportunistic purge of expired rows.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260628085536_AddOAuthState")]
    public partial class AddOAuthState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OAuthStates",
                columns: table => new
                {
                    State = table.Column<string>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_OAuthStates", x => x.State));

            migrationBuilder.CreateIndex("IX_OAuthStates_ExpiresAt", "OAuthStates", "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("OAuthStates");
        }
    }
}
