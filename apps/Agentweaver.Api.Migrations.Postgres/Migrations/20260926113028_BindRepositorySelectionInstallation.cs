using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class BindRepositorySelectionInstallation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropAutomationRepositoryGrantForeignKey(migrationBuilder);

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
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddPrimaryKey(
                name: "PK_github_repository_grants",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_grants_installation_id_repository_id_proj~",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id", "project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_activations_installation_id_repository_id_projec~",
                table: "automation_activations",
                columns: new[] { "installation_id", "repository_id", "project_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_automation_activations_repository_grants_authority",
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
            DropAutomationRepositoryGrantForeignKey(migrationBuilder);

            migrationBuilder.DropPrimaryKey(
                name: "PK_github_repository_grants",
                table: "github_repository_grants");

            migrationBuilder.DropIndex(
                name: "IX_github_repository_grants_installation_id_repository_id_proj~",
                table: "github_repository_grants");

            migrationBuilder.DropIndex(
                name: "IX_automation_activations_installation_id_repository_id_projec~",
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

        private static void DropAutomationRepositoryGrantForeignKey(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                DO $$
                DECLARE existing_constraint text;
                BEGIN
                    SELECT constraint_record.conname
                    INTO existing_constraint
                    FROM pg_constraint AS constraint_record
                    INNER JOIN pg_class AS source_table
                        ON source_table.oid = constraint_record.conrelid
                    INNER JOIN pg_class AS target_table
                        ON target_table.oid = constraint_record.confrelid
                    WHERE constraint_record.contype = 'f'
                      AND source_table.relname = 'automation_activations'
                      AND target_table.relname = 'github_repository_grants'
                    LIMIT 1;

                    IF existing_constraint IS NOT NULL THEN
                        EXECUTE format(
                            'ALTER TABLE automation_activations DROP CONSTRAINT %I',
                            existing_constraint);
                    END IF;
                END
                $$;
                """);
    }
}
