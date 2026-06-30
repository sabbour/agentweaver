using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds <c>DeferredDecisions</c> — the cross-replica confirm/revise inbox. When a secondary
    /// replica receives a confirm/revise HTTP request it cannot apply the MAF checkpoint directly
    /// (the SDK <c>$type</c>-ordering bug causes a <c>JsonException</c>). Instead the secondary
    /// validates the gate is armed (<c>PendingRequests</c>), upserts the decision JSON here, and
    /// returns 200 Accepted. The primary replica's coordinator watch loop polls this table every
    /// 2 seconds after arming the gate, atomically deletes the row, and applies the decision
    /// itself — ensuring the checkpoint restore always runs on the pod that owns the MAF workflow
    /// instance. At-most-once delivery is enforced via <c>ExecuteDeleteAsync</c> (atomic delete
    /// returning the deleted row). The unique index on <c>RunId</c> prevents duplicate inserts
    /// when a user rapidly double-confirms (last write wins via upsert).
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260701120000_AddCoordinatorDeferredDecisions")]
    public partial class AddCoordinatorDeferredDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeferredDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(nullable: false),
                    DecisionJson = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_DeferredDecisions", x => x.Id));

            migrationBuilder.CreateIndex("IX_DeferredDecisions_RunId", "DeferredDecisions", "RunId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("DeferredDecisions");
        }
    }
}
