using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using FluentAssertions;
using System.Reflection;

namespace Agentweaver.Tests.Auth;

public sealed class TwoAppCredentialArchitectureTests
{
    private static readonly string[] AllowedReservedCredentialOwners =
    [
        "Auth/ProjectCopilotBindingService.cs",
        "Auth/RepoAppUserAuthorizationService.cs",
        "Auth/TwoAppCredentialVault.cs",
        "Webhooks/RepoAppInstallationService.cs",
    ];

    [Fact]
    public void TwoAppContract_HasOnlyPurposeBoundRepoAndCopilotAppCapabilities()
    {
        Enum.GetValues<GitHubAppKind>().Should().Equal(
            GitHubAppKind.Repo,
            GitHubAppKind.Copilot);
        Enum.GetValues<GitHubAuthorizationPurpose>().Should().Equal(
            GitHubAuthorizationPurpose.InteractiveRepository,
            GitHubAuthorizationPurpose.InteractiveCopilot,
            GitHubAuthorizationPurpose.UnattendedRepository,
            GitHubAuthorizationPurpose.UnattendedCopilot);
        Enum.GetValues<GitHubCapabilityPurpose>().Should().Equal(
            GitHubCapabilityPurpose.InteractiveRepository,
            GitHubCapabilityPurpose.InteractiveCopilot,
            GitHubCapabilityPurpose.UnattendedRepository,
            GitHubCapabilityPurpose.UnattendedCopilot);
    }

