using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Encodings.Web;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentP1InvocationBindingTests
{
    [Fact]
    public async Task MissingCoreBindingRejectsRegistrationBeforeAnyEnvironmentWrite()
    {
        var projects = new ProjectsConfigClient();
        var producer = new LifecycleProducer();
        var bindingReader = new MissingBindingReader();
        var options = EnvironmentEgressManagerTests.Options();
        var egress = new EnvironmentEgressManager(
            projects,
            new CiliumEgressPolicyAdapter(
                new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(),
                options),
            options,
            new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore());
        var manager = new EnvironmentLifecycleManager(egress, producer, bindingReader);
        var credential = new SecretCredential(
            "run-bound.jwt.token",
            DateTimeOffset.UtcNow.AddMinutes(2),
            TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, "tenant-a");

        try
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                manager.RegisterAsync(
                    new CurrentCallerRequest("run-bound.jwt.token", "tenant-a"),
                    "project-a",
                    "run-a",
                    new EnvironmentRegistrationApiRequest(
                        "p1-registration",
                        new P1InvocationBindingExpectation(
                            Guid.NewGuid(),
                            Guid.NewGuid(),
                            new string('a', 64),
                            "root-session-a",
                            new string('b', 64),
                            3)),
                    CancellationToken.None,
                    actor));

            Assert.Equal("p1_invocation_binding_missing", failure.Code);
            Assert.Equal(1, bindingReader.ReadCount);
            Assert.Equal(0, producer.WriteCount);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("tenant")]
    public async Task StaleOrForeignCoreBindingRejectsRegistrationBeforeAnyEnvironmentWrite(string mismatch)
    {
        var projects = new ProjectsConfigClient();
        var producer = new LifecycleProducer();
        var coreInvocationId = Guid.NewGuid();
        var setupInvocationId = Guid.NewGuid();
        var setupReceiptHash = new string('a', 64);
        var selectionHash = new string('b', 64);
        var expectation = new P1InvocationBindingExpectation(
            coreInvocationId,
            setupInvocationId,
            setupReceiptHash,
            "root-session-a",
            selectionHash,
            3);
        var binding = new P1InvocationBinding(
            1,
            mismatch == "invocation" ? Guid.NewGuid() : coreInvocationId,
            setupInvocationId,
            setupReceiptHash,
            mismatch == "tenant" ? "tenant-foreign" : "tenant-a",
            "project-a",
            "run-a",
            "root-session-a",
            "https://identity.example",
            "actor-a",
            Guid.NewGuid(),
            1,
            selectionHash,
            3,
            1,
            DateTimeOffset.UtcNow);
        var bindingReader = new FixedBindingReader(binding);
        var options = EnvironmentEgressManagerTests.Options();
        var egress = new EnvironmentEgressManager(
            projects,
            new CiliumEgressPolicyAdapter(
                new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(),
                options),
            options,
            new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore());
        var manager = new EnvironmentLifecycleManager(egress, producer, bindingReader);
        var credential = new SecretCredential(
            "run-bound.jwt.token",
            DateTimeOffset.UtcNow.AddMinutes(2),
            TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, "tenant-a");

        try
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                manager.RegisterAsync(
                    new CurrentCallerRequest("run-bound.jwt.token", "tenant-a"),
                    "project-a",
                    "run-a",
                    new EnvironmentRegistrationApiRequest("p1-registration", expectation),
                    CancellationToken.None,
                    actor));

            Assert.Equal("p1_invocation_binding_mismatch", failure.Code);
            Assert.Equal(1, bindingReader.ReadCount);
            Assert.Equal(0, producer.WriteCount);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("foreign")]
    public async Task MissingStaleOrForeignBindingRejectsP1VolumeMetadataBeforeAnyWrite(string bindingState)
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-p1-volume");
        var fence = new EnvironmentGenerationFence(owner, 1);
        var lifecycleStore = new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
        var projects = new ProjectsConfigClient();
        var provider = new P1VolumeProvider();
        var binding = new P1InvocationBinding(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            bindingState == "foreign" ? "tenant-foreign" : owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            "root-session-p1",
            "https://identity.example",
            "actor-a",
            Guid.NewGuid(),
            1,
            new string('b', 64),
            3,
            1,
            DateTimeOffset.UtcNow);
        var expectation = new P1InvocationBindingExpectation(
            bindingState == "stale" ? Guid.NewGuid() : binding.CoreInvocationId,
            binding.SetupInvocationId,
            binding.SetupReceiptHash,
            binding.RootSessionId,
            binding.AcceptedSelectionHash,
            binding.ExecutionFence);
        IEnvironmentP1InvocationBindingReader bindingReader = bindingState == "missing"
            ? new MissingBindingReader()
            : new FixedBindingReader(binding);
        var manager = new EnvironmentWorkspaceVolumeManager(
            projects,
            lifecycleStore,
            new WorkspaceVolumeService(lifecycleStore, provider),
            p1BindingReader: bindingReader);
        var credential = new SecretCredential(
            "run-bound.jwt.token",
            DateTimeOffset.UtcNow.AddMinutes(2),
            TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, owner.TenantId);
        var request = new CreateWorkspaceVolumeApiRequest(
            new WorkspaceVolumeSpec(
                "volume-p1",
                owner.ProjectId,
                new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, owner.RunId),
                owner.EnvironmentId,
                WorkspaceVolumeBindingMode.Environment,
                WorkspaceVolumeAccessMode.ReadWriteMany,
                8,
                "azure-files",
                WorkspaceVolumeConsistency.Strict,
                WorkspaceVolumeReclaimPolicy.Delete,
                WorkspaceVolumeOwnerDeletionPolicy.Retain,
                []),
            "p1-volume-create")
        {
            P1Binding = expectation
        };

        try
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                manager.CreateP1Async(
                    new CurrentCallerRequest("run-bound.jwt.token", owner.TenantId),
                    owner.ProjectId,
                    owner.RunId,
                    owner.EnvironmentId,
                    "volume-p1",
                    request,
                    actor,
                    CancellationToken.None));

            Assert.Equal(
                bindingState == "missing"
                    ? "p1_invocation_binding_missing"
                    : "p1_invocation_binding_mismatch",
                failure.Code);
            Assert.Equal(0, provider.Calls);
            Assert.Null(await lifecycleStore.GetWorkspaceVolumeAsync(
                fence, "volume-p1", CancellationToken.None));
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task InternalRegistrationAcceptsExactRunBoundReadSelectionWithoutWriteProjects()
    {
        var projects = new ProjectsConfigClient();
        var producer = new LifecycleProducer();
        var ownerTemplate = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "unused");
        var binding = new P1InvocationBinding(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            ownerTemplate.TenantId,
            ownerTemplate.ProjectId,
            ownerTemplate.RunId,
            "root-session-a",
            "https://identity.example",
            "actor-a",
            Guid.NewGuid(),
            1,
            new string('b', 64),
            3,
            1,
            DateTimeOffset.UtcNow);
        var expectation = new P1InvocationBindingExpectation(
            binding.CoreInvocationId,
            binding.SetupInvocationId,
            binding.SetupReceiptHash,
            binding.RootSessionId,
            binding.AcceptedSelectionHash,
            binding.ExecutionFence);
        var bindingReader = new FixedBindingReader(binding);
        var options = EnvironmentEgressManagerTests.Options();
        var egress = new EnvironmentEgressManager(
            projects,
            new CiliumEgressPolicyAdapter(
                new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(),
                options),
            options,
            new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore());
        var manager = new EnvironmentLifecycleManager(egress, producer, bindingReader);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication("p1-test")
            .AddScheme<AuthenticationSchemeOptions, P1TestAuthenticationHandler>("p1-test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(manager);
        builder.Services.AddScoped<EnvironmentEgressManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentWorkspaceVolumeManager>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<EnvironmentSandboxManager>(_ => throw new NotSupportedException());
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEnvironmentEndpoints();

        var request = new EnvironmentRegistrationApiRequest("p1-http-registration", expectation);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        var address = Assert.Single(addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var publicRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/projects/project-a/runs/run-a/environments")
        {
            Content = JsonContent.Create(request)
        };
        publicRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "run-bound.jwt.token");
        publicRequest.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var publicResponse = await client.SendAsync(publicRequest);
        Assert.Equal(HttpStatusCode.BadRequest, publicResponse.StatusCode);
        Assert.Equal(0, producer.WriteCount);

        using var internalRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/projects/project-a/runs/run-a/environments")
        {
            Content = JsonContent.Create(request)
        };
        internalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "run-bound.jwt.token");
        internalRequest.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var internalResponse = await client.SendAsync(internalRequest);

        Assert.Equal(HttpStatusCode.Created, internalResponse.StatusCode);
        Assert.Equal("no-store", internalResponse.Headers.CacheControl?.ToString());
        Assert.Equal(1, producer.WriteCount);
        Assert.Equal(binding, producer.LastBinding);
        Assert.True(projects.SawRunBoundReadSelection);
    }

    private static DefaultHttpContext CreateEndpointContext(
        WebApplication app,
        EnvironmentRegistrationApiRequest request)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "actor-a")], "p1-test"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = "Bearer run-bound.jwt.token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-a";
        context.Request.RouteValues["projectId"] = "project-a";
        context.Request.RouteValues["runId"] = "run-a";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class MissingBindingReader : IEnvironmentP1InvocationBindingReader
    {
        public int ReadCount { get; private set; }

        public Task<P1InvocationBinding?> ReadCurrentAsync(
            RuntimeActorAuthorization actor,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<P1InvocationBinding?>(null);
        }
    }

    private sealed class FixedBindingReader(P1InvocationBinding binding)
        : IEnvironmentP1InvocationBindingReader
    {
        public int ReadCount { get; private set; }

        public Task<P1InvocationBinding?> ReadCurrentAsync(
            RuntimeActorAuthorization actor,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<P1InvocationBinding?>(binding);
        }
    }

    private sealed class P1VolumeProvider : IWorkspaceVolumeProvider
    {
        public int Calls { get; private set; }

        public Task<WorkspaceVolumeResource> ProvisionAsync(
            WorkspaceVolumeProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The provider must not run without the exact Core binding.");
        }

        public Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
            WorkspaceVolumeReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The provider must not run without the exact Core binding.");
        }
    }

    private sealed class LifecycleProducer : IEnvironmentLifecycleProducer
    {
        public int WriteCount { get; private set; }
        public P1InvocationBinding? LastBinding { get; private set; }

        public Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
            EnvironmentOwnerIdentity owner,
            string idempotencyKey,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null)
        {
            WriteCount++;
            throw new InvalidOperationException("The registration write must not run.");
        }

        public Task<EnvironmentLifecycleTransitionResult> RegisterP1Async(
            EnvironmentOwnerIdentity owner,
            string idempotencyKey,
            P1InvocationBinding binding,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task> beforeCommitCheck)
        {
            WriteCount++;
            LastBinding = binding;
            return RegisterP1ResultAsync(owner, beforeCommitCheck, cancellationToken);
        }

        private static async Task<EnvironmentLifecycleTransitionResult> RegisterP1ResultAsync(
            EnvironmentOwnerIdentity owner,
            Func<CancellationToken, Task> beforeCommitCheck,
            CancellationToken cancellationToken)
        {
            await beforeCommitCheck(cancellationToken);
            return new EnvironmentLifecycleTransitionResult(
                new EnvironmentLifecycleSnapshot(new EnvironmentGenerationFence(owner, 1),
                    EnvironmentLifecycleState.Active),
                Replayed: false)
            {
                OperationId = Guid.NewGuid()
            };
        }

        public Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
            EnvironmentGenerationFence expectedFence,
            EnvironmentLifecycleState targetState,
            string idempotencyKey,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null) =>
            throw new NotSupportedException();
    }

    private sealed class ProjectsConfigClient : IProjectsConfigClient
    {
        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectAuthorizationContextResponse(
                ProjectAuthorizationContextContract.CurrentVersion,
                "https://identity.example",
                "actor-a",
                "tenant-a",
                1,
                "project-a",
                "run-a",
                [
                    new EffectiveProjectAuthorization(
                        ProjectAuthorityResourceType.Project,
                        "project-a",
                        [
                            new(ProjectAuthorizationPermission.ReadRunSelection, 1)
                        ])
                ]));

        public bool SawRunBoundReadSelection { get; private set; }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            ReadSelection(projectId, runId);

        private Task<EffectiveNetworkPolicySelection> ReadSelection(string projectId, string runId)
        {
            SawRunBoundReadSelection = projectId == "project-a" && runId == "run-a";
            return Task.FromResult(EnvironmentEgressManagerTests.Selection());
        }
    }

    private sealed class P1TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "actor-a")], Scheme.Name));
            var properties = new AuthenticationProperties
            {
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, properties, Scheme.Name)));
        }
    }
}
