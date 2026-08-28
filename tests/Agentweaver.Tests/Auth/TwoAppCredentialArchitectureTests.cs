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
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "apps", "Agentweaver.Api", "Auth", "GitHubCapabilityBroker.cs"))
            .Should().NotContain("IGitHubTokenScopeProvider").And.NotContain(".Resolve");
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
    public void BrowseAuthorityPersistenceRemainsOwnedByProjectCreationFlow()
    {
        var root = FindRepositoryRoot();
        Directory.EnumerateFiles(Path.Combine(root, "apps"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("GitHubRepositoryBrowseAuthority", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    [Fact]
    public void AgentHost_HasNoRepositoryCredentialOrVaultDeliveryPath()
    {
        var root = FindRepositoryRoot();
        var agentHost = Path.Combine(root, "apps", "Agentweaver.AgentHost");
        var source = Directory.EnumerateFiles(agentHost, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        source.Should().NotContain(text => text.Contains("KeyVaultUserTokenProvider", StringComparison.Ordinal)
            || text.Contains("IGitHubTokenStore", StringComparison.Ordinal)
            || text.Contains("SharedTokenStore", StringComparison.Ordinal)
            || text.Contains("CsiMountedGitHub", StringComparison.Ordinal)
            || text.Contains("CredentialLocator", StringComparison.Ordinal));

        var manifest = File.ReadAllText(Path.Combine(
            root, "k8s", "base", "sandbox-template-agenthost.yaml"));
        manifest.Should().NotContain("secrets-store")
            .And.NotContain("azure.workload.identity")
            .And.Contain("automountServiceAccountToken: false");
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
