using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Gateway;

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
            ["createProject"] = C("CreateProjectRequest", "ProjectSummary", "201"),
            ["getProject"] = C(null, "ProjectSummary", "200"),
            ["updateProject"] = C("UpdateProjectRequest", "ProjectSummary", "200"),
            ["getProjectConfiguration"] = C(null, "VersionedProjectConfiguration", "200"),
            ["updateProjectConfiguration"] = C("UpdateProjectConfigurationRequest", "VersionedProjectConfiguration", "200"),
            ["acceptRunSelection"] = C("AcceptRunSelectionRequest", "EffectiveRunSelection", "200"),
            ["getRunSelection"] = C(null, "EffectiveRunSelection", "200"),
            ["getPlatformRuntimeDefaults"] = C(null, "VersionedPlatformRuntimeDefaults", "200"),
            ["updatePlatformRuntimeDefaults"] = C("UpdatePlatformRuntimeDefaultsRequest", "VersionedPlatformRuntimeDefaults", "200"),
            ["acceptRunRoot"] = C("AcceptRootRequest", "AcceptedRoot", "201"),
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
            ["restoreKnowledgeRecord"] = C("RestoreKnowledgeRecordRequest", "KnowledgeRecordWriteResult", "200", "201"),
            ["approveKnowledgeDecision"] = C("ApproveKnowledgeDecisionRequest", "KnowledgeRecordWriteResult", "200", "201"),
            ["exportKnowledgeRecords"] = C(null, "KnowledgeRecordTransferBundle", "200"),
            ["importKnowledgeRecords"] = C("KnowledgeRecordTransferBundle", "KnowledgeRecordImportResult", "200", "201"),
            ["readKnowledgeRevisions"] = C(null, "KnowledgeRecordRevisionPage", "200"),
            ["promoteKnowledgeProposal"] = C("PromoteKnowledgeProposalRequest", "KnowledgeProposalPromotionResult", "200", "201"),
            ["rejectKnowledgeProposal"] = C("RejectKnowledgeProposalRequest", "KnowledgeRecordWriteResult", "200", "201"),
            ["compileKnowledgeContext"] = C(null, "MemoryContextCompilation", "200"),
            ["replayRunEvents"] = C(null, "SessionEventPage", "200"),
            ["streamRunEvents"] = C(null, "SessionEventPage", "200"),
            ["readRunUsage"] = C(null, "UsageRunTotals", "200"),
        };

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
                required = false,
                schema = QuerySchema(name),
            }))
            .Append(new
            {
                name = "X-Agentweaver-Tenant",
                @in = "header",
                required = false,
                schema = new { type = "string" },
            })
            .ToArray();

        if (route.OperationId is
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
                    : JsonResponse(
                        Ref(contract.ResponseSchema
                            ?? throw new InvalidOperationException(
                                $"The OpenAPI response schema for '{route.OperationId}' is missing.")),
                        status == "202"
                            ? "Accepted by the owner; this does not mean the requested work is complete."
                            : "Owner response; returned by the owning service.");
        }
        if (route.IsRunEventStream)
        {
            responses["400"] = ProblemResponse("The event cursor is invalid or ambiguous.");
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = route.OperationId,
            ["summary"] = route.Summary,
            ["description"] =
                $"Owner: {route.Owner}. The Gateway forwards the validated bearer and does not infer identity " +
                "from request bodies or caller-controlled identity headers. Owner authorization and JSON " +
                "contracts remain authoritative.",
            ["security"] = new[] { new Dictionary<string, string[]> { ["Bearer"] = [] } },
            ["parameters"] = parameters,
            ["requestBody"] = route.HasJsonBody
                ? new
                {
                    required = true,
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

    private static object JsonResponse(object schema, string description) =>
        new
        {
            description,
            content = new Dictionary<string, object>
            {
                ["application/json"] = new
                {
                    schema,
                },
            },
        };

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

    private static object PathParameterSchema(string constraint) => constraint switch
    {
        "guid" => new { type = "string", format = "uuid" },
        _ => new { type = "string" },
    };

    private static object QuerySchema(string name) => name switch
    {
        "limit" or "page" or "pageSize" or "maxItems" or "maxTokens" or "maximumEvents" or
            "maximumDurationSeconds" or "revision" => new { type = "integer" },
        "includeInactive" => new { type = "boolean" },
        "kind" => Enum("memory", "proposal", "decision", "sessionContext"),
        _ => new { type = "string" },
    };

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
        ["VersionedProjectConfiguration"] = Obj(["projectId", "revision", "configuration", "updatedByActorId", "createdAt"],
            ("projectId", Str()), ("revision", Int()), ("configuration", Ref("ProjectConfiguration")),
            ("updatedByActorId", Str()), ("createdAt", Date())),
        ["UpdateProjectConfigurationRequest"] = Obj(["expectedRevision", "configuration"],
            ("expectedRevision", Int()), ("configuration", Ref("ProjectConfiguration"))),
        ["ProjectConfiguration"] = Obj([],
            ("modelSelection", NullableRef("ModelSelectionSettings")),
            ("providerOverrides", Arr(Ref("ProjectProviderOverride"))),
            ("orderedProviderOverrides", Arr(Ref("ProjectOrderedProviderOverride"))),
            ("agentCharters", Arr(Ref("ProjectAgentCharter"))),
            ("casting", Arr(Ref("ProjectAgentCast"))),
            ("blueprintWorkflowReferences", Arr(Ref("BlueprintWorkflowReference"))),
            ("defaultWorkflowId", NullableStr()), ("skills", Arr(Ref("SkillCatalogSetting"))),
            ("egressNarrowing", Nullable(Arr(Ref("NetworkEgressRule")))),
            ("runLimits", Ref("CopilotRunLimitOverrides")), ("sourceControl", Nullable(Obj([],
                ("repository", Obj(["owner", "name"], ("owner", Str()), ("name", Str()))),
                ("apiSecretReference", Ref("SecretRef")), ("checkoutSecretReference", Ref("SecretRef")),
                ("webhookSecretReference", Ref("SecretRef")))))),
        ["ModelSelectionSettings"] = Obj(["reference"],
            ("reference", Str()), ("credentialReference", NullableRef("SecretRef")),
            ("sourceMode", new { type = new[] { "string", "null" }, @enum = new object?[] { "hostedCopilot", "byok", null } }),
            ("connectionId", new { type = new[] { "string", "null" }, format = "uuid" })),
        ["SecretRef"] = Obj(["id", "version"], ("id", Str()), ("version", Str())),
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
        ["BlueprintWorkflowReference"] = Obj(["blueprintId", "workflowId"],
            ("blueprintId", Str()), ("workflowId", Str())),
        ["SkillCatalogSetting"] = Obj(["skillId", "enabled", "order"],
            ("skillId", Str()), ("enabled", Bool()), ("order", Int())),
        ["CopilotRunLimitOverrides"] = Obj([],
            ("maxModelTurns", NullableInt()), ("maxToolCalls", NullableInt()), ("maxChildren", NullableInt()),
            ("maxConcurrentChildren", NullableInt()), ("maxWallTimeSeconds", NullableInt()),
            ("maxPromptTokens", NullableInt())),
        ["VersionedPlatformRuntimeDefaults"] = Obj(["revision", "defaults", "updatedByActorId", "createdAt"],
            ("revision", Int()), ("defaults", NullableRef("PlatformRuntimeDefaults")),
            ("updatedByActorId", NullableStr()), ("createdAt", NullableDate())),
        ["UpdatePlatformRuntimeDefaultsRequest"] = Obj(["expectedRevision", "defaults"],
            ("expectedRevision", Int()), ("defaults", Ref("PlatformRuntimeDefaults"))),
        ["PlatformRuntimeDefaults"] = Obj(["egressBaseline", "runLimits"],
            ("modelSelection", NullableRef("ModelSelectionSettings")),
            ("egressBaseline", Arr(Ref("NetworkEgressRule"))), ("runLimits", Ref("CopilotRunLimits"))),
        ["CopilotRunLimits"] = Obj(["maxModelTurns", "maxToolCalls", "maxChildren", "maxConcurrentChildren", "maxWallTimeSeconds", "maxPromptTokens"],
            ("maxModelTurns", Int()), ("maxToolCalls", Int()), ("maxChildren", Int()),
            ("maxConcurrentChildren", Int()), ("maxWallTimeSeconds", Int()), ("maxPromptTokens", Int())),
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
        ["EffectiveRunSelection"] = Obj(["projectId", "runId", "projectRevision", "projectConfigurationRevision", "platformRuntimeRevision", "contextRevision", "modelSelection", "providers", "egressAllowlist", "runLimits", "projectConfiguration", "egressBaseline", "requiredEgress"],
            ("projectId", Str()), ("runId", Str()), ("projectRevision", Int()), ("projectConfigurationRevision", Int()),
            ("platformRuntimeRevision", Int()), ("contextRevision", Str()), ("modelSelection", Ref("ModelSelectionSettings")),
            ("providers", Arr(Ref("EffectiveProviderSelection"))), ("egressAllowlist", Arr(Ref("NetworkEgressRule"))),
            ("runLimits", Ref("CopilotRunLimits")), ("projectConfiguration", Ref("ProjectConfiguration")),
            ("egressBaseline", Arr(Ref("NetworkEgressRule"))),
            ("projectEgressNarrowing", Nullable(Arr(Ref("NetworkEgressRule")))),
            ("requiredEgress", Arr(Ref("NetworkEgressRule")))),
        ["EffectiveProviderSelection"] = Obj(["cardinality", "seam", "candidates"],
            ("cardinality", Enum("exclusive", "orderedComposite", "platformSingleton", "layered", "keyedByMeterSource", "perApplication")),
            ("seam", ProviderSeamSchema()), ("candidates", Arr(Ref("EffectiveProviderCandidate"))), ("meterSource", NullableStr())),
        ["EffectiveProviderCandidate"] = Obj(["seam", "providerId", "adapterVersion", "optionsSchemaVersion", "optionsRevision", "hosting", "advertisedCapabilities", "requiredCapabilities"],
            ("seam", ProviderSeamSchema()), ("providerId", Str()), ("adapterVersion", Str()),
            ("optionsSchemaVersion", Int()), ("optionsRevision", Str()),
            ("hosting", Enum("inProcess", "sidecar", "remoteService", "kubernetesController", "managedRuntime")),
            ("advertisedCapabilities", Arr(Str())), ("requiredCapabilities", Arr(Str())),
            ("layer", Nullable(Enum("l3L4", "l7")))),

        ["AcceptRootRequest"] = Obj(["sessionId"], ("sessionId", Str())),
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

        ["SessionEventPage"] = Obj(["events", "nextCursor", "hasMore"],
            ("events", Arr(Ref("SessionEventEnvelope"))), ("nextCursor", NullableStr()), ("hasMore", Bool())),
        ["SessionEventEnvelope"] = Obj(["schemaVersion", "eventVersion", "eventId", "identity", "position", "occurredAt", "kind", "payload", "objectReferences"],
            ("schemaVersion", Int()), ("eventVersion", Int()), ("eventId", Uuid()), ("identity", Ref("SessionIdentity")),
            ("position", Int()), ("occurredAt", Date()),
            ("kind", Enum("turn", "toolCall", "policyEvaluation", "decisionAccepted", "effectAccepted", "artifactReference", "cacheReference", "addressedMessage")),
            ("payload", Ref("SessionEventPayload")), ("objectReferences", Arr(Ref("StoredSessionObjectReference")))),
        ["SessionEventPayload"] = new Dictionary<string, object>
        {
            ["oneOf"] = new[]
            {
                Ref("TurnSessionPayload"), Ref("ToolCallSessionPayload"), Ref("PolicyEvaluationSessionPayload"),
                Ref("AcceptedDecisionSessionPayload"), Ref("AcceptedEffectSessionPayload"), Ref("ArtifactReferenceSessionPayload"),
                Ref("CacheReferenceSessionPayload"), Ref("AddressedMessageSessionPayload"),
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
