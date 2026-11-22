using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialPublicationProviderState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "published_provider_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "published_provider_state",
                schema: "identity_broker",
                table: "model_provider_credential_write_claims");
        }
    }
}
