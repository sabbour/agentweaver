using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Tests.Auth;

public sealed class AiExecutionContextEndpointsTests
{
    [Fact]
    public async Task Run_call_guard_requires_durable_evidence_and_rejects_a_replaced_provider()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = ".", OriginatingBranch = "main",
            ModelSource = ModelSource.Byok, Task = "guarded turn", SubmittingUser = "operator",
            Status = RunStatus.InProgress, StartedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<IRunStore>().InsertAsync(run);
        var guard = services.GetRequiredService<RunModelInvocationGuard>();
        var invoke = () => guard.ValidateAsync(run.Id.ToString(), CancellationToken.None);
        (await invoke.Should().ThrowAsync<AgentProviderException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");

        var resolver = services.GetRequiredService<EffectiveModelProviderResolver>();
        var provider = await resolver.ResolveAsync(null, CancellationToken.None);
        var events = services.GetRequiredService<IRunEventStream>();
        await events.AppendAsync(run.Id.ToString(), new RunEvent(
            0, EventTypes.RunModelProviderResolved,
            provider.ToProvenancePayload(run.Id.ToString(), null, EffectiveModelProviderProvenance.ScopeProject)));
        await invoke();
        (await events.GetPersistedEventsAsync(run.Id.ToString(), 0)).Should().HaveCount(2);

        var copilotOnly = () => guard.PrepareAsync(run.Id.ToString(), CancellationToken.None, supportsByok: false);
        (await copilotOnly.Should().ThrowAsync<AgentProviderException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");
        await SeedByokProviderAsync(factory);
        (await invoke.Should().ThrowAsync<AgentProviderException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");
        (await events.GetPersistedEventsAsync(run.Id.ToString(), 0)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Assistant_preflight_reports_platform_unavailable_without_inventing_model_source()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var client = AuthedClient(factory);

        var response = await client.PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "assistant_turn" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>();
        body.Should().NotBeNull();
        body!.AiRequired.Should().BeTrue();
        body.Operation.Should().Be("assistant_turn");
        body.Phase.Should().Be("prepared");
        body.ExecutionKey.Should().BeNull();
        body.ExpiresAt.Should().BeNull();
        body.EffectiveModelProvider.Should().NotBeNull();
        body.EffectiveModelProvider!.State.Should().Be("unavailable");
        body.EffectiveModelProvider.ProviderKind.Should().Be("unavailable");
        body.EffectiveModelProvider.ResolutionScope.Should().Be("platform");
        body.EffectiveModelProvider.ProviderScope.Should().Be("none");
        body.EffectiveModelProvider.ProviderKey.Should().BeNull();
        body.EffectiveModelProvider.UnavailableReason.Should().Be("no_provider");
    }

    [Fact]
    public async Task Project_generation_preflight_distinguishes_project_resolution_from_platform_byok()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        var client = AuthedClient(factory);
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"provider-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var projectResponse = await client.PostAsJsonAsync("/api/projects", new
            {
                name = "Provider context project",
                origin = "blank",
                working_directory = workingDirectory,
            });
            projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("project_id").GetString();

            var response = await client.PostAsJsonAsync(
                "/api/ai/execution-context",
                new { operation = "workflow_generation", project_id = projectId });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>();
            var provider = body!.EffectiveModelProvider!;
            provider.State.Should().Be("resolved");
            provider.ProviderKind.Should().Be("byok");
            provider.ResolutionScope.Should().Be("project");
            provider.ProviderScope.Should().Be("platform");
            provider.ProviderType.Should().Be("azure");
            provider.ProviderKey.Should().NotBeNullOrWhiteSpace();
            provider.ProviderKey.Should().MatchRegex("^[a-f0-9]{64}$");
            body.ExecutionKey.Should().NotBeNullOrWhiteSpace();
            body.ExecutionKey.Should().NotBe(provider.ProviderKey);
            body.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Project_operation_requires_project_id()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var response = await AuthedClient(factory).PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "casting_generation" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_id_required");
    }

    [Fact]
    public async Task Unknown_operation_is_rejected()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var response = await AuthedClient(factory).PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "make_magic" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_ai_operation");
    }

