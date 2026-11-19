using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class ReserveModelProviderReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_reference_reservations",
                schema: "projects_config",
                columns: table => new
                {
                    reference_folded = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    creation_revision = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_provider_reference_reservations", x => x.reference_folded);
                    table.UniqueConstraint("ux_model_provider_reference_reservations_operation_id", x => x.operation_id);
                    table.UniqueConstraint("ux_model_provider_reference_reservations_provider_id", x => x.provider_id);
                    table.CheckConstraint("ck_provider_reference_creation_revision", "creation_revision = 1");
                    table.CheckConstraint("ck_provider_reference_folded", "reference_folded = translate(reference COLLATE \"C\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')");
                    table.CheckConstraint("ck_provider_reference_syntax", "reference COLLATE \"C\" ~ '^[A-Za-z0-9.-]([A-Za-z0-9._-]{0,254}[A-Za-z0-9.-])?$' AND position('__' in reference) = 0");
                    table.ForeignKey(
                        name: "FK_model_provider_reference_reservations_model_provider_revisi~",
                        columns: x => new { x.provider_id, x.creation_revision },
                        principalSchema: "projects_config",
                        principalTable: "model_provider_revisions",
                        principalColumns: new[] { "provider_id", "provider_revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_reference_reservations_provider_id_creation_~",
                schema: "projects_config",
                table: "model_provider_reference_reservations",
                columns: new[] { "provider_id", "creation_revision" });

            migrationBuilder.Sql("""
                REVOKE CREATE ON SCHEMA projects_config FROM PUBLIC;
                CREATE FUNCTION projects_config.guard_model_provider_reference_cross_table()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                BEGIN
                    PERFORM pg_catalog.pg_advisory_xact_lock(
                        pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                    IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'provider create requires read committed isolation'
                            USING ERRCODE = '40001';
                    END IF;
                    IF TG_TABLE_NAME = 'model_providers' THEN
                        IF EXISTS (
                            SELECT 1 FROM projects_config.model_provider_reference_reservations AS reservation
                            WHERE reservation.reference_folded =
                                pg_catalog.translate(NEW.provider_id COLLATE "C",
                                    'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                        ) THEN
                            RAISE EXCEPTION 'provider reference collision'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_reference_cross_table';
                        END IF;
                    ELSE
                        IF EXISTS (
                            SELECT 1 FROM projects_config.model_providers AS provider
                            WHERE pg_catalog.translate(provider.provider_id COLLATE "C",
                                    'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = NEW.reference_folded
                        ) THEN
                            RAISE EXCEPTION 'provider reference collision'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_reference_cross_table';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.guard_model_provider_reference_cross_table() FROM PUBLIC;
                CREATE TRIGGER model_providers_reference_guard
                BEFORE INSERT ON projects_config.model_providers
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_model_provider_reference_cross_table();
                CREATE TRIGGER model_providers_id_immutable
                BEFORE UPDATE OF provider_id ON projects_config.model_providers
                FOR EACH ROW WHEN (OLD.provider_id IS DISTINCT FROM NEW.provider_id)
                EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER model_provider_reference_cross_table_guard
                BEFORE INSERT ON projects_config.model_provider_reference_reservations
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_model_provider_reference_cross_table();
                CREATE TRIGGER model_provider_reference_reservations_immutable
                BEFORE UPDATE OR DELETE ON projects_config.model_provider_reference_reservations
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER model_provider_reference_reservations_no_truncate
                BEFORE TRUNCATE ON projects_config.model_provider_reference_reservations
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE projects_config.model_provider_reference_reservations IN ACCESS EXCLUSIVE MODE;
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM projects_config.model_provider_reference_reservations) THEN
                        RAISE EXCEPTION 'provider reference reservations must be retained';
                    END IF;
                END;
                $block$;
                DROP TRIGGER model_providers_reference_guard ON projects_config.model_providers;
                DROP TRIGGER model_providers_id_immutable ON projects_config.model_providers;
                DROP TRIGGER model_provider_reference_cross_table_guard ON projects_config.model_provider_reference_reservations;
                DROP FUNCTION projects_config.guard_model_provider_reference_cross_table();
                """);
            migrationBuilder.DropTable(
                name: "model_provider_reference_reservations",
                schema: "projects_config");
        }
    }
}
