using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds <c>coordinator_pod_id</c> (TEXT NULL) to <c>WorkPlans</c>. This column is the distributed
    /// lease owner — the pod name of the replica that currently owns the coordinator dispatch loop for a
    /// given plan row. Written atomically by <c>CoordinatorDispatchService</c> when a dispatch loop
    /// starts and by <c>CoordinatorReconciler</c> when re-arming an orphan. Prevents the multi-replica
    /// race where every pod's reconciler independently sees <c>IsDispatchActive=false</c> and re-arms
    /// the same plan simultaneously. Nullable so existing rows default to unowned (any pod may claim).
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260629100000_AddCoordinatorPodId")]
    public partial class AddCoordinatorPodId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoordinatorPodId",
                table: "WorkPlans",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn("CoordinatorPodId", "WorkPlans");
        }
    }
}
