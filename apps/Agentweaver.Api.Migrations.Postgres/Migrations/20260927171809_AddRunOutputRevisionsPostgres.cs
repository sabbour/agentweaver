using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRunOutputRevisionsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approved_output_revision_id",
                table: "runs",
                type: "text",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "current_output_revision_id",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_output_revisions",
                columns: table => new
                {
                    revision_id = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    lifecycle_generation = table.Column<int>(type: "integer", nullable: false),
                    workflow_digest = table.Column<string>(type: "text", nullable: true),
                    manifest_incomplete = table.Column<bool>(type: "boolean", nullable: false),
                    tree_hash = table.Column<string>(type: "text", nullable: false),
                    diff_sha256 = table.Column<string>(type: "text", nullable: false),
                    predecessor_revision_id = table.Column<string>(type: "text", nullable: true),
                    diff_bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_output_revisions", x => x.revision_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_output_revisions_run_id_lifecycle_generation",
                table: "run_output_revisions",
                columns: new[] { "run_id", "lifecycle_generation" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_run_output_revision_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'run_output_revisions is immutable';
                END;
                $$;
                CREATE TRIGGER trg_run_output_revisions_immutable
                    BEFORE UPDATE OR DELETE ON run_output_revisions
                    FOR EACH ROW EXECUTE FUNCTION reject_run_output_revision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS reject_run_output_revision_mutation() CASCADE;");
            migrationBuilder.DropTable(
                name: "run_output_revisions");
            migrationBuilder.DropColumn(
                name: "approved_output_revision_id",
                table: "runs");
            migrationBuilder.DropColumn(
                name: "current_output_revision_id",
                table: "runs");
        }
    }
}
