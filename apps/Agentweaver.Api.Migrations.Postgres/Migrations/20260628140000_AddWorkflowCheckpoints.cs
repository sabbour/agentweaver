using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Creates the <c>workflow_checkpoints</c> table backing the new shared, concurrency-safe MAF
    /// checkpoint store (<see cref="Agentweaver.Api.Infrastructure.Ef.PostgresJsonCheckpointStore"/>).
    /// This replaces MAF's per-pod <c>FileSystemJsonCheckpointStore</c> on the Postgres path: that file
    /// store takes an EXCLUSIVE process lock on its directory, so under API <c>replicas:2</c> on a shared
    /// RWX volume only one pod could ever own it and checkpoints were never shared across replicas. Each
    /// row here is an independent, unique-PK checkpoint, so concurrent INSERTs from both replicas never
    /// contend and Postgres MVCC makes committed checkpoints immediately visible to the other replica —
    /// enabling genuine cross-pod resume. <c>store_name</c> partitions the previously-separate
    /// <c>checkpoints/</c> (runs) and <c>coordinator-checkpoints/</c> (coordinator) stores.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260628140000_AddWorkflowCheckpoints")]
    public partial class AddWorkflowCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_checkpoints",
                columns: table => new
                {
                    store_name = table.Column<string>(nullable: false),
                    session_id = table.Column<string>(nullable: false),
                    checkpoint_id = table.Column<string>(nullable: false),
                    parent_checkpoint_id = table.Column<string>(nullable: true),
                    has_parent_metadata = table.Column<bool>(nullable: false, defaultValue: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false),
                },
                constraints: table => table.PrimaryKey(
                    "PK_workflow_checkpoints",
                    x => new { x.store_name, x.session_id, x.checkpoint_id }));

            migrationBuilder.CreateIndex(
                "IX_workflow_checkpoints_store_session",
                "workflow_checkpoints",
                new[] { "store_name", "session_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("workflow_checkpoints");
        }
    }
}
