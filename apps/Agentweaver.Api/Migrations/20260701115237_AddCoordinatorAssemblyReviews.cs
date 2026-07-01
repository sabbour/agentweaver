using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorAssemblyReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssemblyReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CoordinatorRunId = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUser = table.Column<string>(type: "TEXT", nullable: true),
                    IntegrationBranch = table.Column<string>(type: "TEXT", nullable: true),
                    AggregateTreeHash = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: true),
                    Reviewer = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionSubmittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssemblyReviews");
        }
    }
}
