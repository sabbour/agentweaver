using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B) — persisted per-plan HUMAN-review round-trip counter. When the
    /// autonomous steering budget is exhausted the plan escalates to the human-review gate; a subsequent
    /// human request-changes resets the autonomous <c>SteeringIterations</c> budget for a fresh
    /// convergence pass, BOUNDED by this cross-replica/crash-safe counter (default cap 3,
    /// <c>CoordinatorSteeringDecider.DefaultMaxHumanReviewRoundTrips</c>). Additive, non-null with a
    /// default so it is safe to apply to a populated database.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260710004451_AddHumanReviewRoundTrips")]
    public partial class AddHumanReviewRoundTrips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HumanReviewRoundTrips", table: "WorkPlans", type: "integer",
                nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HumanReviewRoundTrips", table: "WorkPlans");
        }
    }
}
