using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Regression tests for issue #529: <c>start_preview</c> (agent-initiated
/// <c>POST /api/runs/{runId}/sandbox/preview</c>) returned HTTP 403 in every real deployment
/// because <see cref="EndpointHelpers.IsOwnerOrServiceCaller"/> authorized the run's own agent
/// callback ONLY by comparing <c>CallerContext.User</c> against the configured <c>Auth:User</c>
/// setting — a key no deployment manifest sets (only <c>Auth:ApiKey</c> is injected, see
/// k8s/base/api-deployment.yaml). The shared-key internal caller is actually attributed the
/// hardcoded <see cref="ProjectAuthorization.InternalServiceUser"/> identity by
/// <c>GitHubTokenAuthMiddleware</c>'s internal-key path, which the old check never recognized.
/// </summary>
public sealed class EndpointHelpersIsOwnerOrServiceCallerTests
{
    private static Run MakeRun(string submittingUser) => new()
    {
        Id = RunId.New(),
        RepositoryPath = Path.GetTempPath(),
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "do something",
        SubmittingUser = submittingUser,
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static HttpContext MakeHttpContext(CallerContext caller)
    {
        var http = new DefaultHttpContext();
        http.Items[GitHubTokenAuthMiddleware.CallerItemKey] = caller;
        return http;
    }

    private static IConfiguration MakeConfiguration(string? authUser = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(authUser is null
                ? []
                : new Dictionary<string, string?> { ["Auth:User"] = authUser })
            .Build();

    [Fact]
    public void IsOwnerOrServiceCaller_WithHardcodedInternalServiceIdentity_AndNoAuthUserConfigured_ReturnsTrue()
    {
        // Reproduces the production shape: Auth:User is never configured (only Auth:ApiKey is),
        // and the run's own agent callback resolves to the hardcoded "agentweaver-internal" caller
        // via the shared service key. Before the fix this returned false (403).
        var run = MakeRun("alice");
        var http = MakeHttpContext(new CallerContext
        {
            User = ProjectAuthorization.InternalServiceUser,
            GitHubLogin = ProjectAuthorization.InternalServiceUser,
        });
        var configuration = MakeConfiguration(authUser: null);

        EndpointHelpers.IsOwnerOrServiceCaller(http, run, configuration).Should().BeTrue();
    }

    [Fact]
    public void IsOwnerOrServiceCaller_WithConfiguredAuthUserMatch_ReturnsTrue()
    {
        // Legacy/alternate configuration shape: Auth:User is explicitly configured to some other
        // service identity string. That path must keep working.
        var run = MakeRun("alice");
        var http = MakeHttpContext(new CallerContext { User = "svc-user", GitHubLogin = "svc-user" });
        var configuration = MakeConfiguration(authUser: "svc-user");

        EndpointHelpers.IsOwnerOrServiceCaller(http, run, configuration).Should().BeTrue();
    }

    [Fact]
    public void IsOwnerOrServiceCaller_WithHumanOwner_ReturnsTrue()
    {
        var run = MakeRun("alice");
        var http = MakeHttpContext(new CallerContext { User = "alice", GitHubLogin = "alice" });
        var configuration = MakeConfiguration(authUser: null);

        EndpointHelpers.IsOwnerOrServiceCaller(http, run, configuration).Should().BeTrue();
    }

    [Fact]
    public void IsOwnerOrServiceCaller_WithUnrelatedCaller_ReturnsFalse()
    {
        var run = MakeRun("alice");
        var http = MakeHttpContext(new CallerContext { User = "mallory", GitHubLogin = "mallory" });
        var configuration = MakeConfiguration(authUser: null);

        EndpointHelpers.IsOwnerOrServiceCaller(http, run, configuration).Should().BeFalse();
    }
}
