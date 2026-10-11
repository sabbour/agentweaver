using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxBuildTestAcceptedCommandVerifierTests
{
    [Fact]
    public async Task CurrentCommandCheckUsesOnlyOwnerMetadataWithoutFetchingTheSeedAgain()
    {
        var command = CreateSeedCommand();
        var handler = new SeedCommandHandler(command);
        using var client = new HttpClient(handler);

        await CreateVerifier(client).VerifyCurrentAsync(command, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("/workspace-seed", handler.Requests[0].Uri);
        Assert.True(handler.Requests[0].NoStore);
    }

    [Fact]
    public async Task ManagerEffectChecksReuseTheVerifiedSeedAndReadOnlyCurrentOwnerMetadata()
    {
        var command = CreateSeedCommand() with { WorkspaceSeed = new byte[1024].ToImmutableArray() };
        var handler = new SeedCommandHandler(command);
        using var client = new HttpClient(handler);
        var manager = new EnvironmentSandboxBuildTestCommandManager(
            null!, null!, null!, null!, CreateVerifier(client), null!, null!, TimeProvider.System, null!);
        var persisted = command with { WorkspaceSeed = null };

        for (var effect = 0; effect < 3; effect++)
            await manager.VerifyCurrentCommandAsync(command, persisted, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.DoesNotContain("/workspace-seed", request.Uri);
            Assert.True(request.NoStore);
        });
        Assert.Equal(new byte[1024], command.WorkspaceSeed!.Value.ToArray());
    }

    [Fact]
    public async Task ManagerEffectCheckRejectsChangedPersistedIntentWithoutAnyOwnerRequest()
    {
        var command = CreateSeedCommand();
        var changed = command with { Arguments = ["other"] };
        changed = changed with { ImmutableHash = changed.ComputeImmutableHash() };
        var handler = new SeedCommandHandler(command);
        using var client = new HttpClient(handler);
        var manager = new EnvironmentSandboxBuildTestCommandManager(
            null!, null!, null!, null!, CreateVerifier(client), null!, null!, TimeProvider.System, null!);

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            manager.VerifyCurrentCommandAsync(command, changed, CancellationToken.None));

        Assert.Equal("buildtest_command_checkpoint_changed", failure.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("fence")]
    [InlineData("selection")]
    public async Task CurrentCommandCheckRejectsChangedOwnerMetadataBeforeReservation(string change)
    {
        var accepted = CreateSeedCommand();
        var current = change switch
        {
            "operation" => accepted with { OperationId = Guid.NewGuid() },
            "fence" => accepted with { Checkpoint = accepted.Checkpoint with { ExecutionFence = 7 } },
            "selection" => accepted with
            {
                Checkpoint = accepted.Checkpoint with { AcceptedSelectionHash = new string('B', 64) },
                WorkspaceSource = accepted.WorkspaceSource! with { AcceptedSelectionHash = new string('B', 64) }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        current = current with { ImmutableHash = current.ComputeImmutableHash() };
        using var client = new HttpClient(new AcceptedCommandHandler(current));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            CreateVerifier(client).VerifyCurrentAsync(accepted, CancellationToken.None));

        Assert.Equal("buildtest_command_checkpoint_changed", failure.Code);
    }

    [Fact]
    public async Task VersionTwoReadsOnlyTheOwnerSeedThenRechecksMetadataBeforeReturningExecutableCommand()
    {
        var command = CreateSeedCommand();
        var handler = new SeedCommandHandler(command);
        using var client = new HttpClient(handler);

        var resolved = await CreateVerifier(client).ResolveAsync(command.Checkpoint, CancellationToken.None);

        Assert.Equal(command.ImmutableHash, resolved.Validate().ImmutableHash);
        Assert.Equal(new byte[1024], resolved.WorkspaceSeed!.Value.ToArray());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Uri, handler.Requests[2].Uri);
        Assert.Equal(
            handler.Requests[0].Uri.Replace("/test?", "/test/workspace-seed?", StringComparison.Ordinal),
            handler.Requests[1].Uri);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("https://orchestrator.example", new Uri(request.Uri).GetLeftPart(UriPartial.Authority));
            Assert.NotNull(request.Authorization);
            Assert.Equal(handler.Requests[0].Authorization, request.Authorization);
            Assert.Equal("tenant", request.Tenant);
            Assert.True(request.NoStore);
        });
    }

    [Theory]
    [InlineData("short", "buildtest_seed_contract_invalid")]
    [InlineData("long", "buildtest_seed_integrity_invalid")]
    [InlineData("digest", "buildtest_seed_integrity_invalid")]
    [InlineData("length", "buildtest_seed_contract_invalid")]
    [InlineData("media-type", "buildtest_seed_contract_invalid")]
    [InlineData("cache", "buildtest_seed_contract_invalid")]
    [InlineData("redirect", "runtime_owner_redirect_rejected")]
    [InlineData("denied", "runtime_owner_denied")]
    [InlineData("missing", "runtime_owner_unavailable")]
    public async Task VersionTwoRejectsMissingChangedOrUnboundedSeedBeforeFinalMetadataRead(
        string fault, string expectedCode)
    {
        var command = CreateSeedCommand();
        var handler = new SeedCommandHandler(command, fault);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            CreateVerifier(client).ResolveAsync(command.Checkpoint, CancellationToken.None));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("fence")]
    [InlineData("selection")]
    [InlineData("operation")]
    [InlineData("arguments")]
    [InlineData("owner-denied")]
    public async Task VersionTwoCannotReturnSeedWhenCurrentOwnerCommandChangesDuringFetch(string change)
    {
        var command = CreateSeedCommand();
        var handler = new SeedCommandHandler(command, finalChange: change);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            CreateVerifier(client).ResolveAsync(command.Checkpoint, CancellationToken.None));

        Assert.Equal(
            change == "owner-denied" ? "runtime_owner_denied" : "buildtest_command_checkpoint_changed",
            failure.Code);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SeedStreamCannotOutliveTheHttpClientDeadline()
    {
        var command = CreateSeedCommand();
        var handler = new SeedCommandHandler(command, "stall");
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        using var safetyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateVerifier(client).ResolveAsync(command.Checkpoint, safetyDeadline.Token));

        Assert.False(safetyDeadline.IsCancellationRequested);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static EnvironmentSandboxBuildTestAcceptedCommandVerifier CreateVerifier(HttpClient client) =>
        new(client, new HttpContextAccessor { HttpContext = CreateAuthenticatedContext() },
            TimeProvider.System, new(new Uri("https://orchestrator.example/"), new Uri("https://broker.example/")));

    private static SandboxBuildTestAcceptedCommand CreateSeedCommand()
    {
        var checkpoint = new SandboxBuildTestCheckpointReference(
            "project", "run", "session", "checkpoint", "plan", "test", 4, 5, 6, new string('A', 64));
        var command = CreateCommand(checkpoint);
        command = command with
        {
            ContractVersion = 2,
            WorkspaceSource = SandboxBuildTestCommandContractTests.WorkspaceSource(),
            ExecutionOptions = command.ExecutionOptions with
            {
                CollectorMode = SandboxBuildTestLimits.WorkspaceCollectorMode
            }
        };
        return command with { ImmutableHash = command.ComputeImmutableHash() };
    }

    [Fact]
    public async Task ResolveUsesFullCheckpointReferenceAndCurrentActorAuthorization()
    {
        var checkpoint = new SandboxBuildTestCheckpointReference(
            "project",
            "run",
            "session",
            "checkpoint",
            "plan/with space",
            "test",
            4,
            5,
            6,
            new string('A', 64));
        var command = CreateCommand(checkpoint);
        var handler = new AcceptedCommandHandler(command);
        using var client = new HttpClient(handler);
        var accessor = new HttpContextAccessor { HttpContext = CreateAuthenticatedContext() };
        var verifier = new EnvironmentSandboxBuildTestAcceptedCommandVerifier(
            client,
            accessor,
            TimeProvider.System,
            new(
                new Uri("https://orchestrator.example/"),
                new Uri("https://broker.example/")));

        var resolved = await verifier.ResolveAsync(checkpoint, CancellationToken.None);

        Assert.Equal(command.ImmutableHash, resolved.ImmutableHash);
        Assert.Equal(
            "https://orchestrator.example/api/projects/project/runs/run/coordination/sessions/session/" +
            "build-test/commands/checkpoint/test?workPlanId=plan%2Fwith%20space&checkpointRevision=4" +
            "&decisionStateVersion=5&executionFence=6&acceptedSelectionHash=" + new string('A', 64),
            handler.RequestUri);
        Assert.Equal("Bearer actor-token", handler.Authorization);
        Assert.Equal("tenant", handler.TenantSelector);
        Assert.True(handler.RequestNoStore);
    }

    private static DefaultHttpContext CreateAuthenticatedContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(new TestAuthenticationService(
                    DateTimeOffset.UtcNow.AddMinutes(5)))
                .BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "actor")], "test"))
        };
        context.Request.Headers.Authorization = "Bearer actor-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant";
        return context;
    }

    private static SandboxBuildTestAcceptedCommand CreateCommand(
        SandboxBuildTestCheckpointReference checkpoint)
    {
        var options = new SandboxBuildTestAcceptedExecutionOptions(
            "profile",
            "registry.example/build@sha256:" + new string('a', 64),
            "linux/amd64",
            ["/usr/bin/make"],
            "1",
            "512Mi",
            "1Gi",
            60,
            4096,
            1024,
            SandboxBuildTestLimits.OfflineEgressProfile,
            "registry.example/collector@sha256:" + new string('b', 64),
            "linux/amd64",
            SandboxBuildTestLimits.OutputCollectorExecutable,
            [SandboxBuildTestLimits.OutputCollectorAssembly],
            SandboxBuildTestLimits.OutputCollectorMode,
            SandboxBuildTestLimits.OutputCollectorContainerName);
        var command = new SandboxBuildTestAcceptedCommand(
            1,
            Guid.Parse("9dd33480-4e5b-49c5-b678-03d6766a3864"),
            checkpoint,
            "/usr/bin/make",
            ["test"],
            ".",
            [],
            options,
            string.Empty);
        return command with { ImmutableHash = command.ComputeImmutableHash() };
    }

    private sealed class AcceptedCommandHandler(SandboxBuildTestAcceptedCommand command) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? TenantSelector { get; private set; }
        public bool RequestNoStore { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            TenantSelector = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
            RequestNoStore = request.Headers.CacheControl?.NoStore == true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Headers = { CacheControl = new() { NoStore = true } },
                Content = new StringContent(
                    JsonSerializer.Serialize(command, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class SeedCommandHandler(
        SandboxBuildTestAcceptedCommand command, string? seedFault = null, string? finalChange = null)
        : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Tenant, bool NoStore)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("X-Agentweaver-Tenant").Single(),
                request.Headers.CacheControl?.NoStore == true));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Headers = { CacheControl = new() { NoStore = true } }
            };
            if (request.RequestUri.AbsolutePath.EndsWith("/workspace-seed", StringComparison.Ordinal))
            {
                Stream stream = seedFault == "stall" ? new StalledSeedStream() :
                    new MemoryStream(seedFault switch
                    {
                        "short" => new byte[1023],
                        "long" => new byte[1025],
                        "digest" => Enumerable.Repeat((byte)1, 1024).ToArray(),
                        _ => new byte[1024]
                    });
                response.Content = new StreamContent(stream);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                    seedFault == "media-type" ? "application/json" : "application/octet-stream");
                if (seedFault == "length")
                    response.Content.Headers.ContentLength = 1025;
                else if (seedFault is "short" or "long" or "stall")
                    response.Content.Headers.ContentLength = 1024;
                if (seedFault == "cache")
                    response.Headers.CacheControl = new() { NoStore = false };
                response.StatusCode = seedFault switch
                {
                    "redirect" => HttpStatusCode.TemporaryRedirect,
                    "denied" => HttpStatusCode.Forbidden,
                    "missing" => HttpStatusCode.NotFound,
                    _ => HttpStatusCode.OK
                };
            }
            else
            {
                var current = command;
                if (Requests.Count == 3)
                {
                    current = finalChange switch
                    {
                        "checkpoint" => current with
                        {
                            Checkpoint = current.Checkpoint with { CheckpointId = "changed" }
                        },
                        "fence" => current with
                        {
                            Checkpoint = current.Checkpoint with { ExecutionFence = 7 }
                        },
                        "selection" => current with
                        {
                            Checkpoint = current.Checkpoint with { AcceptedSelectionHash = new string('B', 64) },
                            WorkspaceSource = current.WorkspaceSource! with
                            {
                                AcceptedSelectionHash = new string('B', 64)
                            }
                        },
                        "operation" => current with { OperationId = Guid.NewGuid() },
                        "arguments" => current with { Arguments = ["build"] },
                        _ => current
                    };
                    current = current with { ImmutableHash = current.ComputeImmutableHash() };
                    if (finalChange == "owner-denied")
                        response.StatusCode = HttpStatusCode.Forbidden;
                }
                response.Content = new StringContent(
                    JsonSerializer.Serialize(current, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }

    private sealed class StalledSeedStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TestAuthenticationService(DateTimeOffset expiresAt) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                context.User,
                new AuthenticationProperties { ExpiresUtc = expiresAt },
                scheme ?? "test")));

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
