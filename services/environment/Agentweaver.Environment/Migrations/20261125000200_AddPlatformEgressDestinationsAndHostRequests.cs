using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformEgressDestinationsAndHostRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_host_request_receipts",
                schema: "environment",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_revision = table.Column<long>(type: "bigint", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_host_request_receipts", x => x.request_id);
                    table.CheckConstraint("ck_provider_host_request_receipts_configuration_hash", "configuration_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_provider_host_request_receipts_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_provider_host_request_receipts_port", "port = 443");
                    table.CheckConstraint("ck_provider_host_request_receipts_revision", "provider_revision > 0");
                    table.CheckConstraint("ck_provider_host_request_receipts_scope_project", "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_host_request_receipts_created_request",
                schema: "environment",
                table: "provider_host_request_receipts",
                columns: new[] { "created_at", "request_id" });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION environment.reject_provider_host_request_receipt_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'provider host request receipts are immutable'
                        USING ERRCODE = '55000';
                END;
                $function$;
                REVOKE ALL ON FUNCTION environment.reject_provider_host_request_receipt_mutation() FROM PUBLIC;

                CREATE TRIGGER provider_host_request_receipts_immutable
                BEFORE UPDATE OR DELETE ON environment.provider_host_request_receipts
                FOR EACH ROW EXECUTE FUNCTION environment.reject_provider_host_request_receipt_mutation();

                CREATE TRIGGER provider_host_request_receipts_no_truncate
                BEFORE TRUNCATE ON environment.provider_host_request_receipts
                FOR EACH STATEMENT EXECUTE FUNCTION environment.reject_provider_host_request_receipt_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_host_request_receipts",
                schema: "environment");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS environment.reject_provider_host_request_receipt_mutation();");
        }
    }
}
