using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Gateway;

public enum GatewayOwner
{
    Projects,
    Orchestrator,
    Knowledge,
    Events,
    IdentityBroker,
}

public sealed record GatewayRoute(
    string Method,
    string PublicPath,
    string OwnerPath,
    string OperationId,
    string Summary,
    GatewayOwner Owner,
    ImmutableArray<string> QueryParameters,
    bool HasJsonBody = false,
    bool AcceptsOnly = false,
    bool IsRunEventStream = false,
    bool RequiresTenantSelector = false,
    bool ForwardSetCookie = false,
    bool JsonBodyRequired = true,
    long? MaximumRequestBodyBytes = null,
    bool ForwardTenantSelector = false);

public static class GatewayRouteCatalog
{
    public const string VersionPrefix = "/api/v1";

    public static ImmutableArray<GatewayRoute> Routes { get; } = BuildRoutes();

    private static ImmutableArray<GatewayRoute> BuildRoutes()
    {
        var routes = ImmutableArray.CreateBuilder<GatewayRoute>();

        void Add(
            string method,
            GatewayOwner owner,
            string publicSuffix,
            string ownerPath,
            string operationId,
            string summary,
            string[]? query = null,
            bool body = false,
            bool acceptsOnly = false,
            bool eventStream = false,
            bool requiresTenantSelector = false,
            bool jsonBodyRequired = true,
            long? maximumRequestBodyBytes = null,
            bool forwardTenantSelector = false) =>
            routes.Add(new GatewayRoute(
                method,
                VersionPrefix + publicSuffix,
                ownerPath,
                operationId,
                summary,
                owner,
                (query ?? []).ToImmutableArray(),
                body,
                acceptsOnly,
                eventStream,
                requiresTenantSelector,
                JsonBodyRequired: jsonBodyRequired,
                MaximumRequestBodyBytes: maximumRequestBodyBytes,
                ForwardTenantSelector: forwardTenantSelector || requiresTenantSelector));

        Add("GET", GatewayOwner.Projects, "/projects", "/api/projects/",
            "listProjects", "List projects", forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects", "/api/projects/",
            "createProject", "Create a project", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}", "/api/projects/{projectId}",
            "getProject", "Read a project", ["runId"], forwardTenantSelector: true);
        Add("PATCH", GatewayOwner.Projects, "/projects/{projectId}", "/api/projects/{projectId}",
            "updateProject", "Update a project", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/configuration",
            "/api/projects/{projectId}/configuration",
            "getProjectConfiguration", "Read project configuration", ["revision"],
            forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/configuration",
            "/api/projects/{projectId}/configuration",
            "updateProjectConfiguration", "Update project configuration", body: true,
            forwardTenantSelector: true);
        const long skillContentMaximumRequestBytes = 3 * 1024 * 1024;
        var marketplaceSources = "/projects/{projectId}/skill-marketplaces/sources";
        Add("GET", GatewayOwner.Projects, marketplaceSources, "/api" + marketplaceSources,
            "listMarketplaceSources", "List project marketplace sources",
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, marketplaceSources, "/api" + marketplaceSources,
            "createMarketplaceSource", "Create a project marketplace source",
            body: true, forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, marketplaceSources + "/{sourceId}",
            "/api" + marketplaceSources + "/{sourceId}",
            "updateMarketplaceSource", "Update a project marketplace source",
            body: true, forwardTenantSelector: true);
        Add("DELETE", GatewayOwner.Projects, marketplaceSources + "/{sourceId}",
            "/api" + marketplaceSources + "/{sourceId}",
            "removeMarketplaceSource", "Tombstone a project marketplace source",
            ["expectedRevision"], forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, marketplaceSources + "/{sourceId}/browse",
            "/api" + marketplaceSources + "/{sourceId}/browse",
            "browseMarketplaceSource", "Browse one revision of a project marketplace source",
            ["expectedSourceRevision", "query", "page", "pageSize"], forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/skills/preview", "/api/skills/preview",
            "previewSkillContent", "Validate skill content without importing it",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects/{projectId}/skills/import",
            "/api/projects/{projectId}/skills/import",
            "importProjectSkill", "Import validated skill content to a project",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/skills/{skillId}/assignment",
            "/api/projects/{projectId}/skills/{skillId}/assignment",
            "updateProjectSkillAssignment", "Update a project skill assignment",
            body: true, forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/runs/{runId}/selection",
            "/api/projects/{projectId}/runs/{runId}/selection",
            "acceptRunSelection", "Accept a run selection", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/runs/{runId}/selection",
            "/api/projects/{projectId}/runs/{runId}/selection",
            "getRunSelection", "Read the accepted run selection", forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/authorization/context", "/api/authorization/context",
            "getAuthorizationContext", "Read the caller's current authorization context",
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/platform/runtime-defaults",
            "/api/platform/runtime-defaults/",
            "getPlatformRuntimeDefaults", "Read platform runtime defaults",
            forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, "/platform/runtime-defaults",
            "/api/platform/runtime-defaults/",
            "updatePlatformRuntimeDefaults", "Update platform runtime defaults", body: true,
            forwardTenantSelector: true);

