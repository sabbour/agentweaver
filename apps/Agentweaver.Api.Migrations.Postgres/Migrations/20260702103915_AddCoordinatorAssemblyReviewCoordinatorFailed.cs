using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260702103915_AddCoordinatorAssemblyReviewCoordinatorFailed")]
    public partial class AddCoordinatorAssemblyReviewCoordinatorFailed : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CoordinatorFailedAt",
                table: "AssemblyReviews",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoordinatorFailureReason",
                table: "AssemblyReviews",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoordinatorFailedAt",
                table: "AssemblyReviews");

            migrationBuilder.DropColumn(
                name: "CoordinatorFailureReason",
                table: "AssemblyReviews");
        }
    }
}
