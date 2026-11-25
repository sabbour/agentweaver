using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowGenerationContractTests
{
    [Fact]
    public void GenerationRoutesExposeSeparateStatusAndResultReads()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<OrchestratorOptions>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<ProjectsRunSelectionClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinationOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<CoordinatorDecisionOwnerStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<RuntimeRegistrationOwner>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<WorkflowGenerationJobStore>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton<WorkflowGenerationWorkQueue>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve services."));
        builder.Services.AddSingleton(TimeProvider.System);
        using var app = builder.Build();
        app.MapWorkflowGenerationEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        AssertRoute(routes, "/api/projects/{projectId}/workflows/generate", "POST");
        AssertRoute(routes, "/api/projects/{projectId}/workflows/generation-jobs/{jobId:guid}", "GET");
        AssertRoute(routes, "/api/projects/{projectId}/workflows/generation-jobs/{jobId:guid}/result", "GET");
    }

    [Fact]
    public void RequestBoundsPromptAndRequiresAValidRunDecisionBinding()
    {
        new GenerateProjectWorkflowRequest("run", "root", 1, "Create a workflow.").Validate();

        var oversized = new GenerateProjectWorkflowRequest(
            "run", "root", 1,
            new string('x', GenerateProjectWorkflowRequest.MaximumPromptLength + 1));
        var invalid = Assert.Throws<CoordinationException>(() => oversized.Validate());
        Assert.Equal("workflow_generation_request_invalid", invalid.Code);

        var missingDecision = new GenerateProjectWorkflowRequest("run", "root", 0, "Create a workflow.");
        Assert.Equal("workflow_generation_request_invalid",
            Assert.Throws<CoordinationException>(() => missingDecision.Validate()).Code);
    }

    [Fact]
    public void RequiresSeparateCurrentRunAndProjectWriterAuthority()
    {
        const string projectId = "project";
        const string runId = "run";
        var actor = new CoordinationActor("https://identity.example/", Guid.NewGuid().ToString("D"));
        var accepted = new AuthorizedRunSelection(
            new EffectiveRunSelection(
                projectId, runId, 1, 1, 1, "context-v1", JsonSerializer.SerializeToElement(new { })),
            new ProjectsAuthorizationContext(
                1, actor.Issuer, actor.Subject, "tenant", 1, projectId, runId,
                [
                    new ProjectsAuthority("project", projectId,
                        [new ProjectsPermissionGrant("acceptRunSelection", 1)])
                ]));
        var writer = new ProjectsAuthorizationContext(
            1, actor.Issuer, actor.Subject, "tenant", 1, null, null,
            [new ProjectsAuthority("project", projectId, [new ProjectsPermissionGrant("writeProjects", 1)])]);

        CoordinationEndpoints.RequireProjectWriteAndOrchestratorAuthority(
            accepted, writer, projectId, runId, actor);
        var revokedRun = accepted with
        {
            Authorization = accepted.Authorization with
            {
                EffectiveAuthority = []
            }
        };
        var denied = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireProjectWriteAndOrchestratorAuthority(
                revokedRun, writer, projectId, runId, actor));
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);

        var revokedWriter = writer with
        {
            EffectiveAuthority = [new ProjectsAuthority("project", projectId, [])]
        };
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireProjectWriteAndOrchestratorAuthority(
                    accepted, revokedWriter, projectId, runId, actor)).StatusCode);

        var missingScope = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", "projects.read")], "test"));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RequireOrchestratorScope(missingScope)).StatusCode);
        var acceptedScope = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scp", "projects.orchestrator")], "test"));
        CoordinationEndpoints.RequireOrchestratorScope(acceptedScope);
    }

    [Fact]
    public void SameRequestFingerprintCanBeRecomputedFromThePersistedJobBinding()
    {
        const string projectId = "project";
        const string runId = "run";
        const string sessionId = "root";
        const string tenantId = "tenant";
        const string modelReference = "accepted-reference";
        var actor = new CoordinationActor("https://identity.example/", Guid.NewGuid().ToString("D"));
        var selection = new EffectiveRunSelection(
            projectId,
            runId,
            1,
            1,
            1,
            "context-v1",
            CoordinatorRunAdmissionTests.Selection(projectId, runId));
        var selectionHash = CoordinationOwnerStore.HashSelection(selection).ToLowerInvariant();
        var authorized = new AuthorizedRunSelection(selection, new ProjectsAuthorizationContext(
            1,
            actor.Issuer,
            actor.Subject,
            tenantId,
            1,
            projectId,
            runId,
            []));
        var binding = new RuntimeBinding(
            actor.Issuer,
            actor.Subject,
            tenantId,
            projectId,
            runId,
            sessionId,
            "coordinator",
            "root-turn",
            1,
            1,
            1,
            "context-v1",
            selectionHash,
            1,
            "environment",
            "placement",
            1,
            "profile",
            new Uri("https://runtime.example/runtime/v1/configure"),
            new Uri("https://runtime.example/runtime/v1/observe"))
        {
            ModelSelectionReference = modelReference,
            ModelSourceMode = ModelSourceMode.Byok,
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1,
            Image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1)
        };
        var registration = new RuntimeRegistration(
            Guid.NewGuid(), 2, binding, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
        var request = new GenerateProjectWorkflowRequest(runId, sessionId, 7, "Create a workflow.");
        var fingerprint = WorkflowGenerationExecution.RequestFingerprint(
            request, authorized, actor, registration, 1);
        var job = new WorkflowGenerationJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            projectId,
            runId,
            sessionId,
            tenantId,
            actor.Issuer,
            actor.Subject,
            "key",
            fingerprint,
            WorkflowGenerationExecution.PurposeBoundPrompt(request.Prompt),
            WorkflowGenerationJobStore.Hash(request.Prompt),
            request.ExpectedDecisionStateVersion,
            selection.ProjectConfigurationRevision,
            selectionHash,
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            modelReference,
            "queued",
            "queued",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Assert.Equal(fingerprint, WorkflowGenerationExecution.RequestFingerprint(request, job));

        var staleFence = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireRegistrationBinding(
                registration, projectId, runId, sessionId, 2, selectionHash, 1, TimeProvider.System));
        Assert.Equal(StatusCodes.Status409Conflict, staleFence.StatusCode);
        var staleSelection = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireRegistrationBinding(
                registration, projectId, runId, sessionId, 1, new string('b', 64), 1, TimeProvider.System));
        Assert.Equal(StatusCodes.Status409Conflict, staleSelection.StatusCode);
    }

    [Fact]
    public void RejectsStaleDecisionVersionsAndAnyExistingWorkflowOrPlan()
    {
        var state = ProposedPlanState();
        var current = new CoordinatorDecisionCurrentState(state, 7, new string('a', 64));
        var afterPlan = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequirePrePlanState(current, 7));
        Assert.Equal("workflow_generation_not_available_after_plan", afterPlan.Code);

        var noPlan = CoordinatorDecisionState.Create(1);
        var stale = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequirePrePlanState(
                new CoordinatorDecisionCurrentState(noPlan, 8, new string('a', 64)), 7));
        Assert.Equal("workflow_generation_decision_stale", stale.Code);
    }

    [Fact]
    public void ZeroCreditNoSendProofSealsValidatesAndRedactsSensitiveFields()
    {
        var jobId = Guid.NewGuid();
        var nativeRequestId = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();
        var selectionHash = new string('a', 64);
        var promptHash = new string('b', 64);
        var requestHash = new string('c', 64);
        var guardHash = new string('d', 64);
        var noSendHash = new string('e', 64);
        var cursorHash = new string('f', 64);
        var recordedAt = new DateTimeOffset(2026, 11, 25, 1, 0, 0, TimeSpan.Zero);
        var verifiedAt = recordedAt.AddSeconds(2);

        var proof = WorkflowGenerationZeroCreditNoSendProof.Seal(new WorkflowGenerationZeroCreditNoSendProof(
            1,
            jobId,
            nativeRequestId,
            "project",
            "run",
            "root",
            "tenant",
            "https://identity.example/",
            "actor-1",
            3,
            2,
            selectionHash,
            4,
            runtimeId,
            5,
            "model:selected",
            promptHash,
            requestHash,
            guardHash,
            noSendHash,
            "hostPreQueue",
            "hardLimitZero",
            0,
            true,
            cursorHash,
            cursorHash,
            recordedAt,
            verifiedAt,
            string.Empty)).Validate();

        Assert.Throws<RuntimeAuthorizationException>(() =>
            (proof with { ExecutionFence = 5 }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (proof with { ObservedEventCount = 1 }).Validate());

        var status = proof.ToStatus();
        Assert.Equal(nativeRequestId, status.NativeRequestId);
        Assert.Equal(runtimeId, status.RuntimeInstanceId);
        Assert.Equal(5, status.RegistrationRevision);
        Assert.Equal(4, status.ExecutionFence);
        Assert.Equal(noSendHash, status.ReceiptSha256);

        var job = new WorkflowGenerationJob(
            jobId, nativeRequestId, "project", "run", "root", "tenant",
            "https://identity.example/", "actor-1", "key", new string('1', 64),
            "prompt", promptHash, 3, 2, selectionHash, 4, runtimeId, 5, "model:selected",
            "running", "send-admitted", null, null, null, recordedAt, verifiedAt);
        Assert.True(proof.MatchesJob(job));
        Assert.False(proof.MatchesJob(job with { ExecutionFence = 5 }));

        var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyEnvelopeJson = JsonSerializer.Serialize(WorkflowGenerationExecution.CreateEnvelope(job), webJson);
        Assert.DoesNotContain("zeroCreditNoSend", legacyEnvelopeJson, StringComparison.Ordinal);

        var proofEnvelopeJson = JsonSerializer.Serialize(
            WorkflowGenerationExecution.CreateEnvelope(job, status), webJson);
        Assert.Contains("zeroCreditNoSend", proofEnvelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceGrant", proofEnvelopeJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sdkSession", proofEnvelopeJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(promptHash, proofEnvelopeJson, StringComparison.Ordinal);
    }

    private static CoordinatorDecisionState ProposedPlanState()
    {
        const string actor = "workflow-owner";
        var proposedOutcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(1),
            new CoordinatorOutcomeSpecification(
                "outcome-1",
                "Implement the requested capability.",
                "A validated implementation is available.",
                "Only the requested workflow behavior changes.",
                "Existing provider pins remain authoritative.",
                []),
            "outcome-confirmation",
            actor);
        var outcomeGate = proposedOutcome.State!.PendingGate!;
        var confirmedOutcome = proposedOutcome.State.ApplyGateAnswer(
            new CoordinatorGateAnswer(
                outcomeGate.RequestId, actor, proposedOutcome.State.Fence,
                CoordinatorGateChoices.Approve, null));
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome.State,
            new AuthorizedWorkflowCatalog(WorkflowTestData.Definition(), [WorkflowTestData.Definition()]),
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        var plan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State!,
            WorkflowTestData.Plan(WorkflowTestData.Item("first")),
            WorkflowTestData.SelectionContext(),
            "plan-confirmation",
            actor);
        return plan.State!;
    }

    private static void AssertRoute(
        IReadOnlyCollection<RouteEndpoint> routes,
        string path,
        string method)
    {
        var route = Assert.Single(routes, endpoint =>
            endpoint.RoutePattern.RawText == path &&
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                method, StringComparer.Ordinal) == true);
        Assert.Contains(route.Metadata, metadata => metadata.GetType().Name == "AuthorizeAttribute");
    }
}
