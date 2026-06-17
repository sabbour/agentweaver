using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scaffolder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomeSpec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutcomeSpecs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    CoordinatorRunId = table.Column<string>(type: "TEXT", nullable: false),
                    Goal = table.Column<string>(type: "TEXT", nullable: false),
                    DesiredOutcome = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Assumptions = table.Column<string>(type: "TEXT", nullable: false),
                    ClarifyingQuestions = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutcomeSpecs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutcomeSpecs_ProjectId_CoordinatorRunId",
                table: "OutcomeSpecs",
                columns: new[] { "ProjectId", "CoordinatorRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutcomeSpecs");
        }
    }
}
