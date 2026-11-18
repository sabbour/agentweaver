using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations;

[DbContext(typeof(ProjectsConfigDbContext))]
[Migration("20261117150000_AddProjectGovernanceDecisionCheckpoints")]
public partial class AddProjectGovernanceDecisionCheckpoints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "project_governance_decision_checkpoints",
            schema: "projects_config",
            columns: table => new
            {
                checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                scope_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                resource_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                sequence = table.Column<long>(type: "bigint", nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                head_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                checkpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                canonical_checkpoint = table.Column<byte[]>(type: "bytea", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_governance_decision_checkpoints", row => row.checkpoint_id);
                table.CheckConstraint(
                    "ck_project_governance_decision_checkpoints_hash",
                    "head_hash ~ '^[0-9a-f]{64}$' AND checkpoint_hash ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint(
                    "ck_project_governance_decision_checkpoints_position",
                    "sequence >= 0 AND (sequence > 0 OR head_hash = repeat('0', 64))");
                table.CheckConstraint(
                    "ck_project_governance_decision_checkpoints_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                table.CheckConstraint(
                    "ck_project_governance_decision_checkpoints_size",
                    "octet_length(canonical_checkpoint) BETWEEN 1 AND 8192");
                table.CheckConstraint(
                    "ck_project_governance_decision_checkpoints_idempotency",
                    "length(idempotency_key) BETWEEN 1 AND 128 AND idempotency_key = btrim(idempotency_key)");
                table.ForeignKey(
                    name: "FK_project_governance_decision_checkpoints_project_governance_~",
                    columns: row => new
                    {
                        row.scope_type,
                        row.scope_id,
                        row.resource_type,
                        row.resource_id,
                    },
                    principalSchema: "projects_config",
                    principalTable: "project_governance_decision_stream_heads",
                    principalColumns: ["scope_type", "scope_id", "resource_type", "resource_id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_project_governance_decision_checkpoints_stream_sequence",
            schema: "projects_config",
            table: "project_governance_decision_checkpoints",
            columns: ["scope_type", "scope_id", "resource_type", "resource_id", "sequence"]);
        migrationBuilder.CreateIndex(
            name: "IX_project_governance_decision_checkpoints_stream_idempotency",
            schema: "projects_config",
            table: "project_governance_decision_checkpoints",
            columns: ["scope_type", "scope_id", "resource_type", "resource_id", "idempotency_key"],
            unique: true);

        migrationBuilder.Sql(
            """
            CREATE FUNCTION projects_config.require_governance_checkpoint_anchor()
            RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, projects_config AS $body$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM projects_config.project_governance_decision_stream_heads AS head
                    WHERE head.scope_type = NEW.scope_type
                      AND head.scope_id = NEW.scope_id
                      AND head.resource_type = NEW.resource_type
                      AND head.resource_id = NEW.resource_id
                      AND head.sequence >= NEW.sequence
                      AND (
                          NEW.sequence = 0 AND head.sequence = 0
                              AND NEW.head_hash = repeat('0', 64)
                          OR NEW.sequence > 0 AND EXISTS (
                              SELECT 1
                              FROM projects_config.project_governance_decisions AS decision
                              WHERE decision.scope_type = NEW.scope_type
                                AND decision.scope_id = NEW.scope_id
                                AND decision.resource_type = NEW.resource_type
                                AND decision.resource_id = NEW.resource_id
                                AND decision.sequence = NEW.sequence
                                AND decision.record_hash = NEW.head_hash
                          )
                      )
                ) THEN
                    RAISE EXCEPTION 'governance checkpoint must reference a stream record'
                        USING ERRCODE = '55000';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER project_governance_decision_checkpoints_anchor
            BEFORE INSERT ON projects_config.project_governance_decision_checkpoints
            FOR EACH ROW EXECUTE FUNCTION projects_config.require_governance_checkpoint_anchor();
            CREATE TRIGGER project_governance_decision_checkpoints_immutable
            BEFORE UPDATE OR DELETE ON projects_config.project_governance_decision_checkpoints
            FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
            CREATE TRIGGER project_governance_decision_checkpoints_no_truncate
            BEFORE TRUNCATE ON projects_config.project_governance_decision_checkpoints
            FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
            REVOKE ALL ON FUNCTION projects_config.require_governance_checkpoint_anchor() FROM PUBLIC;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "project_governance_decision_checkpoints",
            schema: "projects_config");
        migrationBuilder.Sql(
            "DROP FUNCTION IF EXISTS projects_config.require_governance_checkpoint_anchor();");
    }
}