    [Fact]
    public void Operation_catalog_matches_blueprint_and_assistant_endpoint_authentication()
    {
        AiOperationCatalog.TryGet("blueprint_generation", out var blueprint).Should().BeTrue();
        blueprint.RequiresPlatformRole.Should().BeTrue();
        blueprint.AllowsBroker.Should().BeTrue(
            "projectless blueprint generation is exposed by the PlatformOrMcp endpoint group");

        AiOperationCatalog.TryGet("assistant_turn", out var assistant).Should().BeTrue();
        assistant.RequiresPlatformRole.Should().BeTrue();
        assistant.AllowsBroker.Should().BeFalse(
            "Assistant endpoints require a platform principal rather than an MCP broker token");
    }

    [Theory]
    [InlineData("orchestration", ProjectRole.Contributor)]
    [InlineData("workflow_generation", ProjectRole.Owner)]
    [InlineData("skill_generation", ProjectRole.Contributor)]
    [InlineData("casting_generation", ProjectRole.Contributor)]
    [InlineData("backlog_decomposition", ProjectRole.Contributor)]
    [InlineData("marketplace_catalog_classification", ProjectRole.Viewer)]
    public void Project_operations_keep_their_endpoint_specific_minimum_role(
        string operationName,
        ProjectRole expectedRole)
    {
        AiOperationCatalog.TryGet(operationName, out var operation).Should().BeTrue();

        operation.ResolutionMode.Should().Be(AiResolutionMode.RequiredProject);
        operation.MinimumProjectRole.Should().Be(expectedRole);
        operation.RequiresPlatformRole.Should().BeFalse();
    }

    [Fact]
    public void User_session_preflight_requires_an_explicit_entra_identity()
    {
        AiOperationCatalog.TryGet("user_session", out var operation).Should().BeTrue();

        AiExecutionContextEndpoints.HasRequiredCallerIdentity(
            operation,
            new CallerContext { User = "api-key-caller" }).Should().BeFalse();
        AiExecutionContextEndpoints.HasRequiredCallerIdentity(
            operation,
            new CallerContext
            {
                User = "entra-caller",
                EntraObjectId = "entra-caller",
            }).Should().BeTrue();
    }

