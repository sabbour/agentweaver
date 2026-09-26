using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionedKnowledgePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentRevisionId",
                table: "Decisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Decisions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "CurrentRevisionId",
                table: "AgentMemory",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ReplacedById",
                table: "AgentMemory",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "AgentMemory",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "AgentMemory",
                type: "text",
                nullable: false,
                defaultValue: "active");

            migrationBuilder.CreateTable(
                name: "agent_memory_revisions",
                columns: table => new
                {
                    RevisionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MemoryId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PreviousRevisionId = table.Column<string>(type: "text", nullable: true),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    SourceRunId = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    AgentName = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Importance = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReplacedById = table.Column<int>(type: "integer", nullable: true),
                    SourceKind = table.Column<string>(type: "text", nullable: false),
                    SourceIdentityFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceRunReference = table.Column<string>(type: "text", nullable: true),
                    TrustState = table.Column<string>(type: "text", nullable: false),
                    ApprovedByFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_memory_revisions", x => x.RevisionId);
                    table.ForeignKey(
                        name: "FK_agent_memory_revisions_AgentMemory_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "AgentMemory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_revisions",
                columns: table => new
                {
                    RevisionId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DecisionId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PreviousRevisionId = table.Column<string>(type: "text", nullable: true),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    SourceRunId = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    AgentName = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    SupersededById = table.Column<int>(type: "integer", nullable: true),
                    SourceKind = table.Column<string>(type: "text", nullable: false),
                    SourceIdentityFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceRunReference = table.Column<string>(type: "text", nullable: true),
                    TrustState = table.Column<string>(type: "text", nullable: false),
                    ApprovedByFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decision_revisions", x => x.RevisionId);
                    table.ForeignKey(
                        name: "FK_decision_revisions_Decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "Decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                UPDATE "AgentMemory"
                SET "CurrentRevisionId" = md5('memory:' || "Id"::text);

                INSERT INTO "agent_memory_revisions"
                    ("RevisionId", "MemoryId", "ProjectId", "Revision", "PreviousRevisionId",
                     "Actor", "SourceRunId", "Reason", "AgentName", "SessionId", "Type",
                     "Importance", "Content", "Tags", "Status", "ReplacedById", "SourceKind",
                     "SourceIdentityFingerprint", "SourceRunReference", "TrustState",
                     "ApprovedByFingerprint", "ApprovedAt", "CreatedAt")
                SELECT
                    "CurrentRevisionId", "Id", "ProjectId", 1, NULL,
                    "AgentName", "SourceRunId", 'legacy import', "AgentName", "SessionId", "Type",
                    "Importance", "Content", "Tags", "Status", NULL, "SourceKind",
                    NULL, "SourceRunId", "TrustState", NULL, "ApprovedAt", "UpdatedAt"
                FROM "AgentMemory";

                UPDATE "Decisions"
                SET "CurrentRevisionId" = md5('decision:' || "Id"::text);

                INSERT INTO "decision_revisions"
                    ("RevisionId", "DecisionId", "ProjectId", "Revision", "PreviousRevisionId",
                     "Actor", "SourceRunId", "Reason", "AgentName", "Type", "Status", "Title",
                     "Content", "Rationale", "Tags", "SupersededById", "SourceKind",
                     "SourceIdentityFingerprint", "SourceRunReference", "TrustState",
                     "ApprovedByFingerprint", "ApprovedAt", "CreatedAt")
                SELECT
                    "CurrentRevisionId", "Id", "ProjectId", 1, NULL,
                    "AgentName", "SourceRunId", 'legacy import', "AgentName", "Type", "Status", "Title",
                    "Content", "Rationale", "Tags", "SupersededById", "SourceKind",
                    NULL, "SourceRunId", "TrustState", NULL, "ApprovedAt", "UpdatedAt"
                FROM "Decisions";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_ReplacedById",
                table: "AgentMemory",
                column: "ReplacedById");

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_revisions_MemoryId_Revision",
                table: "agent_memory_revisions",
                columns: new[] { "MemoryId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_memory_revisions_ProjectId_MemoryId_Revision",
                table: "agent_memory_revisions",
                columns: new[] { "ProjectId", "MemoryId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_decision_revisions_DecisionId_Revision",
                table: "decision_revisions",
                columns: new[] { "DecisionId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_decision_revisions_ProjectId_DecisionId_Revision",
                table: "decision_revisions",
                columns: new[] { "ProjectId", "DecisionId", "Revision" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentMemory_AgentMemory_ReplacedById",
                table: "AgentMemory",
                column: "ReplacedById",
                principalTable: "AgentMemory",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentMemory_AgentMemory_ReplacedById",
                table: "AgentMemory");

            migrationBuilder.DropTable(
                name: "agent_memory_revisions");

            migrationBuilder.DropTable(
                name: "decision_revisions");

            migrationBuilder.DropIndex(
                name: "IX_AgentMemory_ReplacedById",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "CurrentRevisionId",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "CurrentRevisionId",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "ReplacedById",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "AgentMemory");
        }
    }
}
