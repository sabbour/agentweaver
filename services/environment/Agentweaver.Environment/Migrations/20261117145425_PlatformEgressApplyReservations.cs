using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class PlatformEgressApplyReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_policy_apply_reservations",
                schema: "environment",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_epoch = table.Column<long>(type: "bigint", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    source_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    source_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    intent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    resource_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    provider_request_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_policy_apply_reservations", x => x.reservation_id);
                    table.CheckConstraint("ck_environment_platform_policy_apply_reservations_hashes", "source_hash ~ '^[0-9a-f]{64}$' AND intent_hash ~ '^[0-9a-f]{64}$' AND request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_environment_platform_policy_apply_reservations_request", "jsonb_typeof(provider_request_json) = 'object'");
                    table.CheckConstraint("ck_environment_platform_policy_apply_reservations_versions", "source_epoch > 0 AND generation > 0 AND source_revision > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_environment_platform_policy_apply_reservations_latest",
                schema: "environment",
                table: "platform_policy_apply_reservations",
                columns: new[] { "target_id", "source_epoch", "generation", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_environment_platform_policy_apply_reservations_idempotency",
                schema: "environment",
                table: "platform_policy_apply_reservations",
                columns: new[] { "target_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION environment.enforce_platform_policy_apply_reservation_identity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.target_id, 0));
                    IF EXISTS (
                        SELECT 1
                        FROM environment.platform_policy_apply_reservations existing
                        WHERE existing.target_id = NEW.target_id
                          AND existing.source_epoch = NEW.source_epoch
                          AND existing.generation = NEW.generation
                          AND (
                              existing.source_id IS DISTINCT FROM NEW.source_id OR
                              existing.source_revision IS DISTINCT FROM NEW.source_revision OR
                              existing.source_hash IS DISTINCT FROM NEW.source_hash OR
                              existing.intent_hash IS DISTINCT FROM NEW.intent_hash OR
                              existing.provider_request_json IS DISTINCT FROM NEW.provider_request_json
                          )
                    ) THEN
                        RAISE EXCEPTION 'platform policy apply reservation content conflicts'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER platform_policy_apply_reservations_consistent_identity
                    BEFORE INSERT ON environment.platform_policy_apply_reservations
                    FOR EACH ROW
                    EXECUTE FUNCTION environment.enforce_platform_policy_apply_reservation_identity();

                CREATE FUNCTION environment.reject_platform_policy_apply_reservation_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'platform policy apply reservations are append-only'
                        USING ERRCODE = '55000';
                END;
                $function$;

                CREATE TRIGGER platform_policy_apply_reservations_immutable
                    BEFORE UPDATE OR DELETE ON environment.platform_policy_apply_reservations
                    FOR EACH ROW
                    EXECUTE FUNCTION environment.reject_platform_policy_apply_reservation_mutation();

                CREATE TRIGGER platform_policy_apply_reservations_no_truncate
                    BEFORE TRUNCATE ON environment.platform_policy_apply_reservations
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION environment.reject_platform_policy_apply_reservation_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER platform_policy_apply_reservations_consistent_identity
                    ON environment.platform_policy_apply_reservations;
                DROP FUNCTION environment.enforce_platform_policy_apply_reservation_identity();
                DROP TRIGGER platform_policy_apply_reservations_no_truncate
                    ON environment.platform_policy_apply_reservations;
                DROP TRIGGER platform_policy_apply_reservations_immutable
                    ON environment.platform_policy_apply_reservations;
                DROP FUNCTION environment.reject_platform_policy_apply_reservation_mutation();
                """);
            migrationBuilder.DropTable(
                name: "platform_policy_apply_reservations",
                schema: "environment");
        }
    }
}
