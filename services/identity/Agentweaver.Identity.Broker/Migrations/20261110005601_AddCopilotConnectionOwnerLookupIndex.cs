using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotConnectionOwnerLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_copilot_connections_OwnerIssuer_OwnerActorId_TenantId_Scope~",
                schema: "identity_broker",
                table: "copilot_connections",
                columns: new[] { "OwnerIssuer", "OwnerActorId", "TenantId", "Scope", "ScopeId", "ConnectionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_copilot_connections_OwnerIssuer_OwnerActorId_TenantId_Scope~",
                schema: "identity_broker",
                table: "copilot_connections");
        }
    }
}
