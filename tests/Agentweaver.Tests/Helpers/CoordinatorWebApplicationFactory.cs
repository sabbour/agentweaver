using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for the Feature 008 Phase 1 coordinator outcome-spec flow.
///
/// It wires the API exactly like production (real in-process host, real SQLite database,
/// real <c>CoordinatorRunService</c> + <c>CoordinatorWorkflowFactory</c> + MAF workflow,
/// real request-port suspend/resume) with two seams that keep the suite deterministic and
/// hermetic — both of which are real components, not mocks (Principle VII):
///
/// <list type="bullet">
/// <item>A <see cref="FakeCoordinatorSpecDrafter"/> in place of the production Copilot drafter, so
/// the drafting step produces a deterministic outcome spec with no live model call or network — the
/// hermetic seam that keeps the draft -> gate -> confirm/revise lifecycle stable.</item>
/// <item>A <see cref="SignedOutGitHubTokenStore"/> so any other agent path fails closed (no live
/// model call, no network) without a real GitHub session.</item>
/// <item>A no-op <see cref="ProjectGitInitializer"/> so a blank project can be created without
/// touching real git, mirroring <see cref="ProjectsWebApplicationFactory"/>.</item>
/// </list>
///
/// Two API keys are registered (owner + other) so owner-scoping (403) can be exercised
/// without mocking identity, mirroring <see cref="ReviewWebApplicationFactory"/>.
/// </summary>
public sealed class CoordinatorWebApplicationFactory : ApiWebApplicationFactory
{
    public const string OwnerApiKey = "coordinator-test-owner-key-12345";
    public const string OwnerUser   = "coordinator-owner-user";
    public const string OtherApiKey = "coordinator-test-other-key-99999";
    public const string OtherUser   = "coordinator-other-user";

    private readonly string _agentExecutionMode;
    private readonly int? _memoryContextMaxTokens;

    public CoordinatorWebApplicationFactory() : this("in-api", null)
    {
    }

    public static CoordinatorWebApplicationFactory CreatePodPerRun() => new("pod-per-run", null);

    public static CoordinatorWebApplicationFactory CreateWithMemoryContextMaxTokens(int memoryContextMaxTokens) =>
        new("in-api", memoryContextMaxTokens);

    private CoordinatorWebApplicationFactory(string agentExecutionMode, int? memoryContextMaxTokens)
        : base("agentweaver-coord", createWorkspaceRoot: true)
    {
        _agentExecutionMode         = agentExecutionMode;
        _memoryContextMaxTokens = memoryContextMaxTokens;
    }

    public HttpClient CreateOwnerClient() => CreateClientWithKey(OwnerApiKey);

    public HttpClient CreateOtherClient() => CreateClientWithKey(OtherApiKey);

