using Agentweaver.Abstractions;
using Agentweaver.ObjectStore.AzureBlob;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Projects.Config;

internal static class ProjectBlobObjectStoreFactory
{
    internal const string SkillContentContainerUriSetting = "ProjectsConfig:SkillContent:ContainerUri";
    internal const string GovernanceCheckpointContainerUriSetting = "ProjectsConfig:GovernanceCheckpoints:ContainerUri";

    internal static IObjectStore Create(Uri containerUri, TokenCredential credential) =>
        new AzureBlobObjectStore(new BlobContainerClient(containerUri, credential));

    internal static (Uri? SkillContent, Uri? GovernanceCheckpoints) ReadContainerUris(
        IConfiguration configuration) =>
        (
            ReadContainerUri(configuration, SkillContentContainerUriSetting, "Skill content"),
            ReadContainerUri(configuration, GovernanceCheckpointContainerUriSetting, "Governance checkpoints"));

    private static Uri? ReadContainerUri(IConfiguration configuration, string settingName, string purpose)
    {
        var value = configuration[settingName];
        if (value is null)
            return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var containerUri) ||
            containerUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(containerUri.UserInfo) ||
            !string.IsNullOrEmpty(containerUri.Query) ||
            !string.IsNullOrEmpty(containerUri.Fragment) ||
            containerUri.AbsolutePath.Trim('/').Contains('/') ||
            string.IsNullOrWhiteSpace(containerUri.AbsolutePath.Trim('/')))
            throw new InvalidOperationException(
                $"{purpose} requires an explicitly configured HTTPS Blob container.");

        return containerUri;
    }
}
