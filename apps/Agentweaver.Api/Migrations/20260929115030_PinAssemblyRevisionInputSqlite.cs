using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Agentweaver.Api.Memory;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260929115030_PinAssemblyRevisionInputSqlite")]
    /// <inheritdoc />
    public partial class PinAssemblyRevisionInputSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RevisionInputCommitHash",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevisionInputRevisionId",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RevisionInputCommitHash",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "RevisionInputRevisionId",
                table: "Subtasks");
        }
    }
}
