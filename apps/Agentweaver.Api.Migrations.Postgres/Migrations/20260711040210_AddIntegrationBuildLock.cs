using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds the cross-process integration-build lock table (<c>IntegrationBuildLocks</c>), one row per
    /// project. Serializes integration-branch builds on the shared <c>/workspace/{projectId}/.git</c>
    /// repo so two pods (or two runs on one pod) never race the same repo and hit a LockedFileException
    /// or a null ref mid-swap (issue #218). The row is claimed with a conditional UPSERT keyed on
    /// <c>ProjectId</c> (repo granularity), carries a per-acquisition <c>OwnerToken</c> that fences
    /// release, and an <c>AcquiredAt</c> timestamp so a crashed holder's lock is reclaimable after the
    /// stale TTL instead of deadlocking the project. Mirrors the SQLite memory.db migration of the same
    /// id so both providers converge on identical schema.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260711040210_AddIntegrationBuildLock")]
    public partial class AddIntegrationBuildLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationBuildLocks",
                columns: table => new
                {
                    ProjectId = table.Column<string>(nullable: false),
                    OwnerToken = table.Column<string>(nullable: false),
                    OwnerPodId = table.Column<string>(nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_IntegrationBuildLocks", x => x.ProjectId));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("IntegrationBuildLocks");
        }
    }
}
