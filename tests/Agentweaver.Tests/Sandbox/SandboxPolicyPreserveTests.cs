using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentTools;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Endpoints;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// Regression tests for the sandbox-policy data-loss bug (todo sandbox-policy-preserve): PUT
/// /api/sandbox-policy used to be a FULL REPLACE, so a partial save (e.g. the MCP
/// <c>sandbox_policy_set</c> sending only repository_path + shell_enabled) wiped the user's
/// allowed_repository_roots and destructive_command_patterns. The PUT now has PATCH/preserve
/// semantics: an omitted field keeps the existing value; an explicitly provided value (including an
/// empty array, which clears) is applied.
///
/// The HTTP tests run against a real in-process host (<see cref="AgentweaverWebApplicationFactory"/>)
/// over the real <c>YamlSandboxPolicyStore</c> writing each repository's <c>.agentweaver/settings.yml</c>
/// — no mocks (Principle VII). The merge tests pin the preserve-vs-clear rule directly.
/// </summary>
public sealed class SandboxPolicyPreserveTests : IClassFixture<ProjectsWebApplicationFactory>, IDisposable
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _repoPath;

    public SandboxPolicyPreserveTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ProjectsWebApplicationFactory.TestApiKey);
        _repoPath = Path.Combine(Path.GetTempPath(), $"sandbox-preserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { /* best effort */ }
    }

    // ── Pure merge logic (preserve-vs-clear rule) ───────────────────────────────────────────────

    [Fact]
    public void Merge_OmittedFields_PreserveExisting_OnlyProvidedFieldChanges()
    {
        var existing = new SandboxPolicy
        {
            RepositoryPath = "/repo",
            ShellEnabled = true,
            Direct = true,
            NetworkEnabled = true,
            AllowedRepositoryRoots = ["/a", "/b"],
            DestructiveCommandPatterns = ["rm -rf"],
            RequireApprovalForAllShell = true,
            RedactPii = true,
            MaxOutputBytes = 9999,
        };

        // Minimal MCP-style payload: only shell_enabled flips.
        var merged = EndpointHelpers.MergeSandboxPolicy(existing,
            new SandboxPolicyUpdateRequest { RepositoryPath = "/repo", ShellEnabled = false });

        merged.ShellEnabled.Should().BeFalse();
        merged.Direct.Should().BeTrue();
        merged.NetworkEnabled.Should().BeTrue();
        merged.AllowedRepositoryRoots.Should().Equal("/a", "/b");
        merged.DestructiveCommandPatterns.Should().Equal("rm -rf");
        merged.RequireApprovalForAllShell.Should().BeTrue();
        merged.RedactPii.Should().BeTrue();
        merged.MaxOutputBytes.Should().Be(9999);
    }

    [Fact]
    public void Merge_ExplicitEmptyArray_Clears_ButOmittedArrayPreserves()
    {
        var existing = new SandboxPolicy
        {
            RepositoryPath = "/repo",
            AllowedRepositoryRoots = ["/a", "/b"],
            DestructiveCommandPatterns = ["rm -rf"],
        };

        // allowed_repository_roots explicitly [] clears; destructive_command_patterns omitted preserves.
        var merged = EndpointHelpers.MergeSandboxPolicy(existing, new SandboxPolicyUpdateRequest
        {
            RepositoryPath = "/repo",
            AllowedRepositoryRoots = [],
        });

        merged.AllowedRepositoryRoots.Should().BeEmpty("an explicit empty array is a real clear intent");
        merged.DestructiveCommandPatterns.Should().Equal(new[] { "rm -rf" });
    }

    // ── HTTP round-trip over the real store ─────────────────────────────────────────────────────

    [Fact]
    public async Task PartialPut_OnlyShellEnabled_PreservesAllOtherFields()
    {
        await SeedFullPolicyAsync();

        // The exact minimal payload the MCP sandbox_policy_set sends.
        var resp = await _client.PutAsJsonAsync("/api/sandbox-policy", new
        {
            repository_path = _repoPath,
            shell_enabled = false,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("shell_enabled").GetBoolean().Should().BeFalse();
        AssertSeededFieldsPreserved(body);

        // Round-trip GET shows the same preserved state from disk.
        var get = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/sandbox-policy?repository_path={Uri.EscapeDataString(_repoPath)}");
        get.GetProperty("shell_enabled").GetBoolean().Should().BeFalse();
        AssertSeededFieldsPreserved(get);
    }

    [Fact]
    public async Task PartialPut_ExplicitEmptyArray_ClearsThatArray_KeepsTheOther()
    {
        await SeedFullPolicyAsync();

        var resp = await _client.PutAsJsonAsync("/api/sandbox-policy", new
        {
            repository_path = _repoPath,
            allowed_repository_roots = Array.Empty<string>(),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("allowed_repository_roots").GetArrayLength().Should().Be(0);
        Roots(body, "destructive_command_patterns").Should().Equal("rm -rf", "git reset --hard");
        // Untouched scalars remain.
        body.GetProperty("direct").GetBoolean().Should().BeTrue();
        body.GetProperty("network_enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FullPut_StillReplacesAllFields()
    {
        await SeedFullPolicyAsync();

        var resp = await _client.PutAsJsonAsync("/api/sandbox-policy", new
        {
            repository_path = _repoPath,
            shell_enabled = false,
            direct = false,
            network_enabled = false,
            allowed_repository_roots = new[] { "x" },
            destructive_command_patterns = new[] { "shutdown" },
            require_approval_for_all_shell = false,
            redact_pii = false,
            max_output_bytes = 4242,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("shell_enabled").GetBoolean().Should().BeFalse();
        body.GetProperty("direct").GetBoolean().Should().BeFalse();
        body.GetProperty("network_enabled").GetBoolean().Should().BeFalse();
        Roots(body, "allowed_repository_roots").Should().Equal("x");
        Roots(body, "destructive_command_patterns").Should().Equal("shutdown");
        body.GetProperty("require_approval_for_all_shell").GetBoolean().Should().BeFalse();
        body.GetProperty("redact_pii").GetBoolean().Should().BeFalse();
        body.GetProperty("max_output_bytes").GetInt32().Should().Be(4242);
    }

    [Fact]
    public async Task Put_MissingRepositoryPath_Returns400()
    {
        var resp = await _client.PutAsJsonAsync("/api/sandbox-policy", new { repository_path = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetPolicy_FileWithoutSandboxSection_UsesCanonicalApprovalPatterns()
    {
        await WriteSettingsAsync(
            """
            project:
              name: policy-test
            """);

        var policy = await _factory.Services.GetRequiredService<ISandboxPolicyStore>()
            .GetPolicyAsync(_repoPath);

        AssertCanonicalPatternDefaults(policy, _repoPath);
        await AssertCanonicalCommandsRequireApprovalAsync(policy);
    }

    [Fact]
    public async Task GetPolicy_SandboxWithoutDestructivePatterns_UsesCanonicalApprovalPatterns()
    {
        await WriteSettingsAsync(
            """
            sandbox:
              shell_enabled: true
              network_enabled: false
            """);

        var policy = await _factory.Services.GetRequiredService<ISandboxPolicyStore>()
            .GetPolicyAsync(_repoPath);

        policy.NetworkEnabled.Should().BeFalse();
        AssertCanonicalPatternDefaults(policy, _repoPath);
        await AssertCanonicalCommandsRequireApprovalAsync(policy);
    }

    [Fact]
    public async Task GetPolicy_ExplicitDestructivePatterns_RemainsAnIntentionalOverride()
    {
        await WriteSettingsAsync(
            """
            sandbox:
              destructive_command_patterns:
                - user-defined-command
            """);

        var policy = await _factory.Services.GetRequiredService<ISandboxPolicyStore>()
            .GetPolicyAsync(_repoPath);

        policy.DestructiveCommandPatterns.Should().Equal("user-defined-command");
        policy.DestructiveCommandPatterns.Should().NotContain("gh workflow run");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task SeedFullPolicyAsync()
    {
        // Register the repo path as a project so the endpoint can authorize it.
        var createResp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"sandbox-test-{Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _repoPath,
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created, "test project must be created before seeding policy");

        var resp = await _client.PutAsJsonAsync("/api/sandbox-policy", new
        {
            repository_path = _repoPath,
            shell_enabled = true,
            direct = true,
            network_enabled = true,
            allowed_repository_roots = new[] { "srv/shared", "opt/libs" },
            destructive_command_patterns = new[] { "rm -rf", "git reset --hard" },
            require_approval_for_all_shell = true,
            redact_pii = true,
            max_output_bytes = 65536,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "seeding the full policy must succeed");
    }

    private static void AssertSeededFieldsPreserved(JsonElement body)
    {
        body.GetProperty("direct").GetBoolean().Should().BeTrue();
        body.GetProperty("network_enabled").GetBoolean().Should().BeTrue();
        Roots(body, "allowed_repository_roots").Should().Equal("srv/shared", "opt/libs");
        Roots(body, "destructive_command_patterns").Should().Equal("rm -rf", "git reset --hard");
        body.GetProperty("require_approval_for_all_shell").GetBoolean().Should().BeTrue();
        body.GetProperty("redact_pii").GetBoolean().Should().BeTrue();
        body.GetProperty("max_output_bytes").GetInt32().Should().Be(65536);
    }

    private static string[] Roots(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static void AssertCanonicalPatternDefaults(SandboxPolicy policy, string repositoryPath) =>
        policy.DestructiveCommandPatterns.Should().Equal(
            SandboxPolicy.Default(repositoryPath).DestructiveCommandPatterns);

    private Task WriteSettingsAsync(string yaml)
    {
        var settingsDirectory = Path.Combine(_repoPath, ".agentweaver");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "settings.yml"), yaml);
        return Task.CompletedTask;
    }

    private static async Task AssertCanonicalCommandsRequireApprovalAsync(SandboxPolicy policy)
    {
        var executor = new ApprovalRequiredExecutor();
        using var tracker = new ShellExecutionTracker();
        var context = new SandboxToolContext(
            AgentId: "agent",
            WorkingDirectory: policy.RepositoryPath,
            SandboxRoot: policy.RepositoryPath,
            Executor: executor,
            FileTools: new SandboxedFileTools(policy.RepositoryPath),
            SearchTools: new SandboxedSearchTools(policy.RepositoryPath),
            Redactor: SandboxOutputRedactor.Default,
            Options: new SandboxToolOptions(ShellEnabled: true)
            {
                DestructiveCommandPatterns = [.. policy.DestructiveCommandPatterns],
            },
            Logger: NullLogger.Instance,
            ShellExecutionTracker: tracker);
        var tool = CopilotAIAgent.BuildSessionConfigTools(
            context,
            includeControlledRunCommand: true).Single(tool => tool.Name == "run_command");

        foreach (var command in new[]
                 {
                     "gh api /user",
                     "git push origin main",
                     "gh auth login",
                     "gh auth logout",
                     "gh pr create --title test --body test",
                     "gh pr merge 1",
                     "gh pr close 1",
                     "gh repo delete example/repo",
                     "gh repo archive example/repo",
                     "gh workflow run ci.yml --repo sabbour/agentweaver",
                 })
        {
            var result = await tool.InvokeAsync(new AIFunctionArguments(
                new Dictionary<string, object?> { ["command"] = command }));
            result?.ToString().Should().Contain("requires operator approval");
        }

        executor.ExecuteCalls.Should().Be(0);
    }

    private sealed class ApprovalRequiredExecutor : ISandboxExecutor
    {
        public int ExecuteCalls { get; private set; }
        public bool IsRealIsolation => true;
        public string BackendName => "test";
        public string SelectionReason => "test";
        public bool HasNetworkWarning => false;
        public string? NetworkWarningMessage => null;

        public Task<SandboxExecResult> ExecuteAsync(
            SandboxCommand command,
            CancellationToken ct = default)
        {
            ExecuteCalls++;
            return Task.FromResult(new SandboxExecResult(0, "", "", false, false));
        }

        public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
            SandboxCommand command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
