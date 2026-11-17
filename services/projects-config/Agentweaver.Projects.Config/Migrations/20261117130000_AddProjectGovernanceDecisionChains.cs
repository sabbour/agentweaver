using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectGovernanceDecisionChains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_governance_decision_stream_heads",
                schema: "projects_config",
                columns: table => new
                {
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    fence_generation = table.Column<long>(type: "bigint", nullable: false),
                    head_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_governance_decision_stream_heads", x => new { x.scope_type, x.scope_id, x.resource_type, x.resource_id });
                    table.CheckConstraint("ck_project_governance_decision_stream_heads_hash", "head_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_project_governance_decision_stream_heads_position", "sequence >= 0 AND fence_generation = sequence");
                    table.CheckConstraint("ck_project_governance_decision_stream_heads_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                });

            migrationBuilder.CreateTable(
                name: "project_governance_decisions",
                schema: "projects_config",
                columns: table => new
                {
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    canonical_record = table.Column<byte[]>(type: "bytea", nullable: false),
                    write_transaction_id = table.Column<string>(type: "text", nullable: false, defaultValueSql: "pg_current_xact_id()::text"),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_governance_decisions", x => new { x.scope_type, x.scope_id, x.resource_type, x.resource_id, x.sequence });
                    table.UniqueConstraint("AK_project_governance_decisions_decision_id", x => x.decision_id);
                    table.CheckConstraint("ck_project_governance_decisions_hash", "request_digest ~ '^[0-9a-f]{64}$' AND previous_hash ~ '^[0-9a-f]{64}$' AND record_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_project_governance_decisions_scope", "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                    table.CheckConstraint("ck_project_governance_decisions_sequence", "sequence > 0");
                    table.CheckConstraint("ck_project_governance_decisions_size", "octet_length(canonical_record) BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "FK_project_governance_decisions_project_governance_decision_st~",
                        columns: x => new { x.scope_type, x.scope_id, x.resource_type, x.resource_id },
                        principalSchema: "projects_config",
                        principalTable: "project_governance_decision_stream_heads",
                        principalColumns: new[] { "scope_type", "scope_id", "resource_type", "resource_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_governance_decision_outbox",
                schema: "projects_config",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    record_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    write_transaction_id = table.Column<string>(type: "text", nullable: false, defaultValueSql: "pg_current_xact_id()::text"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_governance_decision_outbox", x => x.outbox_id);
                    table.CheckConstraint("ck_project_governance_decision_outbox_event", "event_type = 'project.governance_decision_appended.v1'");
                    table.CheckConstraint("ck_project_governance_decision_outbox_hash", "record_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_project_governance_decision_outbox_project_governance_decis~",
                        column: x => x.decision_id,
                        principalSchema: "projects_config",
                        principalTable: "project_governance_decisions",
                        principalColumn: "decision_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_governance_decision_outbox_decision_id",
                schema: "projects_config",
                table: "project_governance_decision_outbox",
                column: "decision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_governance_decisions_scope_type_scope_id_resource_t~",
                schema: "projects_config",
                table: "project_governance_decisions",
                columns: new[] { "scope_type", "scope_id", "resource_type", "resource_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER project_governance_decisions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_governance_decisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_governance_decisions_no_truncate
                BEFORE TRUNCATE ON projects_config.project_governance_decisions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE FUNCTION projects_config.stamp_governance_decision_transaction_id()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
                BEGIN
                    NEW.write_transaction_id := pg_catalog.pg_current_xact_id()::text;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER project_governance_decisions_stamp_transaction
                BEFORE INSERT ON projects_config.project_governance_decisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.stamp_governance_decision_transaction_id();

                CREATE TRIGGER project_governance_decision_outbox_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_governance_decision_outbox
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_governance_decision_outbox_no_truncate
                BEFORE TRUNCATE ON projects_config.project_governance_decision_outbox
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_governance_decision_outbox_stamp_transaction
                BEFORE INSERT ON projects_config.project_governance_decision_outbox
                FOR EACH ROW EXECUTE FUNCTION projects_config.stamp_governance_decision_transaction_id();

                CREATE FUNCTION projects_config.require_governance_decision_head_initialize()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, projects_config AS $body$
                BEGIN
                    IF NEW.sequence <> 0 OR NEW.fence_generation <> 0
                       OR NEW.head_hash <> repeat('0', 64) THEN
                        RAISE EXCEPTION 'governance decision stream head must start at genesis'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER project_governance_decision_head_initialize
                BEFORE INSERT ON projects_config.project_governance_decision_stream_heads
                FOR EACH ROW EXECUTE FUNCTION projects_config.require_governance_decision_head_initialize();

                CREATE FUNCTION projects_config.require_governance_decision_head_advance()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, projects_config AS $body$
                BEGIN
                    IF NEW.sequence <> OLD.sequence + 1
                       OR NEW.fence_generation <> OLD.fence_generation + 1
                       OR NEW.head_hash !~ '^[0-9a-f]{64}$'
                       OR NOT EXISTS (
                           SELECT 1
                           FROM projects_config.project_governance_decisions AS decision
                           JOIN projects_config.project_governance_decision_outbox AS outbox
                             ON outbox.decision_id = decision.decision_id
                           WHERE decision.scope_type = NEW.scope_type
                             AND decision.scope_id = NEW.scope_id
                             AND decision.resource_type = NEW.resource_type
                             AND decision.resource_id = NEW.resource_id
                             AND decision.sequence = NEW.sequence
                             AND decision.previous_hash = OLD.head_hash
                             AND decision.record_hash = NEW.head_hash
                             AND decision.write_transaction_id = pg_current_xact_id()::text
                             AND outbox.sequence = decision.sequence
                             AND outbox.record_hash = decision.record_hash
                             AND outbox.write_transaction_id = pg_current_xact_id()::text
                       ) THEN
                        RAISE EXCEPTION 'governance decision head must advance with its record and outbox'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER project_governance_decision_head_advance
                BEFORE UPDATE ON projects_config.project_governance_decision_stream_heads
                FOR EACH ROW EXECUTE FUNCTION projects_config.require_governance_decision_head_advance();

                CREATE FUNCTION projects_config.reject_governance_decision_head_removal()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
                BEGIN
                    RAISE EXCEPTION 'governance decision stream heads cannot be removed'
                        USING ERRCODE = '55000';
                END;
                $body$;
                CREATE TRIGGER project_governance_decision_heads_no_delete
                BEFORE DELETE ON projects_config.project_governance_decision_stream_heads
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_governance_decision_head_removal();
                CREATE TRIGGER project_governance_decision_heads_no_truncate
                BEFORE TRUNCATE ON projects_config.project_governance_decision_stream_heads
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_governance_decision_head_removal();

                REVOKE ALL ON FUNCTION projects_config.require_governance_decision_head_initialize() FROM PUBLIC;
                REVOKE ALL ON FUNCTION projects_config.require_governance_decision_head_advance() FROM PUBLIC;
                REVOKE ALL ON FUNCTION projects_config.reject_governance_decision_head_removal() FROM PUBLIC;
                REVOKE ALL ON FUNCTION projects_config.stamp_governance_decision_transaction_id() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_governance_decision_outbox",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_governance_decisions",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_governance_decision_stream_heads",
                schema: "projects_config");

            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS projects_config.reject_governance_decision_head_removal();
                DROP FUNCTION IF EXISTS projects_config.require_governance_decision_head_advance();
                DROP FUNCTION IF EXISTS projects_config.require_governance_decision_head_initialize();
                DROP FUNCTION IF EXISTS projects_config.stamp_governance_decision_transaction_id();
                """);
        }
    }
}
