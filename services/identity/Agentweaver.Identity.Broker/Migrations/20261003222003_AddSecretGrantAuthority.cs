using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretGrantAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "secret_grant_heads",
                schema: "identity_broker",
                columns: table => new
                {
                    grant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    current_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_grant_heads", x => x.grant_id);
                    table.CheckConstraint("ck_secret_grant_heads_revision", "current_revision >= 0");
                });

            migrationBuilder.CreateTable(
                name: "secret_grant_operations",
                schema: "identity_broker",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    grant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_grant_operations", x => x.idempotency_key);
                });

            migrationBuilder.CreateTable(
                name: "secret_grant_revisions",
                schema: "identity_broker",
                columns: table => new
                {
                    grant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    purpose = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    secret_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_grant_revisions", x => new { x.grant_id, x.revision });
                    table.ForeignKey(
                        name: "FK_secret_grant_revisions_secret_grant_heads_grant_id",
                        column: x => x.grant_id,
                        principalSchema: "identity_broker",
                        principalTable: "secret_grant_heads",
                        principalColumn: "grant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secret_grant_revisions_actor_id_project_id_run_id",
                schema: "identity_broker",
                table: "secret_grant_revisions",
                columns: new[] { "actor_id", "project_id", "run_id" });

            migrationBuilder.Sql("""
                CREATE FUNCTION identity_broker.reject_secret_grant_revision_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Secret grant revisions are append-only';
                END;
                $$;
                CREATE TRIGGER secret_grant_revisions_append_only
                    BEFORE UPDATE OR DELETE ON identity_broker.secret_grant_revisions
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_secret_grant_revision_mutation();

                CREATE FUNCTION identity_broker.require_secret_grant_revision_advance()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' OR NEW.current_revision <> OLD.current_revision + 1 THEN
                        RAISE EXCEPTION 'Secret grant revision must advance by one';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER secret_grant_heads_revision_advance
                    BEFORE UPDATE OR DELETE ON identity_broker.secret_grant_heads
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.require_secret_grant_revision_advance();

                CREATE FUNCTION identity_broker.complete_secret_grant_operation_once()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE'
                       OR OLD.revision <> 0
                       OR NEW.revision <= 0
                       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                       OR NEW.request_hash IS DISTINCT FROM OLD.request_hash
                       OR NEW.grant_id IS DISTINCT FROM OLD.grant_id THEN
                        RAISE EXCEPTION 'Secret grant operation receipts are immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER secret_grant_operations_complete_once
                    BEFORE UPDATE OR DELETE ON identity_broker.secret_grant_operations
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.complete_secret_grant_operation_once();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS secret_grant_operations_complete_once ON identity_broker.secret_grant_operations;
                DROP FUNCTION IF EXISTS identity_broker.complete_secret_grant_operation_once();
                DROP TRIGGER IF EXISTS secret_grant_heads_revision_advance ON identity_broker.secret_grant_heads;
                DROP FUNCTION IF EXISTS identity_broker.require_secret_grant_revision_advance();
                DROP TRIGGER IF EXISTS secret_grant_revisions_append_only ON identity_broker.secret_grant_revisions;
                DROP FUNCTION IF EXISTS identity_broker.reject_secret_grant_revision_mutation();
                """);

            migrationBuilder.DropTable(
                name: "secret_grant_operations",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "secret_grant_revisions",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "secret_grant_heads",
                schema: "identity_broker");
        }
    }
}
