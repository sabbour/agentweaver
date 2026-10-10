using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Projects.Config.Migrations;

public partial class AddProjectSkillContentProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE projects_config.project_skill_content_revisions (
                project_id character varying(32) NOT NULL,
                skill_id character varying(256) NOT NULL,
                revision bigint NOT NULL,
                name character varying(64) NOT NULL,
                description character varying(1024) NOT NULL,
                content_digest character varying(64) NOT NULL,
                object_key character varying(512) NOT NULL,
                resource_count integer NOT NULL,
                total_bytes bigint NOT NULL,
                created_by_actor_id character varying(256) NOT NULL,
                created_at timestamp with time zone NOT NULL,
                source_id character varying(256),
                source_revision character varying(256),
                requested_ref character varying(256),
                resolved_commit_sha character varying(64),
                selected_path character varying(1024),
                CONSTRAINT pk_project_skill_content_revisions
                    PRIMARY KEY (project_id, skill_id, revision),
                CONSTRAINT ck_project_skill_revision CHECK (revision > 0),
                CONSTRAINT ck_project_skill_resource_count CHECK (resource_count BETWEEN 0 AND 64),
                CONSTRAINT ck_project_skill_total_bytes CHECK (total_bytes >= 0),
                CONSTRAINT ck_project_skill_digest CHECK (content_digest ~ '^[0-9a-f]{64}$'),
                CONSTRAINT fk_project_skill_content_revisions_project
                    FOREIGN KEY (project_id)
                    REFERENCES projects_config.projects (project_id)
                    ON DELETE RESTRICT
            );

            CREATE TABLE projects_config.project_skill_import_idempotency (
                project_id character varying(32) NOT NULL,
                scope_digest character varying(64) NOT NULL,
                actor_issuer character varying(512) NOT NULL,
                actor_id character varying(256) NOT NULL,
                idempotency_key character varying(128) NOT NULL,
                request_digest character varying(64) NOT NULL,
                receipt jsonb NOT NULL,
                created_at timestamp with time zone NOT NULL,
                CONSTRAINT pk_project_skill_import_idempotency
                    PRIMARY KEY (project_id, scope_digest),
                CONSTRAINT fk_project_skill_import_idempotency_project
                    FOREIGN KEY (project_id)
                    REFERENCES projects_config.projects (project_id)
                    ON DELETE RESTRICT
            );

            CREATE TABLE projects_config.project_skill_content_revocations (
                project_id character varying(32) NOT NULL,
                skill_id character varying(256) NOT NULL,
                revision bigint NOT NULL,
                revoked_by_actor_id character varying(256) NOT NULL,
                reason character varying(2000) NOT NULL,
                revoked_at timestamp with time zone NOT NULL,
                CONSTRAINT pk_project_skill_content_revocations
                    PRIMARY KEY (project_id, skill_id, revision),
                CONSTRAINT ck_project_skill_revocation_revision CHECK (revision > 0),
                CONSTRAINT fk_project_skill_content_revocations_revision
                    FOREIGN KEY (project_id, skill_id, revision)
                    REFERENCES projects_config.project_skill_content_revisions
                        (project_id, skill_id, revision)
                    ON DELETE RESTRICT
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE projects_config.project_skill_content_revocations;
            DROP TABLE projects_config.project_skill_import_idempotency;
            DROP TABLE projects_config.project_skill_content_revisions;
            """);
    }
}
