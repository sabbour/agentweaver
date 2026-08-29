using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

public partial class AddTwoAppPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE github_authorizations (
                state text PRIMARY KEY, app_kind integer NOT NULL, purpose integer NOT NULL,
                entra_object_id text NOT NULL, project_id text NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                expires_at_unix_ms bigint NOT NULL, return_route_key text NOT NULL,
                pkce_verifier_protected text NOT NULL, callback_cookie_hash text NOT NULL,
                status integer NOT NULL, created_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone NULL);
            CREATE INDEX "IX_github_authorizations_entra_object_id_state" ON github_authorizations(entra_object_id, state);
            CREATE INDEX "IX_github_authorizations_expires_at_unix_ms" ON github_authorizations(expires_at_unix_ms);

            CREATE TABLE github_app_authorizations (
                id text PRIMARY KEY, entra_object_id text NOT NULL, app_kind integer NOT NULL, purpose integer NOT NULL,
                credential_reference text NOT NULL, credential_version text NOT NULL, grant_digest text NOT NULL,
                created_at timestamp with time zone NOT NULL, revoked_at timestamp with time zone NULL);
            CREATE INDEX "IX_github_app_authorizations_entra_object_id_app_kind_purpose"
                ON github_app_authorizations(entra_object_id, app_kind, purpose);

            CREATE TABLE github_installations (
                installation_id bigint PRIMARY KEY, app_kind integer NOT NULL,
                project_id text NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                created_at timestamp with time zone NOT NULL, revoked_at timestamp with time zone NULL);

            CREATE TABLE github_repository_grants (
                installation_id bigint NOT NULL REFERENCES github_installations(installation_id) ON DELETE CASCADE,
                repository_id bigint NOT NULL, project_id text NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                full_name_display text NOT NULL, permission_digest text NOT NULL,
                granted_at timestamp with time zone NOT NULL, revoked_at timestamp with time zone NULL,
                PRIMARY KEY(installation_id, repository_id));

            CREATE TABLE project_copilot_bindings (
                id text PRIMARY KEY, project_id text NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                entra_object_id text NOT NULL, credential_reference text NOT NULL, credential_version text NOT NULL,
                grant_digest text NOT NULL, status integer NOT NULL, bound_at timestamp with time zone NOT NULL,
                deactivated_at timestamp with time zone NULL);
            CREATE UNIQUE INDEX "UX_project_copilot_bindings_active_project"
                ON project_copilot_bindings(project_id) WHERE status = 0;

            CREATE TABLE automation_activations (
                id text PRIMARY KEY, project_id text NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                installation_id bigint NOT NULL, repository_id bigint NOT NULL, automation_key text NOT NULL,
                status integer NOT NULL, activated_at timestamp with time zone NOT NULL,
                invalidated_at timestamp with time zone NULL,
                UNIQUE(project_id, installation_id, repository_id, automation_key),
                FOREIGN KEY(installation_id, repository_id)
                    REFERENCES github_repository_grants(installation_id, repository_id) ON DELETE CASCADE);

            CREATE TABLE automation_invocations (
                id text PRIMARY KEY, project_id text NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                activation_id text NOT NULL REFERENCES automation_activations(id) ON DELETE CASCADE,
                occurrence_key text NOT NULL, delivery_id text NULL, event_name text NULL,
                installation_id bigint NULL, repository_id bigint NULL, outcome integer NOT NULL,
                received_at timestamp with time zone NOT NULL, completed_at timestamp with time zone NULL,
                UNIQUE(activation_id, occurrence_key), UNIQUE(delivery_id, event_name));

            CREATE TABLE run_github_identity_snapshots (
                run_id text PRIMARY KEY, project_id text NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
                app_kind integer NOT NULL, purpose integer NOT NULL, credential_reference text NOT NULL,
                credential_version text NOT NULL, grant_digest text NOT NULL, installation_id bigint NULL,
                repository_id bigint NULL, entra_object_id text NULL, captured_at timestamp with time zone NOT NULL);

            CREATE TABLE github_audit_records (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, entra_object_id text NULL,
                actor_kind integer NOT NULL CHECK (actor_kind IN (0, 1)), action integer NOT NULL,
                resource_id text NOT NULL, app_kind integer NULL, purpose integer NULL, outcome integer NOT NULL,
                reason_code integer NOT NULL, correlation_id text NOT NULL,
                occurred_at timestamp with time zone NOT NULL, credential_version_or_digest text NULL);
            CREATE INDEX "IX_github_audit_records_occurred_at" ON github_audit_records(occurred_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS github_audit_records;
            DROP TABLE IF EXISTS run_github_identity_snapshots;
            DROP TABLE IF EXISTS automation_invocations;
            DROP TABLE IF EXISTS automation_activations;
            DROP TABLE IF EXISTS project_copilot_bindings;
            DROP TABLE IF EXISTS github_repository_grants;
            DROP TABLE IF EXISTS github_installations;
            DROP TABLE IF EXISTS github_app_authorizations;
            DROP TABLE IF EXISTS github_authorizations;
            """);
    }
}
