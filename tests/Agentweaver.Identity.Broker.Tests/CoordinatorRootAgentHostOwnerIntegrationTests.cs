extern alias OrchestratorHost;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using OrchestratorHost::Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

[Collection("IdentityBrokerPostgres")]
public sealed class CoordinatorRootAgentHostOwnerIntegrationTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task AcceptedCoordinatorRootGenerationUsesPersistedOwnerAdmissionAndCompletes()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var schema = "root_host_" + Guid.NewGuid().ToString("N");
        await CoordinationOwnerMigrator.MigrateAsync(dataSource, schema);

        var seed = RuntimeCopilotSessionTests.Registration();
        var connectionId = Guid.NewGuid();
        var model = new
        {
            reference = seed.Binding.ModelSelectionReference,
            sourceMode = "hostedCopilot",
            connectionId,
            reasoningEffort = seed.Binding.ReasoningEffort
        };
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            projectId = seed.Binding.ProjectId,
            runId = seed.Binding.RunId,
            projectRevision = seed.Binding.ProjectRevision,
            projectConfigurationRevision = seed.Binding.ProjectConfigurationRevision,
            platformRuntimeRevision = seed.Binding.PlatformRuntimeRevision,
            contextRevision = seed.Binding.ContextRevision,
            modelSelection = model,
            runLimits = new { maxModelTurns = 1, maxToolCalls = 1 },
            projectConfiguration = new { modelSelection = model }
        });
        var selectionHash = RuntimeContractValidation.Hash(
            Encoding.UTF8.GetBytes(snapshot.GetRawText()));
        var binding = seed.Binding with
        {
            AgentId = RuntimeActionContract.CoordinatorRootStepId,
            AcceptedSelectionHash = selectionHash,
            ConfigureEndpoint = new("https://runtime.test/runtime/v1/configure"),
            PlacementProviderId = "sandbox-test",
            EnvironmentLifecycleGeneration = 1,
            EnvironmentLeaseRevision = 1,
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1,
            Image = new("sha256:" + new string('a', 64), "linux/amd64", 123456789),
            ModelConnectionId = connectionId,
            ModelConnectionScope = ProjectAuthorityResourceType.Project,
            WorkflowStepId = null,
            MaxModelTurns = 1,
            MaxToolCalls = 1
        };
        var authority = new ProjectsAuthorizationContext(
            1, binding.ActorIssuer, binding.ActorId, binding.TenantId, 1,
            binding.ProjectId, binding.RunId,
            [new("project", binding.ProjectId, [new("acceptRunSelection", 1)])]);
        var selection = new AuthorizedRunSelection(
            new(binding.ProjectId, binding.RunId, binding.ProjectRevision,
                binding.ProjectConfigurationRevision, binding.PlatformRuntimeRevision,
                binding.ContextRevision, snapshot),
            authority);
        var actor = new CoordinationActor(binding.ActorIssuer, binding.ActorId);
        var root = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
        var coordination = new CoordinationOwnerStore(dataSource, schema);
        var accepted = await coordination.AcceptRootAsync(actor, selection, binding.SessionId, default);
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var selectionContexts = new CoordinatorRunSelectionContextStore(
            dataSource, schema, catalog, new ProviderResolver(catalog), []);
        var decisions = new CoordinatorDecisionOwnerStore(dataSource, schema, selectionContexts);
        var decision = await decisions.InitializeRootAsync(actor, root, selection, default);
        var registration = await new RuntimeRegistrationStore(
            dataSource, schema, TimeProvider.System).RegisterAsync(
                binding with { ExecutionFence = accepted.ExecutionFence },
                DateTimeOffset.UtcNow.AddMinutes(4), default);

        await using var hostFixture = new RuntimeAgentHostTests.HostFixture(
            coordinatorRoot: true, maxModelTurns: 1, maxToolCalls: 1,
            registrationOverride: registration);
        var options = new OrchestratorOptions(
            binding.ActorIssuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", schema);
        using var projectsHttp = new HttpClient(new AcceptedSelectionHandler(selection));
        var projects = new ProjectsRunSelectionClient(
            projectsHttp, options, new ReviewedRemoteToolSnapshotStore(dataSource, schema));
        var ownerContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", binding.ActorId),
                new Claim("aud", options.Audience),
                new Claim("aud", options.ProjectsAudience),
                new Claim("project_id", binding.ProjectId),
                new Claim("run_id", binding.RunId),
                new Claim("scope", "api.read projects.orchestrator")
            ], "owner-test"))
        };
        ownerContext.Request.Headers.Authorization = $"Bearer {hostFixture.Token}";
        ownerContext.Request.Headers["X-Agentweaver-Tenant"] = binding.TenantId;
        var grantStore = new ExecutableActionGrantOwnerStore(
            dataSource, options, TimeProvider.System, projects,
            new HttpContextAccessor { HttpContext = ownerContext });
        var ownerSelection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            ownerContext, binding.ProjectId, binding.RunId, default);
        Assert.Equal(binding.AcceptedSelectionHash,
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                ownerSelection.Selection.Snapshot.GetRawText())));
        Assert.Equal(binding.ActorIssuer, ownerSelection.Authorization.Issuer);
        Assert.Equal(binding.ActorId, ownerSelection.Authorization.ActorId);
        Assert.Equal(binding.TenantId, ownerSelection.Authorization.TenantId);
        Assert.Equal(binding.ProjectRevision, ownerSelection.Selection.ProjectRevision);
        Assert.Equal(binding.ProjectConfigurationRevision,
            ownerSelection.Selection.ProjectConfigurationRevision);
        Assert.Equal(binding.PlatformRuntimeRevision,
            ownerSelection.Selection.PlatformRuntimeRevision);
        Assert.Equal(binding.ContextRevision, ownerSelection.Selection.ContextRevision);
        var prompt = "Generate an unconfirmed casting proposal.";
        hostFixture.Sdk.ExpectedPrompt = prompt;
        var requestId = Guid.NewGuid();
        var promptHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt));
        await grantStore.AdmitCoordinatorRootModelTurnAsync(
            ownerContext, registration, requestId, promptHash, decision.StateVersion, default);
        hostFixture.ActionGrantOwner = async (action, token) =>
        {
            var current = await new RuntimeRegistrationStore(
                dataSource, schema, TimeProvider.System).ReadAsync(
                    registration.RuntimeInstanceId, token)
                ?? throw new InvalidOperationException("The registered coordinator root disappeared.");
            Assert.Equal(registration, current);
            await grantStore.IssueRuntimeActionGrantAsync(
                ownerContext, current, action, token);
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readiness = await hostFixture.ConfigureAsync(timeout.Token);
        var message = hostFixture.Message(
            hostFixture.Proof(readiness), text: prompt, messageId: requestId);
        using var server = await hostFixture.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = registration.Binding.ConfigureEndpoint;
        client.DefaultRequestHeaders.Authorization = new("Bearer", hostFixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", binding.TenantId);
        using var response = await client.PostAsJsonAsync(
            "/runtime/v1/a2a/message:send", message, RuntimeAgentHostTests.HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<RuntimeA2AResponse>(
            RuntimeAgentHostTests.HostFixture.Json, timeout.Token))!;

        Assert.Equal(hostFixture.Sdk.AssistantResponse, Assert.Single(result.Parts).Text);
        var action = Assert.Single(hostFixture.ModelTurnActions);
        Assert.Equal(requestId, action.EventId);
        Assert.Equal(requestId, action.DispatchId);
        Assert.Equal("model.turn", action.ActionId);
        Assert.Equal(1, await CountRootModelGrantsAsync(dataSource, schema, binding));
        Assert.Single(hostFixture.Sdk.Requests, request => request.Method == "session.send");
    }

    private static async Task<long> CountRootModelGrantsAsync(
        NpgsqlDataSource dataSource, string schema, RuntimeBinding binding)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand($"""
            SELECT count(*) FROM "{schema}".executable_action_grants
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND action_ids @> '["model.turn"]'::jsonb
            """, connection);
        count.Parameters.AddWithValue("project", binding.ProjectId);
        count.Parameters.AddWithValue("run", binding.RunId);
        count.Parameters.AddWithValue("session", binding.SessionId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private sealed class AcceptedSelectionHandler(AuthorizedRunSelection selection) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/authorization/context")
                body = selection.Authorization;
            else if (path.EndsWith("/runs/" + selection.Selection.RunId + "/selection", StringComparison.Ordinal))
                body = selection.Selection.Snapshot;
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body, options: RuntimeAgentHostTests.HostFixture.Json)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
