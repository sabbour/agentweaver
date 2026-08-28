using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Api;

public sealed class ToolApprovalEndpointTests
{
    [Fact]
    public async Task Approve_PendingChildApproval_Succeeds_WhenCoordinatorIsTerminal()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.Failed);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        const string requestId = "pending-child-approval";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            childId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    // Regression guard: ResolveApprovalOwningRunIdAsync can resolve the approval-gate context to a
    // SYNTHETIC coordinator-phase key ("{coordinatorId}-coordinator-draft") when the approval was
    // raised during a coordinator-phase LLM turn (spec drafting) rather than by a persisted child
    // subtask run. That synthetic id is not a real run-store row and must never reach RunId.Parse —
    // before the fix this crashed with a bare 500 ("Guid should contain 32 digits...") on the very
    // FIRST approval click of a run, before decompose even started.
    [Fact]
    public async Task Approve_CoordinatorPhaseApproval_DoesNotCrash_AndResolvesToCoordinatorRun()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.InProgress);

        const string requestId = "toolu_coordinator_draft_web_fetch";
        var draftRunKey = coordinatorId + "-coordinator-draft";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            draftRunKey, requestId, "web_fetch", "https://github.com/example/repo/issues/1",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the coordinator-phase synthetic key must resolve without crashing");
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    // #349 — exact repro: an agent issues 3 concurrent approval-gated web_fetch tool.call events,
    // but the SDK invokes the permission callback sequentially so only the FIRST registers a real
    // backend approval gate (+ tool.approval_required). The frontend optimistically renders a card
    // per tool.call, so #2/#3 are "phantom" cards whose request_ids are Unknown to the backend.
    // Approving the first (posted to the coordinator, resolved to the active child) must succeed;
    // approving the phantom #2/#3 must NOT be mislabeled "Run is not active" — the coordinator the
    // card posted to may already be AssembleReady while the owning child is still active.
    [Fact]
    public async Task Approve_ThreeConcurrentWebFetch_FirstSucceeds_PhantomsReportUnknownNotRunNotActive()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        // Coordinator has already moved on to assembly while the child research subtask keeps
        // fetching — this is what makes the phantom-card fallback hit a non-active run.
        await InsertRunAsync(runStore, coordinatorId, RunStatus.AssembleReady);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        // Only the FIRST web_fetch reached the permission gate and registered a real request.
        const string firstRequestId = "toolu_first_web_fetch";
        var firstApproval = approvalGate.WaitForApprovalAsync(
            childId.ToString(), firstRequestId, "web_fetch", "https://anthropic.com/a",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        // First card: posted to the coordinator, resolves to the active child, approves cleanly.
        var firstResponse = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = firstRequestId, scope = "once" });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await firstApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        // Phantom cards #2 and #3: never backend-registered. Must not 409 "Run is not active".
        foreach (var phantomRequestId in new[] { "toolu_second_web_fetch", "toolu_third_web_fetch" })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/runs/{coordinatorId}/tool-approvals",
                new { request_id = phantomRequestId, scope = "once" });

            response.StatusCode.Should().NotBe(
                HttpStatusCode.Conflict, $"phantom {phantomRequestId} must not be mislabeled Run-not-active");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("Run is not active");
            body.Should().Contain("unknown", "an unregistered request must report an accurate unknown-request error");
        }
    }

    [Fact]
    public async Task Deny_PhantomCard_UnknownRequestOnNonActiveCoordinator_DoesNotReturnRunNotActive()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.AssembleReady);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-denials",
            new { request_id = "toolu_never_registered", scope = "once" });

        response.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Run is not active");
        body.Should().Contain("unknown");
    }

    [Fact]
    public async Task Approve_PendingApprovalOnTerminalOwningRun_ReturnsConflict()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        const string requestId = "stale-terminal-approval";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            runId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        approvalGate.Deny(runId.ToString(), requestId).Should().BeTrue();
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task Deny_PendingChildApproval_Succeeds_WhenCoordinatorIsTerminal()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(runStore, coordinatorId, RunStatus.Failed);
        await InsertRunAsync(runStore, childId, RunStatus.InProgress, coordinatorId.ToString());

        const string requestId = "pending-child-denial";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            childId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-denials",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task Deny_PendingApprovalOnTerminalOwningRun_ReturnsConflict()
    {
        using var factory = new AgentweaverWebApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();

        var runId = RunId.New();
        await InsertRunAsync(runStore, runId, RunStatus.Failed);

        const string requestId = "stale-terminal-denial";
        var pendingApproval = approvalGate.WaitForApprovalAsync(
            runId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-denials",
            new { request_id = requestId, scope = "once" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        approvalGate.Deny(runId.ToString(), requestId).Should().BeTrue();
        (await pendingApproval.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }

    [Fact]
    public async Task ApproveAlways_AffectsOnlyPersistedInitiatingOwnerInTheSameProject()
    {
        using var factory = new CoordinatorWebApplicationFactory();
        using var ownerClient = factory.CreateOwnerClient();
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var source = RunId.New();
        var ownerFuture = RunId.New();
        var otherFuture = RunId.New();
        var projectResponse = await ownerClient.PostAsJsonAsync("/api/projects", new
        {
            name = $"Approval scope {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = ProjectId.Parse(
            (await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("project_id").GetString()!);
        await InsertRunAsync(
            runStore, source, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser,
            projectId: project);
        await InsertRunAsync(
            runStore, ownerFuture, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser,
            projectId: project);
        await InsertRunAsync(
            runStore, otherFuture, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OtherUser,
            projectId: project);
        var pending = approvalGate.WaitForApprovalAsync(
            source.ToString(), "owner-always", "web_fetch", "https://example.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = "owner-always", scope = "always" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        approvalGate.IsAutoApproved(ownerFuture.ToString(), "web_fetch", "https://owner.test")
            .Should().BeTrue();
        approvalGate.IsAutoApproved(otherFuture.ToString(), "web_fetch", "https://other.test")
            .Should().BeFalse();
        var policy = await ownerClient.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/runs/{ownerFuture}/tool-approval-policies/web_fetch");
        policy.GetProperty("auto_approved").GetBoolean().Should().BeTrue(
            "a new AgentHost pod reads this durable policy before it decides whether to prompt");
    }

    [Fact]
    public async Task ProjectContributor_CanApproveOnceButCannotCreateScopesForAnotherContributorsRun()
    {
        const string ownerUser = "tool-approval-owner-oid";
        const string otherUser = "tool-approval-contributor-oid";
        using var factory = new EntraWebApplicationFactory();
        using var ownerClient = factory.CreateAuthenticatedClientForObjectId(
            ownerUser,
            PlatformRoles.ProjectCreator);
        using var otherClient = factory.CreateAuthenticatedClientForObjectId(
            otherUser,
            PlatformRoles.Contributor);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var roles = factory.Services.GetRequiredService<IProjectRoleAssignmentStore>();
        var project = ProjectId.New();
        await factory.Services.GetRequiredService<IProjectStore>().InsertAsync(new Project
        {
            Id = project,
            Name = $"Approval scope {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = factory.NewWorkingDirectory(),
            DefaultBranch = "main",
            Owner = ownerUser,
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await roles.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = project,
            PrincipalId = ownerUser,
            Role = ProjectRole.Owner,
            GrantedBy = ownerUser,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await roles.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = project,
            PrincipalId = otherUser,
            Role = ProjectRole.Contributor,
            GrantedBy = ownerUser,
            GrantedAt = DateTimeOffset.UtcNow,
        });

        var ownerRun = RunId.New();
        await InsertRunAsync(
            runStore,
            ownerRun,
            RunStatus.InProgress,
            submittingUser: ownerUser,
            projectId: project);
        var ownerPending = approvalGate.WaitForApprovalAsync(
            ownerRun.ToString(), "other-contributor-owner-run", "web_fetch", "https://example.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var forbidden = await otherClient.PostAsJsonAsync(
            $"/api/runs/{ownerRun}/tool-approvals",
            new { request_id = "other-contributor-owner-run", scope = "always" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        approvalGate.GetRequestState(ownerRun.ToString(), "other-contributor-owner-run")
            .Should().Be(ToolApprovalRequestState.Pending);

        var oneTime = await otherClient.PostAsJsonAsync(
            $"/api/runs/{ownerRun}/tool-approvals",
            new { request_id = "other-contributor-owner-run", scope = "once" });
        oneTime.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ownerPending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "a project contributor may still perform the one-time review action");

        var otherRun = RunId.New();
        var otherFuture = RunId.New();
        var ownerFuture = RunId.New();
        await InsertRunAsync(
            runStore,
            otherRun,
            RunStatus.InProgress,
            submittingUser: otherUser,
            projectId: project);
        await InsertRunAsync(
            runStore,
            otherFuture,
            RunStatus.InProgress,
            submittingUser: otherUser,
            projectId: project);
        await InsertRunAsync(
            runStore,
            ownerFuture,
            RunStatus.InProgress,
            submittingUser: ownerUser,
            projectId: project);
        var otherPending = approvalGate.WaitForApprovalAsync(
            otherRun.ToString(), "other-contributor-own-run", "web_fetch", "https://example.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var ownPolicy = await otherClient.PostAsJsonAsync(
            $"/api/runs/{otherRun}/tool-approvals",
            new { request_id = "other-contributor-own-run", scope = "always" });
        ownPolicy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await otherPending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        approvalGate.IsAutoApproved(otherFuture.ToString(), "web_fetch", "https://other.test")
            .Should().BeTrue();
        approvalGate.IsAutoApproved(ownerFuture.ToString(), "web_fetch", "https://owner.test")
            .Should().BeFalse("a contributor's policy cannot authorize another contributor's run");
    }

    [Fact]
    public async Task AgentHostPolicyRead_RequiresTheRunsBoundCapability()
    {
        const string ownerUser = "tool-approval-agenthost-owner";
        const string capabilityToken = "valid-run-capability";
        using var factory = new EntraWebApplicationFactory();
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var capabilities = factory.Services.GetRequiredService<IRunAuthorshipCapabilityStore>();
        var project = ProjectId.New();
        await factory.Services.GetRequiredService<IProjectStore>().InsertAsync(new Project
        {
            Id = project,
            Name = $"Approval scope {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = factory.NewWorkingDirectory(),
            DefaultBranch = "main",
            Owner = ownerUser,
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var source = RunId.New();
        var future = RunId.New();
        await InsertRunAsync(runStore, source, RunStatus.InProgress, submittingUser: ownerUser, projectId: project);
        await InsertRunAsync(runStore, future, RunStatus.InProgress, submittingUser: ownerUser, projectId: project);
        var pending = approvalGate.WaitForApprovalAsync(
            source.ToString(), "agenthost-policy-source", "web_fetch", "https://source.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);
        (await approvalGate.GrantAsync(source.ToString(), "agenthost-policy-source", ApprovalScope.Always))
            .Should().BeTrue();
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        await capabilities.RegisterAsync(
            future.ToString(),
            capabilityToken,
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "internal-test-api-key");

        var missingCapability = await client.GetAsync(
            $"/api/runs/{future}/tool-approval-policies/web_fetch");
        missingCapability.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        client.DefaultRequestHeaders.Add(RunAuthorshipHeaders.RunId, future.ToString());
        client.DefaultRequestHeaders.Add(RunAuthorshipHeaders.RunToken, capabilityToken);
        var response = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/runs/{future}/tool-approval-policies/web_fetch");
        response.GetProperty("auto_approved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PodPerRun_AgentHostAcceptedScopes_AuthorizeSiblingAndFuturePodRun()
    {
        var agentHost = new RecordingAgentHostClient(
            new AgentHostApprovalOutcome(
                Resolved: true,
                State: "approved",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: true,
                ToolName: "web_fetch"));
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                    ["Agentweaver:RemoteApiBaseUrl"] = "http://agentweaver-api:8080",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        var project = await CreateProjectAsync(baseFactory, ownerClient);
        var parent = RunId.New();
        var child = RunId.New();
        var sibling = RunId.New();
        foreach (var id in new[] { parent, child, sibling })
        {
            await InsertRunAsync(
                runStore,
                id,
                RunStatus.InProgress,
                parentRunId: id == parent ? null : parent.ToString(),
                submittingUser: CoordinatorWebApplicationFactory.OwnerUser,
                projectId: project);
        }
        approvalGate.RegisterParentRun(child.ToString(), parent.ToString());
        approvalGate.RegisterParentRun(sibling.ToString(), parent.ToString());
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(child.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{child}/tool-approvals",
            new { request_id = "pod-session-scope", scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        agentHost.LastScope.Should().Be("run");
        approvalGate.IsAutoApproved(sibling.ToString(), "web_fetch", "https://sibling.test")
            .Should().BeTrue(
                "the API persists the scope after the AgentHost has completed its local approval");

        var alwaysSource = RunId.New();
        var futurePodRun = RunId.New();
        foreach (var id in new[] { alwaysSource, futurePodRun })
        {
            await InsertRunAsync(
                runStore,
                id,
                RunStatus.InProgress,
                submittingUser: CoordinatorWebApplicationFactory.OwnerUser,
                projectId: project);
        }
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(alwaysSource.ToString()),
            "pod-always-credential");

        var always = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{alwaysSource}/tool-approvals",
            new { request_id = "pod-always-scope", scope = "always" });

        always.StatusCode.Should().Be(HttpStatusCode.OK);
        agentHost.LastScope.Should().Be("always");
        var futurePolicy = await ownerClient.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/runs/{futurePodRun}/tool-approval-policies/web_fetch");
        futurePolicy.GetProperty("auto_approved").GetBoolean().Should().BeTrue(
            "a freshly configured AgentHost pod reads the same project-and-owner-bound policy");
    }

    [Fact]
    public async Task PodPerRun_AgentHostScopePersistenceFailure_DoesNotAuthorizeFutureCalls()
    {
        var agentHost = new RecordingAgentHostClient(
            new AgentHostApprovalOutcome(
                Resolved: true,
                State: "approved",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: true));
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
                services.RemoveAll<IAgentHostToolApprovalPersistence>();
                services.AddSingleton<IAgentHostToolApprovalPersistence, FailingAgentHostApprovalPersistence>();
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        var runId = RunId.New();
        await InsertRunAsync(
            runStore,
            runId,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(runId.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = "pod-missing-context", scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        agentHost.LastScope.Should().Be("run");
        agentHost.DenyCalls.Should().Be(0, "the one-time approval is already terminal");
        approvalGate.IsAutoApproved(runId.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse("a failed AgentHost scope persistence must not widen future access");
    }

    [Fact]
    public async Task PodPerRun_AppliedScopeWithDroppedResponse_IsRolledBackBeforeTransportFailure()
    {
        var source = RunId.New();
        const string requestId = "pod-dropped-scoped-response";
        var agentHost = new CurrentHostBridgeAgentHostClient(
            source.ToString(),
            requestId,
            CoordinatorWebApplicationFactory.OwnerUser)
        {
            DropScopedGrantResponse = true,
        };
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        await InsertRunAsync(
            runStore,
            source,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = requestId, scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await agentHost.InitialTool.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the pod applied the scoped approval before its response was dropped");
        agentHost.RollbackCalls.Should().Be(1);
        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeFalse(
            "an ambiguous transport outcome must not leave the pod-local scope usable");
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse("no durable scope is persisted without an applied response proof");
    }

    [Fact]
    public async Task PodPerRun_TerminalizedPodOwnedScope_DoesNotForwardAfterPendingContextLookup()
    {
        var agentHost = new TerminalizingPendingContextAgentHostClient();
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        var runId = RunId.New();
        await InsertRunAsync(
            runStore,
            runId,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(runId.ToString()),
            "pod-approval-credential");
        agentHost.Terminalize = () => runStore.TrySetTerminalStatusAsync(
            runId,
            RunStatus.Failed,
            DateTimeOffset.UtcNow,
            "terminal-race",
            CancellationToken.None);

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{runId}/tool-approvals",
            new { request_id = "terminal-pod-owned", scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        agentHost.PendingContextCalls.Should().Be(1);
        agentHost.GrantCalls.Should().Be(0,
            "a pod-owned pending approval cannot be released after its target run terminalizes");
        approvalGate.IsAutoApproved(runId.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("run", "unreachable", true, StatusCodes.Status503ServiceUnavailable)]
    [InlineData("tool", "unreachable", true, StatusCodes.Status503ServiceUnavailable)]
    [InlineData("run", "error", false, StatusCodes.Status503ServiceUnavailable)]
    [InlineData("always", "expired", false, StatusCodes.Status200OK)]
    [InlineData("always", "denied", false, StatusCodes.Status200OK)]
    public async Task PodPerRun_AgentHostScopeForwardingFailure_DoesNotAuthorizeLaterCalls(
        string scope,
        string state,
        bool unreachable,
        int expectedStatusCode)
    {
        var agentHost = new RecordingAgentHostClient(
            new AgentHostApprovalOutcome(
                Resolved: state is "expired" or "denied",
                State: state,
                Unreachable: unreachable,
                StatusCode: unreachable ? null : StatusCodes.Status200OK));
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        var project = await CreateProjectAsync(baseFactory, ownerClient);
        var source = RunId.New();
        var later = RunId.New();
        await InsertRunAsync(
            runStore, source, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser, projectId: project);
        await InsertRunAsync(
            runStore, later, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser, projectId: project);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = $"pod-{scope}-{state}", scope });

        response.StatusCode.Should().Be((HttpStatusCode)expectedStatusCode);
        agentHost.LastScope.Should().Be(scope);
        if (state is "expired" or "denied")
        {
            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            body.GetProperty("state").GetString().Should().Be(state);
            body.GetProperty("approved").GetBoolean().Should().BeFalse();
        }
        approvalGate.IsAutoApproved(
            scope == "always" ? later.ToString() : source.ToString(),
            "web_fetch",
            "https://following.test").Should().BeFalse(
            "an AgentHost {0} result must not create a {1} policy", state, scope);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("tool")]
    [InlineData("always")]
    public async Task PodPerRun_OnceWinsLateScopedForward_DoesNotPersistDurablePolicy(string scope)
    {
        var agentHost = new OnceWinsAgentHostClient();
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        var project = await CreateProjectAsync(baseFactory, ownerClient);
        var source = RunId.New();
        var future = RunId.New();
        await InsertRunAsync(
            runStore, source, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser, projectId: project);
        await InsertRunAsync(
            runStore, future, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser, projectId: project);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        var lateScope = ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = "pod-once-wins", scope });
        await agentHost.LateScopeForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var once = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = "pod-once-wins", scope = "once" });
        once.StatusCode.Should().Be(HttpStatusCode.OK);

        var late = await lateScope.WaitAsync(TimeSpan.FromSeconds(5));
        late.StatusCode.Should().Be(HttpStatusCode.OK);
        var lateBody = await late.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        lateBody.GetProperty("state").GetString().Should().Be("approved");
        lateBody.GetProperty("applied").GetBoolean().Should().BeFalse();
        approvalGate.IsAutoApproved(
            scope == "always" ? future.ToString() : source.ToString(),
            "web_fetch",
            "https://following.test").Should().BeFalse(
            "an unapplied scoped retry must not persist a durable policy");
    }

    [Fact]
    public async Task PodPerRun_CurrentHostScopeDoesNotAuthorizeBeforeDurablePolicyCommit()
    {
        var source = RunId.New();
        const string requestId = "pod-current-host-bridge";
        var agentHost = new CurrentHostBridgeAgentHostClient(
            source.ToString(),
            requestId,
            CoordinatorWebApplicationFactory.OwnerUser);
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
                services.RemoveAll<IAgentHostToolApprovalPersistence>();
                services.AddSingleton<BlockingAgentHostApprovalPersistence>();
                services.AddSingleton<IAgentHostToolApprovalPersistence>(sp =>
                    sp.GetRequiredService<BlockingAgentHostApprovalPersistence>());
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        var persistence = factory.Services.GetRequiredService<BlockingAgentHostApprovalPersistence>();
        agentHost.DurablePolicyAuthorizes = () => persistence.Committed;
        await InsertRunAsync(
            runStore,
            source,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeFalse();
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse();

        var approval = ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = requestId, scope = "run" });
        await persistence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        agentHost.LastScope.Should().Be("run");
        (await agentHost.InitialTool.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeFalse(
            "the provisional AgentHost scope must not bypass the durable policy before it commits");
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse("the durable policy is still blocked from committing");

        persistence.Release();
        var response = await approval.WaitAsync(TimeSpan.FromSeconds(5));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeTrue(
            "the AgentHost may authorize once its durable policy reader confirms the committed policy");
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeTrue("the durable policy is published after successful local acceptance");
    }

    [Fact]
    public async Task PodPerRun_CurrentHostBridgeRollsBackBeforeReportingPersistenceException()
    {
        var source = RunId.New();
        const string requestId = "pod-current-host-rollback";
        var agentHost = new CurrentHostBridgeAgentHostClient(
            source.ToString(),
            requestId,
            CoordinatorWebApplicationFactory.OwnerUser);
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
                services.RemoveAll<IAgentHostToolApprovalPersistence>();
                services.AddSingleton<IAgentHostToolApprovalPersistence>(
                    new FailingAgentHostApprovalPersistence(throwOnPersist: true));
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        await InsertRunAsync(
            runStore,
            source,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = requestId, scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await agentHost.InitialTool.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        agentHost.RollbackCalls.Should().Be(1);
        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeFalse(
            "the API must revoke the current pod's provisional bridge before reporting persistence failure");
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse();
    }

    [Fact]
    public async Task PodPerRun_ImmediateRollbackFailure_ClosesProvisionalScopeViaHelperBeforeReporting503()
    {
        // PR #972 finding 1: when durable persistence fails AND the immediate provisional-scope
        // rollback attempt also fails, the endpoint must not return 503 while the exact local
        // scope could still authorize the pod. It must fall back to the same close-or-expire
        // helper (EnsureProvisionalAgentHostScopeClosedAsync) already relied on for dropped or
        // unproven AgentHost forwards, which retries the rollback and only returns once the scope
        // is guaranteed removed (or its lease has elapsed).
        var source = RunId.New();
        const string requestId = "pod-rollback-retry";
        var agentHost = new CurrentHostBridgeAgentHostClient(
            source.ToString(),
            requestId,
            CoordinatorWebApplicationFactory.OwnerUser)
        {
            // The FIRST rollback attempt (the direct, pre-existing call) fails; the SECOND
            // attempt -- made only if the new call to EnsureProvisionalAgentHostScopeClosedAsync
            // is actually reached -- succeeds, keeping this test fast and deterministic.
            FailRollbackAttempts = 1,
        };
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
                services.RemoveAll<IAgentHostToolApprovalPersistence>();
                services.AddSingleton<IAgentHostToolApprovalPersistence>(
                    new FailingAgentHostApprovalPersistence(throwOnPersist: true));
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        await InsertRunAsync(
            runStore,
            source,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = requestId, scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await agentHost.InitialTool.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        agentHost.RollbackCalls.Should().Be(
            2,
            "the immediate rollback attempt failed once, so the new fallback to " +
            "EnsureProvisionalAgentHostScopeClosedAsync must retry it rather than failing immediately");
        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeFalse(
            "the retried rollback via the helper must still remove the current pod's provisional bridge");
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse();

        var detail = await response.Content.ReadAsStringAsync();
        detail.Should().NotContain(
            "Access may remain active",
            "the response must not claim the scope may still be active once it is guaranteed closed or expired");
    }

    [Fact]
    public async Task EnsureProvisionalAgentHostScopeClosedAsync_ReturnsImmediately_WhenRollbackSucceeds()
    {
        // Isolated, reflection-based coverage of the shared close-or-expire helper itself (used
        // both by the pre-existing dropped/unproven-forward branches and by finding 1's new call
        // site): when the rollback succeeds, it must return immediately without waiting out the
        // remaining lease, no matter how far in the future that lease still is.
        var agentHost = new CurrentHostBridgeAgentHostClient(
            RunId.New().ToString(), "reflection-success", CoordinatorWebApplicationFactory.OwnerUser);
        var farFutureExpiry = DateTimeOffset.UtcNow.AddSeconds(30);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await InvokeEnsureProvisionalAgentHostScopeClosedAsync(
            agentHost, farFutureExpiry, "grant-success");
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5),
            "a successful rollback must short-circuit the lease wait entirely");
        agentHost.RollbackCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureProvisionalAgentHostScopeClosedAsync_WaitsOutLease_WhenRollbackKeepsFailing()
    {
        // Symmetric coverage of the other branch: when rollback cannot be confirmed, the helper
        // must genuinely block until the API-stamped lease elapses (never return early while the
        // provisional scope could still authorize), using a short custom expiry to keep the test
        // fast and deterministic.
        var agentHost = new CurrentHostBridgeAgentHostClient(
            RunId.New().ToString(), "reflection-wait", CoordinatorWebApplicationFactory.OwnerUser)
        {
            FailRollbackAttempts = int.MaxValue,
        };
        var nearExpiry = DateTimeOffset.UtcNow.AddMilliseconds(300);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await InvokeEnsureProvisionalAgentHostScopeClosedAsync(
            agentHost, nearExpiry, "grant-wait");
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeCloseTo(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(250),
            "the helper must wait out the remaining lease rather than returning as soon as rollback fails");
        agentHost.RollbackCalls.Should().Be(1);
    }

    private static async Task InvokeEnsureProvisionalAgentHostScopeClosedAsync(
        IAgentHostApprovalHttpClient agentHostApprovalClient,
        DateTimeOffset scopeExpiresAt,
        string scopeGrantId)
    {
        var method = typeof(RunEndpoints).GetMethod(
            "EnsureProvisionalAgentHostScopeClosedAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull(
            "the private helper reused by finding 1's fix must still exist under this exact name");

        var task = (Task)method!.Invoke(
            null,
            new object?[]
            {
                "target-run",
                "request-id",
                scopeGrantId,
                scopeExpiresAt,
                new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" },
                agentHostApprovalClient,
                null,
                true,
            })!;
        await task;
    }

    [Fact]
    public async Task PodPerRun_DurableScopeClaimLosesToTerminalizationAndRollsBackCurrentHostBridge()
    {
        var source = RunId.New();
        const string requestId = "pod-terminal-durable-claim";
        var agentHost = new CurrentHostBridgeAgentHostClient(
            source.ToString(),
            requestId,
            CoordinatorWebApplicationFactory.OwnerUser);
        using var baseFactory = new CoordinatorWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:AgentExecutionMode"] = "pod-per-run",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentHostApprovalHttpClient>();
                services.AddSingleton<IAgentHostApprovalHttpClient>(agentHost);
                services.RemoveAll<IAgentHostToolApprovalPersistence>();
                services.AddSingleton<IAgentHostToolApprovalPersistence>(sp =>
                    new TerminalizingAgentHostApprovalPersistence(
                        sp.GetRequiredService<DurableToolApprovalGate>(),
                        sp.GetRequiredService<IRunStore>()));
            });
        });
        using var ownerClient = CreateOwnerClient(factory, CoordinatorWebApplicationFactory.OwnerApiKey);
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var secretStore = factory.Services.GetRequiredService<ISecretStore>();
        await InsertRunAsync(
            runStore,
            source,
            RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await secretStore.SetSecretAsync(
            PreviewRunnerCredential.SecretKey(source.ToString()),
            "pod-approval-credential");

        var response = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{source}/tool-approvals",
            new { request_id = requestId, scope = "run" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await agentHost.InitialTool.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        agentHost.RollbackCalls.Should().Be(1);
        agentHost.IsAutoApproved("web_fetch", "https://following.test").Should().BeFalse();
        approvalGate.IsAutoApproved(source.ToString(), "web_fetch", "https://following.test")
            .Should().BeFalse("a terminal winner cannot leave a durable scope behind");
    }

    [Fact]
    public async Task Approve_ParentOwnerCannotGrantApprovalOwnedByDifferentPersistedChildOwner()
    {
        using var factory = new CoordinatorWebApplicationFactory();
        using var ownerClient = factory.CreateOwnerClient();
        using var otherClient = factory.CreateOtherClient();
        var runStore = factory.Services.GetRequiredService<IRunStore>();
        var approvalGate = factory.Services.GetRequiredService<IToolApprovalGate>();
        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(
            runStore, coordinatorId, RunStatus.InProgress,
            submittingUser: CoordinatorWebApplicationFactory.OwnerUser);
        await InsertRunAsync(
            runStore, childId, RunStatus.InProgress, coordinatorId.ToString(),
            CoordinatorWebApplicationFactory.OtherUser);
        var pending = approvalGate.WaitForApprovalAsync(
            childId.ToString(), "cross-owner-child", "web_fetch", "https://example.test",
            TimeSpan.FromMinutes(1), CancellationToken.None);

        var unauthorized = await ownerClient.PostAsJsonAsync(
            $"/api/runs/{coordinatorId}/tool-approvals",
            new { request_id = "cross-owner-child", scope = "always" });

        unauthorized.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        approvalGate.GetRequestState(childId.ToString(), "cross-owner-child")
            .Should().Be(ToolApprovalRequestState.Pending);

        var authorized = await otherClient.PostAsJsonAsync(
            $"/api/runs/{childId}/tool-approvals",
            new { request_id = "cross-owner-child", scope = "once" });

        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    private static HttpClient CreateAuthenticatedClient(AgentweaverWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }

    private static HttpClient CreateOwnerClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static async Task<ProjectId> CreateProjectAsync(
        CoordinatorWebApplicationFactory workspaceFactory,
        HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Approval scope {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = workspaceFactory.NewWorkingDirectory(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return ProjectId.Parse(
            (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("project_id").GetString()!);
    }

    private static Task InsertRunAsync(
        IRunStore runStore,
        RunId id,
        RunStatus status,
        string? parentRunId = null,
        string? submittingUser = null,
        ProjectId? projectId = null) =>
        runStore.InsertAsync(new Run
        {
            Id = id,
            RepositoryPath = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "tool approval endpoint test",
            SubmittingUser = submittingUser ?? AgentweaverWebApplicationFactory.TestUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = parentRunId,
            AgentName = parentRunId is null ? "Coordinator" : "Researcher",
            SubtaskId = parentRunId is null ? null : "1",
            ProjectId = projectId,
        });

    private sealed class RecordingAgentHostClient(AgentHostApprovalOutcome outcome) : IAgentHostApprovalHttpClient
    {
        public string? LastScope { get; private set; }
        public int DenyCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public int FinalizeCalls { get; private set; }

        public Task<AgentHostApprovalOutcome> GetPendingContextAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct) =>
            Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "pending",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                ToolName: "web_fetch"));

        public Task<AgentHostApprovalOutcome> GrantAsync(
            string childRunId,
            string requestId,
            string scope,
            string? bearer,
            CancellationToken ct)
        {
            LastScope = scope;
            return Task.FromResult(outcome with
            {
                ScopeGrantId = outcome.ScopeGrantId
                    ?? (outcome.Applied && scope != "once" ? "recorded-scope-grant" : null),
            });
        }

        public Task<AgentHostApprovalOutcome> GrantScopedAsync(
            string childRunId,
            string requestId,
            string scope,
            string scopeGrantId,
            DateTimeOffset scopeExpiresAt,
            string? bearer,
            CancellationToken ct)
        {
            LastScope = scope;
            return Task.FromResult(outcome with { ScopeGrantId = scopeGrantId });
        }

        public Task<AgentHostApprovalOutcome> RollbackScopeAsync(
            string childRunId,
            string requestId,
            string scopeGrantId,
            string? bearer,
            CancellationToken ct)
        {
            RollbackCalls++;
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "rolled_back",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                RolledBack: true));
        }

        public Task<AgentHostApprovalOutcome> FinalizeScopeAsync(
            string childRunId,
            string requestId,
            string scopeGrantId,
            string? bearer,
            CancellationToken ct)
        {
            FinalizeCalls++;
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "finalized",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Finalized: true));
        }

        public Task<AgentHostApprovalOutcome> DenyAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct)
        {
            DenyCalls++;
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: true,
                State: "denied",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: true));
        }
    }

    private sealed class FailingAgentHostApprovalPersistence(bool throwOnPersist = false) : IAgentHostToolApprovalPersistence
    {
        public Task<bool> PersistAgentHostApprovalAsync(
            string runId,
            string requestId,
            string toolName,
            string? url,
            ApprovalScope scope)
        {
            if (throwOnPersist)
                throw new InvalidOperationException("durable persistence failure");
            return Task.FromResult(false);
        }
    }

    private sealed class TerminalizingPendingContextAgentHostClient : IAgentHostApprovalHttpClient
    {
        public Func<Task<bool>>? Terminalize { get; set; }
        public int PendingContextCalls { get; private set; }
        public int GrantCalls { get; private set; }

        public async Task<AgentHostApprovalOutcome> GetPendingContextAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct)
        {
            PendingContextCalls++;
            if (Terminalize is not null)
                await Terminalize().ConfigureAwait(false);
            return new AgentHostApprovalOutcome(
                Resolved: false,
                State: "pending",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                ToolName: "web_fetch",
                Url: "https://first.test");
        }

        public Task<AgentHostApprovalOutcome> GrantAsync(
            string childRunId,
            string requestId,
            string scope,
            string? bearer,
            CancellationToken ct)
        {
            GrantCalls++;
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: true,
                State: "approved",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: true,
                ScopeGrantId: "terminal-scope-grant"));
        }

        public Task<AgentHostApprovalOutcome> DenyAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct) =>
            Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "unknown",
                Unreachable: false,
                StatusCode: StatusCodes.Status404NotFound));
    }

    private sealed class OnceWinsAgentHostClient : IAgentHostApprovalHttpClient
    {
        private readonly TaskCompletionSource _onceApplied =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LateScopeForwarded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AgentHostApprovalOutcome> GetPendingContextAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct) =>
            Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "pending",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                ToolName: "web_fetch"));

        public async Task<AgentHostApprovalOutcome> GrantAsync(
            string childRunId,
            string requestId,
            string scope,
            string? bearer,
            CancellationToken ct)
        {
            if (scope == "once")
            {
                _onceApplied.TrySetResult();
                return new AgentHostApprovalOutcome(
                    Resolved: true,
                    State: "approved",
                    Unreachable: false,
                    StatusCode: StatusCodes.Status200OK,
                    Applied: true,
                    ToolName: "web_fetch");
            }

            LateScopeForwarded.TrySetResult();
            await _onceApplied.Task.WaitAsync(ct);
            return new AgentHostApprovalOutcome(
                Resolved: true,
                State: "approved",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: false,
                ToolName: "web_fetch");
        }

        public Task<AgentHostApprovalOutcome> DenyAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct) =>
            Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: true,
                State: "denied",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: true));
    }

    private sealed class CurrentHostBridgeAgentHostClient : IAgentHostApprovalHttpClient
    {
        private readonly string _runId;
        private readonly string _requestId;
        private readonly InMemoryToolApprovalGate _gate;

        public CurrentHostBridgeAgentHostClient(string runId, string requestId, string owner)
        {
            _runId = runId;
            _requestId = requestId;
            _gate = new InMemoryToolApprovalGate(new SingleRunOwnerResolver(runId, owner));
            InitialTool = _gate.WaitForApprovalAsync(
                runId,
                requestId,
                "web_fetch",
                "https://first.test",
                TimeSpan.FromMinutes(1),
                CancellationToken.None);
        }

        public string? LastScope { get; private set; }
        public int RollbackCalls { get; private set; }
        public Task<bool> InitialTool { get; }
        public bool DropScopedGrantResponse { get; set; }
        public Func<bool>? DurablePolicyAuthorizes { get; set; }

        /// <summary>
        /// When greater than zero, the next that many <see cref="RollbackScopeAsync"/> calls
        /// report an unreachable/transport failure (decrementing this count) without touching the
        /// underlying gate, so the provisional scope remains applied. Used to deterministically
        /// simulate an immediate rollback attempt failing before a later retry succeeds (PR #972
        /// finding 1), without any real network flakiness or timing.
        /// </summary>
        public int FailRollbackAttempts { get; set; }

        public bool IsAutoApproved(string toolName, string? url) =>
            DurablePolicyAuthorizes?.Invoke() == true &&
            _gate.IsAutoApproved(_runId, toolName, url);

        public Task<AgentHostApprovalOutcome> GetPendingContextAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct)
        {
            var context = _gate.GetRequestContext(_runId, _requestId);
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "pending",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                ToolName: context?.ToolName,
                Url: context?.Url));
        }

        public Task<AgentHostApprovalOutcome> GrantAsync(
            string childRunId,
            string requestId,
            string scope,
            string? bearer,
            CancellationToken ct) =>
            GrantAsyncCore(scope, scopeGrantId: null, scopeExpiresAt: null);

        public Task<AgentHostApprovalOutcome> GrantScopedAsync(
            string childRunId,
            string requestId,
            string scope,
            string scopeGrantId,
            DateTimeOffset scopeExpiresAt,
            string? bearer,
            CancellationToken ct) =>
            GrantAsyncCore(scope, scopeGrantId, scopeExpiresAt);

        private async Task<AgentHostApprovalOutcome> GrantAsyncCore(
            string scope,
            string? scopeGrantId,
            DateTimeOffset? scopeExpiresAt)
        {
            LastScope = scope;
            var approvalScope = scope switch
            {
                "run" => ApprovalScope.Run,
                "always" => ApprovalScope.Always,
                "tool" => ApprovalScope.Tool,
                _ => ApprovalScope.Once,
            };
            var applied = scopeGrantId is not null && scopeExpiresAt is not null
                ? await _gate.GrantProvisionalScopeAsync(
                    _runId,
                    _requestId,
                    approvalScope,
                    scopeGrantId,
                    scopeExpiresAt.Value)
                : await _gate.GrantAsync(_runId, _requestId, approvalScope);
            if (DropScopedGrantResponse)
                return new AgentHostApprovalOutcome(
                    Resolved: false,
                    State: "unreachable",
                    Unreachable: true,
                    StatusCode: null);

            return new AgentHostApprovalOutcome(
                Resolved: _gate.GetRequestState(_runId, _requestId) == ToolApprovalRequestState.Approved,
                State: "approved",
                Unreachable: false,
                StatusCode: StatusCodes.Status200OK,
                Applied: applied,
                ToolName: "web_fetch",
                Url: "https://first.test",
                ScopeGrantId: applied && scope != "once"
                    ? _gate.GetScopeGrantId(_runId, _requestId)
                    : null);
        }

        public Task<AgentHostApprovalOutcome> RollbackScopeAsync(
            string childRunId,
            string requestId,
            string scopeGrantId,
            string? bearer,
            CancellationToken ct)
        {
            RollbackCalls++;
            if (FailRollbackAttempts > 0)
            {
                FailRollbackAttempts--;
                return Task.FromResult(new AgentHostApprovalOutcome(
                    Resolved: false,
                    State: "unreachable",
                    Unreachable: true,
                    StatusCode: null));
            }

            var rolledBack = _gate.RollbackScopeGrant(childRunId, requestId, scopeGrantId);
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: rolledBack ? "rolled_back" : "scope_not_found",
                Unreachable: false,
                StatusCode: rolledBack ? StatusCodes.Status200OK : StatusCodes.Status409Conflict,
                RolledBack: rolledBack));
        }

        public Task<AgentHostApprovalOutcome> FinalizeScopeAsync(
            string childRunId,
            string requestId,
            string scopeGrantId,
            string? bearer,
            CancellationToken ct)
        {
            var finalized = _gate.FinalizeScopeGrant(childRunId, requestId, scopeGrantId);
            return Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: finalized ? "finalized" : "scope_not_found",
                Unreachable: false,
                StatusCode: finalized ? StatusCodes.Status200OK : StatusCodes.Status409Conflict,
                Finalized: finalized));
        }

        public Task<AgentHostApprovalOutcome> DenyAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct) =>
            Task.FromResult(new AgentHostApprovalOutcome(
                Resolved: false,
                State: "unknown",
                Unreachable: false,
                StatusCode: StatusCodes.Status404NotFound));
    }

    private sealed class BlockingAgentHostApprovalPersistence(
        DurableToolApprovalGate inner) : IAgentHostToolApprovalPersistence
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Committed { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<bool> PersistAgentHostApprovalAsync(
            string runId,
            string requestId,
            string toolName,
            string? url,
            ApprovalScope scope)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            Committed = await inner.PersistAgentHostApprovalAsync(runId, requestId, toolName, url, scope)
                .ConfigureAwait(false);
            return Committed;
        }
    }

    private sealed class TerminalizingAgentHostApprovalPersistence(
        DurableToolApprovalGate inner,
        IRunStore runStore) : IAgentHostToolApprovalPersistence
    {
        public async Task<bool> PersistAgentHostApprovalAsync(
            string runId,
            string requestId,
            string toolName,
            string? url,
            ApprovalScope scope)
        {
            await runStore.TrySetTerminalStatusAsync(
                RunId.Parse(runId),
                RunStatus.Failed,
                DateTimeOffset.UtcNow,
                "terminal-race",
                CancellationToken.None);
            return await inner.PersistAgentHostApprovalAsync(runId, requestId, toolName, url, scope)
                .ConfigureAwait(false);
        }
    }

    private sealed class SingleRunOwnerResolver(string runId, string owner) : IToolApprovalOwnerResolver
    {
        public string? GetCanonicalOwner(string candidateRunId) =>
            string.Equals(candidateRunId, runId, StringComparison.Ordinal) ? owner : null;
    }
}