        var sourceControl = "/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}";
        void SourceControl(
            string method,
            string suffix,
            string operationId,
            string summary,
            bool body = false,
            bool acceptsOnly = false,
            bool jsonBodyRequired = true) =>
            Add(
                method,
                GatewayOwner.Orchestrator,
                sourceControl + suffix,
                "/api" + sourceControl + suffix,
                operationId,
                summary,
                body: body,
                acceptsOnly: acceptsOnly,
                requiresTenantSelector: true,
                jsonBodyRequired: jsonBodyRequired);

        SourceControl("POST", "/pin",
            "pinSourceControlRepository", "Pin the selected repository",
            body: true,
            jsonBodyRequired: false);
        SourceControl("POST", "/issues",
            "createSourceControlIssue", "Create a repository issue", body: true);
        SourceControl("POST", "/pull-requests",
            "createSourceControlPullRequest", "Create or reuse a pull request", body: true);
        SourceControl("GET", "/pull-requests/{pullRequestNumber:long}/reviews",
            "readSourceControlReviews", "Read pull request reviews");
        SourceControl("POST", "/workspaces",
            "prepareSourceControlWorkspace", "Prepare a repository workspace", body: true);
        SourceControl("POST", "/workspaces/{workspaceId}/diff",
            "readSourceControlWorkspaceDiff", "Read a repository workspace diff", body: true);
        SourceControl("POST", "/merge-intents",
            "prepareSourceControlMergeIntent", "Request approval for a pull request merge",
            body: true,
            acceptsOnly: true);
        SourceControl("GET", "/merge-intents/{intentId}",
            "readSourceControlMergeIntent", "Read a pull request merge intent");
        SourceControl("POST", "/merge-intents/{intentId}/execute",
            "executeSourceControlMergeIntent", "Execute an approved pull request merge");

        var coordination = "/projects/{projectId}/runs/{runId}/coordination";
        void Coordination(
            string method,
            string suffix,
            string operationId,
            string summary,
            bool body = false,
            bool acceptsOnly = false) =>
            Add(
                method,
                GatewayOwner.Orchestrator,
                coordination + suffix,
                "/api" + coordination + suffix,
                operationId,
                summary,
                body: body,
                acceptsOnly: acceptsOnly,
                forwardTenantSelector: true);

        Coordination("POST", "/root", "acceptRunRoot", "Accept the run root", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/outcome",
            "proposeOutcome", "Propose an outcome", body: true);
        Coordination("GET", "/status", "readRunStatus", "Read run status");
        Coordination("POST", "/recovery", "recoverRun", "Request run recovery", body: true);
        Coordination("POST", "/sessions/{sessionId}/actions/propose_outcome_spec",
            "proposeOutcomeSpec", "Propose an outcome specification", body: true);
        Coordination("POST", "/sessions/{sessionId}/actions/select_workflow",
            "selectWorkflow", "Select a workflow", body: true);
        Coordination("POST", "/sessions/{sessionId}/actions/propose_work_plan",
            "proposeWorkPlan", "Propose a work plan", body: true);
        Coordination("POST", "/sessions/{sessionId}/actions/revise_work_plan",
            "reviseWorkPlan", "Revise a work plan", body: true);
        Coordination("POST", "/sessions/{sessionId}/actions/request_assembly",
            "requestAssembly", "Request assembly", body: true);
        Coordination("GET", "/sessions/{sessionId}/decisions",
            "readDecisions", "Read coordinator decisions");
        Coordination("GET", "/sessions/{sessionId}/tree",
            "readSessionTree", "Read the session tree");
        Coordination("GET", "/sessions/{sessionId}/status",
            "readSessionStatus", "Read session status");
        Coordination("POST", "/sessions/{sessionId}/turn-failure",
            "reportTurnFailure", "Report a turn failure", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/gates/{requestId}/answer",
            "answerGate", "Answer a coordinator gate", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/questions",
            "askQuestion", "Ask a coordinator question", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/outcome/questions/next",
            "askOutcomeQuestion", "Ask the next outcome question", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/approvals",
            "requestApproval", "Request approval", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/gates/{requestId}/acknowledge",
            "acknowledgeGate", "Acknowledge a coordinator gate", body: true);
        Coordination("POST", "/sessions/{parentSessionId}/children",
            "registerChild", "Register a child session", body: true);
        Coordination("POST", "/sessions/{parentSessionId}/spawn",
            "spawnSession", "Spawn a child session", body: true, acceptsOnly: true);
        Coordination("POST", "/sessions/{sessionId}/fork",
            "forkSession", "Fork a session", body: true);
        Coordination("POST", "/sessions/{sessionId}/detach",
            "detachSession", "Detach a session", body: true);
        Coordination("POST", "/sessions/{parentSessionId}/children/{childSessionId}/archive",
            "archiveChild", "Archive a child session", body: true);
        Coordination("POST", "/sessions/{sessionId}/idle-subscriptions",
            "subscribeToIdle", "Subscribe to session idle", body: true, acceptsOnly: true);
        Coordination("POST", "/sessions/{sessionId}/steering",
            "steerSession", "Steer a session", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/gates/{requestId}/approve",
            "approveGate", "Approve a coordinator gate", body: true);
        Coordination("POST", "/sessions/{sessionId}/decisions/gates/{requestId}/reject",
            "rejectGate", "Reject a coordinator gate", body: true);
        Coordination("POST", "/sessions/{sessionId}/messages",
            "sendMessage", "Send a session message", body: true, acceptsOnly: true);
        Coordination("POST", "/sessions/{sessionId}/turn-boundary",
            "advanceTurnBoundary", "Advance a turn boundary", body: true);
        Coordination("POST", "/sessions/{sessionId}/turn-completion",
            "completeTurn", "Complete a turn", body: true);
        Coordination("GET", "/sessions/{sessionId}/notifications",
            "readNotifications", "Read session notifications");
        Coordination("POST", "/sessions/{sessionId}/notifications/{notificationId:guid}/acknowledge",
            "acknowledgeNotification", "Acknowledge a notification");
        Coordination("POST", "/sessions/{sessionId}/messages/{messageId:guid}/acknowledge",
            "acknowledgeMessage", "Acknowledge a message", body: true);
        Coordination("GET", "/policy-evaluations/{receiptId:guid}",
            "readPolicyEvaluation", "Read a policy evaluation receipt");
        Coordination("GET", "/policy-evaluations/{receiptId:guid}/admission",
            "validatePolicyEvaluationAdmission", "Validate policy evaluation admission");

