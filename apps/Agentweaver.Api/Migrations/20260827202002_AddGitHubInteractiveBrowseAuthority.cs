using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubInteractiveBrowseAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_interactive_browse_authorities",
                columns: table => new
                {
                    authority_ref = table.Column<string>(type: "TEXT", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_authorization_id = table.Column<string>(type: "TEXT", nullable: false),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: false),
                    credential_version = table.Column<string>(type: "TEXT", nullable: false),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_interactive_browse_authorities", x => x.authority_ref);
                });

            migrationBuilder.CreateTable(
                name: "github_browse_selections",
                columns: table => new
                {
                    selection_ref = table.Column<string>(type: "TEXT", nullable: false),
                    authority_ref = table.Column<string>(type: "TEXT", nullable: false),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: false),
                    full_name_display = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_browse_selections", x => x.selection_ref);
                    table.ForeignKey(
                        name: "FK_github_browse_selections_authorities_authority_ref",
                        column: x => x.authority_ref,
                        principalTable: "github_interactive_browse_authorities",
                        principalColumn: "authority_ref",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_github_browse_selections_authority_ref",
                table: "github_browse_selections",
                column: "authority_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_interactive_browse_authorities_expires_at",
                table: "github_interactive_browse_authorities",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Browse-authority persistence is forward-only.");
        }
    }
}
