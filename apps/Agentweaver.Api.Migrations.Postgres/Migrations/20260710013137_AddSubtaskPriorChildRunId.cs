using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Req-1, change #1) — durable pointer to the PRIOR child run captured
    /// immediately before a conscious fresh dispatch / lockout rotation clears <c>ChildRunId</c>, so the
    /// fresh/rotated agent can be handed the prior diff + integration-branch state instead of a blank
    /// pod. Nullable/additive; safe to apply to a populated database.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260710013137_AddSubtaskPriorChildRunId")]
    public partial class AddSubtaskPriorChildRunId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PriorChildRunId", table: "Subtasks", type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriorChildRunId", table: "Subtasks");
        }
    }
}