    public async Task PrepareAiExecutionAsync(
        HttpClient client,
        string operation,
        string? projectId = null,
        string? runId = null,
        bool ensureProvider = true)
    {
        if (ensureProvider)
            await EnsurePlatformProviderAsync(projectId);
        var response = await client.PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation, project_id = projectId, run_id = runId });
        response.EnsureSuccessStatusCode();
        var context = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>()
            ?? throw new InvalidOperationException("AI execution context response was empty.");
        var providerKey = context.ExecutionKey
            ?? throw new InvalidOperationException(
                $"AI execution context did not return a provider key ({context.EffectiveModelProvider?.UnavailableReason ?? "unknown"}).");
        client.DefaultRequestHeaders.Remove(AiExecutionPlanHeaders.ProviderKey);
        client.DefaultRequestHeaders.Add(AiExecutionPlanHeaders.ProviderKey, providerKey);
    }

    public async Task ChangePlatformProviderIdentityAsync(string githubLogin)
    {
        await EnsurePlatformProviderAsync(projectId: null);
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
        binding.CredentialVersion = $"coordinator-test-version-{Guid.NewGuid():N}";
        await db.SaveChangesAsync();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.SetSecretAsync(
            binding.CredentialReference,
            JsonSerializer.Serialize(new
            {
                status = "signed-in",
                accessToken = "coordinator-test-token",
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                githubLogin,
            }));
    }

    private async Task EnsurePlatformProviderAsync(string? projectId)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        if (!string.IsNullOrWhiteSpace(projectId) &&
            !await db.Projects.AnyAsync(project => project.ProjectId == projectId))
        {
            db.Projects.Add(new ProjectRecord { ProjectId = projectId });
        }

        var credentialReference = "copilot-app-platform-default-coordinator-test";
        var binding = await db.PlatformDefaultCopilotBindings.SingleOrDefaultAsync();
        if (binding is null)
        {
            binding = new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            };
            db.PlatformDefaultCopilotBindings.Add(binding);
        }

        binding.EntraObjectId = "coordinator-test-platform-admin";
        binding.CredentialReference = credentialReference;
        binding.CredentialVersion = "coordinator-test-version";
        binding.GrantDigest = "coordinator-test-grant";
        binding.Status = GitHubBindingStatus.Active;
        binding.BoundAt = DateTimeOffset.UtcNow;
        binding.DeactivatedAt = null;
        await db.SaveChangesAsync();

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secrets.SetSecretAsync(
            credentialReference,
            """{"status":"signed-in","accessToken":"coordinator-test-token","expiresAt":"2099-01-01T00:00:00Z","githubLogin":"coordinator-test-bot"}""");
    }

    /// <summary>
    /// The hermetic reply classifier wired into this host. Tests may set its <c>Override</c> to force
    /// a specific confirm/revise/null result (e.g. to prove fail-closed behavior on model outage).
    /// </summary>
    public FakeOutcomeSpecReplyClassifier ReplyClassifier { get; } = new();
    public FakeStoryIndependenceClassifier StoryIndependenceClassifier { get; } = new();
    public FakeAssemblyGateCodeClassifier AssemblyGateCodeClassifier { get; } = new();
    public FakeWorkflowSelectionModel WorkflowSelectionModel { get; } = new();
    public FakePreviewClassifier PreviewClassifier { get; } = new();

    private HttpClient CreateClientWithKey(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    /// <summary>Creates an isolated project working directory under the workspace root.</summary>
    public string NewWorkingDirectory() => CreateWorkspaceDirectory();

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubOrgAuthorization"] = "true";
        configuration["Testing:BypassGitHubTokenAuth"] = "true";
        configuration["Auth:Mode"] = "GitHubLegacy";
        configuration["Auth:ApiKey"] = OwnerApiKey;
        configuration["Auth:User"] = OwnerUser;
        configuration["Auth:Keys:0:Token"] = OtherApiKey;
        configuration["Auth:Keys:0:User"] = OtherUser;
        configuration["Auth:Keys:0:PlatformRoles"] = PlatformRoles.Viewer;
        configuration["Coordinator:OutcomeSpecDraftTimeoutSeconds"] = "1";
        configuration["Coordinator:AutoDispatch"] = "false";
        configuration["Sandbox:AgentExecutionMode"] = _agentExecutionMode;
        if (_memoryContextMaxTokens is not null)
            configuration["MemoryContext:MaxTokens"] = _memoryContextMaxTokens.Value.ToString();
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
            // Replace the production Copilot drafter with a deterministic, hermetic fake so the
            // drafting step never makes a live model call. The boilerplate spec lives in the test
            // project, not production (production fails the run when the model is unavailable).
            RemoveService<Agentweaver.Api.Coordinator.ICoordinatorSpecDrafter>(services);
            services.AddSingleton<Agentweaver.Api.Coordinator.ICoordinatorSpecDrafter>(sp =>
                new FakeCoordinatorSpecDrafter(sp.GetRequiredService<RunStreamStore>()));

            // Replace the production Copilot-backed reply classifier with a deterministic, hermetic
            // fake so the confirm/revise routing at the outcome-spec gate never makes a live model
            // call. Tests can drive its Override to force confirm/revise/null (fail-closed) results.
            RemoveService<Agentweaver.Api.Coordinator.IOutcomeSpecReplyClassifier>(services);
            services.AddSingleton<Agentweaver.Api.Coordinator.IOutcomeSpecReplyClassifier>(ReplyClassifier);

            RemoveService<Agentweaver.Api.Coordinator.IStoryIndependenceClassifier>(services);
            services.AddSingleton<Agentweaver.Api.Coordinator.IStoryIndependenceClassifier>(StoryIndependenceClassifier);

            RemoveService<Agentweaver.Api.Coordinator.IAssemblyGateCodeClassifier>(services);
            services.AddSingleton<Agentweaver.Api.Coordinator.IAssemblyGateCodeClassifier>(AssemblyGateCodeClassifier);

            RemoveService<Agentweaver.Api.Coordinator.IWorkflowSelectionModel>(services);
            services.AddSingleton<Agentweaver.Api.Coordinator.IWorkflowSelectionModel>(WorkflowSelectionModel);

            RemoveService<Agentweaver.Api.Coordinator.IPreviewClassifier>(services);
            services.AddSingleton<Agentweaver.Api.Coordinator.IPreviewClassifier>(PreviewClassifier);

            // Any other agent path still fails closed (signed out) so it never reaches the network.
            // This is a real IGitHubTokenStore, not a mock.
            RemoveService<IGitHubTokenStore>(services);
            services.AddSingleton<IGitHubTokenStore>(new SignedOutGitHubTokenStore());

            // Skip real git init when creating the project that owns the coordinator run.
            RemoveService<ProjectGitInitializer>(services);
            services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
    }
}

/// <summary>
/// Real <see cref="IGitHubTokenStore"/> that reports an explicit signed-out state for every
/// scope. With this state <c>GitHubCopilotClientFactory.CreateClientAsync</c> fails closed and
/// throws before any network call, keeping any non-drafting agent path hermetic in tests. Distinct
/// from <see cref="NullGitHubTokenStore"/> (NeverSignedIn), which would let the config fallback
/// token through and attempt a real client connection.
/// </summary>
public sealed class SignedOutGitHubTokenStore : IGitHubTokenStore
{
    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null));

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubToken?>(null);

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubIdentity?>(null);

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.CompletedTask;
}
