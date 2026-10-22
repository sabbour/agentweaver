using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Gateway;

/// <summary>Identifies the service that owns an API route and its authorization semantics.</summary>
public enum GatewayOwner
{
    Projects,
    Orchestrator,
    Knowledge,
    Events,
    IdentityBroker,
    Environment,
}

/// <summary>Describes one versioned public route and how the Gateway proxies its request.</summary>
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
    bool ForwardTenantSelector = false,
    ImmutableArray<string> ExposedResponseHeaders = default,
    long? MaximumResponseBytes = null);

/// <summary>Defines the public v1 API surface and its owner paths, request bounds, and forwarding flags.</summary>
public static class GatewayRouteCatalog
{
    public const string VersionPrefix = "/api/v1";

    /// <summary>Response header that carries the SHA-256 digest of a captured output file.</summary>
    public const string SourceControlOutputSha256Header = "X-Source-Control-Output-Sha256";

    /// <summary>
    /// Response limit for produced-output reads. A capture holds up to a 32 MiB manifest,
    /// an 8 MiB diff, and 16 MiB files, so these reads exceed the default proxy limit.
    /// </summary>
    public const long SourceControlOutputMaximumResponseBytes = 64L * 1024 * 1024;

    public static ImmutableArray<GatewayRoute> Routes { get; } = BuildRoutes();

