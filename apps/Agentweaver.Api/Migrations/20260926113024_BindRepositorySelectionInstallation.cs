using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class BindRepositorySelectionInstallation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_automation_activations_repository_grants_installation_id_repository_id",
                table: "automation_activations");

            migrationBuilder.DropForeignKey(
                name: "FK_github_installations_projects_project_id",
                table: "github_installations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_github_repository_grants",
                table: "github_repository_grants");

            migrationBuilder.DropIndex(
                name: "IX_github_repository_grants_installation_id_repository_id",
                table: "github_repository_grants");

            migrationBuilder.DropIndex(
                name: "IX_github_installations_project_id",
                table: "github_installations");

            migrationBuilder.DropIndex(
                name: "IX_automation_activations_installation_id_repository_id",
                table: "automation_activations");

            migrationBuilder.AddColumn<long>(
                name: "installation_id",
                table: "github_repository_selection_codes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddPrimaryKey(
                name: "PK_github_repository_grants",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_grants_installation_id_repository_id_project_id",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id", "project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_activations_installation_id_repository_id_project_id",
                table: "automation_activations",
                columns: new[] { "installation_id", "repository_id", "project_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_automation_activations_repository_grants_installation_id_repository_id",
                table: "automation_activations",
                columns: new[] { "installation_id", "repository_id", "project_id" },
                principalTable: "github_repository_grants",
                principalColumns: new[] { "installation_id", "repository_id", "project_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("UPDATE github_installations SET project_id = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_automation_activations_repository_grants_installation_id_repository_id",
                table: "automation_activations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_github_repository_grants",
                table: "github_repository_grants");

            migrationBuilder.DropIndex(
                name: "IX_github_repository_grants_installation_id_repository_id_project_id",
                table: "github_repository_grants");

            migrationBuilder.DropIndex(
                name: "IX_automation_activations_installation_id_repository_id_project_id",
                table: "automation_activations");

            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "github_repository_selection_codes");

            migrationBuilder.AddPrimaryKey(
                name: "PK_github_repository_grants",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id" });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_grants_installation_id_repository_id",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_project_id",
                table: "github_installations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_automation_activations_installation_id_repository_id",
                table: "automation_activations",
                columns: new[] { "installation_id", "repository_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_automation_activations_repository_grants_installation_id_repository_id",
                table: "automation_activations",
                columns: new[] { "installation_id", "repository_id" },
                principalTable: "github_repository_grants",
                principalColumns: new[] { "installation_id", "repository_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_github_installations_projects_project_id",
                table: "github_installations",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "project_id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