        var agent = "/projects/{projectId}/runs/{runId}/agents/{agentId}";
        void Knowledge(
            string method,
            string suffix,
            string operationId,
            string summary,
            string[]? query = null,
            bool body = false,
            long? maximumRequestBodyBytes = null) =>
            Add(
                method,
                GatewayOwner.Knowledge,
                agent + suffix,
                "/api" + agent + suffix,
                operationId,
                summary,
                query,
                body,
                maximumRequestBodyBytes: maximumRequestBodyBytes,
                forwardTenantSelector: true);

        Knowledge("POST", "/records", "createKnowledgeRecord", "Create a Knowledge record", body: true);
        Knowledge("GET", "/records", "searchKnowledge", "Search Knowledge records",
            ["kind", "q", "includeInactive", "page", "pageSize"]);
        Knowledge("GET", "/records/{recordId:guid}", "readKnowledgeRecord", "Read a Knowledge record");
        Knowledge("PUT", "/records/{recordId:guid}", "updateKnowledgeRecord",
            "Update a Knowledge record", body: true);
        Knowledge("POST", "/records/{recordId:guid}/restore", "restoreKnowledgeRecord",
            "Restore a Knowledge record revision", body: true);
        Knowledge("POST", "/records/{recordId:guid}/approve", "approveKnowledgeDecision",
            "Approve a Knowledge decision", body: true);
        Knowledge("GET", "/records/export", "exportKnowledgeRecords", "Export Knowledge records");
        Knowledge("POST", "/records/import", "importKnowledgeRecords", "Import Knowledge records",
            body: true, maximumRequestBodyBytes: KnowledgeRecordTransferContract.MaximumBytes);
        Knowledge("GET", "/records/{recordId:guid}/revisions", "readKnowledgeRevisions",
            "Read Knowledge record revisions", ["page", "pageSize"]);
        Knowledge("POST", "/proposals/{proposalId:guid}/promote", "promoteKnowledgeProposal",
            "Promote a Knowledge proposal", body: true);
        Knowledge("POST", "/proposals/{proposalId:guid}/reject", "rejectKnowledgeProposal",
            "Reject a Knowledge proposal", body: true);
        Knowledge("GET", "/context", "compileKnowledgeContext", "Compile Knowledge context",
            ["q", "maxItems", "maxTokens"]);

        var run = "/projects/{projectId}/runs/{runId}";
        Add("GET", GatewayOwner.Events, run + "/events",
            "/internal" + run + "/events",
            "replayRunEvents", "Replay committed run events", ["cursor", "limit"],
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, run + "/events/live",
            "/internal" + run + "/events",
            "streamRunEvents", "Stream committed run events using SSE", ["cursor"],
            eventStream: true,
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, run + "/usage",
            "/internal" + run + "/usage",
            "readRunUsage", "Read run usage totals", forwardTenantSelector: true);

        return routes.ToImmutable();
    }
}
