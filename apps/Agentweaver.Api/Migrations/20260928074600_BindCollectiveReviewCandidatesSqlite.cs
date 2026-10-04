using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260928074600_BindCollectiveReviewCandidatesSqlite")]
public sealed class BindCollectiveReviewCandidatesSqlite : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("OutputRevisionId", "AssemblyReviews", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("OutputRevisionId", "AssemblyReviews");
    }
}
