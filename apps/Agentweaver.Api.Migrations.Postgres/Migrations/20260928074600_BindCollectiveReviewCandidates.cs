using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260928074600_BindCollectiveReviewCandidates")]
public sealed class BindCollectiveReviewCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_run_output_revisions_run_id_lifecycle_generation", "run_output_revisions");
        migrationBuilder.CreateIndex(
            "IX_run_output_revisions_run_id_lifecycle_generation", "run_output_revisions",
            new[] { "run_id", "lifecycle_generation" });
        migrationBuilder.AddColumn<string>("OutputRevisionId", "AssemblyReviews", type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>(
            "execution_input_required", "runs", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(
            "execution_input_source_commit_hash", "runs", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(
            "execution_input_commit_hash", "runs", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(
            "execution_input_composite_id", "runs", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("OutputRevisionId", "AssemblyReviews");
        migrationBuilder.DropColumn("execution_input_required", "runs");
        migrationBuilder.DropColumn("execution_input_source_commit_hash", "runs");
        migrationBuilder.DropColumn("execution_input_commit_hash", "runs");
        migrationBuilder.DropColumn("execution_input_composite_id", "runs");
        migrationBuilder.DropIndex("IX_run_output_revisions_run_id_lifecycle_generation", "run_output_revisions");
        migrationBuilder.CreateIndex(
            "IX_run_output_revisions_run_id_lifecycle_generation", "run_output_revisions",
            new[] { "run_id", "lifecycle_generation" }, unique: true);
    }
}
