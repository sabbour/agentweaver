using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryTrustProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "Decisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Decisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIdentity",
                table: "Decisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "Decisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "SourceRunId",
                table: "Decisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustState",
                table: "Decisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "SourceIdentity",
                table: "DecisionInbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "DecisionInbox",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "SourceRunId",
                table: "DecisionInbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "AgentMemory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "AgentMemory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIdentity",
                table: "AgentMemory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "AgentMemory",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "SourceRunId",
                table: "AgentMemory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustState",
                table: "AgentMemory",
                type: "TEXT",
                nullable: false,
                defaultValue: "legacy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "SourceIdentity",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "SourceRunId",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "TrustState",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "SourceIdentity",
                table: "DecisionInbox");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "DecisionInbox");

            migrationBuilder.DropColumn(
                name: "SourceRunId",
                table: "DecisionInbox");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "SourceIdentity",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "SourceRunId",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "TrustState",
                table: "AgentMemory");
        }
    }
}
