using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260701140000_AddCoordinatorAssemblyReviews")]
    public partial class AddCoordinatorAssemblyReviews : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssemblyReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoordinatorRunId = table.Column<string>(type: "text", nullable: false),
                    OwnerUser = table.Column<string>(type: "text", nullable: true),
                    IntegrationBranch = table.Column<string>(type: "text", nullable: true),
                    AggregateTreeHash = table.Column<string>(type: "text", nullable: true),
                    DecisionJson = table.Column<string>(type: "text", nullable: true),
                    Reviewer = table.Column<string>(type: "text", nullable: true),
                    DecisionSubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssemblyReviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssemblyReviews_CoordinatorRunId",
                table: "AssemblyReviews",
                column: "CoordinatorRunId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssemblyReviews");
        }
    }
}
