using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceCapabilityExpiryCleanupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_marketplace_copilot_capabilities_expiry_cleanup",
                table: "marketplace_copilot_capabilities",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_marketplace_copilot_capabilities_expiry_cleanup",
                table: "marketplace_copilot_capabilities");
        }
    }
}
