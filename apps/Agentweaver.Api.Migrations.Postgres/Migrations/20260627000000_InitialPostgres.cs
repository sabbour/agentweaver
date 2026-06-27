using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260627000000_InitialPostgres")]
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Existing MemoryDbContext entities ──────────────────────────────────

            migrationBuilder.CreateTable(
                name: "AgentMemory",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<string>(nullable: false),
                    AgentName = table.Column<string>(nullable: false),
                    Type = table.Column<string>(nullable: false),
                    Content = table.Column<string>(nullable: false),
                    Importance = table.Column<string>(nullable: false),
                    Tags = table.Column<string>(nullable: true),
                    SessionId = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AgentMemory", x => x.Id));

            migrationBuilder.CreateIndex("IX_AgentMemory_ProjectId_AgentName", "AgentMemory", new[] { "ProjectId", "AgentName" });
            migrationBuilder.CreateIndex("IX_AgentMemory_ProjectId_Type", "AgentMemory", new[] { "ProjectId", "Type" });

            migrationBuilder.CreateTable(
                name: "Decisions",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<string>(nullable: false),
                    AgentName = table.Column<string>(nullable: false),
                    Type = table.Column<string>(nullable: false),
                    Title = table.Column<string>(nullable: false),
                    Content = table.Column<string>(nullable: false),
                    Rationale = table.Column<string>(nullable: true),
                    Tags = table.Column<string>(nullable: true),
                    Status = table.Column<string>(nullable: false),
                    SupersededById = table.Column<int>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decisions", x => x.Id);
                    table.ForeignKey("FK_Decisions_Decisions_SupersededById", x => x.SupersededById, "Decisions", "Id");
                });

            migrationBuilder.CreateIndex("IX_Decisions_SupersededById", "Decisions", "SupersededById");
            migrationBuilder.CreateIndex("IX_Decisions_ProjectId_AgentName", "Decisions", new[] { "ProjectId", "AgentName" });
            migrationBuilder.CreateIndex("IX_Decisions_ProjectId_Status", "Decisions", new[] { "ProjectId", "Status" });

            migrationBuilder.CreateTable(
                name: "DecisionInbox",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<string>(nullable: false),
                    AgentName = table.Column<string>(nullable: false),
                    Type = table.Column<string>(nullable: false),
                    Title = table.Column<string>(nullable: false),
                    Content = table.Column<string>(nullable: false),
                    Rationale = table.Column<string>(nullable: true),
                    Slug = table.Column<string>(nullable: false),
                    Status = table.Column<string>(nullable: false),
                    DecisionId = table.Column<int>(nullable: true),
                    MergedAt = table.Column<DateTimeOffset>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionInbox", x => x.Id);
                    table.ForeignKey("FK_DecisionInbox_Decisions_DecisionId", x => x.DecisionId, "Decisions", "Id");
                });

            migrationBuilder.CreateIndex("IX_DecisionInbox_DecisionId", "DecisionInbox", "DecisionId");
            migrationBuilder.CreateIndex("IX_DecisionInbox_ProjectId_Slug", "DecisionInbox", new[] { "ProjectId", "Slug" }, unique: true);
            migrationBuilder.CreateIndex("IX_DecisionInbox_ProjectId_Status", "DecisionInbox", new[] { "ProjectId", "Status" });

            migrationBuilder.CreateTable(
                name: "SessionContexts",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<string>(nullable: false),
                    SessionId = table.Column<string>(nullable: false),
                    FocusArea = table.Column<string>(nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(nullable: true),
                    Summary = table.Column<string>(nullable: true),
                    SerializedState = table.Column<string>(nullable: true),
                    ActiveIssues = table.Column<string>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_SessionContexts", x => x.Id));

            migrationBuilder.CreateIndex("IX_SessionContexts_ProjectId_EndedAt", "SessionContexts", new[] { "ProjectId", "EndedAt" });
            migrationBuilder.CreateIndex("IX_SessionContexts_ProjectId_SessionId", "SessionContexts", new[] { "ProjectId", "SessionId" }, unique: true);

            migrationBuilder.CreateTable(
                name: "RunEvents",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(nullable: false),
                    Sequence = table.Column<int>(nullable: false),
                    EventType = table.Column<string>(nullable: false),
                    PayloadJson = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_RunEvents", x => x.Id));

            migrationBuilder.CreateIndex("IX_RunEvents_RunId", "RunEvents", "RunId");
            migrationBuilder.CreateIndex("IX_RunEvents_RunId_Sequence", "RunEvents", new[] { "RunId", "Sequence" }, unique: true);

            migrationBuilder.CreateTable(
                name: "OutcomeSpecs",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<string>(nullable: false),
                    CoordinatorRunId = table.Column<string>(nullable: false),
                    Goal = table.Column<string>(nullable: false),
                    DesiredOutcome = table.Column<string>(nullable: false),
                    Scope = table.Column<string>(nullable: false),
                    Assumptions = table.Column<string>(nullable: false),
                    ClarifyingQuestions = table.Column<string>(nullable: true),
                    Status = table.Column<string>(nullable: false),
                    ConfirmedBy = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_OutcomeSpecs", x => x.Id));

            migrationBuilder.CreateIndex("IX_OutcomeSpecs_ProjectId_CoordinatorRunId", "OutcomeSpecs", new[] { "ProjectId", "CoordinatorRunId" });

            migrationBuilder.CreateTable(
                name: "WorkPlans",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoordinatorRunId = table.Column<string>(nullable: false),
                    OutcomeSpecId = table.Column<int>(nullable: false),
                    Status = table.Column<string>(nullable: false),
                    WorkflowId = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPlans", x => x.Id);
                    table.ForeignKey("FK_WorkPlans_OutcomeSpecs_OutcomeSpecId", x => x.OutcomeSpecId, "OutcomeSpecs", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_WorkPlans_CoordinatorRunId", "WorkPlans", "CoordinatorRunId");
            migrationBuilder.CreateIndex("IX_WorkPlans_OutcomeSpecId", "WorkPlans", "OutcomeSpecId");

            migrationBuilder.CreateTable(
                name: "Subtasks",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkPlanId = table.Column<int>(nullable: false),
                    Title = table.Column<string>(nullable: false),
                    Scope = table.Column<string>(nullable: false),
                    AssignedAgent = table.Column<string>(nullable: false),
                    IsolationStrategy = table.Column<string>(nullable: false),
                    SelectedModelId = table.Column<string>(nullable: false),
                    AgentCharter = table.Column<string>(nullable: true),
                    LockedOutAgents = table.Column<string>(nullable: true),
                    Status = table.Column<string>(nullable: false),
                    Phase = table.Column<string>(nullable: false),
                    ChildRunId = table.Column<string>(nullable: true),
                    RecoveryAttempts = table.Column<int>(nullable: false),
                    RecoveryGuidance = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subtasks", x => x.Id);
                    table.ForeignKey("FK_Subtasks_WorkPlans_WorkPlanId", x => x.WorkPlanId, "WorkPlans", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_Subtasks_WorkPlanId", "Subtasks", "WorkPlanId");

            migrationBuilder.CreateTable(
                name: "SubtaskDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubtaskId = table.Column<int>(nullable: false),
                    DependsOnSubtaskId = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtaskDependencies", x => x.Id);
                    table.ForeignKey("FK_SubtaskDependencies_Subtasks_SubtaskId", x => x.SubtaskId, "Subtasks", "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_SubtaskDependencies_Subtasks_DependsOnSubtaskId", x => x.DependsOnSubtaskId, "Subtasks", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex("IX_SubtaskDependencies_SubtaskId", "SubtaskDependencies", "SubtaskId");
            migrationBuilder.CreateIndex("IX_SubtaskDependencies_DependsOnSubtaskId", "SubtaskDependencies", "DependsOnSubtaskId");

            migrationBuilder.CreateTable(
                name: "SteeringDirectives",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoordinatorRunId = table.Column<string>(nullable: false),
                    Instruction = table.Column<string>(nullable: false),
                    Kind = table.Column<string>(nullable: false),
                    Status = table.Column<string>(nullable: false),
                    CreatedBy = table.Column<string>(nullable: false),
                    TargetChildRunId = table.Column<string>(nullable: true),
                    RelayedAt = table.Column<DateTimeOffset>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_SteeringDirectives", x => x.Id));

            migrationBuilder.CreateIndex("IX_SteeringDirectives_CoordinatorRunId_Status", "SteeringDirectives", new[] { "CoordinatorRunId", "Status" });

            migrationBuilder.CreateTable(
                name: "McpRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TokenHash = table.Column<string>(nullable: false),
                    ChainId = table.Column<string>(nullable: false),
                    Subject = table.Column<string>(nullable: false),
                    GithubLogin = table.Column<string>(nullable: false),
                    ClientId = table.Column<string>(nullable: false),
                    Scope = table.Column<string>(nullable: false),
                    Org = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_McpRefreshTokens", x => x.Id));

            migrationBuilder.CreateIndex("IX_McpRefreshTokens_TokenHash", "McpRefreshTokens", "TokenHash", unique: true);
            migrationBuilder.CreateIndex("IX_McpRefreshTokens_ChainId", "McpRefreshTokens", "ChainId");
            migrationBuilder.CreateIndex("IX_McpRefreshTokens_Subject_ClientId", "McpRefreshTokens", new[] { "Subject", "ClientId" });

            migrationBuilder.CreateTable(
                name: "McpRevokedJtis",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Jti = table.Column<string>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_McpRevokedJtis", x => x.Id));

            migrationBuilder.CreateIndex("IX_McpRevokedJtis_Jti", "McpRevokedJtis", "Jti", unique: true);
            migrationBuilder.CreateIndex("IX_McpRevokedJtis_ExpiresAt", "McpRevokedJtis", "ExpiresAt");

            migrationBuilder.CreateTable(
                name: "McpClientRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<string>(nullable: false),
                    RedirectUris = table.Column<string>(nullable: false),
                    ClientName = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_McpClientRegistrations", x => x.Id));

            migrationBuilder.CreateIndex("IX_McpClientRegistrations_ClientId", "McpClientRegistrations", "ClientId", unique: true);

            // ── agentweaver.db entities (spec-018 P2) ─────────────────────────────

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    project_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    origin_kind = table.Column<string>(nullable: false),
                    source_repository = table.Column<string>(nullable: true),
                    working_directory = table.Column<string>(nullable: false),
                    default_branch = table.Column<string>(nullable: false, defaultValue: "main"),
                    owner = table.Column<string>(nullable: false),
                    default_provider = table.Column<string>(nullable: false),
                    default_model_copilot = table.Column<string>(nullable: true),
                    default_model_foundry = table.Column<string>(nullable: true),
                    state = table.Column<string>(nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false),
                    max_ready_per_heartbeat = table.Column<int>(nullable: false, defaultValue: 3),
                    pickup_autopilot = table.Column<bool>(nullable: false, defaultValue: true),
                    pickup_auto_approve_tools = table.Column<bool>(nullable: false, defaultValue: false),
                    default_workflow_id = table.Column<string>(nullable: true),
                    active_review_policy_name = table.Column<string>(nullable: true),
                    sandbox_profile = table.Column<string>(nullable: true),
                    source_blueprint_id = table.Column<string>(nullable: true),
                    source_blueprint_type = table.Column<string>(nullable: true),
                    allowed_workflow_ids = table.Column<string>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_projects", x => x.project_id));

            migrationBuilder.CreateIndex("IX_projects_state", "projects", "state");

            migrationBuilder.CreateTable(
                name: "runs",
                columns: table => new
                {
                    run_id = table.Column<string>(nullable: false),
                    repository_path = table.Column<string>(nullable: false),
                    originating_branch = table.Column<string>(nullable: false),
                    model_source = table.Column<string>(nullable: false),
                    task = table.Column<string>(nullable: false),
                    submitting_user = table.Column<string>(nullable: false),
                    status = table.Column<string>(nullable: false),
                    started_at = table.Column<DateTimeOffset>(nullable: false),
                    ended_at = table.Column<DateTimeOffset>(nullable: true),
                    result = table.Column<string>(nullable: true),
                    worktree_path = table.Column<string>(nullable: true),
                    worktree_branch = table.Column<string>(nullable: true),
                    tree_hash = table.Column<string>(nullable: true),
                    step_count = table.Column<int>(nullable: false, defaultValue: 0),
                    diff = table.Column<string>(nullable: true),
                    merge_conflicts = table.Column<string>(nullable: true),
                    project_id = table.Column<string>(nullable: true),
                    model_id = table.Column<string>(nullable: true),
                    agent_name = table.Column<string>(nullable: true),
                    agent_charter = table.Column<string>(nullable: true),
                    reviewed_by = table.Column<string>(nullable: true),
                    workflow_run_id = table.Column<string>(nullable: true),
                    merged_commit_hash = table.Column<string>(nullable: true),
                    parent_run_id = table.Column<string>(nullable: true),
                    subtask_id = table.Column<string>(nullable: true),
                    origin = table.Column<string>(nullable: false, defaultValue: "interactive"),
                    retried_from = table.Column<string>(nullable: true),
                    review_ready_at = table.Column<DateTimeOffset>(nullable: true),
                    review_wait_ms = table.Column<long>(nullable: false, defaultValue: 0L),
                    archived_at = table.Column<DateTimeOffset>(nullable: true),
                    owner_id = table.Column<string>(nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(nullable: true),
                    heartbeat_at = table.Column<DateTimeOffset>(nullable: true),
                    fencing_token = table.Column<long>(nullable: false, defaultValue: 0L),
                    attempt = table.Column<int>(nullable: false, defaultValue: 0)
                },
                constraints: table => table.PrimaryKey("PK_runs", x => x.run_id));

            migrationBuilder.CreateIndex("IX_runs_ProjectId_Status", "runs", new[] { "project_id", "status" });
            migrationBuilder.CreateIndex("IX_runs_Origin_Status", "runs", new[] { "origin", "status" });
            migrationBuilder.CreateIndex("IX_runs_ParentRunId_SubtaskId", "runs", new[] { "parent_run_id", "subtask_id" });
            migrationBuilder.CreateIndex("IX_runs_WorkflowRunId", "runs", "workflow_run_id");
            migrationBuilder.CreateIndex(
                name: "IX_runs_lease",
                table: "runs",
                columns: new[] { "owner_id", "lease_expires_at" });

            migrationBuilder.CreateTable(
                name: "run_revisions",
                columns: table => new
                {
                    run_id = table.Column<string>(nullable: false),
                    revision_number = table.Column<int>(nullable: false),
                    reviewer_user = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    raw_comment = table.Column<string>(nullable: false),
                    sanitized_comment = table.Column<string>(nullable: false),
                    previous_tree_hash = table.Column<string>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_run_revisions", x => new { x.run_id, x.revision_number }));

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    workflow_run_id = table.Column<string>(nullable: false),
                    project_id = table.Column<string>(nullable: false),
                    task = table.Column<string>(nullable: false),
                    submitting_user = table.Column<string>(nullable: false),
                    started_at = table.Column<DateTimeOffset>(nullable: false),
                    orchestration_worktree_path = table.Column<string>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_workflow_runs", x => x.workflow_run_id));

            migrationBuilder.CreateIndex("IX_workflow_runs_project_id", "workflow_runs", "project_id");

            migrationBuilder.CreateTable(
                name: "backlog_tasks",
                columns: table => new
                {
                    task_id = table.Column<string>(nullable: false),
                    project_id = table.Column<string>(nullable: false),
                    title = table.Column<string>(nullable: false),
                    description = table.Column<string>(nullable: true),
                    state = table.Column<string>(nullable: false),
                    order_key = table.Column<string>(nullable: false),
                    captured_by = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    committed_at = table.Column<DateTimeOffset>(nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(nullable: true),
                    run_id = table.Column<string>(nullable: true),
                    workflow_override_id = table.Column<string>(nullable: true),
                    archived_at = table.Column<DateTimeOffset>(nullable: true),
                    source_file_path = table.Column<string>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_backlog_tasks", x => x.task_id));

            migrationBuilder.CreateIndex("IX_backlog_tasks_project_state_orderkey", "backlog_tasks", new[] { "project_id", "state", "order_key" });

            migrationBuilder.CreateIndex(
                name: "IX_backlog_tasks_orderkey_unique",
                table: "backlog_tasks",
                columns: new[] { "project_id", "state", "order_key" },
                unique: true,
                filter: "state IN ('backlog','ready') AND archived_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backlog_tasks_run",
                table: "backlog_tasks",
                column: "run_id",
                unique: true,
                filter: "run_id IS NOT NULL");

            migrationBuilder.CreateTable(
                name: "cast_proposals",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    project_id = table.Column<string>(nullable: false),
                    owner = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    expires_at = table.Column<DateTimeOffset>(nullable: false),
                    proposal_json = table.Column<string>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_cast_proposals", x => x.id));

            migrationBuilder.CreateIndex("IX_cast_proposals_project_id", "cast_proposals", "project_id");

            // Append-only enforcement for run_revisions: equivalent to the SQLite RAISE(ABORT) triggers.
            // Using REVOKE is simpler and safer than a trigger for this table.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION fn_run_revisions_no_update_delete()
                RETURNS trigger LANGUAGE plpgsql AS
                $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        RAISE EXCEPTION 'run_revisions is append-only: UPDATE is not permitted';
                    ELSIF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'run_revisions is append-only: DELETE is not permitted';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER trg_run_revisions_no_update
                    BEFORE UPDATE ON run_revisions
                    FOR EACH ROW EXECUTE FUNCTION fn_run_revisions_no_update_delete();

                CREATE TRIGGER trg_run_revisions_no_delete
                    BEFORE DELETE ON run_revisions
                    FOR EACH ROW EXECUTE FUNCTION fn_run_revisions_no_update_delete();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_run_revisions_no_delete ON run_revisions;
                DROP TRIGGER IF EXISTS trg_run_revisions_no_update ON run_revisions;
                DROP FUNCTION IF EXISTS fn_run_revisions_no_update_delete;
                """);
            migrationBuilder.DropTable("cast_proposals");
            migrationBuilder.DropTable("backlog_tasks");
            migrationBuilder.DropTable("workflow_runs");
            migrationBuilder.DropTable("run_revisions");
            migrationBuilder.DropTable("runs");
            migrationBuilder.DropTable("projects");
            migrationBuilder.DropTable("McpClientRegistrations");
            migrationBuilder.DropTable("McpRevokedJtis");
            migrationBuilder.DropTable("McpRefreshTokens");
            migrationBuilder.DropTable("SteeringDirectives");
            migrationBuilder.DropTable("SubtaskDependencies");
            migrationBuilder.DropTable("Subtasks");
            migrationBuilder.DropTable("WorkPlans");
            migrationBuilder.DropTable("OutcomeSpecs");
            migrationBuilder.DropTable("RunEvents");
            migrationBuilder.DropTable("SessionContexts");
            migrationBuilder.DropTable("DecisionInbox");
            migrationBuilder.DropTable("Decisions");
            migrationBuilder.DropTable("AgentMemory");
        }
    }
}
