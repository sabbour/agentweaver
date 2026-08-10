using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Postgres counterpart of the SQLite-side <c>AddLinkedGitHubAccountStateAndProjectOverrides</c>
    /// migration (apps/Agentweaver.Api/Migrations/20260729212946_AddLinkedGitHubAccountStateAndProjectOverrides.cs).
    /// That migration was only ever added to the SQLite dev-migrations project; the Postgres
    /// production provider resolves migrations from <c>Agentweaver.Api.Migrations.Postgres</c> (see
    /// MigrationsAssembly("Agentweaver.Api.Migrations.Postgres") in Program.cs), so
    /// github_account_link_states / project_github_identity_overrides were never created live,
    /// causing "Link another GitHub account" to 500 with
    /// 42P01: relation "github_account_link_states" does not exist.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260803000000_AddLinkedGitHubAccountStateAndProjectOverrides")]
    public partial class AddLinkedGitHubAccountStateAndProjectOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_account_link_states",
                columns: table => new
                {
                    state = table.Column<string>(nullable: false),
                    entra_user_id = table.Column<string>(nullable: false),
                    expires_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_account_link_states", x => x.state);
                });

            migrationBuilder.CreateTable(
                name: "project_github_identity_overrides",
                columns: table => new
                {
                    project_id = table.Column<string>(nullable: false),
                    entra_user_id = table.Column<string>(nullable: false),
                    github_login = table.Column<string>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_github_identity_overrides", x => new { x.project_id, x.entra_user_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_account_link_states_expires_at",
                table: "github_account_link_states",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_project_github_identity_overrides_user_login",
                table: "project_github_identity_overrides",
                columns: new[] { "entra_user_id", "github_login" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_account_link_states");

            migrationBuilder.DropTable(
                name: "project_github_identity_overrides");
        }
    }
}
