using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueSyncGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "issue_sync_grants",
                schema: "identity_broker",
                columns: table => new
                {
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RepositoryOwner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RepositoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectionHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issue_sync_grants", x => x.GrantId);
                });

            migrationBuilder.CreateTable(
                name: "issue_sync_mint_audit",
                schema: "identity_broker",
                columns: table => new
                {
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Repository = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issue_sync_mint_audit", x => x.AuditId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issue_sync_grants_ProjectId_Active",
                schema: "identity_broker",
                table: "issue_sync_grants",
                columns: new[] { "ProjectId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_issue_sync_mint_audit_GrantId_CreatedAt",
                schema: "identity_broker",
                table: "issue_sync_mint_audit",
                columns: new[] { "GrantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issue_sync_grants",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "issue_sync_mint_audit",
                schema: "identity_broker");
        }
    }
}
