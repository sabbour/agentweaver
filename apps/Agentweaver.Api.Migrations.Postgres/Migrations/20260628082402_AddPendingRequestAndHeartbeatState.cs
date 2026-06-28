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
    /// Adds the replica-safe singleton state tables moved out of per-pod process memory:
    /// <c>PendingRequests</c> (the HITL review/confirm gate, keyed by the unique run id, single-consumed
    /// atomically across replicas) and <c>HeartbeatStatuses</c> (one row per API pod, UPSERTED each
    /// coordinator-heartbeat tick so the diagnostics endpoint can aggregate a consistent cross-replica
    /// view). Persisting these in Postgres makes them correct at <c>replicas:2</c> regardless of which
    /// replica serves a given request.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260628082402_AddPendingRequestAndHeartbeatState")]
    public partial class AddPendingRequestAndHeartbeatState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HeartbeatStatuses",
                columns: table => new
                {
                    PodName = table.Column<string>(nullable: false),
                    LastTickUtc = table.Column<DateTimeOffset>(nullable: false),
                    ActedCount = table.Column<int>(nullable: false),
                    ErrorCount = table.Column<int>(nullable: false),
                    DurationMs = table.Column<long>(nullable: false),
                    Error = table.Column<string>(nullable: true),
                    Enabled = table.Column<bool>(nullable: false),
                    IntervalSeconds = table.Column<int>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_HeartbeatStatuses", x => x.PodName));

            migrationBuilder.CreateTable(
                name: "PendingRequests",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(nullable: false),
                    RequestJson = table.Column<string>(nullable: false),
                    OwnerUser = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_PendingRequests", x => x.Id));

            migrationBuilder.CreateIndex("IX_PendingRequests_RunId", "PendingRequests", "RunId", unique: true);
            migrationBuilder.CreateIndex("IX_PendingRequests_ExpiresAt", "PendingRequests", "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("PendingRequests");
            migrationBuilder.DropTable("HeartbeatStatuses");
        }
    }
}
