using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for GitHubCopilotClientFactory fail-closed semantics.
/// Verifies that an explicit sign-out (SignedOut tombstone) prevents client creation
/// even when a config fallback token is present (FR-015 fail-closed).
/// </summary>
public sealed class CopilotSignOutFailClosedTests
{
    // =========================================================================
    // FC-01: After sign-out, CreateClientAsync throws GitHubCopilotUnauthorizedException
    //        even when a config token is present.
    // =========================================================================
    [Fact]
    public async Task AfterSignOut_CreateClientAsync_ThrowsUnauthorized()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        // Sign in then explicitly sign out
        await tokenStore.SetAsync(scope, new GitHubToken("ghp_some_token", null, null, "user", null, []));
        await tokenStore.SignOutAsync(scope);

        var factory = BuildFactory(tokenStore, configToken: "ghp_config_fallback_token");

        var act = async () => await factory.CreateClientAsync(scope, null, CancellationToken.None);

        await act.Should().ThrowAsync<GitHubCopilotUnauthorizedException>(
            "SignedOut tombstone must take precedence over config fallback token");
    }

    // =========================================================================
    // FC-02: NeverSignedIn + config token set => does NOT throw (allows fallback)
    // =========================================================================
    [Fact]
    public async Task NeverSignedIn_WithConfigToken_DoesNotThrow()
    {
        var tokenStore = new InMemoryGitHubTokenStore(); // NeverSignedIn
        var scope      = GitHubTokenScope.Installation;

        var factory = BuildFactory(tokenStore, configToken: "ghp_config_fallback_token");

        // NeverSignedIn + config token available -> should succeed (returns a client)
        var act = async () => await factory.CreateClientAsync(scope, null, CancellationToken.None);

        // It may or may not throw depending on whether the CopilotClient validates the token at creation.
        // The key assertion is that the SignedOut tombstone check is not the failure path here.
        // We verify the exception, if any, is NOT GitHubCopilotUnauthorizedException.
        Exception? caughtException = null;
        try { await factory.CreateClientAsync(scope, null, CancellationToken.None); }
        catch (GitHubCopilotUnauthorizedException ex) { caughtException = ex; }

        caughtException.Should().BeNull(
            "NeverSignedIn with a config token should not throw GitHubCopilotUnauthorizedException");
    }

    // =========================================================================
    // FC-03: NeverSignedIn + no config token => throws GitHubCopilotUnauthorizedException
    // =========================================================================
    [Fact]
    public async Task NeverSignedIn_WithNoConfigToken_ThrowsUnauthorized()
    {
        var tokenStore = new InMemoryGitHubTokenStore(); // NeverSignedIn
        var scope      = GitHubTokenScope.Installation;

        var factory = BuildFactory(tokenStore, configToken: null);

        var act = async () => await factory.CreateClientAsync(scope, null, CancellationToken.None);

        await act.Should().ThrowAsync<GitHubCopilotUnauthorizedException>();
    }

    // =========================================================================
    // FC-04: SignedIn state => CreateClientAsync does not throw
    // =========================================================================
    [Fact]
    public async Task SignedIn_CreateClientAsync_DoesNotThrow()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        await tokenStore.SetAsync(scope, new GitHubToken("ghp_valid_token", null, null, "user", null, []));

        var factory = BuildFactory(tokenStore, configToken: null);

        // Should not throw GitHubCopilotUnauthorizedException
        Exception? caughtUnauth = null;
        try { await factory.CreateClientAsync(scope, null, CancellationToken.None); }
        catch (GitHubCopilotUnauthorizedException ex) { caughtUnauth = ex; }

        caughtUnauth.Should().BeNull(
            "a SignedIn token must allow client creation without throwing Unauthorized");
    }

    // =========================================================================
    // FC-05: SignedOut tombstone is checked BEFORE config fallback (order matters)
    // =========================================================================
    [Fact]
    public async Task SignedOut_ConfigTokenPresent_FailsClosedBeforeCheckingConfigToken()
    {
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;

        await tokenStore.SetAsync(scope, new GitHubToken("ghp_real_token", null, null, "user", null, []));
        await tokenStore.SignOutAsync(scope);

        // Config token also present — SignedOut must win
        var factory = BuildFactory(tokenStore, configToken: "ghp_should_be_ignored");

        await Assert.ThrowsAsync<GitHubCopilotUnauthorizedException>(
            async () => await factory.CreateClientAsync(scope, null, CancellationToken.None));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static GitHubCopilotClientFactory BuildFactory(
        IGitHubTokenStore tokenStore, string? configToken)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
            ["Providers:GitHubCopilot:Model"]    = "gpt-4o",
        };

        if (configToken is not null)
            configValues["Providers:GitHubCopilot:ApiKey"] = configToken;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new GitHubCopilotClientFactory(config, tokenStore, new FixedInstallationScopeProvider());
    }
}
