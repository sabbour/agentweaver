using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotUserConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "copilot_connections",
                schema: "identity_broker",
                columns: table => new
                {
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    OwnerActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GitHubUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CredentialKind = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SecretId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SecretVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FreshUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_connections", x => x.ConnectionId);
                });
            migrationBuilder.Sql("""
                ALTER TABLE identity_broker.copilot_connections ADD CONSTRAINT copilot_connection_shape
                    CHECK ("Revision" > 0 AND "CredentialKind" = 0 AND "Scope" IN (0, 2)
                        AND "State" BETWEEN 0 AND 6
                        AND "StateHash" ~ '^[0-9a-f]{64}$'
                        AND ("State" <> 1 OR ("SecretId" IS NOT NULL AND "SecretVersion" IS NOT NULL
                            AND "GitHubUserId" IS NOT NULL)));
                CREATE FUNCTION identity_broker.protect_copilot_connection()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN
                        RAISE EXCEPTION 'Copilot connection identity must be retained';
                    END IF;
                    IF NEW."ConnectionId" IS DISTINCT FROM OLD."ConnectionId"
                       OR NEW."OwnerIssuer" IS DISTINCT FROM OLD."OwnerIssuer"
                       OR NEW."OwnerActorId" IS DISTINCT FROM OLD."OwnerActorId"
                       OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                       OR NEW."Scope" IS DISTINCT FROM OLD."Scope"
                       OR NEW."ScopeId" IS DISTINCT FROM OLD."ScopeId"
                       OR NEW."CredentialKind" IS DISTINCT FROM OLD."CredentialKind"
                       OR NEW."StateHash" IS DISTINCT FROM OLD."StateHash"
                       OR (OLD."GitHubUserId" IS NOT NULL
                           AND NEW."GitHubUserId" IS DISTINCT FROM OLD."GitHubUserId")
                       OR NEW."Revision" <> OLD."Revision" + 1
                       OR (OLD."State" = 5 AND NEW."State" <> 5) THEN
                        RAISE EXCEPTION 'Copilot connection binding or revision changed illegally';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER copilot_connection_binding
                    BEFORE UPDATE OR DELETE ON identity_broker.copilot_connections
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.protect_copilot_connection();
                CREATE TRIGGER copilot_connection_retained
                    BEFORE TRUNCATE ON identity_broker.copilot_connections
                    FOR EACH STATEMENT EXECUTE FUNCTION identity_broker.protect_copilot_connection();
                """);

            migrationBuilder.CreateTable(
                name: "copilot_connection_revisions",
                schema: "identity_broker",
                columns: table => new
                {
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SecretId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SecretVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FreshUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_copilot_connection_revisions", x => new { x.ConnectionId, x.Revision });
                    table.ForeignKey(
                        name: "FK_copilot_connection_revisions_copilot_connections_Connection~",
                        column: x => x.ConnectionId,
                        principalSchema: "identity_broker",
                        principalTable: "copilot_connections",
                        principalColumn: "ConnectionId",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.Sql("""
                CREATE TRIGGER copilot_connection_history_immutable
                    BEFORE UPDATE OR DELETE ON identity_broker.copilot_connection_revisions
                    FOR EACH ROW EXECUTE FUNCTION identity_broker.reject_runtime_grant_mutation();
                CREATE TRIGGER copilot_connection_history_retained
                    BEFORE TRUNCATE ON identity_broker.copilot_connection_revisions
                    FOR EACH STATEMENT EXECUTE FUNCTION identity_broker.reject_runtime_grant_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER copilot_connection_history_retained ON identity_broker.copilot_connection_revisions;
                DROP TRIGGER copilot_connection_history_immutable ON identity_broker.copilot_connection_revisions;
                DROP TRIGGER copilot_connection_retained ON identity_broker.copilot_connections;
                DROP TRIGGER copilot_connection_binding ON identity_broker.copilot_connections;
                DROP FUNCTION identity_broker.protect_copilot_connection();
                """);
            migrationBuilder.DropTable(
                name: "copilot_connection_revisions",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "copilot_connections",
                schema: "identity_broker");
        }
    }
}
