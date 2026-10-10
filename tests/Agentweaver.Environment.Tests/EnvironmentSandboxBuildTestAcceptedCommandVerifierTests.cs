using System.Net;
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