    [Fact]
    public async Task Agent_turn_preflight_accepts_an_owned_non_project_run()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = "C:\\repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.Byok,
            Task = "Continue a legacy non-project run.",
            SubmittingUser = AgentweaverWebApplicationFactory.TestUser,
            Status = RunStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "worker",
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRunStore>()
                .InsertAsync(run, CancellationToken.None);
        }

        var response = await AuthedClient(factory).PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "agent_turn", run_id = run.Id.ToString() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>();
        body!.EffectiveModelProvider!.ResolutionScope.Should().Be("platform");
        body.ExecutionKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Assistant_invocation_returns_conflict_when_provider_key_is_missing()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var response = await AuthedClient(factory).PostAsJsonAsync(
            "/api/assistant/runs",
            new { message = "Invoke the assistant now." });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("ai_execution_context_required");
    }

    [Fact]
    public async Task Assistant_invocation_rejects_a_tampered_provider_key_before_model_execution()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        var client = AuthedClient(factory);
        var preflight = await client.PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "assistant_turn" });
        var context = await preflight.Content.ReadFromJsonAsync<AiExecutionContextResponse>();
        client.DefaultRequestHeaders.Add(
            AiExecutionPlanHeaders.ProviderKey,
            $"{context!.ExecutionKey}tampered");

        var response = await client.PostAsJsonAsync(
            "/api/assistant/runs",
            new { message = "Invoke the assistant now." });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("model_provider_changed");
        body.GetProperty("context").GetProperty("phase").GetString().Should().Be("prepared");
    }

    [Fact]
    public async Task Copilot_only_operation_reports_unavailable_when_byok_is_active()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AiExecutionPlanService>();
        AiOperationCatalog.TryGet("orchestration", out var operation).Should().BeTrue();

        var plan = await service.PrepareAsync(
            operation,
            ProjectId.New(),
            new CallerContext { User = "user-1" },
            CancellationToken.None);

        plan.Provider.Should().BeOfType<EffectiveModelProviderResult.Unavailable>()
            .Which.UnavailableReason.Should().Be(
                EffectiveModelProviderUnavailableReason.OperationRequiresGitHubCopilot);
        plan.ProviderKey.Should().BeNull();
    }

    [Fact]
    public async Task Accepted_plan_rejects_expiry_wrong_binding_and_provider_switch()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero));
        var service = new AiExecutionPlanService(resolver, configuration, clock);
        AiOperationCatalog.TryGet("assistant_turn", out var operation).Should().BeTrue();
        AiOperationCatalog.TryGet("blueprint_generation", out var otherOperation).Should().BeTrue();
        var caller = new CallerContext { User = "user-1" };
        var prepared = await service.PrepareAsync(operation, null, caller, CancellationToken.None);
        var queuedProviderKey = service.CreateQueuedProviderKey(prepared);

        var missing = () => service.AcceptAsync(
            null,
            operation,
            null,
            caller,
            CancellationToken.None);
        var missingException = (await missing.Should().ThrowAsync<AiExecutionPlanException>()).Which;
        missingException.ErrorCode.Should().Be("ai_execution_context_required");
        missingException.StatusCode.Should().Be((int)HttpStatusCode.Conflict);

        var invalid = () => service.AcceptAsync(
            $"{prepared.ProviderKey}tampered",
            operation,
            null,
            caller,
            CancellationToken.None);
        (await invalid.Should().ThrowAsync<AiExecutionPlanException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");

        (await service.AcceptAsync(
            prepared.ProviderKey,
            operation,
            null,
            caller,
            CancellationToken.None)).ProviderKey.Should().Be(prepared.ProviderKey);

        var wrongCaller = () => service.AcceptAsync(
            prepared.ProviderKey,
            operation,
            null,
            new CallerContext { User = "user-2" },
            CancellationToken.None);
        (await wrongCaller.Should().ThrowAsync<AiExecutionPlanException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");

        var wrongOperation = () => service.AcceptAsync(
            prepared.ProviderKey,
            otherOperation,
            null,
            caller,
            CancellationToken.None);
        (await wrongOperation.Should().ThrowAsync<AiExecutionPlanException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");

        clock.Advance(TimeSpan.FromDays(8));
        (await service.RevalidateAcceptedAsync(prepared, CancellationToken.None))
            .ProviderKey.Should().Be(prepared.ProviderKey,
                "expiry fences initial acceptance, not an already-running multi-pass operation");
        var expired = () => service.AcceptAsync(
            prepared.ProviderKey,
            operation,
            null,
            caller,
            CancellationToken.None);
        (await expired.Should().ThrowAsync<AiExecutionPlanException>())
            .Which.ErrorCode.Should().Be("ai_execution_context_expired");
        var expiredQueued = () => service.RestoreAcceptedAsync(
            prepared.ProviderKey!,
            operation,
            null,
            caller.User,
            CancellationToken.None);
        (await expiredQueued.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("persisted AI execution plan");
        (await service.RestoreAcceptedAsync(
            queuedProviderKey,
            operation,
            null,
            caller.User,
            CancellationToken.None)).ProviderKey.Should().Be(queuedProviderKey);

        clock.Advance(TimeSpan.FromDays(-8));
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        var current = await settings.GetAsync(CancellationToken.None);
        await settings.UpdateAsync(
            current!.Id,
            current with { BaseUrl = "https://replacement.example.test" },
            CancellationToken.None);
        var switched = () => service.AcceptAsync(
            prepared.ProviderKey,
            operation,
            null,
            caller,
            CancellationToken.None);
        (await switched.Should().ThrowAsync<AiExecutionPlanException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");
    }

    [Fact]
    public async Task Production_requires_an_independent_provider_key_signing_secret()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>();
        var production = new StubHostEnvironment(Environments.Production);
        var authOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:ApiKey"] = "client-visible-api-key",
        }).Build();

        var missingSecret = () => new AiExecutionPlanService(resolver, authOnly, production);
        missingSecret.Should().Throw<InvalidOperationException>()
            .WithMessage("*AiExecution:ProviderKeySigningKey*");

        var independentSecret = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Keys:automation:Token"] = "client-visible-api-key",
            ["AiExecution:ProviderKeySigningKey"] = "independent-server-only-signing-secret",
        }).Build();
        var service = new AiExecutionPlanService(resolver, independentSecret, production);
        AiOperationCatalog.TryGet("assistant_turn", out var operation).Should().BeTrue();

        var plan = await service.PrepareAsync(
            operation,
            projectId: null,
            new CallerContext { User = "automation" },
            CancellationToken.None);

        plan.ProviderKey.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Auth:CopilotApp:ClientSecret")]
    [InlineData("Auth:RepoApp:ClientSecret")]
    public async Task Production_can_use_an_already_provisioned_server_only_app_secret(string setting)
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [setting] = "existing-server-only-app-secret" }).Build();
        var environment = new StubHostEnvironment(Environments.Production);
        var issuer = new AiExecutionPlanService(resolver, configuration, environment);
        var receiver = new AiExecutionPlanService(resolver, configuration, environment);
        AiOperationCatalog.TryGet("assistant_turn", out var operation).Should().BeTrue();
        var caller = new CallerContext { User = "operator" };
        var prepared = await issuer.PrepareAsync(operation, null, caller, CancellationToken.None);
        var accepted = await receiver.AcceptAsync(
            prepared.ProviderKey, operation, null, caller, CancellationToken.None);
        accepted.Provider.Should().Be(prepared.Provider);
    }

    [Fact]
    public async Task User_session_plan_preparation_rejects_a_fallback_display_identity()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var service = new AiExecutionPlanService(
            scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiExecution:ProviderKeySigningKey"] = "test-provider-signing-key",
                })
                .Build());
        AiOperationCatalog.TryGet("user_session", out var operation).Should().BeTrue();

        var act = () => service.PrepareAsync(
            operation,
            projectId: null,
            new CallerContext { User = "display-name-only" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit Entra object identity*");
    }

    [Fact]
    public void Accepted_plan_keeps_the_first_byok_configuration_for_multi_pass_generation()
    {
        var accessor = new AiExecutionPlanAccessor();
        var provider = new EffectiveModelProviderResult.Byok(
            "provider-1",
            "azure",
            "configuration-fingerprint");
        var plan = new AiExecutionPlan(
            "workflow_generation",
            ProjectId.New(),
            "user-1",
            EffectiveModelProviderProvenance.ScopeProject,
            provider,
            "opaque-provider-key",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var first = new ByokProviderConfiguration(
            "provider-1",
            "Azure",
            "azure",
            "https://first.example.test",
            "gpt-5",
            "first-key");
        var later = first with
        {
            BaseUrl = "https://later.example.test",
            ApiKey = "later-key",
        };

        using var scope = accessor.Push(plan);
        accessor.FreezeByokConfiguration(first);
        var conflictingFreeze = () => accessor.FreezeByokConfiguration(later);
        conflictingFreeze.Should().Throw<InvalidOperationException>()
            .WithMessage("*already frozen*");
        accessor.FrozenByokConfiguration.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Disposed_plan_scope_is_not_visible_to_a_captured_background_context()
    {
        var accessor = new AiExecutionPlanAccessor();
        var plan = new AiExecutionPlan(
            "agent_turn",
            ProjectId.New(),
            "user-1",
            EffectiveModelProviderProvenance.ScopeProject,
            new EffectiveModelProviderResult.Byok("provider-1", "azure", "configuration-fingerprint"),
            "opaque-provider-key",
            DateTimeOffset.UtcNow.AddMinutes(5));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AiExecutionPlan?> captured;

        using (accessor.Push(plan))
        {
            captured = Task.Run(async () =>
            {
                await release.Task;
                return accessor.Current;
            });
        }
        release.SetResult();

        (await captured).Should().BeNull();
    }

    [Fact]
    public async Task Accepted_plan_lease_is_activated_in_the_callers_async_context()
    {
        var accessor = new AiExecutionPlanAccessor();
        var plan = new AiExecutionPlan(
            "agent_turn",
            ProjectId.New(),
            "user-1",
            EffectiveModelProviderProvenance.ScopeProject,
            new EffectiveModelProviderResult.Byok("provider-1", "azure", "configuration-fingerprint"),
            "opaque-provider-key",
            DateTimeOffset.UtcNow.AddMinutes(5));

        using var lease = await CreateLeaseAfterAsyncBoundaryAsync(accessor, plan);
        accessor.Current.Should().BeNull();

        lease.Activate();

        accessor.Current.Should().BeSameAs(plan);
    }

    private static async Task<EndpointHelpers.AiExecutionLease> CreateLeaseAfterAsyncBoundaryAsync(
        AiExecutionPlanAccessor accessor,
        AiExecutionPlan plan)
    {
        await Task.Yield();
        return new EndpointHelpers.AiExecutionLease(plan, accessor, null);
    }

    private static async Task SeedByokProviderAsync(AgentweaverWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        var created = await settings.AddAsync(
            new ByokProviderConfiguration(
                Id: string.Empty,
                Name: "Test Azure provider",
                Type: "azure",
                BaseUrl: "https://provider.example.test",
                Model: "gpt-4.1",
                ApiKey: "test-key"),
            CancellationToken.None);
        await settings.SetActiveAsync(created.Id, CancellationToken.None);
    }

    private static HttpClient AuthedClient(AgentweaverWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Agentweaver.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
