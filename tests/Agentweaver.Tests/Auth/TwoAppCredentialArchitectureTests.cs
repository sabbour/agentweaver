using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using FluentAssertions;

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
