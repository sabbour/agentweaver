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
    public void RequiresCurrentProjectWriteAndOrchestratorAuthority()
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
                    [
                        new ProjectsPermissionGrant("acceptRunSelection", 1),
                        new ProjectsPermissionGrant("writeProjects", 1)
                    ])
                ]));

        CoordinationEndpoints.RequireProjectWriteAndOrchestratorAuthority(
            accepted, projectId, runId, actor);
        var revoked = accepted with
        {
            Authorization = accepted.Authorization with
            {
                EffectiveAuthority = [new ProjectsAuthority("project", projectId,
                    [new ProjectsPermissionGrant("acceptRunSelection", 2)])]
            }
        };
        var denied = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.RequireProjectWriteAndOrchestratorAuthority(
                revoked, projectId, runId, actor));
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);

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
