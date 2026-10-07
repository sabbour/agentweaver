using System.Collections.Immutable;

namespace Agentweaver.Gateway;

public enum GatewayOwner
{
    Projects,
    Orchestrator,
    Knowledge,
    Events,
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
    bool IsRunEventStream = false);

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
            bool eventStream = false) =>
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
                eventStream));

        Add("GET", GatewayOwner.Projects, "/projects", "/api/projects/",
            "listProjects", "List projects");
        Add("POST", GatewayOwner.Projects, "/projects", "/api/projects/",
            "createProject", "Create a project", body: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}", "/api/projects/{projectId}",
            "getProject", "Read a project", ["runId"]);
        Add("PATCH", GatewayOwner.Projects, "/projects/{projectId}", "/api/projects/{projectId}",
            "updateProject", "Update a project", body: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/configuration",
            "/api/projects/{projectId}/configuration",
            "getProjectConfiguration", "Read project configuration", ["revision"]);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/configuration",
            "/api/projects/{projectId}/configuration",
            "updateProjectConfiguration", "Update project configuration", body: true);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/runs/{runId}/selection",
            "/api/projects/{projectId}/runs/{runId}/selection",
            "acceptRunSelection", "Accept a run selection", body: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/runs/{runId}/selection",
            "/api/projects/{projectId}/runs/{runId}/selection",
            "getRunSelection", "Read the accepted run selection");
        Add("GET", GatewayOwner.Projects, "/platform/runtime-defaults",
            "/api/platform/runtime-defaults/",
            "getPlatformRuntimeDefaults", "Read platform runtime defaults");
        Add("PUT", GatewayOwner.Projects, "/platform/runtime-defaults",
            "/api/platform/runtime-defaults/",
            "updatePlatformRuntimeDefaults", "Update platform runtime defaults", body: true);

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
                acceptsOnly: acceptsOnly);

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
            "acknowledgeNotification", "Acknowledge a notification", body: true);
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
            bool body = false) =>
            Add(
                method,
                GatewayOwner.Knowledge,
                agent + suffix,
                "/api" + agent + suffix,
                operationId,
                summary,
                query,
                body);

        Knowledge("POST", "/records", "createKnowledgeRecord", "Create a Knowledge record", body: true);
        Knowledge("GET", "/records", "searchKnowledge", "Search Knowledge records",
            ["kind", "q", "includeInactive", "page", "pageSize"]);
        Knowledge("GET", "/records/{recordId:guid}", "readKnowledgeRecord", "Read a Knowledge record");
        Knowledge("PUT", "/records/{recordId:guid}", "updateKnowledgeRecord",
            "Update a Knowledge record", body: true);
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
            "replayRunEvents", "Replay committed run events", ["cursor", "limit"]);
        Add("GET", GatewayOwner.Events, run + "/events/live",
            "/internal" + run + "/events",
            "streamRunEvents", "Stream committed run events using SSE", ["cursor"],
            eventStream: true);
        Add("GET", GatewayOwner.Events, run + "/usage",
            "/internal" + run + "/usage",
            "readRunUsage", "Read run usage totals");

        return routes.ToImmutable();
    }
}
