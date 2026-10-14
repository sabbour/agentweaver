using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentLifecycleManagerTests
{
    private const string ProjectId = "project-a";
    private const string RunId = "run-a";
    private const string TenantId = "tenant-a";
    private const string IdempotencyKey = "create-environment";

    [Fact]
    public void EnvironmentLifecycleAndProviderReadRoutesAreAuthenticated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<EnvironmentLifecycleManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentEgressManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentRuntimePlacementReader>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentRuntimeBootstrapProfileRegistry>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IEnvironmentRuntimeBootstrapProfileReplacementStore>(
            _ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentWorkspaceVolumeManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentSandboxManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentSandboxBuildTestCommandManager>(
            _ => throw new NotSupportedException());
        builder.Services.AddSingleton(TimeProvider.System);
        using var app = builder.Build();
        app.MapEnvironmentEndpoints();

        var expectedMethods = new Dictionary<string, string[]>
        {
            ["/api/projects/{projectId}/runs/{runId}/environments"] = ["POST"],
            ["/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/release"] = ["POST"],
            ["/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/lifecycle"] = ["GET"],
            ["/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/operations/{operationId:guid}/provider-attempts"] = ["GET"],
            ["/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/v1/late-resource-cleanups"] = ["GET"],
        };
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(route => expectedMethods.ContainsKey(route.RoutePattern.RawText?.TrimEnd('/') ?? string.Empty))
            .ToArray();

        Assert.Equal(expectedMethods.Count, endpoints.Length);
        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText!.TrimEnd('/');
            Assert.Equal(expectedMethods[route], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        }
    }

    [Fact]
    public void RegistrationAndReleaseRequestsRejectExtraJsonProperties()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentRegistrationApiRequest>(
            """{"idempotencyKey":"create","tenantId":"caller-controlled"}""", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentReleaseApiRequest>(
            """{"expectedLifecycleGeneration":1,"idempotencyKey":"release","resourceId":"caller-controlled"}""",
            options));
    }

    [Fact]
    public async Task RegistrationRechecksExactSelectionBeforeWriteAndIdempotentReplay()
    {
        var accepted = EnvironmentEgressManagerTests.Selection();
        var changed = accepted with { ContextRevision = "signed-selection-revision-2" };
        var projects = new LifecycleProjectsConfigClient(
            [AuthorizationContext()],
            accepted, accepted, accepted, changed, accepted, accepted, accepted, accepted);
        var producer = new CapturingLifecycleProducer();
        var manager = CreateManager(projects, producer);

        var first = await manager.RegisterAsync(
            Caller(), ProjectId, RunId, new(IdempotencyKey), CancellationToken.None);
        var changedReplay = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.RegisterAsync(
                Caller(), ProjectId, RunId, new(IdempotencyKey), CancellationToken.None));
        Assert.Equal("run_selection_changed", changedReplay.Code);
        var replay = await manager.RegisterAsync(
            Caller(), ProjectId, RunId, new(IdempotencyKey), CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(
            EnvironmentLifecycleManager.CreateEnvironmentId(TenantId, ProjectId, RunId, IdempotencyKey),
            first.Snapshot.Fence.Owner.EnvironmentId);
        Assert.Equal(first.Snapshot, replay.Snapshot);
        Assert.Equal(3, producer.RegisterCalls);
        Assert.Equal(first.Snapshot.Fence.Owner, producer.LastOwner);

        var released = await manager.ReleaseAsync(
            Caller(),
            ProjectId,
            RunId,
            first.Snapshot.Fence.Owner.EnvironmentId,
            new(first.Snapshot.Fence.LifecycleGeneration, "release-environment"),
            CancellationToken.None);
        Assert.Equal(EnvironmentLifecycleState.Released, released.Snapshot.State);
        Assert.Equal(first.Snapshot.Fence.LifecycleGeneration, producer.LastExpectedFence!.LifecycleGeneration);
        Assert.Equal(EnvironmentLifecycleState.Released, producer.LastTargetState);
        Assert.Equal("release-environment", producer.LastTransitionKey);
    }

    [Fact]
    public async Task RegistrationRequiresSeparateReadRunSelectionPermission()
    {
        var projects = new LifecycleProjectsConfigClient(
            [AuthorizationContext(canReadSelection: false)],
            EnvironmentEgressManagerTests.Selection());
        var producer = new CapturingLifecycleProducer();
        var manager = CreateManager(projects, producer);

        var denied = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.RegisterAsync(
                Caller(), ProjectId, RunId, new(IdempotencyKey), CancellationToken.None));

        Assert.Equal("run_selection_not_authorized", denied.Code);
        Assert.Equal(0, producer.RegisterCalls);
    }

    [Fact]
    public async Task RegistrationRejectsAuthorityChangeAtCommitBoundary()
    {
        var original = AuthorizationContext();
        var changed = original with { MembershipRevision = original.MembershipRevision + 1 };
        var projects = new LifecycleProjectsConfigClient(
            [original, original, original, original, changed],
            EnvironmentEgressManagerTests.Selection(),
            EnvironmentEgressManagerTests.Selection());
        var producer = new CapturingLifecycleProducer();
        var manager = CreateManager(projects, producer);

        var denied = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.RegisterAsync(
                Caller(), ProjectId, RunId, new(IdempotencyKey), CancellationToken.None));

        Assert.Equal("authorization_changed", denied.Code);
        Assert.Equal(1, producer.RegisterCalls);
    }

    private static EnvironmentLifecycleManager CreateManager(
        IProjectsConfigClient projects,
        CapturingLifecycleProducer producer)
    {
        var options = EnvironmentEgressManagerTests.Options();
        var egress = new EnvironmentEgressManager(
            projects,
            new CiliumEgressPolicyAdapter(
                new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(),
                options),
            options,
            new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore());
        return new(egress, producer);
    }

    private static ProjectAuthorizationContextResponse AuthorizationContext(bool canReadSelection = true)
    {
        var permissions = ImmutableArray.CreateBuilder<ProjectAuthorizationPermissionGrant>();
        permissions.Add(new(ProjectAuthorizationPermission.WriteProjects, 6));
        if (canReadSelection)
            permissions.Add(new(ProjectAuthorizationPermission.ReadRunSelection, 5));
        return new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://identity.example",
            "actor-a",
            TenantId,
            4,
            null,
            null,
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    ProjectId,
                    permissions.ToImmutable())
            ]);
    }

    private static CurrentCallerRequest Caller() => new("validated.jwt.token", TenantId);

    private sealed class LifecycleProjectsConfigClient(
        IEnumerable<ProjectAuthorizationContextResponse> authorizations,
        params EffectiveNetworkPolicySelection[] selections) : IProjectsConfigClient
    {
        private readonly Queue<ProjectAuthorizationContextResponse> _authorizations = new(authorizations);
        private readonly Queue<EffectiveNetworkPolicySelection> _selections = new(selections);
        private ProjectAuthorizationContextResponse? _lastAuthorization;
        private EffectiveNetworkPolicySelection? _lastSelection;

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            if (_authorizations.TryDequeue(out var next))
                _lastAuthorization = next;
            return Task.FromResult(_lastAuthorization ??
                throw new InvalidOperationException("At least one authorization context is required."));
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            if (_selections.TryDequeue(out var next))
                _lastSelection = next;
            return Task.FromResult(_lastSelection ??
                throw new InvalidOperationException("At least one run selection is required."));
        }
    }

    private sealed class CapturingLifecycleProducer : IEnvironmentLifecycleProducer
    {
        private EnvironmentLifecycleTransitionResult? _registration;
        private string? _registrationKey;

        public int RegisterCalls { get; private set; }
        public EnvironmentOwnerIdentity? LastOwner { get; private set; }
        public EnvironmentGenerationFence? LastExpectedFence { get; private set; }
        public EnvironmentLifecycleState? LastTargetState { get; private set; }
        public string? LastTransitionKey { get; private set; }

        public async Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
            EnvironmentOwnerIdentity owner,
            string idempotencyKey,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null)
        {
            RegisterCalls++;
            LastOwner = owner;
            if (beforeCommitCheck is not null)
                await beforeCommitCheck(cancellationToken).ConfigureAwait(false);
            if (_registration is not null)
            {
                if (_registration.Snapshot.Fence.Owner != owner || _registrationKey != idempotencyKey)
                    throw new InvalidOperationException("The registration key was reused for another owner.");
                return new(_registration.Snapshot, Replayed: true);
            }

            _registrationKey = idempotencyKey;
            _registration = new(
                new EnvironmentLifecycleSnapshot(
                    new EnvironmentGenerationFence(owner, 1),
                    EnvironmentLifecycleState.Active),
                Replayed: false);
            return _registration;
        }

        public async Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
            EnvironmentGenerationFence expectedFence,
            EnvironmentLifecycleState targetState,
            string idempotencyKey,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null)
        {
            LastExpectedFence = expectedFence;
            LastTargetState = targetState;
            LastTransitionKey = idempotencyKey;
            if (beforeCommitCheck is not null)
                await beforeCommitCheck(cancellationToken).ConfigureAwait(false);
            return new(
                new EnvironmentLifecycleSnapshot(
                    new EnvironmentGenerationFence(expectedFence.Owner, expectedFence.LifecycleGeneration + 1),
                    targetState),
                Replayed: false);
        }
    }
}
