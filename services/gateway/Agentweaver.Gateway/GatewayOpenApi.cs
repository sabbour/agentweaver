using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Gateway;

/// <summary>Builds and validates the public OpenAPI document from the Gateway route catalog.</summary>
internal static partial class GatewayOpenApi
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record OperationContract(
        string? RequestSchema,
        string? ResponseSchema,
        string[] SuccessStatuses);

    private static readonly IReadOnlyDictionary<string, OperationContract> OperationContracts =
        new Dictionary<string, OperationContract>(StringComparer.Ordinal)
        {
            ["listProjects"] = C(null, "ProjectSummaryList", "200"),
            ["listUserNotifications"] = C(null, "UserNotificationPage", "200"),
            ["dismissUserNotification"] = C(null, null, "204"),
            ["createProject"] = C("CreateProjectRequest", "ProjectSummary", "201"),
            ["getProject"] = C(null, "ProjectSummary", "200"),
            ["updateProject"] = C("UpdateProjectRequest", "ProjectSummary", "200"),
            ["listProjectRoleAssignments"] = C(null, "ProjectRoleAssignmentList", "200"),
            ["assignProjectRole"] = C("AssignProjectRoleRequest", "ProjectRoleAssignment", "201"),
            ["revokeProjectRoleAssignment"] = C(null, "ProjectRoleAssignment", "200"),
            ["getProjectConfiguration"] = C(null, "VersionedProjectConfiguration", "200"),
            ["updateProjectConfiguration"] = C("UpdateProjectConfigurationRequest", "VersionedProjectConfiguration", "200"),
            // Casting contracts mirror Projects, including draft and configuration revision fences.
            ["createProjectCastingProposal"] = C("CreateProjectCastingProposalRequest", "ProjectCastingProposal", "201"),
            ["createScenarioProjectCastingProposal"] = C("CreateScenarioProjectCastingProposalRequest", "ProjectCastingProposal", "201"),
            ["createManualProjectCastingProposal"] = C("CreateManualProjectCastingProposalRequest", "ProjectCastingProposal", "201"),
            ["listProjectCastingProposals"] = C(null, "ProjectCastingProposalList", "200"),
            ["getProjectCastingProposal"] = C(null, "ProjectCastingProposal", "200"),
            ["updateProjectCastingProposal"] = C("UpdateProjectCastingProposalRequest", "ProjectCastingProposal", "200"),
            ["confirmProjectCastingProposal"] = C("TransitionProjectCastingProposalRequest", "ProjectCastingProposal", "200"),
            ["rejectProjectCastingProposal"] = C("TransitionProjectCastingProposalRequest", "ProjectCastingProposal", "200"),
            ["exportProjectCasting"] = C(null, "ProjectCastingTransfer", "200"),
            ["importProjectCasting"] = C("ImportProjectCastingTransferRequest", "ProjectCastingProposal", "201"),
            ["listProjectCastingTemplates"] = C(null, "ProjectCastingTemplateList", "200"),
            ["listProjectCastingRoles"] = C(null, "ProjectCastingRoleList", "200"),
            ["listMarketplaceSources"] = C(null, "MarketplaceSourceList", "200"),
            ["createMarketplaceSource"] = C("CreateMarketplaceSourceRequest", "MarketplaceSource", "201"),
            ["updateMarketplaceSource"] = C("UpdateMarketplaceSourceRequest", "MarketplaceSource", "200"),
            ["removeMarketplaceSource"] = C(null, "MarketplaceSource", "200"),
            ["browseMarketplaceSource"] = C(null, "MarketplaceBrowsePage", "200"),
            ["previewMarketplaceSkill"] = C("MarketplaceSkillPreviewRequest", "SkillContentPreview", "200"),
            ["importMarketplaceSkill"] = C("MarketplaceSkillImportRequest", "SkillContentImportReceipt", "200"),
            ["previewSkillContent"] = C("PreviewSkillContentRequest", "SkillContentPreview", "200"),
        ["packageSkillContent"] = C("PackageSkillContentRequest", "SkillContentPackageResult", "200"),
        ["revokeProjectSkillRevision"] = C("RevokeSkillRevisionRequest", null, "204"),
            ["importProjectSkill"] = C("ImportSkillContentRequest", "SkillContentImportReceipt", "200"),
            ["updateProjectSkillAssignment"] = C("UpdateSkillAssignmentRequest", "VersionedProjectConfiguration", "200"),
            ["listProjectSkillInventory"] = C(null, "ProjectSkillInventoryPage", "200"),
            ["getAuthorizationContext"] = C(null, "ProjectAuthorizationContext", "200"),
            ["acceptRunSelection"] = C("AcceptRunSelectionRequest", "EffectiveRunSelection", "200"),
            ["getRunSelection"] = C(null, "EffectiveRunSelection", "200"),
            ["getPlatformRuntimeDefaults"] = C(null, "VersionedPlatformRuntimeDefaults", "200"),
            ["updatePlatformRuntimeDefaults"] = C("UpdatePlatformRuntimeDefaultsRequest", "VersionedPlatformRuntimeDefaults", "200"),
            ["getPlatformComponents"] = C(null, "PlatformComponentReport", "200"),
            ["pinSourceControlRepository"] = C("SourceControlRepositoryPinRequest", "SourceControlRepositoryPinView", "200", "202"),
            ["createSourceControlIssue"] = C("SourceControlIssueRequest", "SourceControlIssue", "200"),
            ["createSourceControlPullRequest"] = C("SourceControlPullRequestRequest", "SourceControlPullRequest", "200"),
            ["readSourceControlReviews"] = C(null, "SourceControlReviewList", "200"),
            ["prepareSourceControlWorkspace"] = C("PrepareSourceControlWorkspaceRequest", "SourceControlWorkspaceView", "202"),
            ["readSourceControlWorkspaceDiff"] = C("PrepareSourceControlWorkspaceRevisionRequest", "SourceControlWorkspaceDiffView", "200"),
            ["prepareSourceControlMergeIntent"] = C("PrepareSourceControlMergeIntentRequest", "SourceControlMergeApprovalView", "200", "202"),
            ["readSourceControlMergeIntent"] = C(null, "SourceControlMergeIntentView", "200"),
            ["executeSourceControlMergeIntent"] = C(null, "SourceControlMergeExecutionView", "200"),
            ["listChiefOfStaffConversations"] = C(null, "ChiefOfStaffConversationPage", "200"),
            ["bindChiefOfStaffConversation"] = C("BindChiefOfStaffConversationRequest", "ChiefOfStaffConversation", "200"),
            ["getChiefOfStaffConversation"] = C(null, "ChiefOfStaffConversation", "200"),
            ["listChiefOfStaffPriorities"] = C(null, "ChiefOfStaffPriorityList", "200"),
            ["createChiefOfStaffPriority"] = C("CreateChiefOfStaffPriorityRequest", "ChiefOfStaffPriority", "200", "201"),
            ["reorderChiefOfStaffPriorities"] = C("ReorderChiefOfStaffPrioritiesRequest", "ChiefOfStaffPriorityList", "200"),
            ["getChiefOfStaffPriority"] = C(null, "ChiefOfStaffPriority", "200"),
            ["updateChiefOfStaffPriority"] = C("UpdateChiefOfStaffPriorityRequest", "ChiefOfStaffPriority", "200"),
            ["addChiefOfStaffPriorityLink"] = C("AddChiefOfStaffPriorityLinkRequest", "ChiefOfStaffPriority", "200"),
            ["removeChiefOfStaffPriorityLink"] = C(null, "ChiefOfStaffPriority", "200"),
            ["captureSourceControlWorkspaceOutput"] = C("PrepareSourceControlWorkspaceRevisionRequest", "SourceControlOutputCaptureSummary", "200", "201"),
            ["listSourceControlOutputCaptures"] = C(null, "SourceControlOutputCapturePage", "200"),
            ["readSourceControlOutputCapture"] = C(null, "SourceControlOutputCaptureDetail", "200"),
            ["readSourceControlOutputCaptureDiff"] = C(null, "SourceControlOutputCaptureDiff", "200"),
            ["readSourceControlOutputCaptureFile"] = C(null, "SourceControlOutputFileContent", "200"),
            ["listProjectRuns"] = C(null, "ProjectRunPage", "200"),
            // Reporting contracts stay separate across the two data owners.
            ["readProjectMetrics"] = C(null, "ProjectMetrics", "200"),
            ["readProjectUsage"] = C(null, "ProjectUsage", "200"),
            ["acceptRunRoot"] = C("AcceptRootRequest", "AcceptedRoot", "201"),
            ["createProjectRun"] = C("CreateProjectRunRequest", "CreateProjectRunResponse", "201"),
            ["startProjectRun"] = C(null, "StartProjectRunResponse", "200"),
            ["cancelProjectRun"] = C("CancelProjectRunRequest", "CancelProjectRunResponse", "200"),
            ["proposeOutcome"] = C("ProposeCoordinatorOutcomeRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["readRunStatus"] = C(null, "OwnerRunStatus", "200"),
            ["recoverRun"] = C("RecoverRunExecutionRequest", "RunExecutionTransitionResult", "200"),
            ["proposeOutcomeSpec"] = C("ProposeCoordinatorOutcomeRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["selectWorkflow"] = C("SelectCoordinatorWorkflowRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["proposeWorkPlan"] = C("ProposeCoordinatorWorkPlanRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["reviseWorkPlan"] = C("ReviseCoordinatorWorkPlanRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["requestAssembly"] = C("RequestCoordinatorAssemblyRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["readDecisions"] = C(null, "CoordinatorDecisionStateView", "200"),
            ["readSessionTree"] = C(null, "SessionTreeSnapshot", "200"),
            ["readSessionStatus"] = C(null, "SessionStatusSnapshot", "200"),
            ["readRuntimeObservationV1"] = C(null, "RuntimeObservationV1", "200", "409", "503"),
            ["reportTurnFailure"] = C("ReportRunFailureRequest", "RunExecutionTransitionResult", "200"),
            ["answerGate"] = C("AnswerCoordinatorGateRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["askQuestion"] = C("AskCoordinatorQuestionRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["askOutcomeQuestion"] = C("AskNextOutcomeClarifyingQuestionRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["requestApproval"] = C("RequestCoordinatorApprovalRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["acknowledgeGate"] = C("AcknowledgeCoordinatorGateRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["registerChild"] = C("RegisterChildRequest", "RegisteredChild", "201"),
            ["spawnSession"] = C("SpawnSessionRequest", "SpawnedSession", "202"),
            ["forkSession"] = C("CoordinationSessionForkRequest", "CoordinationSessionForkResult", "200", "201", "409"),
            ["detachSession"] = C("SessionTreeCommandRequest", "CoordinationTreeCommandResult", "200"),
            ["archiveChild"] = C("SessionTreeCommandRequest", "CoordinationTreeCommandResult", "200"),
            ["subscribeToIdle"] = C("SubscribeToIdleRequest", "IdleSubscriptionResult", "202"),
            ["steerSession"] = C("SteerSessionRequest", "CoordinationMessageResult", "200", "202"),
            ["approveGate"] = C("ResolveCoordinatorGateRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["rejectGate"] = C("ResolveCoordinatorGateRequest", "CoordinatorDecisionOperationResponse", "200"),
            ["sendMessage"] = C("CoordinationMessageRequest", "CoordinationMessageResult", "200", "202"),
            ["advanceTurnBoundary"] = C("TurnBoundaryRequest", "TurnBoundaryResult", "200"),
            ["completeTurn"] = C("FinishTurnRequest", "TurnBoundaryResult", "200"),
            ["readNotifications"] = C(null, "ParentNotificationList", "200"),
            ["acknowledgeNotification"] = C(null, null, "204"),
            ["acknowledgeMessage"] = C("AddressedMessageClaimFenceRequest", "MessageAdmissionReceipt", "200"),
            ["readPolicyEvaluation"] = C(null, "PolicyEvaluationReceiptView", "200"),
            ["validatePolicyEvaluationAdmission"] = C(null, "PolicyEvaluationReceiptAdmissionAcknowledgment", "200"),
            ["createKnowledgeRecord"] = C("CreateKnowledgeRecordRequest", "KnowledgeRecordWriteResult", "200", "201"),
            ["searchKnowledge"] = C(null, "KnowledgeRecordPage", "200"),
            ["readKnowledgeRecord"] = C(null, "KnowledgeRecord", "200"),
            ["updateKnowledgeRecord"] = C("UpdateKnowledgeRecordRequest", "KnowledgeRecordWriteResult", "200", "201"),
            ["restoreKnowledgeRecord"] = C("RestoreKnowledgeRecordRequest", "KnowledgeRecordWriteResult", "200", "201", "409"),
            ["approveKnowledgeDecision"] = C("ApproveKnowledgeDecisionRequest", "KnowledgeRecordWriteResult", "200", "201", "409"),
            ["exportKnowledgeRecords"] = C(null, "KnowledgeRecordTransferBundle", "200"),
            ["importKnowledgeRecords"] = C("KnowledgeRecordTransferBundle", "KnowledgeRecordImportResult", "200", "201", "400", "409", "413"),
            ["readKnowledgeRevisions"] = C(null, "KnowledgeRecordRevisionPage", "200"),
            ["promoteKnowledgeProposal"] = C("PromoteKnowledgeProposalRequest", "KnowledgeProposalPromotionResult", "200", "201"),
            ["rejectKnowledgeProposal"] = C("RejectKnowledgeProposalRequest", "KnowledgeRecordWriteResult", "200", "201"),
            ["compileKnowledgeContext"] = C(null, "MemoryContextCompilation", "200"),
            ["readSessionTurnContent"] = C(null, "SessionTurnContent", "200"),
            ["replayRunEvents"] = C(null, "SessionEventPage", "200"),
            ["streamRunEvents"] = C(null, "SessionEventPage", "200"),
            ["readRunUsage"] = C(null, "UsageRunTotals", "200"),
        };

    /// <summary>Checks operation/schema coverage and returns the versioned API document.</summary>
    internal static object CreateDocument()
    {
        var schemas = CreateSchemas();
        ValidateSchemaReferences(schemas);
        var routes = GatewayRouteCatalog.Routes;
        if (routes.Select(route => route.OperationId).Distinct(StringComparer.Ordinal).Count() != routes.Length ||
            OperationContracts.Count != routes.Length)
            throw new InvalidOperationException("The OpenAPI operation contracts do not match the Gateway route catalog.");

        foreach (var route in routes)
        {
            var contract = GetContract(route);
            if (route.HasJsonBody != (contract.RequestSchema is not null))
                throw new InvalidOperationException(
                    $"The OpenAPI request schema for '{route.OperationId}' does not match its owner route.");
            if (contract.RequestSchema is { } requestSchema && !schemas.ContainsKey(requestSchema))
                throw new InvalidOperationException(
                    $"The OpenAPI request schema '{requestSchema}' for '{route.OperationId}' is missing.");
            if (contract.ResponseSchema is { } responseSchema && !schemas.ContainsKey(responseSchema))
                throw new InvalidOperationException(
                    $"The OpenAPI response schema '{responseSchema}' for '{route.OperationId}' is missing.");
        }

        var paths = routes
            .GroupBy(route => NormalizePath(route.PublicPath), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (object)group.ToDictionary(
                    route => route.Method.ToLowerInvariant(),
                    CreateOperation,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new
        {
            openapi = "3.1.0",
            info = new
            {
                title = "Agentweaver Gateway API",
                version = "1.0.0",
                description =
                    "Versioned browser, CLI, and first-party MCP entry. Requests and responses are delegated " +
                    "to the named owner API; owner status codes and JSON bodies are preserved. A 202 response " +
                    "means accepted by the owner, not completed.",
            },
            paths,
            components = new
            {
                securitySchemes = new
                {
                    Bearer = new
                    {
                        type = "http",
                        scheme = "bearer",
                        bearerFormat = "JWT",
                        description = "Identity Broker access token for the Gateway audience.",
                    },
                },
                schemas,
            },
            security = new[] { new Dictionary<string, string[]> { ["Bearer"] = [] } },
        };
    }

    /// <summary>Creates path parameters, request bodies, and documented responses for one catalog route.</summary>
    private static object CreateOperation(GatewayRoute route)
    {
        var parameters = PathParameterRegex().Matches(route.PublicPath)
            .Select(match => (object)new
            {
                name = match.Groups["name"].Value,
                @in = "path",
                required = true,
                schema = PathParameterSchema(match.Groups["constraint"].Value),
            })
            .Concat(route.QueryParameters.Select(name => (object)new
            {
                name,
                @in = "query",
                required = IsRequiredQueryParameter(route.OperationId, name),
                schema = QuerySchema(route.OperationId, name),
            }))
            .ToArray();
        if (route.ForwardTenantSelector || route.RequiresTenantSelector)
            parameters = parameters.Append(new
            {
                name = "X-Agentweaver-Tenant",
                @in = "header",
                required = route.RequiresTenantSelector,
                schema = new { type = "string" },
            }).ToArray();

        if (route.OperationId is
            "createProjectRun" or
            "cancelProjectRun" or
            "createChiefOfStaffPriority" or
            "createKnowledgeRecord" or
            "updateKnowledgeRecord" or
            "restoreKnowledgeRecord" or
            "approveKnowledgeDecision" or
            "importKnowledgeRecords" or
            "promoteKnowledgeProposal" or
            "rejectKnowledgeProposal")
        {
            parameters = parameters.Append(new
            {
                name = "Idempotency-Key",
                @in = "header",
                required = true,
                schema = new
                {
                    type = "string",
                    minLength = 1,
                    maxLength = 128,
                    pattern = "^[A-Za-z0-9._:-]+$",
                },
            }).ToArray();
        }

        var contract = GetContract(route);
        var responses = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["default"] = new
            {
                description =
                    "Owner-defined status and response are passed through unchanged, including structured errors.",
            },
            ["401"] = ProblemResponse("The access token is missing, invalid, or expired."),
            ["403"] = ProblemResponse("The Gateway or owning service denied the request."),
            ["502"] = ProblemResponse("The owner is unavailable, redirected, or returned an invalid contract."),
            ["504"] = ProblemResponse("The finite owner request timed out."),
        };
        foreach (var status in contract.SuccessStatuses)
        {
            responses[status] = status == "204"
                ? new { description = "The owning service completed the operation without a response body." }
                : route.IsRunEventStream
                    ? EventStreamResponse()
                    : route.OperationId == "readSourceControlOutputCaptureFile"
                    ? OutputFileResponse()
                    : JsonResponse(
                        Ref(contract.ResponseSchema
                            ?? throw new InvalidOperationException(
                                $"The OpenAPI response schema for '{route.OperationId}' is missing.")),
                        status == "202"
                            ? "Accepted by the owner; this does not mean the requested work is complete."
                            : "Owner response; returned by the owning service.",
                        route.OperationId == "readRuntimeObservationV1" && status == "200"
                            ? new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["Cache-Control"] = new
                                {
                                    description = "Runtime evidence responses are not cacheable.",
                                    schema = new { type = "string", example = "no-store" },
                                },
                            }
                            : null);
        }
        if (route.IsRunEventStream)
        {
            responses["400"] = ProblemResponse("The event cursor is invalid or ambiguous.");
        }
        else if (route.OperationId.Contains("Casting", StringComparison.Ordinal) &&
            route.OperationId is not ("listProjectCastingTemplates" or "listProjectCastingRoles"))
        {
            responses["400"] = ProblemResponse("The owner rejected an invalid casting request.");
            responses["404"] = ProblemResponse("The owner did not find an accessible project or proposal.");
            responses["409"] = ProblemResponse("The configuration revision, draft revision, or proposal state conflicts.");
            // Casting writes require the owner's current WriteProjects authority.
            if (route.HasJsonBody)
                responses["403"] = ProblemResponse("The owner denied current WriteProjects authority.");
            if (route.OperationId == "importProjectCasting")
            {
                responses["413"] = ProblemResponse("The casting import request exceeds 1 MiB.");
                responses["422"] = ProblemResponse("The transfer version or imported team is incompatible.");
            }
        }
        else if (route.OperationId == "listProjectSkillInventory")
        {
            responses["400"] = ProblemResponse(
                "The owner rejected an unknown or repeated query key, invalid cursor, or page size outside 1 through 50.");
            responses["404"] = ProblemResponse(
                "The owner hides foreign projects and memberships found inactive or stale during the inventory recheck. " +
                "Inactive membership rejected during initial caller resolution returns 403.");
        }
        else if (route.OperationId is "readProjectMetrics" or "readProjectUsage")
        {
            responses["400"] = ProblemResponse(
                "The owner rejected an unknown or repeated query key, an invalid ISO date or offset timestamp, " +
                "a reversed interval, a range longer than 92 days, or a bucket other than day.");
        }
        else if (route.OperationId == "listProjectRuns")
        {
            responses["400"] = ProblemResponse(
                "The owner rejected an unknown or repeated query key, unknown state, invalid cursor, " +
                "or page size outside 1 through 100.");
        }
        else if (route.OperationId is "previewMarketplaceSkill" or "importMarketplaceSkill")
        {
            responses["400"] = ProblemResponse(
                "The owner rejected the request or found invalid selected skill content.");
            responses["404"] = ProblemResponse("The owner did not find the project or marketplace source.");
            responses["409"] = ProblemResponse(
                route.OperationId == "importMarketplaceSkill"
                    ? "The source or skill revision, content digest, or import idempotency key conflicts."
                    : "The marketplace source revision changed.");
            responses["422"] = ProblemResponse("The selected marketplace source exceeds the owner's size limits.");
            responses["503"] = ProblemResponse("The marketplace source or skill content store is unavailable.");
            if (route.OperationId == "importMarketplaceSkill")
                responses["500"] = ProblemResponse("The owner reported a skill content store integrity error.");
}
else if (route.OperationId is
    "captureSourceControlWorkspaceOutput" or
            "listSourceControlOutputCaptures" or
            "readSourceControlOutputCapture" or
            "readSourceControlOutputCaptureDiff" or
            "readSourceControlOutputCaptureFile")
        {
            responses["400"] = ProblemResponse("The owner rejected the request, cursor, or file path.");
            responses["404"] = ProblemResponse(
                "The owner did not find the capture or file, or withholds captures while the run is failed or indeterminate.");
            responses["409"] = ProblemResponse("The accepted run selection or owner authority changed during the request.");
            responses["503"] = ProblemResponse("The capture store is unavailable or failed an integrity check.");
        }
        else if (route.OperationId == "listProjectRoleAssignments")
        {
            responses["400"] = ProblemResponse("The request included query parameters.");
            responses["403"] = ProblemResponse(
                "The caller is not a tenant admin or a project Owner, or the token lacks api.read or is resource-bound.");
            responses["404"] = ProblemResponse("The owner did not find the project in the caller's tenant.");
        }
        else if (route.OperationId is "assignProjectRole" or "revokeProjectRoleAssignment")
        {
            responses["400"] = ProblemResponse(
                "The owner rejected the role, principal ID, or expected revision.");
            responses["403"] = ProblemResponse(
                "The caller is not a current tenant admin with api.read and projects.admin.");
            responses["404"] = ProblemResponse(
                "The owner did not find the project, the assignment, or an active tenant member with the principal ID.");
            responses["409"] = ProblemResponse(
                route.OperationId == "assignProjectRole"
                    ? "The project is archived or the principal already has this active role."
                    : "The assignment revision changed, or it is the last Owner of an active project.");
            responses["503"] = ProblemResponse(
                "Project role assignment is not enabled because the runtime database grants are not applied.");
        }

        var description =
            $"Owner: {route.Owner}. The Gateway forwards the validated bearer and does not infer identity " +
            "from request bodies or caller-controlled identity headers. Owner authorization and JSON " +
            "contracts remain authoritative.";
        if (route.OperationId == "listProjectRoleAssignments")
        {
            description +=
                " Returns only active assignments. A current tenant admin or a project Owner can read the list.";
        }
        if (route.OperationId is "assignProjectRole" or "revokeProjectRoleAssignment")
        {
            description +=
                " Only a current tenant admin can change project roles. The owner rechecks the caller's exact " +
                "TenantAdmin assignment in the same serializable transaction as the change and writes an " +
                "authority audit event. Revocation requires the assignment's current revision.";
        }
        if (route.OperationId == "listProjectSkillInventory")
        {
            description +=
                " This metadata-only read requires api.read, projects.admin, active membership in the current " +
                "tenant, and current TenantAdmin or project Owner authority. The owner rechecks authority " +
                "after the read and permits archived projects. The opaque cursor uses stable SkillId-ascending " +
                "order in C collation, then revision-descending order. It does not make multiple pages a " +
                "snapshot. Query rejection and authorization statuses remain owner-defined and pass through unchanged.";
        }
        else if (route.OperationId is "readProjectMetrics" or "readProjectUsage")
        {
            description +=
                " Requires api.read, the owner and Projects audiences, and fresh Projects readProjects or " +
                "readRunSelection project authority, or tenant readProjects. A run-bound caller sees only its run. " +
                "Responses are no-store. The half-open UTC interval [from,to) defaults to the preceding 30 days " +
                "and cannot exceed 92 days. Cross-project overview is out of scope. Usage reads never price or budget.";
        }
        else if (route.OperationId == "listProjectRuns")
        {
            description +=
                " This read requires api.read and fresh Projects readProjects or readRunSelection authority on " +
                "the project, or tenant readProjects authority. A run-bound token sees only its own run. Runs are " +
                "ordered by createdAt descending, then runId descending. The opaque cursor is valid only with " +
                "the same project, state filter, and page size, and multiple pages are not a snapshot.";
        }
        else if (route.OperationId.Contains("Casting", StringComparison.Ordinal))
        {
            description += route.OperationId switch
            {
                "listProjectCastingTemplates" or "listProjectCastingRoles" =>
                    " Catalog reads require an authenticated Broker bearer.",
                "listProjectCastingProposals" or "getProjectCastingProposal" =>
                    " The owner requires current ReadProjects authority.",
                _ => " The owner requires current WriteProjects authority, including api.read and projects.admin. " +
                    "Revision comparisons remain in request bodies; no Idempotency-Key header is required.",
            };
        }
        else if (route.OperationId.Contains("ChiefOfStaff", StringComparison.Ordinal))
        {
            description +=
                " This caller-private route requires api.read and an unbound token. The owner reads fresh Projects " +
                "authority on every call and returns only the caller's rows. Content in a project the caller can no " +
                "longer read is returned with access unavailable and without project fields. Priority writes require " +
                "the current revision and never change run, session, or gate state.";
        }
        else if (route.OperationId is "previewMarketplaceSkill" or "importMarketplaceSkill")
        {
            description +=
                " The owner reads selected source bytes at the supplied resolved commit and path, then rechecks " +
                "the source revision and current actor/project authority. Preview returns validation metadata " +
                "and a content digest, not the source bytes. Import also requires the expected digest and " +
                "idempotency key and returns the owner's immutable revision receipt with source provenance.";
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = route.OperationId,
            ["summary"] = route.Summary,
            ["description"] = description,
            ["security"] = new[] { new Dictionary<string, string[]> { ["Bearer"] = [] } },
            ["parameters"] = parameters,
            ["requestBody"] = route.HasJsonBody
                ? new
                {
                    required = route.JsonBodyRequired,
                    content = new Dictionary<string, object>
                    {
                        ["application/json"] = new
                        {
                            schema = Ref(contract.RequestSchema
                                ?? throw new InvalidOperationException(
                                    $"The OpenAPI request schema for '{route.OperationId}' is missing.")),
                        },
                    },
                }
                : null,
            ["responses"] = responses,
            ["x-agentweaver-owner"] = route.Owner.ToString(),
            ["x-agentweaver-owner-path"] = route.OwnerPath,
            ["x-agentweaver-accepted-only"] = route.AcceptsOnly,
            ["x-agentweaver-stream"] = route.IsRunEventStream ? "server-sent-events" : null,
            ["x-agentweaver-max-request-body-bytes"] = route.MaximumRequestBodyBytes,
        };
    }

    private static object JsonResponse(
        object schema,
        string description,
        IReadOnlyDictionary<string, object>? headers = null)
    {
        var response = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["description"] = description,
            ["content"] = new Dictionary<string, object>
            {
                ["application/json"] = new
                {
                    schema,
                },
            },
        };
        if (headers is not null)
            response["headers"] = headers;
        return response;
    }

    private static object ProblemResponse(string description) =>
        new
        {
            description,
            content = new Dictionary<string, object>
            {
                ["application/problem+json"] = new
                {
                    schema = new Dictionary<string, object> { ["$ref"] = "#/components/schemas/Problem" },
                },
            },
        };

    private static object EventStreamResponse() =>
        new
        {
            description =
                "Ordered committed session events. Each SSE id is the journal cursor and can be supplied as " +
                "Last-Event-ID when reconnecting. Current project read authority is checked before each event.",
            content = new Dictionary<string, object>
            {
                ["text/event-stream"] = new
                {
                    schema = new { type = "string" },
                },
            },
        };

    /// <summary>Describes the raw bytes of one captured file and the digest header that verifies them.</summary>
    private static object OutputFileResponse() =>
        new
        {
            description =
                "The exact bytes of the captured file. The digest header holds the lowercase hexadecimal " +
                "SHA-256 of the body.",
            headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [GatewayRouteCatalog.SourceControlOutputSha256Header] = new
                {
                    description = "Lowercase hexadecimal SHA-256 digest of the response body.",
                    schema = new { type = "string" },
                },
            },
            content = new Dictionary<string, object>
            {
                ["application/octet-stream"] = new
                {
                    schema = Ref("SourceControlOutputFileContent"),
                },
            },
        };

    private static object PathParameterSchema(string constraint) => constraint switch
    {
        "guid" => new { type = "string", format = "uuid" },
        "long" => Int(),
        _ => new { type = "string" },
    };

    private static bool IsRequiredQueryParameter(string operationId, string name) =>
        operationId == "removeMarketplaceSource" && name == "expectedRevision" ||
        operationId == "revokeProjectRoleAssignment" && name == "expectedRevision" ||
        operationId == "removeChiefOfStaffPriorityLink" && name == "expectedRevision" ||
        operationId == "browseMarketplaceSource" && name == "expectedSourceRevision" ||
        operationId == "readSourceControlOutputCaptureFile" && name == "path";

    private static object QuerySchema(string operationId, string name)
    {
        if (operationId == "listSourceControlOutputCaptures")
        {
            return name switch
            {
                "limit" => new { type = "integer", minimum = 1, maximum = 100, @default = 50 },
                "beforeCapturedAt" => new { type = "string", format = "date-time" },
                _ => new { type = "string" },
            };
        }

        // Reporting owners accept dates and explicitly offset timestamps, never local-time strings.
        if (operationId is "readProjectMetrics" or "readProjectUsage")
            return name == "bucket"
                ? new { type = "string", @enum = new[] { "day" }, @default = "day" }
                : new
                {
                    type = "string",
                    pattern = @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2}))?$",
                    description = "ISO date or timestamp with explicit offset; UTC half-open range, at most 92 days."
                };

        if (operationId == "listProjectCastingProposals" && name == "limit")
            return new { type = "integer", minimum = 1, maximum = 100, @default = 50 };

        if (operationId == "listProjectSkillInventory" && name == "pageSize")
            return new { type = "integer", minimum = 1, maximum = 50, @default = 25 };
        if (operationId == "listUserNotifications" && name == "pageSize")
            return new { type = "integer", minimum = 1, maximum = 100, @default = 25 };
        if (operationId == "listUserNotifications" && name == "includeDismissed")
            return new { type = "boolean", @default = false };
        if (operationId is "listProjectRuns" or "listChiefOfStaffConversations" && name == "pageSize")
            return new { type = "integer", minimum = 1, maximum = 100, @default = 25 };
        if (operationId == "listProjectRuns" && name == "state")
            return new
            {
                type = "string",
                description = "Comma-separated run states, or the open and terminal aliases.",
                pattern = "^(idle|active|blocked|completed|failed|indeterminate|cancelled|open|terminal)" +
                    "(,(idle|active|blocked|completed|failed|indeterminate|cancelled|open|terminal))*$",
            };

        if ((operationId is "removeMarketplaceSource" or "revokeProjectRoleAssignment" && name == "expectedRevision") ||
            (operationId == "removeChiefOfStaffPriorityLink" && name == "expectedRevision") ||
            (operationId == "browseMarketplaceSource" && name == "expectedSourceRevision"))
            return Int();

        return name switch
        {
            "limit" or "page" or "pageSize" or "maxItems" or "maxTokens" or "maximumEvents" or
                "maximumDurationSeconds" or "revision" => new { type = "integer" },
            "includeInactive" => new { type = "boolean" },
            "kind" => Enum("memory", "proposal", "decision", "sessionContext"),
            _ => new { type = "string" },
        };
    }

    private static OperationContract GetContract(GatewayRoute route) =>
        OperationContracts.TryGetValue(route.OperationId, out var contract)
            ? contract
            : throw new InvalidOperationException(
                $"The Gateway route '{route.OperationId}' has no OpenAPI contract.");

    private static void ValidateSchemaReferences(IReadOnlyDictionary<string, object> schemas)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schemas, JsonOptions));
        ValidateSchemaReferences(document.RootElement, schemas);
    }

    private static void ValidateSchemaReferences(
        JsonElement element,
        IReadOnlyDictionary<string, object> schemas)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("$ref", out var reference))
            {
                const string prefix = "#/components/schemas/";
                var value = reference.GetString();
                if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
                    !schemas.ContainsKey(value[prefix.Length..]))
                    throw new InvalidOperationException($"The OpenAPI schema reference '{value}' is invalid.");
            }
            foreach (var property in element.EnumerateObject())
                ValidateSchemaReferences(property.Value, schemas);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateSchemaReferences(item, schemas);
        }
    }

    private static OperationContract C(
        string? requestSchema,
        string? responseSchema,
        params string[] successStatuses) =>
        new(requestSchema, responseSchema, successStatuses);

    private static Dictionary<string, object> CreateSchemas() => new(StringComparer.Ordinal)
    {
        ["Problem"] = Obj(["title", "status", "code"],
            ("type", Str()), ("title", Str()), ("status", Int()), ("detail", Str()), ("code", Str())),

        ["ProjectSummary"] = Obj(["projectId", "name", "state", "revision", "configurationRevision", "createdAt", "updatedAt"],
            ("projectId", Str()), ("name", Str()), ("state", Enum("active", "archived")),
            ("revision", Int()), ("configurationRevision", Int()), ("createdAt", Date()), ("updatedAt", Date())),
        ["ProjectSummaryList"] = Arr(Ref("ProjectSummary")),
        ["CreateProjectRequest"] = Obj(["name"], ("name", Str())),
        ["UpdateProjectRequest"] = Obj(["expectedRevision", "name", "state"],
            ("expectedRevision", Int()), ("name", Str()), ("state", Enum("active", "archived"))),
        ["AssignProjectRoleRequest"] = StrictObj(["principalId", "role"],
            ("principalId", Str()), ("role", Enum("owner", "contributor", "viewer", "orchestrator"))),
        ["ProjectRoleAssignmentList"] = Obj(["projectId", "assignments"],
            ("projectId", Str()), ("assignments", Arr(Ref("ProjectRoleAssignmentSummary")))),
        ["ProjectRoleAssignmentSummary"] = Obj(
            ["assignmentId", "principalId", "role", "revision", "createdBy", "createdAt"],
            ("assignmentId", Uuid()), ("principalId", Str()),
            ("role", Enum("owner", "contributor", "viewer", "orchestrator")),
            ("revision", Int()), ("createdBy", Str()), ("createdAt", Date())),
        ["ProjectRoleAssignment"] = Obj(
            ["assignmentId", "projectId", "principalId", "role", "state", "revision", "grantedBy", "grantedAt",
                "revokedBy", "revokedAt"],
            ("assignmentId", Uuid()), ("projectId", Str()), ("principalId", Str()),
            ("role", Enum("owner", "contributor", "viewer", "orchestrator")),
            ("state", Enum("active", "revoked")), ("revision", Int()), ("grantedBy", Str()),
            ("grantedAt", Date()), ("revokedBy", NullableStr()), ("revokedAt", Nullable(Date()))),
        ["ProjectAuthorizationContext"] = Obj(
            ["contractVersion", "issuer", "actorId", "tenantId", "membershipRevision", "boundProjectId", "boundRunId", "effectiveAuthority"],
            ("contractVersion", Int()), ("issuer", Str()), ("actorId", Str()), ("tenantId", Str()),
            ("membershipRevision", Int()), ("boundProjectId", NullableStr()), ("boundRunId", NullableStr()),
            ("effectiveAuthority", Arr(Ref("EffectiveProjectAuthorization")))),
        ["EffectiveProjectAuthorization"] = Obj(
            ["resourceType", "resourceId", "permissions"],
            ("resourceType", Enum("platform", "tenant", "project")), ("resourceId", Str()),
            ("permissions", Arr(Ref("ProjectAuthorizationPermissionGrant")))),
        ["ProjectAuthorizationPermissionGrant"] = Obj(
            ["permission", "roleRevision"],
            ("permission", Enum("readProjects", "writeProjects", "createProjects", "readRunSelection",
                "acceptRunSelection", "accessPrivateKnowledge", "readPlatformRuntimeDefaults",
                "writePlatformRuntimeDefaults")),
            ("roleRevision", Int())),
        ["VersionedProjectConfiguration"] = Obj(["projectId", "revision", "configuration", "updatedByActorId", "createdAt"],
            ("projectId", Str()), ("revision", Int()), ("configuration", Ref("ProjectConfiguration")),
            ("updatedByActorId", Str()), ("createdAt", Date())),
        ["UpdateProjectConfigurationRequest"] = Obj(["expectedRevision", "configuration"],
            ("expectedRevision", Int()), ("configuration", Ref("ProjectConfiguration"))),
        ["CreateProjectCastingProposalRequest"] = StrictObj(["expectedConfigurationRevision", "draft"],
            ("expectedConfigurationRevision", Int()), ("draft", Ref("ProjectConfiguration"))),
        ["CreateScenarioProjectCastingProposalRequest"] = StrictObj(["expectedConfigurationRevision", "templateId"],
            ("expectedConfigurationRevision", Int()), ("templateId", Str())),
        ["CreateManualProjectCastingProposalRequest"] = StrictObj(["expectedConfigurationRevision", "roleIds"],
            ("expectedConfigurationRevision", Int()), ("roleIds", Arr(Str()))),
        ["UpdateProjectCastingProposalRequest"] = StrictObj(["expectedDraftRevision", "draft"],
            ("expectedDraftRevision", Int()), ("draft", Ref("ProjectConfiguration"))),
        ["TransitionProjectCastingProposalRequest"] = StrictObj(["expectedDraftRevision"], ("expectedDraftRevision", Int())),
        ["ImportProjectCastingTransferRequest"] = StrictObj(["expectedConfigurationRevision", "transfer"],
            ("expectedConfigurationRevision", Int()), ("transfer", Ref("ProjectCastingTransfer"))),
        ["ProjectCastingProposal"] = StrictObj(
            ["proposalId", "projectId", "baseConfigurationRevision", "draftRevision", "state", "draft",
                "confirmedConfigurationRevision", "result", "transferProvenance", "createdByActorId", "createdAt",
                "updatedByActorId", "updatedAt"],
            ("proposalId", Uuid()), ("projectId", Str()), ("baseConfigurationRevision", Int()),
            ("draftRevision", Int()), ("state", Enum("pending", "confirmed", "rejected")),
            ("draft", Ref("ProjectConfiguration")), ("confirmedConfigurationRevision", Nullable(Int())),
            ("result", NullableRef("ProjectConfiguration")), ("transferProvenance", NullableRef("ProjectCastingTransferProvenance")),
            ("createdByActorId", Str()), ("createdAt", Date()), ("updatedByActorId", Str()), ("updatedAt", Date())),
        ["ProjectCastingProposalList"] = Arr(Ref("ProjectCastingProposal")),
        ["ProjectCastingTransfer"] = StrictObj(
            ["formatVersion", "sourceProjectId", "sourceConfigurationRevision", "agentCharters", "casting", "contentDigest"],
            ("formatVersion", new { type = "integer", @const = 1 }), ("sourceProjectId", Str()),
            ("sourceConfigurationRevision", Int()), ("agentCharters", Arr(Ref("ProjectAgentCharter"))),
            ("casting", Arr(Ref("ProjectAgentCast"))), ("contentDigest", Str())),
        ["ProjectCastingTransferProvenance"] = StrictObj(
            ["formatVersion", "sourceProjectId", "sourceConfigurationRevision", "contentDigest"],
            ("formatVersion", new { type = "integer", @const = 1 }), ("sourceProjectId", Str()),
            ("sourceConfigurationRevision", Int()), ("contentDigest", Str())),
        ["ProjectCastingTemplateDefinition"] = StrictObj(["id", "title", "description", "roleIds"],
            ("id", Str()), ("title", Str()), ("description", Str()), ("roleIds", Arr(Str()))),
        ["ProjectCastingTemplateList"] = Arr(Ref("ProjectCastingTemplateDefinition")),
        ["ProjectCastingRoleDefinition"] = StrictObj(["id", "title", "summary", "responsibilities", "boundaries"],
            ("id", Str()), ("title", Str()), ("summary", Str()), ("responsibilities", Arr(Str())), ("boundaries", Arr(Str()))),
        ["ProjectCastingRoleList"] = Arr(Ref("ProjectCastingRoleDefinition")),
        ["MarketplaceSource"] = Obj(
            ["sourceId", "name", "repository", "requestedRef", "subpath", "revision", "state"],
            ("sourceId", Uuid()), ("name", Str()), ("repository", Str()), ("requestedRef", Str()),
            ("subpath", NullableStr()), ("revision", Int()), ("state", Enum("active", "removed"))),
        ["MarketplaceSourceList"] = Arr(Ref("MarketplaceSource")),
        ["CreateMarketplaceSourceRequest"] = StrictObj(["repository"],
            ("name", NullableStr()), ("repository", Str()), ("requestedRef", NullableStr()),
            ("subpath", NullableStr())),
        ["UpdateMarketplaceSourceRequest"] = StrictObj(["expectedRevision", "repository"],
            ("expectedRevision", Int()), ("name", NullableStr()), ("repository", Str()),
            ("requestedRef", NullableStr()), ("subpath", NullableStr())),
        ["MarketplaceBrowseCandidate"] = Obj(["location", "name", "description"],
            ("location", Str()), ("name", Str()), ("description", NullableStr())),
        ["MarketplaceBrowsePage"] = Obj(
            ["sourceId", "sourceRevision", "requestedRef", "resolvedCommitSha", "candidates",
                "total", "page", "pageSize", "hasMore"],
            ("sourceId", Uuid()), ("sourceRevision", Int()), ("requestedRef", Str()),
            ("resolvedCommitSha", Str()), ("candidates", Arr(Ref("MarketplaceBrowseCandidate"))),
            ("total", Int()), ("page", Int()), ("pageSize", Int()), ("hasMore", Bool())),
        ["MarketplaceSkillPreviewRequest"] = StrictObj(
            ["expectedSourceRevision", "resolvedCommitSha", "selectedPath"],
            ("expectedSourceRevision", Int()), ("resolvedCommitSha", Str()), ("selectedPath", Str())),
        ["MarketplaceSkillImportRequest"] = StrictObj(
            ["expectedSourceRevision", "resolvedCommitSha", "selectedPath", "idempotencyKey",
                "expectedContentDigest"],
            ("expectedSourceRevision", Int()), ("resolvedCommitSha", Str()), ("selectedPath", Str()),
            ("idempotencyKey", Str()), ("expectedContentDigest", Str()),
            ("skillId", NullableStr()), ("expectedRevision", NullableInt())),
        ["ProjectSkillInventoryAssignment"] = StrictObj(
            ["skillId", "enabled", "order", "revision", "contentDigest", "agentIds"],
            ("skillId", Str()), ("enabled", Bool()), ("order", Int()), ("revision", NullableInt()),
            ("contentDigest", NullableStr()), ("agentIds", Nullable(Arr(Str())))),
        ["ProjectSkillInventoryRevision"] = StrictObj(
            ["skillId", "revision", "name", "description", "contentDigest", "resourceCount",
                "totalBytes", "createdByActorId", "createdAt", "sourceId", "sourceRevision",
                "requestedRef", "resolvedCommitSha", "selectedPath", "isRevoked", "revokedByActorId",
                "revokedAt"],
            ("skillId", Str()), ("revision", Int()), ("name", Str()), ("description", Str()),
            ("contentDigest", Str()), ("resourceCount", Int()), ("totalBytes", Int()),
            ("createdByActorId", Str()), ("createdAt", Date()), ("sourceId", NullableStr()),
            ("sourceRevision", NullableStr()), ("requestedRef", NullableStr()),
            ("resolvedCommitSha", NullableStr()), ("selectedPath", NullableStr()),
            ("isRevoked", Bool()), ("revokedByActorId", NullableStr()), ("revokedAt", NullableDate())),
        ["ProjectSkillInventoryPage"] = StrictObj(
            ["configurationRevision", "assignments", "revisions", "pageSize", "nextCursor"],
            ("configurationRevision", Int()), ("assignments", Arr(Ref("ProjectSkillInventoryAssignment"))),
            ("revisions", Arr(Ref("ProjectSkillInventoryRevision"))), ("pageSize", Int()),
            ("nextCursor", NullableStr())),
        ["SkillContentResourceRequest"] = StrictObj(["relativePath", "content"],
            ("relativePath", Str()), ("content", Base64Bytes())),
        ["SkillContentCandidateRequest"] = StrictObj(["skillMarkdown", "resources"],
            ("skillMarkdown", Base64Bytes()),
            ("resources", BoundedArray(Ref("SkillContentResourceRequest"), 0, 64))),
        ["PreviewSkillContentRequest"] = StrictObj(["candidate"],
            ("candidate", Ref("SkillContentCandidateRequest"))),
        ["SkillContentPreview"] = Obj(
            ["name", "description", "contentDigest", "resourceCount", "totalBytes"],
            ("name", Str()), ("description", Str()), ("contentDigest", Str()),
            ("resourceCount", Int()), ("totalBytes", Int())),
        ["SkillPackageFileRequest"] = StrictObj(["relativePath", "content"],
            ("relativePath", Str()), ("content", Base64Bytes())),
        ["SkillPackageDraftRequest"] = StrictObj(["name", "instructions"],
            ("name", Str()), ("description", NullableStr()), ("instructions", Str())),
        ["PackageSkillContentRequest"] = StrictObj([],
            ("files", Nullable(BoundedArray(Ref("SkillPackageFileRequest"), 1, 1024))),
            ("archive", Nullable(Base64Bytes())),
            ("draft", NullableRef("SkillPackageDraftRequest"))),
        ["SkillContentPackage"] = Obj(
            ["location", "candidate", "preview", "error", "skippedPaths"],
            ("location", Str()), ("candidate", NullableRef("SkillContentCandidateRequest")),
            ("preview", NullableRef("SkillContentPreview")), ("error", NullableStr()),
            ("skippedPaths", Arr(Str()))),
        ["SkillContentPackageResult"] = Obj(["skills"],
            ("skills", Arr(Ref("SkillContentPackage")))),
        ["RevokeSkillRevisionRequest"] = StrictObj(["reason"], ("reason", Str())),
        ["SkillContentSourceSelection"] = StrictObj(
            ["sourceId", "sourceRevision", "requestedRef", "resolvedCommitSha", "selectedPath"],
            ("sourceId", Str()), ("sourceRevision", Str()), ("requestedRef", Str()),
            ("resolvedCommitSha", Str()), ("selectedPath", Str())),
        ["ImportSkillContentRequest"] = StrictObj(
            ["idempotencyKey", "expectedContentDigest", "candidate"],
            ("idempotencyKey", Str()), ("expectedContentDigest", Str()), ("skillId", NullableStr()),
            ("expectedRevision", NullableInt()), ("source", NullableRef("SkillContentSourceSelection")),
            ("candidate", Ref("SkillContentCandidateRequest"))),
        ["SkillContentImportReceipt"] = Obj(
            ["skillId", "revision", "name", "description", "contentDigest", "resourceCount", "totalBytes"],
            ("skillId", Str()), ("revision", Int()), ("name", Str()), ("description", Str()),
            ("contentDigest", Str()), ("resourceCount", Int()), ("totalBytes", Int())),
        ["UpdateSkillAssignmentRequest"] = StrictObj(
            ["expectedProjectConfigurationRevision", "revision", "contentDigest", "enabled", "order", "agentIds"],
            ("expectedProjectConfigurationRevision", Int()), ("revision", Int()), ("contentDigest", Str()),
            ("enabled", Bool()), ("order", Int()), ("agentIds", Arr(Str()))),
        ["ProjectConfiguration"] = Obj([],
            ("modelSelection", NullableRef("ModelSelectionSettings")),
            ("modelTaskOverrides", Arr(Ref("ModelTaskSelectionOverride"))),
            ("modelRoleOverrides", Arr(Ref("ModelRoleSelectionOverride"))),
            ("modelAgentOverrides", Arr(Ref("ModelAgentSelectionOverride"))),
            ("providerOverrides", Arr(Ref("ProjectProviderOverride"))),
            ("orderedProviderOverrides", Arr(Ref("ProjectOrderedProviderOverride"))),
            ("agentCharters", Arr(Ref("ProjectAgentCharter"))),
            ("casting", Arr(Ref("ProjectAgentCast"))),
            ("blueprintWorkflowReferences", Arr(Ref("BlueprintWorkflowReference"))),
            ("defaultWorkflowId", NullableStr()), ("skills", Arr(Ref("SkillCatalogSetting"))),
            ("egressNarrowing", Nullable(Arr(Ref("NetworkEgressRule")))),
            ("runLimits", Ref("CopilotRunLimitOverrides")), ("sourceControl", Nullable(Obj([],
                ("authMode", Enum("secret", "githubApp")), ("appConnectionId", Str()),
                ("repository", Obj(["owner", "name"], ("owner", Str()), ("name", Str()))),
                ("apiSecretReference", Ref("SecretRef")), ("checkoutSecretReference", Ref("SecretRef")),
                ("webhookSecretReference", Ref("SecretRef")))))),
        ["ModelSelectionSettings"] = Obj(["reference"],
            ("reference", Str()), ("credentialReference", NullableRef("SecretRef")),
            ("sourceMode", new { type = new[] { "string", "null" }, @enum = new object?[] { "hostedCopilot", "byok", null } }),
            ("connectionId", new { type = new[] { "string", "null" }, format = "uuid" }),
            ("reasoningEffort", Nullable(Enum("low", "medium", "high", "xhigh"))),
            ("modelBindingPin", NullableRef("RuntimeModelBindingPin"))),
        ["RuntimeModelBindingPin"] = Obj(
            ["contractVersion", "modelSelectionReference", "modelId", "sourceMode",
               "configurationRevision", "configurationHash", "reasoningEffort"],
            ("contractVersion", Int()), ("modelSelectionReference", Str()), ("modelId", Str()),
            ("sourceMode", Enum("hostedCopilot", "byok")), ("configurationRevision", Str()),
            ("configurationHash", Str()), ("reasoningEffort", Enum("low", "medium", "high", "xhigh")),
            ("providerType", Nullable(Enum("azure", "openai", "anthropic")))),
        ["SecretRef"] = Obj(["id", "version"], ("id", Str()), ("version", Str())),
        ["SourceControlRepositoryPinRequest"] = Obj(["selectionCode"], ("selectionCode", Str())),
        ["SourceControlIssueRequest"] = Obj(["title"],
            ("title", Str()), ("body", NullableStr())),
        ["SourceControlIssue"] = Obj(["number", "title", "htmlUrl", "state"],
            ("number", Int()), ("title", Str()), ("htmlUrl", new { type = "string", format = "uri" }),
            ("state", Str())),
        ["SourceControlPullRequestRequest"] = Obj(
            ["title", "headBranch", "expectedHeadSha", "baseBranch", "expectedBaseSha", "draft"],
            ("title", Str()), ("body", NullableStr()), ("headBranch", Str()), ("expectedHeadSha", Str()),
            ("baseBranch", Str()), ("expectedBaseSha", Str()), ("draft", Bool())),
        ["SourceControlPullRequest"] = Obj(
            ["number", "htmlUrl", "state", "headBranch", "headSha", "baseBranch", "baseSha", "merged", "disposition"],
            ("number", Int()), ("htmlUrl", new { type = "string", format = "uri" }), ("state", Str()),
            ("headBranch", Str()), ("headSha", Str()), ("baseBranch", Str()), ("baseSha", Str()),
            ("merged", Bool()), ("disposition", Enum("created", "reused", "observed"))),
        ["SourceControlReview"] = Obj(["id", "reviewer", "state"],
            ("id", Int()), ("reviewer", Str()),
            ("state", Enum("approved", "changesRequested", "commented", "pending", "dismissed", "unknown")),
            ("submittedAt", NullableDate())),
        ["SourceControlReviewList"] = Arr(Ref("SourceControlReview")),
        ["PrepareSourceControlWorkspaceRequest"] = Obj(["workspaceId", "baseSha", "branchName"],
            ("workspaceId", Str()), ("baseSha", Str()), ("branchName", Str())),
        ["PrepareSourceControlWorkspaceRevisionRequest"] = Obj(["baseSha", "branchName"],
            ("baseSha", Str()), ("branchName", Str())),
        ["SourceControlWorkspaceView"] = Obj(["workspaceId", "workspacePath", "baseSha", "branchName"],
            ("workspaceId", Str()), ("workspacePath", Str()), ("baseSha", Str()), ("branchName", Str())),
        ["SourceControlWorkspaceDiffView"] = Obj(["workspaceId", "baseSha", "headSha", "status", "patch"],
            ("workspaceId", Str()), ("baseSha", Str()), ("headSha", Str()), ("status", Str()), ("patch", Str())),
        ["PrepareSourceControlMergeIntentRequest"] = Obj(
            ["idempotencyKey", "expectedStateVersion", "workflowId", "definitionRevision", "workPlanId",
                "workflowStepId", "pullRequestNumber", "method"],
            ("idempotencyKey", Str()), ("expectedStateVersion", Int()), ("workflowId", Str()),
            ("definitionRevision", Str()), ("workPlanId", Str()), ("workflowStepId", Str()),
            ("pullRequestNumber", Int()), ("method", Enum("merge", "squash", "rebase"))),
        ["SourceControlRepositoryPinView"] = Obj(
            ["pinId", "repository", "providerId", "resourceId", "resourceGeneration", "providerRepositoryId",
                "defaultBranch", "isPrivate", "pinnedAt"],
            ("pinId", Str()), ("repository", Str()), ("providerId", Str()), ("resourceId", Str()),
            ("resourceGeneration", Int()), ("providerRepositoryId", Int()), ("defaultBranch", Str()),
            ("isPrivate", Bool()), ("pinnedAt", Date())),
        ["SourceControlMergeApprovalView"] = Obj(
            ["intentId", "approvalRequestId", "state", "decisionId", "stateVersion", "pendingGate"],
            ("intentId", Str()), ("approvalRequestId", Str()), ("state", Str()), ("decisionId", Uuid()),
            ("stateVersion", Int()), ("pendingGate", NullableRef("CoordinatorGateRequest"))),
        ["ExecutableActionGrantReference"] = Obj(["grantId", "revision"],
            ("grantId", Str()), ("revision", Str())),
        ["SourceControlMergeIntentView"] = Obj(
            ["intentId", "approvalRequestId", "state", "repository", "pullRequestNumber", "headBranch",
                "headSha", "baseBranch", "baseSha", "method"],
            ("intentId", Str()), ("approvalRequestId", Str()), ("state", Str()), ("repository", Str()),
            ("pullRequestNumber", Int()), ("headBranch", Str()), ("headSha", Str()), ("baseBranch", Str()),
            ("baseSha", Str()), ("method", Enum("merge", "squash", "rebase")),
            ("approvalDecisionId", NullableUuid()), ("approvalStateVersion", NullableInt()),
            ("mergeSha", NullableStr()), ("lastFailureCode", NullableStr()),
            ("grantReference", NullableRef("ExecutableActionGrantReference"))),
        ["SourceControlMergeExecutionView"] = Obj(["intentId", "state"],
            ("intentId", Str()), ("state", Str()), ("mergeSha", NullableStr()), ("failureCode", NullableStr())),
        ["SourceControlOutputCaptureSummary"] = Obj(
            ["captureId", "eventId", "eventPosition", "capturedAt", "workspaceId", "branchName", "baseSha",
                "outputTreeSha", "manifestSha256", "manifestByteLength", "patchSha256", "patchByteLength",
                "packageByteLength"],
            ("captureId", Str()), ("eventId", Uuid()), ("eventPosition", Int()), ("capturedAt", Date()),
            ("workspaceId", Str()), ("branchName", Str()), ("baseSha", Str()), ("outputTreeSha", Str()),
            ("manifestSha256", Str()), ("manifestByteLength", Int()), ("patchSha256", Str()),
            ("patchByteLength", Int()), ("packageByteLength", Int())),
        ["SourceControlOutputCapturePage"] = Obj(["captures"],
            ("captures", Arr(Ref("SourceControlOutputCaptureSummary"))),
            ("nextBeforeCapturedAt", NullableDate()), ("nextBeforeCaptureId", NullableStr())),
        ["SourceControlOutputFile"] = Obj(["path", "mode", "sha256", "byteLength"],
            ("path", Str()), ("mode", Str()), ("sha256", Str()), ("byteLength", Int())),
        ["SourceControlOutputManifest"] = Obj(
            ["contractVersion", "workspaceId", "runId", "repositoryId", "resourceGeneration",
                "workspaceIncarnationId", "branchName", "baseSha", "outputTreeSha", "files"],
            ("contractVersion", Int()), ("workspaceId", Str()), ("runId", Str()), ("repositoryId", Str()),
            ("resourceGeneration", Int()), ("workspaceIncarnationId", Uuid()), ("branchName", Str()),
            ("baseSha", Str()), ("outputTreeSha", Str()), ("files", Arr(Ref("SourceControlOutputFile")))),
        ["SourceControlOutputCaptureDetail"] = Obj(["capture", "manifest"],
            ("capture", Ref("SourceControlOutputCaptureSummary")),
            ("manifest", Ref("SourceControlOutputManifest"))),
        ["SourceControlOutputCaptureDiff"] = Obj(["capture", "patch"],
            ("capture", Ref("SourceControlOutputCaptureSummary")), ("patch", Str())),
        ["SourceControlOutputFileContent"] = new { type = "string", format = "binary" },
        ["ProjectProviderOverride"] = Obj(["seam", "providerId"],
            ("seam", ProviderSeamSchema()),
            ("providerId", Str())),
        ["ProjectOrderedProviderOverride"] = Obj(["seam", "providerIds"],
            ("seam", ProviderSeamSchema()),
            ("providerIds", Arr(Str()))),
        ["ProjectAgentCharter"] = Obj(["agentId", "name", "role", "charter"],
            ("agentId", Str()), ("name", Str()), ("role", Str()), ("charter", Str())),
        ["ProjectAgentCast"] = Obj(["agentId", "role", "order"],
            ("agentId", Str()), ("role", Str()), ("order", Int())),
        ["ModelSelectionOverride"] = Obj([],
            ("reference", NullableStr()), ("reasoningEffort", Nullable(Enum("low", "medium", "high", "xhigh"))),
            ("modelBindingPin", NullableRef("RuntimeModelBindingPin"))),
        ["ModelTaskSelectionOverride"] = Obj(["taskKind", "selection"],
            ("taskKind", Enum("planning", "review", "design", "security", "creativeWriting", "complex",
                "codeWriting", "straightforward")),
            ("selection", Ref("ModelSelectionOverride"))),
        ["ModelRoleSelectionOverride"] = Obj(["roleId", "selection"],
            ("roleId", Str()), ("selection", Ref("ModelSelectionOverride"))),
        ["ModelAgentSelectionOverride"] = Obj(["agentId", "selection"],
            ("agentId", Str()), ("selection", Ref("ModelSelectionOverride"))),
        ["BlueprintWorkflowReference"] = Obj(["blueprintId", "workflowId"],
            ("blueprintId", Str()), ("workflowId", Str())),
        ["SkillCatalogSetting"] = Obj(["skillId", "enabled", "order"],
            ("skillId", Str()), ("enabled", Bool()), ("order", Int()), ("revision", NullableInt()),
            ("contentDigest", NullableStr()), ("agentIds", Nullable(Arr(Str())))),
        ["CopilotRunLimitOverrides"] = Obj([],
            ("maxModelTurns", NullableInt()), ("maxToolCalls", NullableInt()), ("maxChildren", NullableInt()),
            ("maxConcurrentChildren", NullableInt()), ("maxWallTimeSeconds", NullableInt()),
            ("maxPromptTokens", NullableInt()),
            ("maxRevisionAttempts", Nullable(new { type = "integer", minimum = 0 })),
            ("copilotSoftCreditLimit", Nullable(new { type = "number", minimum = 0 })),
            ("copilotHardCreditLimit", Nullable(new { type = "number", minimum = 0 }))),
        ["VersionedPlatformRuntimeDefaults"] = Obj(["revision", "defaults", "updatedByActorId", "createdAt"],
            ("revision", Int()), ("defaults", NullableRef("PlatformRuntimeDefaults")),
            ("updatedByActorId", NullableStr()), ("createdAt", NullableDate())),
        ["UpdatePlatformRuntimeDefaultsRequest"] = Obj(["expectedRevision", "defaults"],
            ("expectedRevision", Int()), ("defaults", Ref("PlatformRuntimeDefaults"))),
        // Platform component report: identities, versions, and readiness only; no options or endpoints.
        ["PlatformComponentReport"] = Obj(["observedAt", "areas"],
            ("observedAt", Date()), ("areas", Arr(Ref("PlatformComponentAreaReport")))),
        ["PlatformComponentAreaReport"] = Obj(
            ["area", "purpose", "owner", "cardinality", "readiness", "reason", "active", "available"],
            ("area", Enum("sessions", "snapshots", "sandbox", "storage", "memory", "policy", "guardrails",
                "networkPolicy", "cost", "applicationHosting", "canvas", "secrets", "sourceControl",
                "telemetry", "messaging", "objectStore")),
            ("purpose", Str()),
            ("owner", Enum("eventsAndSessions", "environment", "knowledge", "orchestrator", "identityBroker", "none")),
            ("cardinality", Enum("exclusive", "orderedComposite", "platformSingleton", "layered",
                "keyedByMeterSource", "perApplication", "perCanvas")),
            ("readiness", Ref("PlatformComponentReadiness")), ("reason", NullableStr()),
            ("active", Arr(Ref("PlatformComponentActiveImplementation"))),
            ("available", Arr(Ref("PlatformComponentAvailableImplementation")))),
        ["PlatformComponentReadiness"] = Enum("ready", "degraded", "notReady", "disabled", "notAvailable", "unknown"),
        ["PlatformComponentActiveImplementation"] = Obj(
            ["implementationId", "displayName", "version", "hosting", "layer", "meterSource", "order", "readiness", "reason", "observedAt"],
            ("implementationId", Str()), ("displayName", Str()), ("version", Str()), ("hosting", Str()),
            ("layer", NullableStr()), ("meterSource", NullableStr()), ("order", NullableInt()),
            ("readiness", Ref("PlatformComponentReadiness")), ("reason", NullableStr()), ("observedAt", Date())),
        ["PlatformComponentAvailableImplementation"] = Obj(
            ["implementationId", "displayName", "version", "enabled"],
            ("implementationId", Str()), ("displayName", Str()), ("version", Str()), ("enabled", Bool())),
        ["PlatformRuntimeDefaults"] = Obj(["egressBaseline", "runLimits"],
            ("modelSelection", NullableRef("ModelSelectionSettings")),
            ("modelTaskDefaults", Arr(Ref("ModelTaskSelectionOverride"))),
            ("modelRoleDefaults", Arr(Ref("ModelRoleSelectionOverride"))),
            ("modelAgentDefaults", Arr(Ref("ModelAgentSelectionOverride"))),
            ("egressBaseline", Arr(Ref("NetworkEgressRule"))), ("runLimits", Ref("CopilotRunLimits"))),
        ["CopilotRunLimits"] = Obj(["maxModelTurns", "maxToolCalls", "maxChildren", "maxConcurrentChildren", "maxWallTimeSeconds", "maxPromptTokens"],
            ("maxModelTurns", Int()), ("maxToolCalls", Int()), ("maxChildren", Int()),
            ("maxConcurrentChildren", Int()), ("maxWallTimeSeconds", Int()), ("maxPromptTokens", Int()),
            ("maxRevisionAttempts", Nullable(new { type = "integer", minimum = 0 })),
            ("copilotSoftCreditLimit", Nullable(new { type = "number", minimum = 0 })),
            ("copilotHardCreditLimit", Nullable(new { type = "number", minimum = 0 }))),
        ["NetworkEgressRule"] = Obj(["purpose", "destinationKind", "destination", "port", "protocol"],
            ("purpose", Enum("dnsResolver", "controlPlane", "modelEndpoint", "sourceControl", "packageRegistry", "remoteMcp", "publicHttps")),
            ("destinationKind", Enum("fqdn", "cidr", "kubernetesService")), ("destination", Str()),
            ("port", Int()), ("protocol", Enum("tcp", "udp"))),
        ["AcceptRunSelectionRequest"] = Obj(["expectedProjectConfigRevision", "expectedPlatformRuntimeRevision", "context"],
            ("expectedProjectConfigRevision", Int()), ("expectedPlatformRuntimeRevision", Int()),
            ("context", Ref("RunSelectionContext"))),
        ["RunSelectionContext"] = Obj(["revision", "availableModelSelectionReferences", "providerRequirements", "requiredEgress"],
            ("revision", Str()), ("availableModelSelectionReferences", Arr(Str())),
            ("providerRequirements", Arr(Ref("ProviderRequirement"))), ("requiredEgress", Arr(Ref("NetworkEgressRule")))),
        ["ProviderRequirement"] = Obj(["seam", "requiredAdapterVersion", "requiredOptionsSchemaVersion", "requiredCapabilities", "requiredL3L4Capabilities", "requiredL7Capabilities"],
            ("seam", ProviderSeamSchema()),
            ("meterSource", NullableStr()), ("requiredAdapterVersion", Str()), ("requiredOptionsSchemaVersion", Int()),
            ("requiredCapabilities", Arr(Str())), ("requiredL3L4Capabilities", Arr(Str())), ("requiredL7Capabilities", Arr(Str()))),
        ["EffectiveRunSelection"] = Obj(["projectId", "runId", "projectRevision", "projectConfigurationRevision", "platformRuntimeRevision", "contextRevision", "modelSelection", "providers", "egressAllowlist", "runLimits", "projectConfiguration", "egressBaseline", "requiredEgress", "modelSelections", "modelTaskSelections"],
            ("projectId", Str()), ("runId", Str()), ("projectRevision", Int()), ("projectConfigurationRevision", Int()),
            ("platformRuntimeRevision", Int()), ("contextRevision", Str()), ("modelSelection", Ref("ModelSelectionSettings")),
            ("providers", Arr(Ref("EffectiveProviderSelection"))), ("egressAllowlist", Arr(Ref("NetworkEgressRule"))),
            ("runLimits", Ref("CopilotRunLimits")), ("projectConfiguration", Ref("ProjectConfiguration")),
            ("egressBaseline", Arr(Ref("NetworkEgressRule"))),
            ("projectEgressNarrowing", Nullable(Arr(Ref("NetworkEgressRule")))),
            ("requiredEgress", Arr(Ref("NetworkEgressRule"))),
            ("modelSelections", Arr(Ref("EffectiveModelSelection"))),
            ("modelTaskSelections", Arr(Ref("ModelTaskSelectionReference")))),
        ["EffectiveModelSelection"] = Obj(["selection", "connectionScope"],
            ("selection", Ref("ModelSelectionSettings")),
            ("connectionScope", Enum("project", "platform"))),
        ["ModelTaskSelectionReference"] = Obj(["taskKind", "modelSelectionReference", "reasoningEffort"],
            ("taskKind", Enum("planning", "review", "design", "security", "creativeWriting", "complex",
                "codeWriting", "straightforward")),
            ("roleId", NullableStr()), ("agentId", NullableStr()),
            ("modelSelectionReference", Str()),
            ("reasoningEffort", Enum("low", "medium", "high", "xhigh"))),
        ["EffectiveProviderSelection"] = Obj(["cardinality", "seam", "candidates"],
            ("cardinality", Enum("exclusive", "orderedComposite", "platformSingleton", "layered", "keyedByMeterSource", "perApplication")),
            ("seam", ProviderSeamSchema()), ("candidates", Arr(Ref("EffectiveProviderCandidate"))), ("meterSource", NullableStr())),
        ["EffectiveProviderCandidate"] = Obj(["seam", "providerId", "adapterVersion", "optionsSchemaVersion", "optionsRevision", "hosting", "advertisedCapabilities", "requiredCapabilities"],
            ("seam", ProviderSeamSchema()), ("providerId", Str()), ("adapterVersion", Str()),
            ("optionsSchemaVersion", Int()), ("optionsRevision", Str()),
            ("hosting", Enum("inProcess", "sidecar", "remoteService", "kubernetesController", "managedRuntime")),
            ("advertisedCapabilities", Arr(Str())), ("requiredCapabilities", Arr(Str())),
            ("layer", Nullable(Enum("l3L4", "l7")))),

        ["ProjectRunStartedBy"] = StrictObj(["issuer", "subject"], ("issuer", Str()), ("subject", Str())),
        ["ProjectRunSummary"] = StrictObj(
            ["runId", "rootSessionId", "title", "goal", "state", "createdAt", "updatedAt", "completedAt", "startedBy"],
            ("runId", Str()), ("rootSessionId", NullableStr()), ("title", NullableStr()), ("goal", NullableStr()),
            ("state", Enum("idle", "active", "blocked", "completed", "failed", "indeterminate", "cancelled")),
            ("createdAt", Date()), ("updatedAt", Date()), ("completedAt", Nullable(Date())),
            ("startedBy", Ref("ProjectRunStartedBy"))),
        ["ProjectRunPage"] = StrictObj(["projectId", "items", "nextCursor"],
            ("projectId", Str()), ("items", Arr(Ref("ProjectRunSummary"))), ("nextCursor", NullableStr())),
        // Strict reporting schemas preserve nullable measurements and independent cost units.
        ["ProjectMetricsDay"] = StrictObj(["date", "created", "completed"],
            ("date", new { type = "string", format = "date" }), ("created", Int()), ("completed", Int())),
        ["ProjectMetrics"] = StrictObj(
            ["projectId", "from", "to", "activeRuns", "blockedRuns", "runsInRange", "runsTotal",
                "completedRuns", "succeededRuns", "series"],
            ("projectId", Str()), ("from", Date()), ("to", Date()),
            ("activeRuns", Int()), ("blockedRuns", Int()), ("runsInRange", Int()), ("runsTotal", Int()),
            ("completedRuns", Int()), ("succeededRuns", Int()), ("series", Arr(Ref("ProjectMetricsDay")))),
        ["ProjectUsageCost"] = StrictObj(["priceUnit", "disposition", "amount"],
            ("priceUnit", NullableStr()), ("disposition", Enum("estimate", "reconciled", "unpriced")),
            ("amount", NullableNumber())),
        ["ProjectUsageItem"] = StrictObj(
            ["id", "requestCount", "inputTokens", "outputTokens", "cachedTokens", "cacheWriteTokens",
                "reasoningTokens", "avgDurationMs", "runs", "unpricedRequestCount", "cost"],
            ("id", Str()), ("requestCount", NullableInt()), ("inputTokens", NullableNumber()),
            ("outputTokens", NullableNumber()), ("cachedTokens", NullableNumber()),
            ("cacheWriteTokens", NullableNumber()), ("reasoningTokens", NullableNumber()),
            ("avgDurationMs", NullableNumber()), ("runs", Int()), ("unpricedRequestCount", NullableInt()),
            ("cost", Arr(Ref("ProjectUsageCost")))),
        ["ProjectUsage"] = StrictObj(["projectId", "from", "to", "usageByModel", "usageByAgent"],
            ("projectId", Str()), ("from", Date()), ("to", Date()),
            ("usageByModel", Arr(Ref("ProjectUsageItem"))), ("usageByAgent", Arr(Ref("ProjectUsageItem")))),
        ["BindChiefOfStaffConversationRequest"] = StrictObj(["projectId", "runId"],
            ("projectId", Str()), ("runId", Str())),
        // An unavailable conversation keeps only its identifier, access, and creation time.
        ["ChiefOfStaffConversation"] = StrictObj(
            ["conversationId", "access", "projectId", "runId", "rootSessionId", "title", "requestState", "runState", "createdAt"],
            ("conversationId", Str()), ("access", Enum("available", "unavailable")),
            ("projectId", NullableStr()), ("runId", NullableStr()), ("rootSessionId", NullableStr()),
            ("title", NullableStr()),
            ("requestState", Nullable(Enum("awaiting_binding", "started", "cancelled"))),
            ("runState", Nullable(Enum("idle", "active", "blocked", "completed", "failed", "indeterminate", "cancelled"))),
            ("createdAt", Date())),
        ["ChiefOfStaffConversationPage"] = StrictObj(["items", "nextCursor"],
            ("items", Arr(Ref("ChiefOfStaffConversation"))), ("nextCursor", NullableStr())),
        ["ChiefOfStaffChecklistItem"] = StrictObj(["text", "done"],
            ("itemId", NullableStr()), ("text", new { type = "string", minLength = 1, maxLength = 500 }),
            ("done", Bool())),
        ["CreateChiefOfStaffPriorityRequest"] = StrictObj(["outcome"],
            ("outcome", new { type = "string", minLength = 1, maxLength = 200 }),
            ("notes", NullableStr()),
            ("checklist", new { type = "array", maxItems = 50, items = Ref("ChiefOfStaffChecklistItem") })),
        // Omitted fields keep their values. An empty notes value clears the notes.
        ["UpdateChiefOfStaffPriorityRequest"] = StrictObj(["expectedRevision"],
            ("expectedRevision", new { type = "integer", minimum = 1 }),
            ("outcome", new { type = "string", minLength = 1, maxLength = 200 }),
            ("notes", NullableStr()),
            ("state", Enum("active", "achieved", "dropped")),
            ("checklist", new { type = "array", maxItems = 50, items = Ref("ChiefOfStaffChecklistItem") })),
        ["ReorderChiefOfStaffPrioritiesRequest"] = StrictObj(["expectedOrderRevision", "priorityIds"],
            ("expectedOrderRevision", new { type = "integer", minimum = 0 }),
            ("priorityIds", new { type = "array", maxItems = 100, items = Str() })),
        ["AddChiefOfStaffPriorityLinkRequest"] = StrictObj(["expectedRevision", "kind", "projectId"],
            ("expectedRevision", new { type = "integer", minimum = 1 }),
            ("kind", Enum("run", "conversation", "issue", "pull_request", "artifact")),
            ("projectId", Str()), ("runId", NullableStr()), ("conversationId", NullableStr()),
            ("number", Nullable(new { type = "integer", minimum = 1 })), ("artifactId", NullableStr())),
        // Unavailable links carry only linkId, kind, and access. Issue, pull request, and artifact state is unavailable.
        ["ChiefOfStaffPriorityLink"] = StrictObj(["linkId", "kind", "access"],
            ("linkId", Str()), ("kind", Enum("run", "conversation", "issue", "pull_request", "artifact")),
            ("access", Enum("available", "missing", "unavailable")),
            ("projectId", Str()), ("runId", Str()), ("conversationId", Str()), ("number", Int()),
            ("artifactId", Str()), ("state", Str()), ("createdAt", Date())),
        ["ChiefOfStaffPriority"] = StrictObj(
            ["priorityId", "outcome", "notes", "state", "checklist", "position", "revision", "links", "createdAt", "updatedAt"],
            ("priorityId", Str()), ("outcome", Str()), ("notes", NullableStr()),
            ("state", Enum("active", "achieved", "dropped")),
            ("checklist", Arr(Ref("ChiefOfStaffChecklistItem"))), ("position", Int()), ("revision", Int()),
            ("links", Arr(Ref("ChiefOfStaffPriorityLink"))), ("createdAt", Date()), ("updatedAt", Date())),
        ["ChiefOfStaffPriorityList"] = StrictObj(["orderRevision", "items"],
            ("orderRevision", Int()), ("items", Arr(Ref("ChiefOfStaffPriority")))),
        ["AcceptRootRequest"] = Obj(["sessionId"], ("sessionId", Str())),
        // The message limit is UTF-8 bytes at the owner, not a JavaScript character count.
        ["CreateProjectRunRequest"] = Obj(["firstMessage", "expectedProjectConfigRevision"],
            ("firstMessage", new { type = "string", minLength = 1, maxLength = 32768 }),
            ("expectedProjectConfigRevision", new { type = "integer", minimum = 1 }),
            ("title", new { type = "string", minLength = 1, maxLength = 256 }),
            ("workflowId", new { type = "string", minLength = 1, maxLength = 256 })),
        ["ProjectRunBinding"] = Obj(["projectId", "runId"], ("projectId", Str()), ("runId", Str())),
        ["CreateProjectRunResponse"] = Obj(["runId", "rootSessionId", "state", "binding"],
            ("runId", Str()), ("rootSessionId", Str()), ("state", Enum("awaiting_binding")),
            ("binding", Ref("ProjectRunBinding"))),
        ["StartProjectRunResponse"] = Obj(["runId", "rootSessionId", "state"],
            ("runId", Str()), ("rootSessionId", Str()), ("state", Enum("started"))),
        ["CancelProjectRunRequest"] = Obj([], ("reason", new { type = "string", maxLength = 2048 })),
        // Terminal cancellations are successful no-ops, not conflicts or invented cancellation times.
        ["CancelProjectRunResponse"] = Obj(["runId", "state", "cancelledAt", "alreadyTerminal"],
            ("runId", Str()), ("state", Enum("cancelled", "completed", "failed", "indeterminate")),
            ("cancelledAt", NullableStr()), ("alreadyTerminal", Bool())),
        ["ProposeCoordinatorOutcomeRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId", "specification"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str()),
            ("specification", Ref("CoordinatorOutcomeSpecification"))),
        ["CoordinatorOutcomeSpecification"] = Obj(["id", "goal", "desiredOutcome", "scope", "assumptions", "clarifyingQuestions"],
            ("id", Str()), ("goal", Str()), ("desiredOutcome", Str()), ("scope", Str()),
            ("assumptions", Str()), ("clarifyingQuestions", Arr(Str()))),
        ["SelectCoordinatorWorkflowRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str()),
            ("workflowId", NullableStr()), ("proposedDefinition", NullableRef("WorkflowDefinition"))),
        ["WorkflowDefinition"] = Obj(["id", "revision", "catalogVersion", "origin", "maximumWorkItems", "steps"],
            ("id", Str()), ("revision", Str()), ("catalogVersion", Str()), ("origin", Enum("builtIn", "generated")),
            ("maximumWorkItems", Int()), ("steps", Arr(Ref("WorkflowStepDefinition")))),
        ["WorkflowStepDefinition"] = Obj(["id", "purpose", "mode", "order", "cardinality", "dependsOn", "allowedRoles", "allowedPhases", "allowedIsolationChoices", "requiredProviderCapabilities"],
            ("id", Str()), ("purpose", Str()), ("mode", Enum("fixed", "open", "platform")), ("order", Int()),
            ("cardinality", Obj(["minimum", "maximum"], ("minimum", Int()), ("maximum", Int()))),
            ("dependsOn", Arr(Str())), ("allowedRoles", Arr(Str())), ("allowedPhases", Arr(Str())),
            ("allowedIsolationChoices", Arr(Str())), ("requiredProviderCapabilities", Arr(Str())),
            ("modelTaskKind", Nullable(Enum("planning", "review", "design", "security", "creativeWriting", "complex",
                "codeWriting", "straightforward"))),
            ("fixedWork", Nullable(Obj(["title", "task", "roleId", "phase", "isolationChoice", "declaredOutputs"],
                ("title", Str()), ("task", Str()), ("roleId", Str()), ("phase", Str()),
                ("isolationChoice", Str()), ("declaredOutputs", Arr(Str()))))),
            ("platformGate", Nullable(Enum("buildTest", "preview", "responsibleAi", "rubberDuck", "independentReview", "openPullRequest", "merge", "scribe", "publish")))),
        ["ProposeCoordinatorWorkPlanRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId", "plan"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str()), ("plan", Ref("WorkPlan"))),
        ["ReviseCoordinatorWorkPlanRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId", "revisedPlan"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str()), ("revisedPlan", Ref("WorkPlan"))),
        ["WorkPlan"] = Obj(["id", "workflowId", "definitionRevision", "catalogVersion", "items"],
            ("id", Str()), ("workflowId", Str()), ("definitionRevision", Str()), ("catalogVersion", Str()),
            ("items", Arr(Obj(["id", "workflowStepId", "title", "task", "roleId", "agentId", "phase", "modelSelectionReference", "isolationProviderId", "isolationChoice", "dependsOn", "declaredOutputs"],
                ("id", Str()), ("workflowStepId", Str()), ("title", Str()), ("task", Str()), ("roleId", Str()),
                ("agentId", Str()), ("phase", Str()), ("modelSelectionReference", Str()),
                ("isolationProviderId", Str()), ("isolationChoice", Str()), ("dependsOn", Arr(Str())),
                ("declaredOutputs", Arr(Str())))))),
        ["RequestCoordinatorAssemblyRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "request"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("request", Ref("CoordinatorAssemblyRequest"))),
        ["CoordinatorAssemblyRequest"] = Obj(["requestId", "workflowId", "definitionRevision", "workPlanId", "workflowStepId", "gate"],
            ("requestId", Str()), ("workflowId", Str()), ("definitionRevision", Str()), ("workPlanId", Str()),
            ("workflowStepId", Str()),
            ("gate", Enum("buildTest", "preview", "responsibleAi", "rubberDuck", "independentReview", "openPullRequest", "merge", "scribe", "publish"))),
        ["AnswerCoordinatorGateRequest"] = Obj(["expectedStateVersion", "idempotencyKey"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("choiceId", NullableStr()), ("freeformAnswer", NullableStr())),
        ["ResolveCoordinatorGateRequest"] = Obj(["expectedStateVersion", "idempotencyKey"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str())),
        ["AskCoordinatorQuestionRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId", "questionId", "prompt", "allowedChoices", "allowsFreeform"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str()), ("questionId", Str()),
            ("prompt", Str()), ("allowedChoices", Arr(Str())), ("allowsFreeform", Bool())),
        ["AskNextOutcomeClarifyingQuestionRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str())),
        ["RequestCoordinatorApprovalRequest"] = Obj(["expectedStateVersion", "idempotencyKey", "requestId", "subjectId"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str()), ("requestId", Str()),
            ("subjectId", Str()), ("prompt", NullableStr())),
        ["AcknowledgeCoordinatorGateRequest"] = Obj(["expectedStateVersion", "idempotencyKey"],
            ("expectedStateVersion", Int()), ("idempotencyKey", Str())),
        ["RegisterChildRequest"] = Obj(["sessionId"], ("sessionId", Str()), ("workPlanItemId", NullableStr())),
        ["SpawnSessionRequest"] = Obj(["sessionId", "kind", "idempotencyKey", "kickoff"],
            ("sessionId", Str()), ("kind", Enum("coordinator", "childWork", "scribe", "operatorChat", "childRun")),
            ("idempotencyKey", Str()), ("kickoff", Str()), ("userQuote", NullableStr()),
            ("coordinatorInstructions", NullableStr()), ("workPlanItemId", NullableStr())),
        ["SessionTreeCommandRequest"] = Obj(["executionFence", "idempotencyKey"],
            ("executionFence", Int()), ("idempotencyKey", Str())),
        ["CoordinationSessionForkRequest"] = Obj(["executionFence", "idempotencyKey", "targetSessionId", "sourceEventId", "sourceCursor", "kind"],
            ("executionFence", Int()), ("idempotencyKey", Str()), ("targetSessionId", Str()),
            ("sourceEventId", Uuid()), ("sourceCursor", Str()),
            ("kind", Enum("coordinator", "childWork", "scribe", "operatorChat", "childRun"))),
        ["SubscribeToIdleRequest"] = Obj(["subscriberSessionId", "executionFence", "idempotencyKey", "mode"],
            ("subscriberSessionId", Str()), ("executionFence", Int()), ("idempotencyKey", Str()), ("mode", Enum("once", "always"))),
        ["SteerSessionRequest"] = Obj(["recipientSessionId", "idempotencyKey", "deliveryMode", "action", "instruction"],
            ("recipientSessionId", Str()), ("idempotencyKey", Str()), ("deliveryMode", Enum("immediate", "enqueue")),
            ("action", Enum("stop", "redirect", "amend")), ("instruction", AnyJson()),
            ("userQuote", NullableStr()), ("coordinatorInstructions", NullableStr())),
        ["CoordinationMessageRequest"] = Obj(["recipientSessionId", "idempotencyKey", "deliveryMode", "purpose", "kind", "payload"],
            ("recipientSessionId", Str()), ("idempotencyKey", Str()), ("deliveryMode", Enum("immediate", "enqueue")),
            ("purpose", Enum("progress", "handoff", "needsInput", "error", "steering", "question", "approvalRequest", "proposal")),
            ("kind", Enum("text", "steering", "question", "approval", "proposal")), ("payload", AnyJson()),
            ("threadId", NullableUuid()), ("replyToId", NullableUuid()), ("requestId", NullableStr()),
            ("replyCorrelationId", NullableStr()), ("userQuote", NullableStr()), ("coordinatorInstructions", NullableStr())),
        ["TurnBoundaryRequest"] = Obj(["executionFence", "expectedStateVersion"],
            ("executionFence", Int()), ("expectedStateVersion", Int())),
        ["FinishTurnRequest"] = Obj(["executionFence", "expectedStateVersion", "completion"],
            ("executionFence", Int()), ("expectedStateVersion", Int()), ("completion", Enum("idle", "blocked", "completed"))),
        ["ReportRunFailureRequest"] = Obj(["executionFence", "expectedRunStateVersion", "expectedSessionStateVersion", "idempotencyKey", "state", "causeCode", "reference"],
            ("executionFence", Int()), ("expectedRunStateVersion", Int()), ("expectedSessionStateVersion", Int()),
            ("idempotencyKey", Str()), ("state", Enum("failed", "indeterminate")),
            ("causeCode", Str()), ("reference", Str())),
        ["RecoverRunExecutionRequest"] = Obj(["executionFence", "expectedRunStateVersion", "idempotencyKey", "causeCode", "reference"],
            ("executionFence", Int()), ("expectedRunStateVersion", Int()), ("idempotencyKey", Str()),
            ("causeCode", Str()), ("reference", Str())),
        ["AddressedMessageClaimFenceRequest"] = Obj(["claimFence"], ("claimFence", Int())),

        ["AcceptedRoot"] = Obj(["projectId", "runId", "rootSessionId", "executionFence", "stateVersion", "logicalTurnOrdinal", "executionState"],
            ("projectId", Str()), ("runId", Str()), ("rootSessionId", Str()), ("executionFence", Int()),
            ("stateVersion", Int()), ("logicalTurnOrdinal", Int()), ("executionState", Str())),
        ["CoordinatorDecisionOperationResponse"] = Obj(["decisionId", "stateVersion", "accepted", "executionFence", "pendingGate", "issues"],
            ("decisionId", Uuid()), ("stateVersion", Int()), ("accepted", Bool()), ("executionFence", Int()),
            ("pendingGate", NullableRef("CoordinatorGateRequest")), ("issues", Arr(Ref("WorkflowValidationIssue"))),
            ("transitionValue", AnyJson())),
        ["CoordinatorGateRequest"] = Obj(["requestId", "kind", "subjectId", "authorizedActorId", "fence", "allowedChoices", "allowsFreeform"],
            ("requestId", Str()), ("kind", Enum("outcomeConfirmation", "generatedWorkflowConfirmation", "workPlanConfirmation", "scopeChangeConfirmation", "question", "approval")),
            ("subjectId", Str()), ("authorizedActorId", Str()), ("fence", Int()), ("allowedChoices", Arr(Str())),
            ("allowsFreeform", Bool()), ("prompt", NullableStr()), ("resolvesOutcomeClarification", Bool())),
        ["WorkflowValidationIssue"] = Obj(["code", "path", "message"],
            ("code", Str()), ("path", Str()), ("message", Str())),
        ["OwnerRunStatus"] = Obj(["projectId", "runId", "rootSessionId", "executionFence", "logicalTurnOrdinal", "executionState", "stateVersion"],
            ("projectId", Str()), ("runId", Str()), ("rootSessionId", Str()), ("executionFence", Int()),
            ("logicalTurnOrdinal", Int()), ("executionState", Str()), ("stateVersion", Int()),
            ("causeCode", NullableStr()), ("reference", NullableStr())),
        ["RunExecutionTransitionResult"] = Obj(["operationId", "session", "previousState", "state", "previousExecutionFence", "executionFence", "logicalTurnOrdinal", "previousSessionStateVersion", "sessionStateVersion", "previousRunStateVersion", "runStateVersion", "fencedSessions", "isDuplicate"],
            ("operationId", Uuid()), ("session", Ref("SessionIdentity")), ("previousState", Str()), ("state", Str()),
            ("previousExecutionFence", Int()), ("executionFence", Int()), ("logicalTurnOrdinal", Int()),
            ("previousSessionStateVersion", Int()), ("sessionStateVersion", Int()), ("previousRunStateVersion", Int()),
            ("runStateVersion", Int()), ("causeCode", NullableStr()), ("reference", NullableStr()),
            ("previousCauseCode", NullableStr()), ("previousReference", NullableStr()),
            ("fencedSessions", Arr(Ref("SessionExecutionFenceChange"))), ("isDuplicate", Bool())),
        ["SessionExecutionFenceChange"] = Obj(["sessionId", "state", "stateVersion"],
            ("sessionId", Str()), ("state", Str()), ("stateVersion", Int())),
        ["CoordinatorDecisionStateView"] = Obj(["stateVersion", "executionFence", "outcomeConfirmed", "workflowConfirmed", "canDecompose", "canDispatch", "pendingGate"],
            ("stateVersion", Int()), ("executionFence", Int()), ("outcomeConfirmed", Bool()), ("workflowConfirmed", Bool()),
            ("canDecompose", Bool()), ("canDispatch", Bool()), ("pendingGate", NullableRef("CoordinatorGateRequest"))),
        ["RuntimeObservationV1"] = StrictObj(
            ["contractVersion", "registration", "execution", "creditGuard", "dispatches", "toolReservations"],
            ("contractVersion", Int()),
            ("registration", Ref("RuntimeObservationRegistrationSection")),
            ("execution", Ref("RuntimeObservationExecutionSection")),
            ("creditGuard", Ref("RuntimeObservationCreditGuardSection")),
            ("dispatches", Ref("RuntimeObservationDispatchesSection")),
            ("toolReservations", Ref("RuntimeObservationToolReservationsSection"))),
        ["RuntimeObservationRegistrationSection"] = StrictObj(["state", "value"],
            ("state", Enum("Available", "Unknown", "Unavailable")),
            ("value", NullableRef("RuntimeObservationRegistration"))),
        ["RuntimeObservationExecutionSection"] = StrictObj(["state", "value"],
            ("state", Enum("Available", "Unknown", "Unavailable")),
            ("value", NullableRef("RuntimeObservationExecutionV2"))),
        ["RuntimeObservationCreditGuardSection"] = StrictObj(["state", "value"],
            ("state", Enum("Available", "Unknown", "Unavailable")),
            ("value", NullableRef("RuntimeObservationCreditGuard"))),
        ["RuntimeObservationDispatchesSection"] = StrictObj(["state", "value"],
            ("state", Enum("Available", "Unknown", "Unavailable")),
            ("value", NullableRef("RuntimeObservationDispatchCollection"))),
        ["RuntimeObservationToolReservationsSection"] = StrictObj(["state", "value"],
            ("state", Enum("Available", "Unknown", "Unavailable")),
            ("value", NullableRef("RuntimeObservationToolReservationCollection"))),
        ["RuntimeObservationRegistration"] = StrictObj(
            ["runtimeInstanceId", "revision", "state", "expiresAt"],
            ("runtimeInstanceId", Uuid()), ("revision", Int()), ("state", Enum("Active", "Revoked")),
            ("expiresAt", Date())),
        ["RuntimeObservationCreditGuard"] = StrictObj(
            ["copilotAicFloor", "accountedThroughLedgerEventCount", "ledgerDigest",
                "guardRevision", "softWarningEmitted", "hardCapExhausted", "updatedAt"],
            ("copilotAicFloor", Number()), ("accountedThroughLedgerEventCount", Int()),
            ("ledgerDigest", Str()), ("guardRevision", Int()), ("softWarningEmitted", Bool()),
            ("hardCapExhausted", Bool()), ("updatedAt", Date())),
        ["RuntimeObservationDispatchCollection"] = StrictObj(["items", "truncated"],
            ("items", BoundedArray(Ref("RuntimeObservationDispatch"), 0, 100)),
            ("truncated", Bool())),
        ["RuntimeObservationDispatch"] = StrictObj(
            ["dispatchId", "rootSessionId", "childSessionId", "workPlanId", "associationId",
                "checkpointId", "checkpointRevision", "executionFence", "ownerRevision", "state",
                "reportState", "accountingState", "witnessWatermark", "witnessDigest", "createdAt", "updatedAt"],
            ("dispatchId", Uuid()), ("rootSessionId", Str()), ("childSessionId", Str()),
            ("workPlanId", Str()), ("associationId", Str()), ("checkpointId", Str()),
            ("checkpointRevision", Int()), ("executionFence", Int()), ("ownerRevision", Int()),
            ("state", Str()), ("reportState", NullableStr()), ("accountingState", NullableStr()),
            ("witnessWatermark", NullableInt()), ("witnessDigest", NullableStr()),
            ("createdAt", Date()), ("updatedAt", Date())),
        ["RuntimeObservationToolReservationCollection"] = StrictObj(["items", "truncated"],
            ("items", BoundedArray(Ref("RuntimeObservationToolReservation"), 0, 100)),
            ("truncated", Bool())),
        ["RuntimeObservationToolReservation"] = StrictObj(
            ["eventId", "sessionId", "executionFence", "occurredAt"],
            ("eventId", Uuid()), ("sessionId", Str()), ("executionFence", Int()), ("occurredAt", Date())),
        ["RuntimeObservationExecutionV2"] = StrictObj(
            ["contractVersion", "observation", "checkpointParent"],
            ("contractVersion", Int()), ("observation", Ref("RuntimeObservationExecution")),
            ("checkpointParent", Ref("RuntimeExecutionCheckpointParent"))),
        ["RuntimeObservationExecution"] = StrictObj(
            ["contractVersion", "rootSessionId", "sessionId", "executionFence", "workPlanId",
                "associationId", "checkpointRevision", "checkpointId", "checkpointDecisionStateVersion",
                "currentDecisionStateVersion", "status"],
            ("contractVersion", Int()), ("rootSessionId", Str()), ("sessionId", Str()),
            ("executionFence", Int()), ("workPlanId", Str()), ("associationId", Str()),
            ("checkpointRevision", Int()), ("checkpointId", Str()), ("checkpointDecisionStateVersion", Int()),
            ("currentDecisionStateVersion", Int()), ("status", Enum("Pending", "Running", "Succeeded", "Failed", "Indeterminate")),
            ("pendingDispatch", NullableRef("RuntimeObservationPendingDispatch")),
            ("resultDigest", NullableRef("RuntimeExecutionResultDigest"))),
        ["RuntimeObservationPendingDispatch"] = StrictObj(["messageId"],
            ("messageId", Uuid())),
        ["RuntimeExecutionResultDigest"] = StrictObj(["sha256", "utf8ByteLength"],
            ("sha256", Str()), ("utf8ByteLength", Int())),
        ["RuntimeExecutionCheckpointParent"] = StrictObj(["hasParentMetadata", "parentCheckpointId"],
            ("hasParentMetadata", Bool()), ("parentCheckpointId", NullableStr())),
        ["UserNotification"] = StrictObj(
            ["notificationId", "projectId", "runId", "sessionId", "requestId", "kind", "prompt", "createdAt",
                "gateKind", "stateVersion", "updatedAt"],
            ("notificationId", Str()), ("projectId", Str()), ("runId", Str()), ("sessionId", Str()),
            ("requestId", Str()),
            ("kind", Enum("awaitingInput", "awaitingPlanApproval", "awaitingApproval", "awaitingOutcomeConfirmation")),
            ("prompt", NullableStr()), ("createdAt", Date()),
            ("gateKind", Enum("outcomeConfirmation", "generatedWorkflowConfirmation", "workPlanConfirmation",
                "scopeChangeConfirmation", "question", "approval")),
            ("stateVersion", Int()), ("updatedAt", Date())),
        ["UserNotificationPage"] = StrictObj(["items", "count", "nextCursor"],
            ("items", Arr(Ref("UserNotification"))), ("count", Int()), ("nextCursor", NullableStr())),
        ["SessionIdentity"] = Obj(["projectId", "runId", "sessionId"],
            ("projectId", Str()), ("runId", Str()), ("sessionId", Str())),
        ["SessionTreeSnapshot"] = Obj(["rootSessionId", "nodes"],
            ("rootSessionId", Str()), ("nodes", Arr(Ref("SessionTreeNode")))),
        ["SessionTreeNode"] = Obj(["identity", "rootSessionId", "kind", "detached", "lifecycle", "executionFence", "logicalTurnOrdinal", "stateVersion", "createdAt", "archivedAt"],
            ("identity", Ref("SessionIdentity")), ("parentSessionId", NullableStr()), ("rootSessionId", Str()),
            ("kind", Enum("coordinator", "childWork", "scribe", "operatorChat", "childRun")), ("detached", Bool()),
            ("lifecycle", Enum("active", "cancelled", "completed", "archived")), ("executionFence", Int()),
            ("logicalTurnOrdinal", Int()), ("stateVersion", Int()), ("createdAt", Date()), ("archivedAt", NullableDate())),
        ["SessionStatusSnapshot"] = Obj(["identity", "rootSessionId", "kind", "detached", "activity", "lifecycle", "executionFence", "stateVersion", "blockers", "runtimeEffectsState", "runtimeEffectsUnavailableCode", "interruptionIntent", "runExecution"],
            ("identity", Ref("SessionIdentity")), ("parentSessionId", NullableStr()), ("rootSessionId", Str()),
            ("kind", Enum("coordinator", "childWork", "scribe", "operatorChat", "childRun")), ("detached", Bool()),
            ("activity", Enum("busy", "idle", "unknown")), ("activityUnavailableCode", NullableStr()),
            ("lifecycle", Enum("active", "cancelled", "completed", "archived")), ("executionFence", Int()),
            ("stateVersion", Int()), ("blockers", Arr(Ref("SessionStatusBlocker"))),
            ("runtimeEffectsState", Str()), ("runtimeEffectsUnavailableCode", Str()),
            ("interruptionIntent", Ref("SessionInterruptionIntentSnapshot")), ("runExecution", Ref("OwnerRunExecutionSnapshot"))),
        ["SessionStatusBlocker"] = Obj(["kind", "requestId", "choices", "allowsFreeform", "prompt"],
            ("kind", Enum("awaitingInput", "awaitingPlanApproval", "awaitingApproval", "awaitingOutcomeConfirmation")),
            ("requestId", Str()), ("choices", Arr(Str())), ("allowsFreeform", Bool()), ("prompt", NullableStr())),
        ["SessionInterruptionIntentSnapshot"] = Obj(["state"],
            ("state", Enum("none", "requested", "acknowledged")), ("ownerMessageId", NullableUuid()), ("causeCode", NullableStr())),
        ["OwnerRunExecutionSnapshot"] = Obj(["state", "stateVersion"],
            ("state", Str()), ("stateVersion", Int()), ("causeCode", NullableStr()), ("reference", NullableStr())),
        ["RegisteredChild"] = Obj(["identity", "parentSessionId", "pendingRequestId", "executionFence"],
            ("identity", Ref("SessionIdentity")), ("parentSessionId", Str()), ("pendingRequestId", Str()), ("executionFence", Int())),
        ["SpawnedSession"] = Obj(["node", "pendingRequestId", "commandId", "dispatchState"],
            ("node", Ref("SessionTreeNode")), ("pendingRequestId", Str()), ("commandId", Uuid()), ("dispatchState", Str())),
        ["CoordinationSessionForkResult"] = Obj(["commandId", "source", "targetSessionId", "kind", "executionFence", "registrationState", "isDuplicate"],
            ("commandId", Uuid()), ("source", Ref("SessionIdentity")), ("targetSessionId", Str()),
            ("kind", Enum("coordinator", "childWork", "scribe", "operatorChat", "childRun")),
            ("executionFence", Int()), ("registrationState", Enum("registrationPending", "registered", "unregistered")),
            ("node", NullableRef("SessionTreeNode")), ("pendingRequestId", NullableStr()),
            ("lineage", AnyJson()), ("unavailableCode", NullableStr()), ("isDuplicate", Bool())),
        ["CoordinationTreeCommandResult"] = Obj(["commandId", "command", "sourceSessionId", "executionFence", "state"],
            ("commandId", Uuid()), ("command", Str()), ("sourceSessionId", Str()), ("targetSessionId", NullableStr()),
            ("executionFence", Int()), ("state", Str())),
        ["IdleSubscriptionResult"] = Obj(["subscriptionId", "targetSessionId", "subscriberSessionId", "mode", "executionFence", "active", "notificationSourceState"],
            ("subscriptionId", Uuid()), ("targetSessionId", Str()), ("subscriberSessionId", Str()),
            ("mode", Enum("once", "always")), ("executionFence", Int()), ("active", Bool()),
            ("notificationSourceState", Enum("available", "unavailable")), ("notificationSourceUnavailableCode", NullableStr())),
        ["CoordinationMessageResult"] = Obj(["ownerMessageId", "recipientSessionId", "status"],
            ("ownerMessageId", Uuid()), ("recipientSessionId", Str()), ("status", Enum("accepted", "admitted")), ("requestId", NullableStr())),
        ["TurnBoundaryResult"] = Obj(["session", "logicalTurnOrdinal", "stateVersion", "executionState", "pendingWake", "parentNotifications", "runtimeTurnId", "executionFence", "runStateVersion", "isDuplicate"],
            ("session", Ref("SessionIdentity")), ("logicalTurnOrdinal", Int()), ("stateVersion", Int()),
            ("executionState", Str()), ("pendingWake", Bool()), ("presentedMessage", NullableRef("AddressedMessageEnvelope")),
            ("parentNotifications", Arr(Ref("ParentNotification"))), ("runtimeTurnId", Str()),
            ("executionFence", Int()), ("runStateVersion", Int()), ("causeCode", NullableStr()),
            ("reference", NullableStr()), ("operationId", NullableUuid()), ("isDuplicate", Bool())),
        ["ParentNotificationList"] = Arr(Ref("ParentNotification")),
        ["ParentNotification"] = Obj(["notificationId", "parentSessionId", "childSessionId", "messageId", "purpose", "wakesParent", "createdAt"],
            ("notificationId", Uuid()), ("parentSessionId", Str()), ("childSessionId", Str()), ("messageId", Uuid()),
            ("purpose", Enum("progress", "handoff", "needsInput", "error", "steering", "question", "approvalRequest", "proposal")),
            ("wakesParent", Bool()), ("createdAt", Date())),
        ["AddressedMessageEnvelope"] = Obj(["messageId", "sender", "recipient", "threadId", "idempotencyKey", "threadSequence", "senderFence", "recipientFence", "claimFence", "deliveryMode", "purpose", "kind", "payload", "status", "createdAt", "expiresAt", "identity", "provider"],
            ("messageId", Uuid()), ("sender", Ref("SessionIdentity")), ("recipient", Ref("SessionIdentity")),
            ("threadId", Uuid()), ("replyToId", NullableUuid()), ("idempotencyKey", Str()), ("threadSequence", Int()),
            ("senderFence", Int()), ("recipientFence", Int()), ("claimFence", Int()),
            ("deliveryMode", Enum("immediate", "enqueue")),
            ("purpose", Enum("progress", "handoff", "needsInput", "error", "steering", "question", "approvalRequest", "proposal")),
            ("kind", Enum("text", "steering", "question", "approval", "proposal")), ("requestId", NullableStr()),
            ("replyCorrelationId", NullableStr()), ("userQuote", NullableStr()), ("coordinatorInstructions", NullableStr()),
            ("payload", AnyJson()), ("status", Enum("accepted", "claimed", "delivered", "acknowledged", "expired", "undeliverable")),
            ("createdAt", Date()), ("expiresAt", Date()), ("presentedAt", NullableDate()), ("acknowledgedAt", NullableDate()),
            ("failureReason", Nullable(Enum("staleFence", "targetCancelled", "targetCompleted", "recipientUnavailable"))),
            ("identity", Ref("AddressedMessageIdentityMetadata")), ("provider", Ref("AddressedMessageProviderMetadata"))),
        ["AddressedMessageIdentityMetadata"] = Obj(["issuer", "subjectHash", "contractVersion"],
            ("issuer", Str()), ("subjectHash", Str()), ("contractVersion", Int())),
        ["AddressedMessageProviderMetadata"] = Obj(["providerId", "adapterVersion", "optionsSchemaVersion", "optionsRevision", "resourceIdHash", "resourceGeneration", "negotiatedCapabilities"],
            ("providerId", Str()), ("adapterVersion", Str()), ("optionsSchemaVersion", Int()),
            ("optionsRevision", Str()), ("resourceIdHash", Str()), ("resourceGeneration", Int()),
            ("negotiatedCapabilities", Arr(Str()))),
        ["MessageAdmissionReceipt"] = Obj(["ownerMessageId", "messageId", "threadId", "threadSequence", "status", "purpose", "sender", "recipient", "senderFence", "recipientFence"],
            ("ownerMessageId", Uuid()), ("messageId", Uuid()), ("threadId", Uuid()), ("threadSequence", Int()),
            ("status", Enum("accepted", "claimed", "delivered", "acknowledged", "expired", "undeliverable")),
            ("requestId", NullableStr()), ("purpose", Enum("progress", "handoff", "needsInput", "error", "steering", "question", "approvalRequest", "proposal")),
            ("sender", Ref("SessionIdentity")), ("recipient", Ref("SessionIdentity")),
            ("senderFence", Int()), ("recipientFence", Int()), ("claim", NullableJson())),
        ["PolicyEvaluationReceiptView"] = Obj(["receiptId", "issuer", "identity", "evidence", "createdAt"],
            ("receiptId", Uuid()), ("issuer", Str()), ("identity", Ref("SessionIdentity")),
            ("evidence", Ref("PolicyEvaluationSessionPayload")), ("createdAt", Date())),
        ["PolicyEvaluationSessionPayload"] = Obj(["actorId", "tenantId", "stepId", "grantId", "grantRevision", "purpose", "actionId", "outcome", "reasonCode", "fence", "providerId", "adapterVersion", "optionsSchemaVersion", "optionsRevision"],
            ("actorId", Str()), ("tenantId", Str()), ("stepId", Str()), ("grantId", Str()), ("grantRevision", Str()),
            ("purpose", Str()), ("actionId", Str()), ("outcome", Enum("allow", "deny", "error")),
            ("reasonCode", Enum("allowed", "defaultDeny", "noEffectiveGrant", "platformRuleDenied", "projectRuleNarrowed", "staleFence", "providerUnavailable", "evaluationFailed")),
            ("fence", Int()), ("providerId", Str()), ("adapterVersion", Str()), ("optionsSchemaVersion", Int()), ("optionsRevision", Str())),
        ["PolicyEvaluationReceiptAdmissionAcknowledgment"] = Obj(["receiptId", "identity"],
            ("receiptId", Uuid()), ("identity", Ref("SessionIdentity"))),

        ["CreateKnowledgeRecordRequest"] = StrictObj(["kind", "type", "content", "importance", "tags"],
            ("kind", Enum("memory", "proposal", "decision", "sessionContext")), ("type", Str()), ("title", NullableStr()),
            ("content", Str()), ("rationale", NullableStr()), ("importance", Str()), ("tags", Arr(Str()))),
        ["UpdateKnowledgeRecordRequest"] = StrictObj(["expectedRevision", "type", "content", "importance", "tags", "state"],
            ("expectedRevision", Int()), ("type", Str()), ("title", NullableStr()), ("content", Str()),
            ("rationale", NullableStr()), ("importance", Str()), ("tags", Arr(Str())),
            ("state", Enum("pending", "active", "rejected", "archived", "promoted", "superseded")),
            ("reason", NullableStr()), ("supersededByRecordId", NullableUuid())),
        ["RestoreKnowledgeRecordRequest"] = StrictObj(["expectedRevision", "revision"],
            ("expectedRevision", Int()), ("revision", Int()), ("reason", NullableStr())),
        ["ApproveKnowledgeDecisionRequest"] = StrictObj(["expectedRevision"],
            ("expectedRevision", Int()), ("reason", NullableStr())),
        ["PromoteKnowledgeProposalRequest"] = StrictObj(["expectedRevision"], ("expectedRevision", Int())),
        ["RejectKnowledgeProposalRequest"] = StrictObj(["expectedRevision"], ("expectedRevision", Int())),
        ["KnowledgeRecord"] = StrictObj(["recordId", "projectId", "agentId", "kind", "type", "content", "importance", "tags", "state", "trustState", "revision", "revisionId", "createdAt", "updatedAt"],
            ("recordId", Uuid()), ("projectId", Str()), ("agentId", Str()),
            ("kind", Enum("memory", "proposal", "decision", "sessionContext")), ("type", Str()), ("title", NullableStr()),
            ("content", Str()), ("rationale", NullableStr()), ("importance", Str()), ("tags", Arr(Str())),
            ("state", Enum("pending", "active", "rejected", "archived", "promoted", "superseded")),
            ("trustState", Enum("pending", "approved", "rejected", "legacy")), ("revision", Int()), ("revisionId", Uuid()),
            ("previousRevisionId", NullableUuid()), ("sourceRunId", NullableStr()), ("sourceSessionId", NullableStr()),
            ("promotedDecisionId", NullableUuid()), ("createdAt", Date()), ("updatedAt", Date()),
            ("supersededByRecordId", NullableUuid())),
        ["KnowledgeRecordPage"] = StrictObj(["items", "totalCount", "page", "pageSize"],
            ("items", Arr(Ref("KnowledgeRecord"))), ("totalCount", Int()), ("page", Int()), ("pageSize", Int())),
        ["KnowledgeRecordRevision"] = StrictObj(["recordId", "revision", "revisionId", "kind", "type", "content", "importance", "tags", "state", "trustState", "reason", "createdAt"],
            ("recordId", Uuid()), ("revision", Int()), ("revisionId", Uuid()), ("previousRevisionId", NullableUuid()),
            ("kind", Enum("memory", "proposal", "decision", "sessionContext")), ("type", Str()), ("title", NullableStr()),
            ("content", Str()), ("rationale", NullableStr()), ("importance", Str()), ("tags", Arr(Str())),
            ("state", Enum("pending", "active", "rejected", "archived", "promoted", "superseded")),
            ("trustState", Enum("pending", "approved", "rejected", "legacy")), ("reason", NullableStr()),
            ("createdAt", Date()), ("supersededByRecordId", NullableUuid()),
            ("sourceRunId", NullableStr()), ("sourceSessionId", NullableStr()),
            ("actorFingerprint", NullableStr()), ("changeKind", NullableStr())),
        ["KnowledgeRecordRevisionPage"] = StrictObj(["items", "totalCount", "page", "pageSize"],
            ("items", Arr(Ref("KnowledgeRecordRevision"))), ("totalCount", Int()), ("page", Int()), ("pageSize", Int())),
        ["KnowledgeRecordWriteResult"] = StrictObj(["status", "isDuplicate"],
            ("status", Enum("created", "updated", "notFound", "stale", "idempotencyConflict", "invalidState",
                "invalidReplacement", "replacementCycle")),
            ("record", NullableRef("KnowledgeRecord")), ("currentRevision", NullableInt()), ("isDuplicate", Bool())),
        ["KnowledgeProposalPromotionResult"] = StrictObj(["status", "isDuplicate"],
            ("status", Enum("created", "updated", "notFound", "stale", "idempotencyConflict", "invalidState",
                "invalidReplacement", "replacementCycle")),
            ("proposal", NullableRef("KnowledgeRecord")), ("decision", NullableRef("KnowledgeRecord")),
            ("outboxEventId", NullableUuid()), ("isDuplicate", Bool()), ("currentRevision", NullableInt()),
            ("delivery", NullableStr()), ("deliveryCode", NullableStr()), ("requiredAudienceSubject", NullableStr()),
            ("requiredAudience", NullableStr()), ("deliveryAcknowledgment", NullableJson())),
        ["KnowledgeRecordTransferBundle"] = StrictObj(
            ["format", "schemaVersion", "projectId", "agentId", "records"],
            ("format", Const(KnowledgeRecordTransferContract.Format)),
            ("schemaVersion", new { type = "integer", @const = KnowledgeRecordTransferContract.SchemaVersion }),
            ("projectId", Str()), ("agentId", Str()),
            ("records", BoundedArray(Ref("KnowledgeRecordTransferEntry"), 1,
                KnowledgeRecordTransferContract.MaximumRecords))),
        ["KnowledgeRecordTransferEntry"] = StrictObj(["record", "revisions"],
            ("record", Ref("KnowledgeRecord")),
            ("revisions", BoundedArray(Ref("KnowledgeRecordRevision"), 1,
                KnowledgeRecordTransferContract.MaximumRevisions))),
        ["KnowledgeRecordImportResult"] = StrictObj(["records", "isDuplicate"],
            ("records", BoundedArray(Ref("KnowledgeRecord"), 1, KnowledgeRecordTransferContract.MaximumRecords)),
            ("isDuplicate", Bool())),
        ["MemoryContextCompilation"] = Obj(["omittedMemoryCount", "omittedSessionCount", "omissionCauses", "revisionReferences"],
            ("text", NullableStr()), ("omittedMemoryCount", Int()), ("omittedSessionCount", Int()),
            ("omissionCauses", Arr(Str())), ("revisionReferences", Arr(Ref("KnowledgeRevisionReference")))),
        ["KnowledgeRevisionReference"] = Obj(["kind", "recordId", "revision", "revisionId"],
            ("kind", Str()), ("recordId", Uuid()), ("revision", Int()), ("revisionId", Uuid())),

        ["SessionTurnContent"] = Obj(["eventId", "identity", "text"],
            ("eventId", Uuid()), ("identity", Ref("SessionIdentity")), ("text", Str())),
        ["SessionEventPage"] = Obj(["events", "nextCursor", "hasMore"],
            ("events", Arr(Ref("SessionEventEnvelope"))), ("nextCursor", NullableStr()), ("hasMore", Bool())),
        ["SessionEventEnvelope"] = Obj(["schemaVersion", "eventVersion", "eventId", "identity", "position", "occurredAt", "kind", "payload", "objectReferences"],
            ("schemaVersion", Int()), ("eventVersion", Int()), ("eventId", Uuid()), ("identity", Ref("SessionIdentity")),
            ("position", Int()), ("occurredAt", Date()),
            ("kind", Enum("turn", "toolCall", "policyEvaluation", "decisionAccepted", "effectAccepted", "artifactReference", "cacheReference", "addressedMessage", "runtimeConfiguration")),
            ("payload", Ref("SessionEventPayload")), ("objectReferences", Arr(Ref("StoredSessionObjectReference")))),
        ["SessionEventPayload"] = new Dictionary<string, object>
        {
            ["oneOf"] = new[]
            {
                Ref("TurnSessionPayload"), Ref("ToolCallSessionPayload"), Ref("PolicyEvaluationSessionPayload"),
                Ref("AcceptedDecisionSessionPayload"), Ref("AcceptedEffectSessionPayload"), Ref("ArtifactReferenceSessionPayload"),
                Ref("CacheReferenceSessionPayload"), Ref("AddressedMessageSessionPayload"),
                Ref("RuntimeConfigurationSessionPayload"),
            },
        },
        ["TurnSessionPayload"] = Obj(["kind", "role", "content"], ("kind", Const("turn")), ("role", Str()), ("content", Ref("SessionObjectReference"))),
        ["ToolCallSessionPayload"] = Obj(["kind", "callId", "toolName", "state"], ("kind", Const("tool_call")), ("callId", Str()),
            ("toolName", Str()), ("state", Str()), ("arguments", NullableRef("SessionObjectReference")), ("result", NullableRef("SessionObjectReference"))),
        ["AcceptedDecisionSessionPayload"] = Obj(["kind", "decisionId", "decisionType", "selectedOption", "acceptedEffectIds"],
            ("kind", Const("decision_accepted")), ("decisionId", Str()), ("decisionType", Str()), ("selectedOption", Str()),
            ("rationale", NullableRef("SessionObjectReference")), ("acceptedEffectIds", Arr(Str()))),
        ["AcceptedEffectSessionPayload"] = Obj(["kind", "effectId", "effectType", "receipt"],
            ("kind", Const("effect_accepted")), ("effectId", Str()), ("effectType", Str()),
            ("receipt", NullableRef("SessionObjectReference"))),
        ["ArtifactReferenceSessionPayload"] = Obj(["kind", "artifact"], ("kind", Const("artifact_reference")), ("artifact", Ref("SessionObjectReference"))),
        ["CacheReferenceSessionPayload"] = Obj(["kind", "cache", "runtimeVersion", "bindingId"],
            ("kind", Const("cache_reference")), ("cache", Ref("SessionObjectReference")), ("runtimeVersion", Str()), ("bindingId", Str())),
        ["RuntimeConfigurationSessionPayload"] = Obj(["kind", "configuration"],
            ("kind", Const("runtime_configuration")),
            ("configuration", new Dictionary<string, object> { ["type"] = "null" })),
        ["AddressedMessageSessionPayload"] = Obj(["kind", "messageId", "sender", "recipient", "threadId", "threadSequence", "purpose"],
            ("kind", Const("addressed_message")), ("messageId", Uuid()), ("sender", Ref("SessionIdentity")),
            ("recipient", Ref("SessionIdentity")), ("threadId", Uuid()), ("threadSequence", Int()),
            ("purpose", Enum("progress", "handoff", "needsInput", "error", "steering", "question", "approvalRequest", "proposal"))),
        ["SessionObjectReference"] = Obj(["key", "purpose"], ("key", Str()), ("purpose", Str()), ("byteLength", NullableInt())),
        ["StoredSessionObjectReference"] = Obj(["reference", "retention"],
            ("reference", Ref("SessionObjectReference")), ("retention", Obj(["ownerId", "retainUntil"], ("ownerId", Str()), ("retainUntil", Date())))),
        ["UsageRunTotals"] = Obj(["tenantId", "projectId", "runId", "events", "isFullyPriced", "agents", "amounts"],
            ("tenantId", Str()), ("projectId", Str()), ("runId", Str()), ("events", Int()), ("isFullyPriced", Bool()),
            ("agents", Arr(Ref("UsageAgentTotals"))), ("amounts", Arr(Ref("UsageAmountTotal")))),
        ["UsageAgentTotals"] = Obj(["agentId", "events", "requestCount", "inputTokens", "outputTokens", "cachedTokens", "reasoningTokens", "durationMilliseconds", "isFullyPriced", "amounts"],
            ("agentId", Str()), ("events", Int()), ("isFullyPriced", Bool()), ("amounts", Arr(Ref("UsageAmountTotal"))),
            ("requestCount", NullableInt()), ("inputTokens", NullableInt()), ("outputTokens", NullableInt()),
            ("cachedTokens", NullableInt()), ("reasoningTokens", NullableInt()), ("durationMilliseconds", NullableNumber()),
            ("cacheWriteTokens", NullableInt())),
        ["UsageAmountTotal"] = Obj(["meterSource", "unit", "amount", "pricedEvents", "unpricedEvents"],
            ("meterSource", Str()), ("unit", Str()), ("amount", Number()), ("pricedEvents", Int()), ("unpricedEvents", Int())),
    };

    private static object Obj(
        string[] required,
        params (string Name, object Schema)[] properties) =>
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = required,
            ["properties"] = properties.ToDictionary(property => property.Name, property => property.Schema, StringComparer.Ordinal),
        };

    private static object StrictObj(
        string[] required,
        params (string Name, object Schema)[] properties) =>
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = required,
            ["properties"] = properties.ToDictionary(
                property => property.Name, property => property.Schema, StringComparer.Ordinal),
            ["additionalProperties"] = false,
        };

    private static object Ref(string name) =>
        new Dictionary<string, string> { ["$ref"] = "#/components/schemas/" + name };

    private static object Str() => new { type = "string" };
    private static object Int() => new { type = "integer", format = "int64" };
    private static object Number() => new { type = "number" };
    private static object Bool() => new { type = "boolean" };
    private static object Base64Bytes() => new { type = "string", contentEncoding = "base64" };
    private static object Uuid() => new { type = "string", format = "uuid" };
    private static object Date() => new { type = "string", format = "date-time" };
    private static object Enum(params string[] values) => new { type = "string", @enum = values };
    private static object Const(string value) => new { type = "string", @const = value };
    private static object Arr(object items) => new { type = "array", items };
    private static object BoundedArray(object items, int minItems, int maxItems) =>
        new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = items,
            ["minItems"] = minItems,
            ["maxItems"] = maxItems,
        };
    private static object AnyJson() => new Dictionary<string, object>();
    private static object NullableJson() => new Dictionary<string, object>();
    private static object Nullable(object schema) =>
        new Dictionary<string, object>
        {
            ["anyOf"] = new object[] { schema, new Dictionary<string, string> { ["type"] = "null" } },
        };
    private static object NullableRef(string name) => Nullable(Ref(name));
    private static object NullableStr() => Nullable(Str());
    private static object NullableInt() => Nullable(Int());
    private static object NullableNumber() => Nullable(Number());
    private static object NullableDate() => Nullable(Date());
    private static object NullableUuid() => Nullable(Uuid());
    private static object ProviderSeamSchema() =>
        Enum("sessions", "snapshots", "sandbox", "storage", "memory", "policy", "guardrails",
            "networkPolicy", "cost", "applicationHosting", "secrets", "sourceControl", "telemetry",
            "messaging", "objectStore");

    private static string NormalizePath(string path) =>
        PathParameterRegex().Replace(
            path,
            match => "{" + match.Groups["name"].Value + "}");

    [GeneratedRegex(
        @"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?::(?<constraint>[^}]+))?\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathParameterRegex();
}
