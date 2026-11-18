using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class PersistRuntimeProviderPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "credential_secret_id",
                schema: "projects_config",
                table: "model_provider_revisions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "credential_slot_id",
                schema: "projects_config",
                table: "model_provider_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rate_card_pin_json",
                schema: "projects_config",
                table: "model_provider_revisions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM projects_config.model_provider_revisions
                        WHERE rate_card_version IS NOT NULL
                          AND CASE
                                WHEN rate_card_version ~ '^-?[0-9]+$'
                                THEN rate_card_version::numeric < -9223372036854775808::numeric
                                  OR rate_card_version::numeric > 9223372036854775807::numeric
                                ELSE TRUE
                              END
                    ) THEN
                        RAISE EXCEPTION 'cannot downgrade provider revisions with rate-card versions that are not signed 64-bit integers'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropColumn(
                name: "credential_secret_id",
                schema: "projects_config",
                table: "model_provider_revisions");

            migrationBuilder.DropColumn(
                name: "credential_slot_id",
                schema: "projects_config",
                table: "model_provider_revisions");

            migrationBuilder.DropColumn(
                name: "rate_card_pin_json",
                schema: "projects_config",
                table: "model_provider_revisions");
        }
    }
}
