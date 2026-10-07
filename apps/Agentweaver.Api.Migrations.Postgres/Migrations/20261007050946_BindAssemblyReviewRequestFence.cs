using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class BindAssemblyReviewRequestFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AssemblyFencingToken",
                table: "AssemblyReviews",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewRequestId",
                table: "AssemblyReviews",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AssemblyReviews"
                SET "ReviewRequestId" = "CoordinatorRunId"
                WHERE "ReviewRequestId" IS NULL;

                UPDATE "AssemblyReviews"
                SET "AssemblyFencingToken" = (
                    SELECT "AssemblyFencingToken"
                    FROM "WorkPlans"
                    WHERE "WorkPlans"."CoordinatorRunId" = "AssemblyReviews"."CoordinatorRunId"
                    LIMIT 1
                )
                WHERE "AssemblyFencingToken" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssemblyFencingToken",
                table: "AssemblyReviews");

            migrationBuilder.DropColumn(
                name: "ReviewRequestId",
                table: "AssemblyReviews");
        }
    }
}
