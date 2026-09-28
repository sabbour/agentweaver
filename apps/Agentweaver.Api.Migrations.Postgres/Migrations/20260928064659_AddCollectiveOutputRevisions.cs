using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectiveOutputRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "accepted_no_change",
                table: "run_output_revisions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "merge_effect_id",
                table: "run_output_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "merged_commit_hash",
                table: "run_output_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "output_kind",
                table: "run_output_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "work_plan_id",
                table: "run_output_revisions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "tree_content",
                table: "run_output_revisions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tree_content_sha256",
                table: "run_output_revisions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "tree_content", table: "run_output_revisions");
            migrationBuilder.DropColumn(name: "tree_content_sha256", table: "run_output_revisions");
            migrationBuilder.DropColumn(
                name: "accepted_no_change",
                table: "run_output_revisions");

            migrationBuilder.DropColumn(
                name: "merge_effect_id",
                table: "run_output_revisions");

            migrationBuilder.DropColumn(
                name: "merged_commit_hash",
                table: "run_output_revisions");

            migrationBuilder.DropColumn(
                name: "output_kind",
                table: "run_output_revisions");

            migrationBuilder.DropColumn(
                name: "work_plan_id",
                table: "run_output_revisions");
        }
    }
}
