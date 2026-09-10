using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class RunGitHubCapabilityCredentialProviderTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;

    public RunGitHubCapabilityCredentialProviderTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CoordinatorSyntheticRunId_RedeemsOwningParentCapabilitySnapshot()
    {
        var runId = RunId.New().ToString();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = "copilot-app-platform-default-synthetic-test",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            db.RunGitHubCapabilitySnapshots.Add(new RunGitHubCapabilitySnapshotRecord
            {
                SnapshotRef = SnapshotRef.Create().Value,
                RunId = runId,
                Purpose = GitHubCapabilityPurpose.UnattendedCopilot,
                AppKind = GitHubAppKind.Copilot,
                SourceKind = GitHubCapabilitySnapshotSourceKind.CopilotBinding,
                SourceBindingId = PlatformDefaultCopilotBindingRecord.SingletonId,
                CredentialReference = "copilot-app-platform-default-synthetic-test",
                CredentialVersion = "version",
                GrantDigest = "digest",
                CapturedAt = DateTimeOffset.UtcNow,
            });
            await secrets.SetSecretAsync(
                "copilot-app-platform-default-synthetic-test",
                """{"status":"signed-in","accessToken":"ghu_parent_snapshot","expiresAt":"2099-01-01T00:00:00Z"}""");
            await db.SaveChangesAsync();
        }

        var provider = _factory.Services.GetRequiredService<IGitHubCopilotCapabilityCredentialProvider>();
        var credential = await provider.GetCredentialAsync(
            runId + "-coordinator-draft",
            CancellationToken.None);

        credential.Should().NotBeNull();
        credential!.AccessToken.Should().Be("ghu_parent_snapshot");
    }
}