    /// <summary>Builds the immutable route table used by endpoint mapping, CORS, and OpenAPI generation.</summary>
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
            bool forwardTenantSelector = false,
            string[]? exposedResponseHeaders = null,
            long? maximumResponseBytes = null) =>
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
                ForwardTenantSelector: forwardTenantSelector || requiresTenantSelector,
                ExposedResponseHeaders: (exposedResponseHeaders ?? []).ToImmutableArray(),
                MaximumResponseBytes: maximumResponseBytes));

        Add("GET", GatewayOwner.Projects, "/projects", "/api/projects/",
            "listProjects", "List projects", forwardTenantSelector: true);
        Add("GET", GatewayOwner.Orchestrator, "/projects/{projectId}/issue-sync",
            "/api/projects/{projectId}/issue-sync/", "readIssueSync", "Read periodic issue sync status",
            forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Orchestrator, "/projects/{projectId}/issue-sync",
            "/api/projects/{projectId}/issue-sync/", "configureIssueSync", "Enable, disable and bound periodic issue sync",
            body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.IdentityBroker, "/projects/{projectId}/issue-sync/grants",
            "/api/projects/{projectId}/issue-sync/grants/", "createIssueSyncGrant", "Create project issue-sync authority",
            body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.IdentityBroker, "/projects/{projectId}/issue-sync/grants/{grantId}",
            "/api/projects/{projectId}/issue-sync/grants/{grantId}", "readIssueSyncGrant", "Validate current issue-sync authority",
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.IdentityBroker, "/projects/{projectId}/issue-sync/grants/{grantId}/revoke",
            "/api/projects/{projectId}/issue-sync/grants/{grantId}/revoke", "revokeIssueSyncGrant", "Revoke project issue-sync authority",
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Orchestrator, "/notifications", "/api/notifications",
            "listUserNotifications", "Read caller attention across runs",
            ["cursor", "pageSize", "projectId", "includeDismissed"],
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, "/notifications/{notificationId}/dismiss",
            "/api/notifications/{notificationId}/dismiss",
            "dismissUserNotification", "Dismiss caller attention without resolving its gate",
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects", "/api/projects/",
            "createProject", "Create a project", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}", "/api/projects/{projectId}",
            "getProject", "Read a project", ["runId"], forwardTenantSelector: true);
        Add("PATCH", GatewayOwner.Projects, "/projects/{projectId}", "/api/projects/{projectId}",
            "updateProject", "Update a project", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/role-assignments",
            "/api/projects/{projectId}/role-assignments",
            "listProjectRoleAssignments", "List active project role assignments", forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects/{projectId}/role-assignments",
            "/api/projects/{projectId}/role-assignments",
            "assignProjectRole", "Assign a project role", body: true, forwardTenantSelector: true);
        Add("DELETE", GatewayOwner.Projects, "/projects/{projectId}/role-assignments/{assignmentId}",
            "/api/projects/{projectId}/role-assignments/{assignmentId}",
            "revokeProjectRoleAssignment", "Revoke a project role assignment",
            ["expectedRevision"], forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/configuration",
            "/api/projects/{projectId}/configuration",
            "getProjectConfiguration", "Read project configuration", ["revision"],
            forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/configuration",
            "/api/projects/{projectId}/configuration",
            "updateProjectConfiguration", "Update project configuration", body: true,
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/configuration/choices",
            "/api/projects/{projectId}/configuration/choices",
            "getProjectConfigurationChoices", "Read eligible model and platform component choices",
            forwardTenantSelector: true);
        // Casting remains owner-authorized; revisions travel in bodies, not idempotency headers.
        var casting = "/projects/{projectId}/casting";
        Add("POST", GatewayOwner.Projects, casting + "/proposals", "/api" + casting + "/proposals",
            "createProjectCastingProposal", "Stage a complete casting draft", body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, casting + "/proposals/scenario", "/api" + casting + "/proposals/scenario",
            "createScenarioProjectCastingProposal", "Stage casting from a scenario", body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, casting + "/proposals/manual", "/api" + casting + "/proposals/manual",
            "createManualProjectCastingProposal", "Stage casting from selected roles", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, casting + "/proposals", "/api" + casting + "/proposals",
            "listProjectCastingProposals", "List casting proposals", ["limit"], forwardTenantSelector: true);
        var proposal = casting + "/proposals/{proposalId:guid}";
        Add("GET", GatewayOwner.Projects, proposal, "/api" + proposal,
            "getProjectCastingProposal", "Read a casting proposal", forwardTenantSelector: true);
        Add("PATCH", GatewayOwner.Projects, proposal, "/api" + proposal,
            "updateProjectCastingProposal", "Revise a pending casting proposal", body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, proposal + "/confirm", "/api" + proposal + "/confirm",
            "confirmProjectCastingProposal", "Confirm reviewed casting", body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, proposal + "/reject", "/api" + proposal + "/reject",
            "rejectProjectCastingProposal", "Reject a casting proposal", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, casting + "/export", "/api" + casting + "/export",
            "exportProjectCasting", "Export charter and casting configuration", ["revision"], forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, casting + "/import", "/api" + casting + "/import",
            "importProjectCasting", "Stage a digest-checked casting transfer", body: true,
            maximumRequestBodyBytes: 1024 * 1024, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/casting/templates", "/api/casting/templates",
            "listProjectCastingTemplates", "List built-in casting templates", forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/catalog/roles", "/api/catalog/roles",
            "listProjectCastingRoles", "List built-in casting roles", forwardTenantSelector: true);
        const long skillContentMaximumRequestBytes = 3 * 1024 * 1024;
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/skill-marketplaces/definitions",
            "/api/projects/{projectId}/skill-marketplaces/definitions",
            "listMarketplaceDefinitions", "List suggested skill marketplaces for a project",
            forwardTenantSelector: true);
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
        Add("POST", GatewayOwner.Projects, marketplaceSources + "/{sourceId}/preview",
            "/api" + marketplaceSources + "/{sourceId}/preview",
            "previewMarketplaceSkill", "Preview selected skill content from a marketplace source",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, marketplaceSources + "/{sourceId}/import",
            "/api" + marketplaceSources + "/{sourceId}/import",
            "importMarketplaceSkill", "Import selected skill content from a marketplace source",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/skills/preview", "/api/skills/preview",
            "previewSkillContent", "Validate skill content without importing it",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/skills/package", "/api/skills/package",
            "packageSkillContent", "Turn uploaded files, a ZIP archive, or draft fields into skill candidates",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects/{projectId}/skills/{skillId}/revisions/{revision}/revoke",
            "/api/projects/{projectId}/skills/{skillId}/revisions/{revision}/revoke",
            "revokeProjectSkillRevision", "Revoke one project skill revision",
            body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects/{projectId}/skills/import",
            "/api/projects/{projectId}/skills/import",
            "importProjectSkill", "Import validated skill content to a project",
            body: true, maximumRequestBodyBytes: skillContentMaximumRequestBytes,
            forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Projects, "/projects/{projectId}/skills/{skillId}/assignment",
            "/api/projects/{projectId}/skills/{skillId}/assignment",
            "updateProjectSkillAssignment", "Update a project skill assignment",
            body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/skills",
            "/api/projects/{projectId}/skills",
            "listProjectSkillInventory", "Read current project skill inventory",
            ["cursor", "pageSize"], forwardTenantSelector: true);
        Add("POST", GatewayOwner.Projects, "/projects/{projectId}/agentsuite-imports",
            "/api/projects/{projectId}/agentsuite-imports",
            "importProjectAgentSuite", "Validate an AgentSuite archive and stage a casting proposal", body: true,
            // Fits a base64 archive of the owner's 16 MiB decoded limit plus request fields.
            maximumRequestBodyBytes: 24 * 1024 * 1024, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/agentsuite-imports/{sourceIdentity}",
            "/api/projects/{projectId}/agentsuite-imports/{sourceIdentity}",
            "getProjectAgentSuiteImport", "Read a retained AgentSuite import record",
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Projects, "/projects/{projectId}/agentsuite-agents/{effectiveAgentId}/policy",
            "/api/projects/{projectId}/agentsuite-agents/{effectiveAgentId}/policy",
            "getProjectAgentSuiteAgentPolicy", "Read the policy bound to an imported AgentSuite agent",
            forwardTenantSelector: true);
        // Project-scoped run reads accept an unbound token; a run-bound token is narrowed to its run.
        Add("GET", GatewayOwner.Orchestrator, "/projects/{projectId}/runs",
            "/api/projects/{projectId}/runs", "listProjectRuns", "List project runs",
            ["state", "cursor", "pageSize"], forwardTenantSelector: true);
        // Stored Source Control reads make no provider call. Issues come from backlog content that issue
        // sync stored. Pull requests come from records that runs wrote on create or reuse; a run-bound
        // token is narrowed to its run.
        Add("GET", GatewayOwner.Orchestrator, "/projects/{projectId}/source-control/issues",
            "/api/projects/{projectId}/source-control/issues", "listProjectIssues", "List synced project issues",
            ["state", "cursor", "pageSize"], forwardTenantSelector: true);
        Add("GET", GatewayOwner.Orchestrator, "/projects/{projectId}/source-control/pull-requests",
            "/api/projects/{projectId}/source-control/pull-requests", "listProjectPullRequests",
            "List pull requests recorded by project runs",
            ["state", "cursor", "pageSize"], forwardTenantSelector: true);
        // Each reporting owner aggregates only its own data; the Gateway remains a catalog proxy.
        Add("GET", GatewayOwner.Orchestrator, "/projects/{projectId}/metrics",
            "/api/projects/{projectId}/metrics", "readProjectMetrics", "Read project run metrics",
            ["from", "to", "bucket"], forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, "/projects/{projectId}/usage",
            "/api/projects/{projectId}/usage", "readProjectUsage", "Read project usage",
            ["from", "to"], forwardTenantSelector: true);
        // Chief of Staff routes are caller-private and require an unbound token.
        var chiefOfStaff = "/chief-of-staff";
        Add("GET", GatewayOwner.Orchestrator, chiefOfStaff + "/conversations", "/api" + chiefOfStaff + "/conversations",
            "listChiefOfStaffConversations", "List my Chief of Staff conversations",
            ["cursor", "pageSize"], forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, chiefOfStaff + "/conversations", "/api" + chiefOfStaff + "/conversations",
            "bindChiefOfStaffConversation", "Add a run I started to my Chief of Staff conversations",
            body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Orchestrator, chiefOfStaff + "/conversations/{conversationId}",
            "/api" + chiefOfStaff + "/conversations/{conversationId}",
            "getChiefOfStaffConversation", "Read one of my Chief of Staff conversations",
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities", "/api" + chiefOfStaff + "/priorities",
            "listChiefOfStaffPriorities", "List my Priorities", forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities", "/api" + chiefOfStaff + "/priorities",
            "createChiefOfStaffPriority", "Create a Priority", body: true, forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities/order",
            "/api" + chiefOfStaff + "/priorities/order",
            "reorderChiefOfStaffPriorities", "Reorder my Priorities", body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities/{priorityId}",
            "/api" + chiefOfStaff + "/priorities/{priorityId}",
            "getChiefOfStaffPriority", "Read a Priority", forwardTenantSelector: true);
        Add("PATCH", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities/{priorityId}",
            "/api" + chiefOfStaff + "/priorities/{priorityId}",
            "updateChiefOfStaffPriority", "Update a Priority", body: true, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities/{priorityId}/links",
            "/api" + chiefOfStaff + "/priorities/{priorityId}/links",
            "addChiefOfStaffPriorityLink", "Link a Priority to a run, conversation, issue, pull request, or artifact",
            body: true, forwardTenantSelector: true);
        Add("DELETE", GatewayOwner.Orchestrator, chiefOfStaff + "/priorities/{priorityId}/links/{linkId}",
            "/api" + chiefOfStaff + "/priorities/{priorityId}/links/{linkId}",
            "removeChiefOfStaffPriorityLink", "Remove a Priority link",
            ["expectedRevision"], forwardTenantSelector: true);
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
        Add("GET", GatewayOwner.Projects, "/platform/components", "/api/platform/components",
            "getPlatformComponents", "Read active platform components",
            forwardTenantSelector: true);

        var sourceControl = "/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}";
        void SourceControl(
            string method,
            string suffix,
            string operationId,
            string summary,
            bool body = false,
            bool acceptsOnly = false,
            bool jsonBodyRequired = true,
            string[]? query = null,
            string[]? exposedResponseHeaders = null,
            long? maximumResponseBytes = null) =>
            Add(
                method,
                GatewayOwner.Orchestrator,
                sourceControl + suffix,
                "/api" + sourceControl + suffix,
                operationId,
                summary,
                query,
                body: body,
                acceptsOnly: acceptsOnly,
                requiresTenantSelector: true,
                jsonBodyRequired: jsonBodyRequired,
                exposedResponseHeaders: exposedResponseHeaders,
                maximumResponseBytes: maximumResponseBytes);

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
        // Produced-output reads come from the admitted capture, not from the live workspace.
        SourceControl("POST", "/workspaces/{workspaceId}/output-captures",
            "captureSourceControlWorkspaceOutput", "Capture produced output from a repository workspace",
            body: true);
        SourceControl("GET", "/output-captures",
            "listSourceControlOutputCaptures", "List captured produced output",
            query: ["limit", "beforeCapturedAt", "beforeCaptureId"]);
        SourceControl("GET", "/output-captures/{captureId}",
            "readSourceControlOutputCapture", "Read a produced-output capture and its file list",
            maximumResponseBytes: SourceControlOutputMaximumResponseBytes);
        SourceControl("GET", "/output-captures/{captureId}/diff",
            "readSourceControlOutputCaptureDiff", "Read the diff of a produced-output capture",
            maximumResponseBytes: SourceControlOutputMaximumResponseBytes);
        SourceControl("GET", "/output-captures/{captureId}/files",
            "readSourceControlOutputCaptureFile", "Read one file from a produced-output capture",
            query: ["path"],
            exposedResponseHeaders: [SourceControlOutputSha256Header],
            maximumResponseBytes: SourceControlOutputMaximumResponseBytes);
        SourceControl("POST", "/merge-intents",
            "prepareSourceControlMergeIntent", "Request approval for a pull request merge",
            body: true,
            acceptsOnly: true);
        SourceControl("GET", "/merge-intents/{intentId}",
            "readSourceControlMergeIntent", "Read a pull request merge intent");
        SourceControl("POST", "/merge-intents/{intentId}/execute",
            "executeSourceControlMergeIntent", "Execute an approved pull request merge");

        // Interactive creation precedes the existing run-bound coordination routes.
        Add("POST", GatewayOwner.Orchestrator, "/projects/{projectId}/runs",
            "/api/projects/{projectId}/runs", "createProjectRun", "Create an interactive run",
            body: true, maximumRequestBodyBytes: 256 * 1024, forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, "/projects/{projectId}/runs/{runId}/start",
            "/api/projects/{projectId}/runs/{runId}/start", "startProjectRun", "Deliver the first run message",
            forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, "/projects/{projectId}/runs/{runId}/cancel",
            "/api/projects/{projectId}/runs/{runId}/cancel", "cancelProjectRun", "Cancel a run",
            body: true, forwardTenantSelector: true);

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
        Coordination("GET", "/sessions/{sessionId}/runtime-observation/v1",
            "readRuntimeObservationV1", "Read current runtime evidence");
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
        var projectBacklog = "/projects/{projectId}/backlog";
        Add("GET", GatewayOwner.Orchestrator, projectBacklog, "/api" + projectBacklog + "/",
            "readProjectBacklog", "Read project backlog tasks and dependencies", forwardTenantSelector: true);
        Add("POST", GatewayOwner.Orchestrator, projectBacklog + "/tasks", "/api" + projectBacklog + "/tasks",
            "captureBacklogTask", "Capture project backlog work", body: true, forwardTenantSelector: true);
        Add("PUT", GatewayOwner.Orchestrator, projectBacklog + "/tasks/{taskId}/content",
            "/api" + projectBacklog + "/tasks/{taskId}/content",
            "editBacklogTaskContent", "Replace backlog task content and lifecycle",
            body: true, forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, run + "/events",
            "/internal" + run + "/events",
            "replayRunEvents", "Replay committed run events", ["cursor", "limit"],
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, run + "/events/live",
            "/internal" + run + "/events",
            "streamRunEvents", "Stream committed run events using SSE", ["cursor"],
            eventStream: true,
            forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, run + "/sessions/{sessionId}/turns/{eventId:guid}",
            "/internal/sessions/{sessionId}/turns/{eventId:guid}",
            "readSessionTurnContent", "Read authorized session turn text", forwardTenantSelector: true);
        Add("GET", GatewayOwner.Events, run + "/usage",
            "/internal" + run + "/usage",
            "readRunUsage", "Read run usage totals", forwardTenantSelector: true);

        Add("GET", GatewayOwner.Environment, "/projects/{projectId}/remote-mcp/connections",
            "/api/projects/{projectId}/remote-mcp/directory",
            "listRemoteMcpConnections",
            "List Remote MCP connections with configuration checksums",
            ["pageSize", "after"],
            forwardTenantSelector: true);

        return routes.ToImmutable();
    }
}
