using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ProjectTeamSyncAuthorityRevalidationTests
{
    private const string Issuer = "https://issuer.test/";
    private const string Actor = "11111111-1111-1111-1111-111111111111";
    private const string TeamSyncSelectionCode = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReadWithholdsSnapshotWhenRepositoryChangesDuringProviderRead()
    {
        // The request must recheck its repository proof after the provider has returned the old tree.
        var state = new ProjectState("readProjects");
        var adapter = new PausingTeamSyncAdapter();
        await using var app = await CreateAppAsync(state, adapter);

        var responseTask = InvokeTeamSyncEndpointAsync(
            app,
            "/internal/projects/project/source-control/team-sync/tree",
            new ProjectTeamSyncReadRequest(TeamSyncSelectionCode, "main"));
        if (await Task.WhenAny(adapter.ReadStarted.Task, responseTask) == responseTask)
        {
            var early = await responseTask;
            Assert.Fail($"The read endpoint returned early with {early.StatusCode}: {early.Body}");
        }
        await adapter.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Repository = new SourceControlRepositoryIdentity("octo", "replacement");
        adapter.ReleaseRead.TrySetResult();

        var response = await responseTask;
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.DoesNotContain("old repository file", response.Body, StringComparison.Ordinal);
        Assert.Contains("team_sync_project_authority_changed", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitDoesNotUpdateBranchWhenWriteAuthorityIsRevokedInFlight()
    {
        // The final ref update must wait for a fresh write-authority check after provider preparation.
        var state = new ProjectState("writeProjects");
        var adapter = new PausingTeamSyncAdapter();
        await using var app = await CreateAppAsync(state, adapter);

        var responseTask = InvokeTeamSyncEndpointAsync(
            app,
            "/internal/projects/project/source-control/team-sync/commit",
            new ProjectTeamSyncCommitRequest(
                TeamSyncSelectionCode,
                CommitSha,
                "Sync team files",
                [new SourceControlTeamSyncChange(".squad/team.md", null, Encoding.UTF8.GetBytes("team"))]));
        if (await Task.WhenAny(adapter.CommitStarted.Task, responseTask) == responseTask)
        {
            var early = await responseTask;
            Assert.Fail($"The commit endpoint returned early with {early.StatusCode}: {early.Body}");
        }
        await adapter.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Permission = "readProjects";
        adapter.ReleaseCommit.TrySetResult();

        var response = await responseTask;
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Equal(0, adapter.RefUpdates);
    }

    private static async Task<WebApplication> CreateAppAsync(ProjectState state, PausingTeamSyncAdapter adapter)
    {
        // Keep owner state and provider barriers mutable so each test can change authority mid-request.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("team-sync-test")
            .AddScheme<AuthenticationSchemeOptions, TeamSyncTestAuthenticationHandler>(
                "team-sync-test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(_ => new HttpClient(new ProjectsOwnerHandler(state)));
        builder.Services.AddSingleton<ProjectsRunSelectionClient>(services =>
            new(
                services.GetRequiredService<HttpClient>(),
                new(Issuer, "orchestrator-api", "https://projects.test/", "projects-api", "", ""),
                new UnusedSnapshotResolver()));
        builder.Services.AddSingleton<IProjectTeamSyncTokenClient, TestTeamSyncTokenClient>();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(adapter);
        builder.Services.AddSingleton<ISourceControlTeamSyncAdapter>(services =>
            services.GetRequiredService<PausingTeamSyncAdapter>());
        builder.Services.AddSingleton(TimeProvider.System);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapProjectTeamSyncEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task<(int StatusCode, string Body)> InvokeTeamSyncEndpointAsync(
        WebApplication app,
        string routePattern,
        object request)
    {
        // Exercise the mapped HTTP route so request binding and response withholding are covered together.
        using var client = app.GetTestClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, routePattern)
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.Authorization = new("Bearer", "test-token");
        using var response = await client.SendAsync(message);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static ClaimsPrincipal CreateCaller() =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", Actor),
            new Claim("aud", "orchestrator-api"),
            new Claim("aud", "projects-api"),
            new Claim("scope", "api.read projects.admin")
        ], "test"));

    private sealed class TeamSyncTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Authenticate only this in-memory test host with the project-scoped test principal.
            var ticket = new AuthenticationTicket(CreateCaller(), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class ProjectState(string permission)
    {
        public string Permission { get; set; } = permission;
        public SourceControlRepositoryIdentity Repository { get; set; } =
            new("octo", "widget");
    }

    private sealed class ProjectsOwnerHandler(ProjectState state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Return no-store owner contracts from the mutable state observed at each validation boundary.
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/authorization/context" => new ProjectsAuthorizationContext(
                    1,
                    Issuer,
                    Actor,
                    "tenant",
                    1,
                    null,
                    null,
                    [new("project", "project", [new(state.Permission, 1)])]),
                "/api/projects/project/issue-sync-authority" => new IssueSyncProjectProof(
                    "project", "tenant", Actor, "connection", state.Repository),
                _ => throw new InvalidOperationException("Unexpected Projects owner request.")
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class TestTeamSyncTokenClient : IProjectTeamSyncTokenClient
    {
        public Task<ProjectTeamSyncTokenResponse> MintAsync(
            ProjectTeamSyncTokenRequest request,
            CancellationToken cancellationToken)
        {
            // Match the token proof to the exact project repository and requested permission.
            return Task.FromResult(new ProjectTeamSyncTokenResponse(
                "test-installation-token",
                DateTimeOffset.UtcNow.AddMinutes(5),
                request.ConnectionId,
                request.Repository.FullName,
                "main",
                new string('a', 64),
                request.WriteRequested ? "write" : "read"));
        }
    }

    private sealed class PausingTeamSyncAdapter : ISourceControlTeamSyncAdapter
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefUpdates => Volatile.Read(ref _refUpdates);
        private int _refUpdates;

        public async Task<SourceControlTeamSyncSnapshot> ReadTeamSyncTreeAsync(
            SourceControlRepositoryIdentity repository,
            string reference,
            SecretCredential credential,
            CancellationToken cancellationToken)
        {
            // Hold the provider result until the test changes the current repository proof.
            ReadStarted.TrySetResult();
            await ReleaseRead.Task.WaitAsync(cancellationToken);
            return new SourceControlTeamSyncSnapshot(
                repository.FullName,
                reference,
                CommitSha,
                [new SourceControlTeamSyncFile(
                    ".squad/team.md",
                    CommitSha,
                    Encoding.UTF8.GetBytes("old repository file"),
                    false)]);
        }

        public async Task<SourceControlTeamSyncCommit> CommitTeamSyncChangesAsync(
            SourceControlRepositoryIdentity repository,
            SecretCredential credential,
            SourceControlTeamSyncCommitRequest request,
            Func<CancellationToken, Task> revalidateBeforeRefUpdate,
            CancellationToken cancellationToken)
        {
            // Hold the final ref update until the test changes the current project permission.
            CommitStarted.TrySetResult();
            await ReleaseCommit.Task.WaitAsync(cancellationToken);
            await revalidateBeforeRefUpdate(cancellationToken);
            Interlocked.Increment(ref _refUpdates);
            return new SourceControlTeamSyncCommit(
                CommitSha,
                request.Branch,
                new Uri($"https://github.com/{repository.FullName}/commit/{CommitSha}"));
        }
    }

    private sealed class UnusedSnapshotResolver : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Team sync does not resolve reviewed tool snapshots.");
    }
}
