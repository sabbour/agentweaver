using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class GuardModelProviderPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE projects_config.model_providers IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE projects_config.model_provider_revisions IN ACCESS EXCLUSIVE MODE;
                DO $precheck$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_index i
                        JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attname = 'operation_id'
                        WHERE i.indrelid = 'projects_config.model_provider_revisions'::regclass
                          AND idx.relname = 'ux_model_provider_revisions_operation_id'
                          AND i.indisunique AND i.indisvalid AND i.indisready
                          AND i.indnkeyatts = 1 AND i.indnatts = 1
                          AND i.indkey::text = a.attnum::text
                          AND i.indpred IS NULL AND i.indexprs IS NULL
                    ) OR (SELECT count(*) FROM pg_catalog.pg_trigger t
                          JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
                          JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                          JOIN pg_catalog.pg_namespace owner_schema ON owner_schema.nspname = 'projects_config'
                          WHERE t.tgrelid = 'projects_config.model_provider_revisions'::regclass
                            AND NOT t.tgisinternal AND t.tgenabled = 'O' AND t.tgnargs = 0
                            AND t.tgqual IS NULL AND t.tgattr = ''::int2vector
                            AND ((t.tgname = 'model_provider_revisions_immutable' AND t.tgtype = 27)
                              OR (t.tgname = 'model_provider_revisions_no_truncate' AND t.tgtype = 34))
                            AND n.nspname = 'projects_config'
                            AND p.proname = 'reject_immutable_record_mutation'
                            AND p.proowner = owner_schema.nspowner) <> 2
                    OR NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_constraint c
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = c.conrelid AND a.attname = 'provider_id'
                        JOIN pg_catalog.pg_attribute b ON b.attrelid = c.confrelid AND b.attname = 'provider_id'
                        WHERE c.conrelid = 'projects_config.model_provider_revisions'::regclass
                          AND c.confrelid = 'projects_config.model_providers'::regclass
                          AND c.contype = 'f' AND c.convalidated
                          AND c.conkey = ARRAY[a.attnum] AND c.confkey = ARRAY[b.attnum]
                    ) OR NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_constraint c
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = c.conrelid AND a.attname = 'project_id'
                        JOIN pg_catalog.pg_attribute b ON b.attrelid = c.confrelid AND b.attname = 'project_id'
                        WHERE c.conrelid = 'projects_config.model_providers'::regclass
                          AND c.confrelid = 'projects_config.projects'::regclass
                          AND c.contype = 'f' AND c.convalidated
                          AND c.conkey = ARRAY[a.attnum] AND c.confkey = ARRAY[b.attnum]
                    ) OR NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_constraint c
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = c.conrelid AND a.attname = 'provider_id'
                        JOIN pg_catalog.pg_attribute b ON b.attrelid = c.conrelid AND b.attname = 'creation_revision'
                        JOIN pg_catalog.pg_attribute x ON x.attrelid = c.confrelid AND x.attname = 'provider_id'
                        JOIN pg_catalog.pg_attribute y ON y.attrelid = c.confrelid AND y.attname = 'provider_revision'
                        WHERE c.conrelid = 'projects_config.model_provider_reference_reservations'::regclass
                          AND c.confrelid = 'projects_config.model_provider_revisions'::regclass
                          AND c.contype = 'f' AND c.convalidated
                          AND c.conkey = ARRAY[a.attnum, b.attnum] AND c.confkey = ARRAY[x.attnum, y.attnum]
                    ) OR EXISTS (
                        SELECT 1 FROM pg_catalog.pg_constraint
                        WHERE conrelid IN ('projects_config.model_providers'::regclass,
                                           'projects_config.model_provider_revisions'::regclass)
                          AND NOT convalidated
                    ) THEN
                        RAISE EXCEPTION 'provider baseline constraints are invalid' USING ERRCODE = '55000';
                    END IF;
                    IF EXISTS (
                        SELECT 1
                        FROM projects_config.model_providers h
                        LEFT JOIN projects_config.model_provider_revisions r
                          ON r.provider_id = h.provider_id AND r.provider_revision = h.current_revision
                        WHERE r.provider_id IS NULL OR
                            ROW(h.scope, h.project_id, h.display_name, h.type, h.base_url, h.deployment,
                                h.wire_api, h.azure_api_version, h.reasoning_efforts, h.prompt_capacity_tokens,
                                h.updated_by_actor_id, h.updated_at)
                            IS DISTINCT FROM
                            ROW(r.scope, r.project_id, r.display_name, r.type, r.base_url, r.deployment,
                                r.wire_api, r.azure_api_version, r.reasoning_efforts, r.prompt_capacity_tokens,
                                r.created_by_actor_id, r.created_at)
                           OR (SELECT count(*) FROM projects_config.model_provider_revisions x
                               WHERE x.provider_id = h.provider_id) <> h.current_revision
                           OR (SELECT min(x.provider_revision) FROM projects_config.model_provider_revisions x
                               WHERE x.provider_id = h.provider_id) <> 1
                           OR (SELECT max(x.provider_revision) FROM projects_config.model_provider_revisions x
                               WHERE x.provider_id = h.provider_id) <> h.current_revision
                    ) OR EXISTS (
                        SELECT 1 FROM projects_config.model_provider_revisions r
                        LEFT JOIN projects_config.model_providers h ON h.provider_id = r.provider_id
                        WHERE h.provider_id IS NULL
                    ) OR EXISTS (
                        SELECT operation_id FROM projects_config.model_provider_revisions
                        GROUP BY operation_id HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'provider baseline data is inconsistent' USING ERRCODE = '55000';
                    END IF;
                END;
                $precheck$;
                """);
            migrationBuilder.AddColumn<string>(
                name: "write_transaction_id",
                schema: "projects_config",
                table: "model_providers",
                type: "text",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "write_transaction_id",
                schema: "projects_config",
                table: "model_provider_revisions",
                type: "text",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.Sql("""
                CREATE FUNCTION projects_config.mark_model_provider_write()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                DECLARE
                    transaction_id text := pg_catalog.pg_current_xact_id()::text;
                BEGIN
                    IF TG_TABLE_NAME = 'model_providers' AND TG_OP = 'UPDATE'
                       AND OLD.write_transaction_id = transaction_id THEN
                        RAISE EXCEPTION 'provider transition is unavailable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                    END IF;
                    NEW.write_transaction_id := transaction_id;
                    RETURN NEW;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.mark_model_provider_write() FROM PUBLIC;

                CREATE FUNCTION projects_config.check_model_provider_pair()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $function$
                DECLARE
                    h projects_config.model_providers%ROWTYPE;
                    r projects_config.model_provider_revisions%ROWTYPE;
                    previous projects_config.model_provider_revisions%ROWTYPE;
                    transaction_id text := pg_catalog.pg_current_xact_id()::text;
                BEGIN
                    IF TG_TABLE_NAME = 'model_providers' THEN
                        SELECT * INTO h FROM projects_config.model_providers
                        WHERE provider_id = NEW.provider_id;
                        SELECT * INTO r FROM projects_config.model_provider_revisions
                        WHERE provider_id = NEW.provider_id AND provider_revision = NEW.current_revision;
                        IF h.provider_id IS NULL OR r.provider_id IS NULL
                           OR h.write_transaction_id <> transaction_id
                           OR r.write_transaction_id <> transaction_id
                           OR h.current_revision <> NEW.current_revision
                           OR NEW.state <> 'Draft'
                           OR (TG_OP = 'INSERT' AND
                               (NEW.current_revision <> 1 OR
                                ROW(NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM ROW(NEW.updated_by_actor_id, NEW.updated_at)))
                           OR (TG_OP = 'UPDATE' AND
                               (OLD.state <> 'Draft' OR
                                NEW.current_revision <> OLD.current_revision + 1 OR
                                ROW(NEW.provider_id, NEW.scope, NEW.project_id, NEW.created_by_actor_id, NEW.created_at)
                                IS DISTINCT FROM
                                ROW(OLD.provider_id, OLD.scope, OLD.project_id, OLD.created_by_actor_id, OLD.created_at))) THEN
                            RAISE EXCEPTION 'provider transition is unavailable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                        END IF;
                    ELSE
                        r := NEW;
                        SELECT * INTO h FROM projects_config.model_providers WHERE provider_id = NEW.provider_id;
                        IF h.provider_id IS NULL OR h.write_transaction_id <> transaction_id
                           OR NEW.write_transaction_id <> transaction_id
                           OR h.current_revision <> NEW.provider_revision
                           OR h.state <> 'Draft'
                           OR ROW(h.scope, h.project_id, h.display_name, h.type, h.base_url, h.deployment,
                                  h.wire_api, h.azure_api_version, h.reasoning_efforts, h.prompt_capacity_tokens,
                                  h.updated_by_actor_id, h.updated_at)
                              IS DISTINCT FROM
                              ROW(NEW.scope, NEW.project_id, NEW.display_name, NEW.type, NEW.base_url, NEW.deployment,
                                  NEW.wire_api, NEW.azure_api_version, NEW.reasoning_efforts, NEW.prompt_capacity_tokens,
                                  NEW.created_by_actor_id, NEW.created_at) THEN
                            RAISE EXCEPTION 'provider transition is unavailable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                        END IF;
                        IF NEW.provider_revision = 1 THEN
                            IF NEW.credential_version IS NOT NULL OR NEW.credential_slot_id IS NOT NULL
                               OR NEW.credential_secret_id IS NOT NULL THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                            END IF;
                        ELSE
                            SELECT * INTO previous FROM projects_config.model_provider_revisions
                            WHERE provider_id = NEW.provider_id AND provider_revision = NEW.provider_revision - 1;
                            IF previous.provider_id IS NULL OR
                               ROW(NEW.credential_version, NEW.credential_slot_id, NEW.credential_secret_id)
                               IS DISTINCT FROM
                               ROW(previous.credential_version, previous.credential_slot_id, previous.credential_secret_id) THEN
                                RAISE EXCEPTION 'provider transition is unavailable'
                                    USING ERRCODE = '23514', CONSTRAINT = 'ck_model_provider_write_guard';
                            END IF;
                        END IF;
                    END IF;
                    RETURN NULL;
                END;
                $function$;
                REVOKE ALL ON FUNCTION projects_config.check_model_provider_pair() FROM PUBLIC;
                CREATE TRIGGER model_providers_write_marker
                BEFORE INSERT OR UPDATE ON projects_config.model_providers
                FOR EACH ROW EXECUTE FUNCTION projects_config.mark_model_provider_write();
                CREATE TRIGGER model_provider_revisions_write_marker
                BEFORE INSERT ON projects_config.model_provider_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.mark_model_provider_write();
                CREATE CONSTRAINT TRIGGER model_providers_write_pair
                AFTER INSERT OR UPDATE ON projects_config.model_providers DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION projects_config.check_model_provider_pair();
                CREATE CONSTRAINT TRIGGER model_provider_revisions_write_pair
                AFTER INSERT ON projects_config.model_provider_revisions DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION projects_config.check_model_provider_pair();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $down$
                BEGIN
                    RAISE EXCEPTION 'provider publication guards cannot be removed'
                        USING ERRCODE = '55000';
                END;
                $down$;
                """);
        }
    }
}
