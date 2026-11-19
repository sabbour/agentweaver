using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261119050000_PlatformPolicyHoldReservationFence")]
public sealed class PlatformPolicyHoldReservationFence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            SET LOCAL lock_timeout = '5s';
            LOCK TABLE environment.platform_policy_apply_reservations IN ACCESS EXCLUSIVE MODE;
            LOCK TABLE environment.platform_policy_proof_holds IN ACCESS EXCLUSIVE MODE;

            DO $check$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM environment.platform_policy_proof_holds h
                    WHERE h.reservation_id IS DISTINCT FROM (
                        SELECT r.reservation_id
                        FROM environment.platform_policy_apply_reservations r
                        WHERE r.target_id = h.target_id
                        ORDER BY r.source_epoch DESC, r.generation DESC, r.created_at DESC, r.reservation_id DESC
                        LIMIT 1
                    )
                ) THEN
                    RAISE EXCEPTION 'existing policy hold does not match the latest reservation'
                        USING ERRCODE = 'PF002';
                END IF;
            END;
            $check$;

            CREATE FUNCTION environment.enforce_platform_policy_reservation_hold_fence()
            RETURNS trigger
            LANGUAGE plpgsql
            VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $function$
            BEGIN
                IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                    RAISE EXCEPTION 'policy fence requires read committed isolation' USING ERRCODE = 'PF001';
                END IF;
                PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(NEW.target_id, 0));
                IF EXISTS (SELECT 1 FROM environment.platform_policy_proof_holds h WHERE h.target_id = NEW.target_id) THEN
                    RAISE EXCEPTION 'target has a policy hold'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_platform_policy_reservation_held';
                END IF;
                RETURN NEW;
            END;
            $function$;

            CREATE FUNCTION environment.enforce_platform_policy_hold_reservation_fence()
            RETURNS trigger
            LANGUAGE plpgsql
            VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $function$
            BEGIN
                IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
                    RAISE EXCEPTION 'policy fence requires read committed isolation' USING ERRCODE = 'PF001';
                END IF;
                PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(NEW.target_id, 0));
                IF NEW.reservation_id IS DISTINCT FROM (
                    SELECT r.reservation_id
                    FROM environment.platform_policy_apply_reservations r
                    WHERE r.target_id = NEW.target_id
                    ORDER BY r.source_epoch DESC, r.generation DESC, r.created_at DESC, r.reservation_id DESC
                    LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'policy hold does not match the latest reservation'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_platform_policy_hold_current_reservation';
                END IF;
                RETURN NEW;
            END;
            $function$;

            ALTER FUNCTION environment.enforce_platform_policy_proof_receipt_hold()
                VOLATILE SECURITY INVOKER SET search_path = pg_catalog;

            CREATE TRIGGER platform_policy_apply_reservations_active_hold_fence
                BEFORE INSERT ON environment.platform_policy_apply_reservations
                FOR EACH ROW EXECUTE FUNCTION environment.enforce_platform_policy_reservation_hold_fence();
            CREATE TRIGGER platform_policy_proof_holds_reservation_fence
                BEFORE INSERT ON environment.platform_policy_proof_holds
                FOR EACH ROW EXECUTE FUNCTION environment.enforce_platform_policy_hold_reservation_fence();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            SET LOCAL lock_timeout = '5s';
            LOCK TABLE environment.platform_policy_apply_reservations IN ACCESS EXCLUSIVE MODE;
            LOCK TABLE environment.platform_policy_proof_holds IN ACCESS EXCLUSIVE MODE;
            DO $check$
            BEGIN
                IF EXISTS (SELECT 1 FROM environment.platform_policy_proof_holds) THEN
                    RAISE EXCEPTION 'policy holds prevent downgrade' USING ERRCODE = 'PF003';
                END IF;
            END;
            $check$;
            DROP TRIGGER platform_policy_apply_reservations_active_hold_fence ON environment.platform_policy_apply_reservations;
            DROP TRIGGER platform_policy_proof_holds_reservation_fence ON environment.platform_policy_proof_holds;
            DROP FUNCTION environment.enforce_platform_policy_reservation_hold_fence();
            DROP FUNCTION environment.enforce_platform_policy_hold_reservation_fence();
            ALTER FUNCTION environment.enforce_platform_policy_proof_receipt_hold() RESET search_path;
            """);
    }
}
