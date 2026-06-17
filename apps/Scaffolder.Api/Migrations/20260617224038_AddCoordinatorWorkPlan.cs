using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Scaffolder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorWorkPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SteeringDirectives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CoordinatorRunId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetChildRunId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RelayedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteeringDirectives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OutcomeSpecId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: false),
                    CoordinatorRunId = table.Column<string>(type: "TEXT", nullable: false),
                    IsolationSummary = table.Column<string>(type: "TEXT", nullable: true),
                    IntegrationBranch = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkPlans_OutcomeSpecs_OutcomeSpecId",
                        column: x => x.OutcomeSpecId,
                        principalTable: "OutcomeSpecs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subtasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkPlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAgent = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    IsolationStrategy = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ChildRunId = table.Column<string>(type: "TEXT", nullable: true),
                    LockedOutAgents = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subtasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subtasks_WorkPlans_WorkPlanId",
                        column: x => x.WorkPlanId,
                        principalTable: "WorkPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubtaskDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubtaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    DependsOnSubtaskId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtaskDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubtaskDependencies_Subtasks_DependsOnSubtaskId",
                        column: x => x.DependsOnSubtaskId,
                        principalTable: "Subtasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubtaskDependencies_Subtasks_SubtaskId",
                        column: x => x.SubtaskId,
                        principalTable: "Subtasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SteeringDirectives_CoordinatorRunId_Status",
                table: "SteeringDirectives",
                columns: new[] { "CoordinatorRunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SubtaskDependencies_DependsOnSubtaskId",
                table: "SubtaskDependencies",
                column: "DependsOnSubtaskId");

            migrationBuilder.CreateIndex(
                name: "IX_SubtaskDependencies_SubtaskId",
                table: "SubtaskDependencies",
                column: "SubtaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Subtasks_WorkPlanId",
                table: "Subtasks",
                column: "WorkPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPlans_CoordinatorRunId",
                table: "WorkPlans",
                column: "CoordinatorRunId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkPlans_OutcomeSpecId",
                table: "WorkPlans",
                column: "OutcomeSpecId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SteeringDirectives");

            migrationBuilder.DropTable(
                name: "SubtaskDependencies");

            migrationBuilder.DropTable(
                name: "Subtasks");

            migrationBuilder.DropTable(
                name: "WorkPlans");
        }
    }
}
