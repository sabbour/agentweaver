using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class SerializeScribeExportOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scribe_operation_attempts_OperationKey_Status",
                table: "scribe_operation_attempts");

            migrationBuilder.CreateIndex(
                name: "IX_scribe_operation_attempts_OperationKey",
                table: "scribe_operation_attempts",
                column: "OperationKey",
                unique: true,
                filter: "\"Status\" IN ('started', 'completed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scribe_operation_attempts_OperationKey",
                table: "scribe_operation_attempts");

            migrationBuilder.CreateIndex(
                name: "IX_scribe_operation_attempts_OperationKey_Status",
                table: "scribe_operation_attempts",
                columns: new[] { "OperationKey", "Status" });
        }
    }
}
