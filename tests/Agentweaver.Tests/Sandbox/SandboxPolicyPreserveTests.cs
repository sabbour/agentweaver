using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Endpoints;
using Agentweaver.Domain;
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
public sealed class SandboxPolicyPreserveTests : IClassFixture<AgentweaverWebApplicationFactory>, IDisposable
{
    private readonly AgentweaverWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _repoPath;

    public SandboxPolicyPreserveTests(AgentweaverWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
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
            allowed_repository_roots = new[] { "/x" },
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
        Roots(body, "allowed_repository_roots").Should().Equal("/x");
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

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task SeedFullPolicyAsync()
    {
        var resp = await _client.PutAsJsonAsync("/api/sandbox-policy", new
        {
            repository_path = _repoPath,
            shell_enabled = true,
            direct = true,
            network_enabled = true,
            allowed_repository_roots = new[] { "/srv/shared", "/opt/libs" },
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
        Roots(body, "allowed_repository_roots").Should().Equal("/srv/shared", "/opt/libs");
        Roots(body, "destructive_command_patterns").Should().Equal("rm -rf", "git reset --hard");
        body.GetProperty("require_approval_for_all_shell").GetBoolean().Should().BeTrue();
        body.GetProperty("redact_pii").GetBoolean().Should().BeTrue();
        body.GetProperty("max_output_bytes").GetInt32().Should().Be(65536);
    }

    private static string[] Roots(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();
}
