using Azure.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class GovernanceCheckpointStorageTests
{
    [Fact]
    public void ProjectBlobObjectStoreFactoryCreatesIndependentContainerClients()
    {
        var credential = new TestTokenCredential();
        var skillStore = ProjectBlobObjectStoreFactory.Create(
            new Uri("https://storage.example.test/skills"),
            credential);
        var checkpointStore = ProjectBlobObjectStoreFactory.Create(
            new Uri("https://storage.example.test/governance"),
            credential);

        Assert.NotSame(skillStore, checkpointStore);
    }

    [Fact]
    public void AppConfigurationSelectsSeparateSkillAndCheckpointContainers()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ProjectBlobObjectStoreFactory.SkillContentContainerUriSetting] =
                    "https://storage.example.test/skills",
                [ProjectBlobObjectStoreFactory.GovernanceCheckpointContainerUriSetting] =
                    "https://storage.example.test/governance",
            })
            .Build();

        var (skillUri, checkpointUri) = ProjectBlobObjectStoreFactory.ReadContainerUris(configuration);

        Assert.Equal(new Uri("https://storage.example.test/skills"), skillUri);
        Assert.Equal(new Uri("https://storage.example.test/governance"), checkpointUri);
        Assert.NotEqual(skillUri, checkpointUri);
    }

    private sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.UtcNow.AddMinutes(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test", DateTimeOffset.UtcNow.AddMinutes(1)));
    }
}