    [Fact]
    public void ReservedCredentialPrefixes_AppearOnlyInTheExplicitShrinkOnlyOwnerList()
    {
        const int expectedOwnerCount = 4;
        AllowedReservedCredentialOwners.Should().HaveCount(expectedOwnerCount)
            .And.OnlyHaveUniqueItems();
        AllowedReservedCredentialOwners.Should().NotContain(owner => owner.Contains('*'));

        var apiRoot = Path.Combine(FindRepositoryRoot(), "apps", "Agentweaver.Api");
        var owners = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => ContainsReservedCredentialPrefix(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(apiRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        owners.Should().BeEquivalentTo(AllowedReservedCredentialOwners);
    }

    [Fact]
    public void BrokerSurface_HasOnlyMandatoryPurposeAndOpaqueSnapshotInputs()
    {
        var methods = typeof(GitHubCapabilityBroker).GetMethods()
            .Where(method => method.DeclaringType == typeof(GitHubCapabilityBroker))
            .ToArray();

        methods.Should().ContainSingle();
        methods[0].GetParameters().Take(2).Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(GitHubCapabilityPurpose), typeof(SnapshotRef));
        methods[0].GetParameters().Should().NotContain(parameter => parameter.IsOptional);

        var credential = typeof(GitHubCapabilityBroker).GetMethod(
            "TryUseRepositoryCredentialAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        credential.Should().NotBeNull();
        credential.GetParameters().Take(2).Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(SnapshotRef), typeof(DateTimeOffset));
        credential.GetParameters().Should().NotContain(parameter => parameter.IsOptional);
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "apps", "Agentweaver.Api", "Auth", "GitHubCapabilityBroker.cs"))
            .Should().NotContain("IGitHubTokenScopeProvider").And.NotContain(".Resolve")
            .And.NotContain("CommandLine").And.NotContain("git ").And.NotContain("gh ");
    }

    [Fact]
    public void InternalBrokerAuthorization_RequiresPurposeAndOpaqueSnapshot()
    {
        var authorize = typeof(GitHubCapabilityBroker).GetMethod(
            "TryAuthorizeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        authorize.Should().NotBeNull();
        authorize!.GetParameters().Take(2).Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(GitHubCapabilityPurpose), typeof(SnapshotRef));
        authorize.GetParameters().Should().NotContain(parameter => parameter.IsOptional);
    }

    [Fact]
    public void ProjectOperationCredentialBroker_RequiresPurposeAndBoundOpaqueInputs()
    {
        var credential = typeof(GitHubCapabilityBroker).GetMethod(
            "TryUseProjectCopilotCredentialAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        credential.Should().NotBeNull();
        credential!.GetParameters().Take(5).Select(parameter => parameter.ParameterType.FullName)
            .Should().Equal(
                typeof(SnapshotRef).FullName,
                "Agentweaver.Domain.GitHubProjectCopilotCapabilityPurpose",
                typeof(string).FullName,
                typeof(string).FullName,
                typeof(DateTimeOffset).FullName);
        credential.GetParameters().Should().NotContain(parameter => parameter.IsOptional);
    }

    [Theory]
    [InlineData(GitHubCapabilityPurpose.InteractiveRepository, GitHubCapabilityOperation.RepositoryRead, true)]
    [InlineData(GitHubCapabilityPurpose.InteractiveRepository, GitHubCapabilityOperation.RepositoryWrite, true)]
    [InlineData(GitHubCapabilityPurpose.InteractiveRepository, GitHubCapabilityOperation.CopilotInference, false)]
    [InlineData(GitHubCapabilityPurpose.InteractiveCopilot, GitHubCapabilityOperation.RepositoryRead, false)]
    [InlineData(GitHubCapabilityPurpose.InteractiveCopilot, GitHubCapabilityOperation.RepositoryWrite, false)]
    [InlineData(GitHubCapabilityPurpose.InteractiveCopilot, GitHubCapabilityOperation.CopilotInference, true)]
    [InlineData(GitHubCapabilityPurpose.UnattendedRepository, GitHubCapabilityOperation.RepositoryRead, true)]
    [InlineData(GitHubCapabilityPurpose.UnattendedRepository, GitHubCapabilityOperation.RepositoryWrite, true)]
    [InlineData(GitHubCapabilityPurpose.UnattendedRepository, GitHubCapabilityOperation.CopilotInference, false)]
    [InlineData(GitHubCapabilityPurpose.UnattendedCopilot, GitHubCapabilityOperation.RepositoryRead, false)]
    [InlineData(GitHubCapabilityPurpose.UnattendedCopilot, GitHubCapabilityOperation.RepositoryWrite, false)]
    [InlineData(GitHubCapabilityPurpose.UnattendedCopilot, GitHubCapabilityOperation.CopilotInference, true)]
    public void PurposeToOperationMapping_IsClosed(
        GitHubCapabilityPurpose purpose,
        GitHubCapabilityOperation operation,
        bool expected) =>
        GitHubCapabilityBroker.IsOperationAllowed(purpose, operation).Should().Be(expected);

    [Fact]
    public void SnapshotLifecycleIsRunBoundAndDoesNotReachSandboxCredentialDelivery()
    {
        var root = FindRepositoryRoot();
        var lifecycle = File.ReadAllText(Path.Combine(
            root, "apps", "Agentweaver.Api", "Auth", "RunGitHubCapabilitySnapshotLifecycle.cs"));

        lifecycle.Should().Contain("GitHubCapabilityBroker")
            .And.Contain("TryFenceAsync")
            .And.NotContain("KubernetesSandboxExecutor")
            .And.NotContain("AgentHost")
            .And.NotContain("IGitHubTokenStore")
            .And.NotContain("IGitHubAccessTokenProvider");

        var orchestrator = File.ReadAllText(Path.Combine(root, "apps", "Agentweaver.Api", "Runs", "RunOrchestrator.cs"));
        orchestrator
            .Should().Contain("PrepareGitHubCapabilitySnapshotsAsync(run, ct)")
            .And.Contain("PrepareGitHubCapabilitySnapshotsAsync(newAgentRun, ct)");
        File.ReadAllText(Path.Combine(root, "apps", "Agentweaver.Api", "Coordinator", "CoordinatorRunService.cs"))
            .Should().Contain("PrepareGitHubCapabilitySnapshotsAsync(run, _appStopping)");
        File.ReadAllText(Path.Combine(root, "apps", "Agentweaver.Api", "Program.cs"))
            .Should().Contain("AddScoped<RunGitHubCapabilitySnapshotLifecycle>");
        File.ReadAllText(Path.Combine(root, "apps", "Agentweaver.Api", "Runs", "WorkflowRestartService.cs"))
            .Should().Contain("PrepareForLaunchAsync(run, ct)");
    }

    [Fact]
    public void RepositoryCredentialRegistry_UsesOnlyTheRunBoundRepositorySnapshot()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "apps",
            "Agentweaver.Api",
            "Sandbox",
            "RunRepositoryCredentialRegistry.cs"));

        source.Should().Contain("GetCapabilitySnapshotsAsync(runId, ct)")
            .And.Contain("GitHubCapabilityPurpose.UnattendedRepository")
            .And.Contain("TryUseRepositoryCredentialAsync")
            .And.Contain("ConcurrentDictionary<string, RepositoryCredential>")
            .And.NotContain("CommandLine")
            .And.NotContain("endpoint")
            .And.NotContain("git ")
            .And.NotContain("gh ");
    }

    [Fact]
    public void RuntimeAndHost_HaveNoAmbientGitHubTokenDependencies()
    {
        var root = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(root, "packages", "Agentweaver.AgentRuntime"),
            Path.Combine(root, "apps", "Agentweaver.AgentHost"),
        };
        var forbidden = new[]
        {
            "IGitHubTokenStore",
            "IGitHubTokenScopeProvider",
            "IGitHubAccessTokenProvider",
        };

        var offenders = sourceRoots
            .SelectMany(sourceRoot => Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => forbidden.Any(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        offenders.Should().BeEmpty("runtime and host credentials must originate from capability snapshots");
    }

    [Fact]
    public void AgentHostConfigure_RequiresLiveRunBoundCapabilityCredential()
    {
        var root = FindRepositoryRoot();
        var hostProgram = File.ReadAllText(Path.Combine(
            root, "apps", "Agentweaver.AgentHost", "Program.cs"));
        var hostProvider = File.ReadAllText(Path.Combine(
            root, "apps", "Agentweaver.AgentHost", "AgentHostGitHubCapabilityCredentialProvider.cs"));
        var runtimeFactory = File.ReadAllText(Path.Combine(
            root, "packages", "Agentweaver.AgentRuntime", "Providers", "GitHubCopilotClientFactory.cs"));

        hostProgram.Should().Contain("A live run-bound Copilot capability credential is required");
        hostProvider.Should().Contain("runtimeState.RunId, runId")
            .And.Contain("credential.ExpiresAt > DateTimeOffset.UtcNow");
        runtimeFactory.Should().Contain("IGitHubCopilotCapabilityCredentialProvider")
            .And.Contain("live run-bound capability snapshot")
            .And.NotContain("GetValue<string>(\"GitHubToken\")")
            .And.NotContain("GetValue<string>(\"ApiKey\")");
    }

    [Fact]
    public void BrowseAuthorityPersistenceRemainsOwnedByProjectCreationFlow()
    {
        var root = FindRepositoryRoot();
        Directory.EnumerateFiles(Path.Combine(root, "apps"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("GitHubRepositoryBrowseAuthority", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    private static bool ContainsReservedCredentialPrefix(string source) =>
        source.Contains("repo-app-user-credential", StringComparison.Ordinal) ||
        source.Contains("copilot-app-project", StringComparison.Ordinal) ||
        source.Contains("Auth:RepoApp:PrivateKeySecretName", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
