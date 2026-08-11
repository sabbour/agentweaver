using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260810220000_AddMemoryTrustProvenance")]
    public partial class AddMemoryTrustProvenance : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            AddTrustColumns(migrationBuilder, "AgentMemory", includeTrust: true);
            AddTrustColumns(migrationBuilder, "Decisions", includeTrust: true);
            AddTrustColumns(migrationBuilder, "DecisionInbox", includeTrust: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropTrustColumns(migrationBuilder, "AgentMemory", includeTrust: true);
            DropTrustColumns(migrationBuilder, "Decisions", includeTrust: true);
            DropTrustColumns(migrationBuilder, "DecisionInbox", includeTrust: false);
        }

        private static void AddTrustColumns(
            MigrationBuilder migrationBuilder,
            string table,
            bool includeTrust)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: table,
                type: "text",
                nullable: false,
                defaultValue: "legacy");
            migrationBuilder.AddColumn<string>(
                name: "SourceIdentity",
                table: table,
                type: "text",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "SourceRunId",
                table: table,
                type: "text",
                nullable: true);

            if (!includeTrust)
                return;

            migrationBuilder.AddColumn<string>(
                name: "TrustState",
                table: table,
                type: "text",
                nullable: false,
                defaultValue: "legacy");
            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: table,
                type: "text",
                nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: table,
                type: "timestamp with time zone",
                nullable: true);
        }

        private static void DropTrustColumns(
            MigrationBuilder migrationBuilder,
            string table,
            bool includeTrust)
        {
            migrationBuilder.DropColumn(name: "SourceKind", table: table);
            migrationBuilder.DropColumn(name: "SourceIdentity", table: table);
            migrationBuilder.DropColumn(name: "SourceRunId", table: table);
            if (!includeTrust)
                return;

            migrationBuilder.DropColumn(name: "TrustState", table: table);
            migrationBuilder.DropColumn(name: "ApprovedBy", table: table);
            migrationBuilder.DropColumn(name: "ApprovedAt", table: table);
        }
    }
}
