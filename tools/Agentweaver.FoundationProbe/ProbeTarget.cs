using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

namespace Agentweaver.FoundationProbe;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeTarget
{
    public required string SourceSha { get; init; }
    public required string SourceTree { get; init; }
    public required string SourceHash { get; init; }
    public required string SubscriptionId { get; init; }
    public required string TenantId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string ResourceGroupId { get; init; }
    public required string DeploymentName { get; init; }
    public required string DeploymentId { get; init; }
    public required ProbeInfrastructure Infrastructure { get; init; }
    public required string AksOidcIssuerUrl { get; init; }
    public required ProbeIdentity FoundationProbeIdentity { get; init; }
    public required ProbeResources FoundationResources { get; init; }
    public required ProbeRuntime Runtime { get; init; }

    public static ProbeTarget Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ProbeException("target_missing");

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProbeTarget>(json, JsonOptions)
                ?? throw new ProbeException("target_invalid");
        }
        catch (ProbeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ProbeException("target_invalid", exception);
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeIdentity
{
    public required string Name { get; init; }
    public required string ResourceId { get; init; }
    public required string ClientId { get; init; }
    public required string PrincipalObjectId { get; init; }
    public required string Namespace { get; init; }
    public required string ServiceAccount { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeResources
{
    public required string ClusterId { get; init; }
    public required string KeyVaultId { get; init; }
    public required string VaultUri { get; init; }
    public required string StorageAccountId { get; init; }
    public required string BlobContainerId { get; init; }
    public required string BlobContainerUri { get; init; }
    public required string PostgresServerId { get; init; }
    public required string PostgresHost { get; init; }
    public required string MonitorWorkspaceResourceId { get; init; }
    public required string MonitorWorkspaceId { get; init; }
    public required string AppInsightsResourceId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeRuntime
{
    public required string DatabaseName { get; init; }
    public required string DatabaseRole { get; init; }
    public required string SchemaName { get; init; }
    public required string KeyVaultSecretName { get; init; }
    public required string KeyVaultSecretVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeInfrastructure
{
    public required string Scope { get; init; }
    public required string SourceSha { get; init; }
    public required string SourceTree { get; init; }
    public required string SourceHash { get; init; }
    public required ProbeFoundationDeployment Foundation { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeFoundationDeployment
{
    public required string Scope { get; init; }
    public required string SourceSha { get; init; }
    public required string SourceTree { get; init; }
    public required string SourceHash { get; init; }
    public required string DeploymentName { get; init; }
    public required string DeploymentId { get; init; }
}

internal sealed record ProbeSource(string Sha, string Tree, string InfrastructureHash)
{
    private static readonly System.Text.RegularExpressions.Regex FullSha =
        new("^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static ProbeSource FromAssembly(Type? entryPoint = null)
    {
        var assembly = (entryPoint ?? typeof(ProbeSource)).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        if (!metadata.TryGetValue("Agentweaver.SourceSha", out var sha) ||
            !metadata.TryGetValue("Agentweaver.SourceTree", out var tree) ||
            !metadata.TryGetValue("Agentweaver.SourceHash", out var sourceHash) ||
            !FullSha.IsMatch(sha ?? "") || !FullSha.IsMatch(tree ?? "") ||
            !System.Text.RegularExpressions.Regex.IsMatch(sourceHash ?? "", "^[0-9a-f]{64}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ProbeException("image_source_unbound");
        return new ProbeSource(sha!, tree!, sourceHash!);
    }

    public static bool IsFullSha(string? value) => FullSha.IsMatch(value ?? "");
}

internal static class ProbeTargetValidator
{
    private const string ExpectedResourceGroup = "aw-v1-p0";
    private const string ExpectedNamespace = "agentweaver-v1-p0";
    private const string ExpectedServiceAccount = "foundation-probe";
    private const string ExpectedSchema = "foundation_probe";
    private const string ExpectedRole = "foundation_probe_runtime";
    private const string ExpectedSecretName = "foundation-probe";
    private static readonly System.Text.RegularExpressions.Regex GuidPattern =
        new("^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ResourceName =
        new("^[a-z][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static void Validate(ProbeTarget target, ProbeSource source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (target.SourceSha is null || target.SourceTree is null || target.SourceHash is null ||
            target.FoundationProbeIdentity is null || target.FoundationResources is null || target.Runtime is null ||
            !ProbeSource.IsFullSha(target.SourceSha) || !ProbeSource.IsFullSha(target.SourceTree) ||
            !System.Text.RegularExpressions.Regex.IsMatch(target.SourceHash, "^[0-9a-f]{64}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ProbeException("target_missing");

        if (target.SourceSha != source.Sha || target.SourceTree != source.Tree ||
            target.SourceHash != source.InfrastructureHash)
            throw new ProbeException("source_mismatch");

        var infrastructure = target.Infrastructure;
        var foundation = infrastructure?.Foundation;
        if (infrastructure is null || foundation is null ||
            !ProbeSource.IsFullSha(infrastructure.SourceSha) || !ProbeSource.IsFullSha(infrastructure.SourceTree) ||
            infrastructure.SourceHash is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(infrastructure.SourceHash, "^[0-9a-f]{64}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !ProbeSource.IsFullSha(foundation.SourceSha) || !ProbeSource.IsFullSha(foundation.SourceTree) ||
            foundation.SourceHash is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(foundation.SourceHash, "^[0-9a-f]{64}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ProbeException("deployment_provenance_missing");

        if (!ValidGuid(target.SubscriptionId) || !ValidGuid(target.TenantId) ||
            target.ResourceGroup != ExpectedResourceGroup ||
            !Equal(target.ResourceGroupId, $"/subscriptions/{target.SubscriptionId}/resourceGroups/{ExpectedResourceGroup}") ||
            infrastructure.Scope is not ("infrastructure-only" or "aks-only") ||
            foundation.Scope != "infrastructure-only" ||
            target.DeploymentName != $"{ExpectedResourceGroup}{(infrastructure.Scope == "aks-only" ? "-aks" : "")}-{infrastructure.SourceSha[..12]}" ||
            !Equal(target.DeploymentId,
                $"{target.ResourceGroupId}/providers/Microsoft.Resources/deployments/{target.DeploymentName}") ||
            foundation.DeploymentName != $"{ExpectedResourceGroup}-{foundation.SourceSha[..12]}" ||
            !Equal(foundation.DeploymentId,
                $"{target.ResourceGroupId}/providers/Microsoft.Resources/deployments/{foundation.DeploymentName}") ||
            infrastructure.Scope == "infrastructure-only" &&
            (infrastructure.SourceSha != foundation.SourceSha || infrastructure.SourceTree != foundation.SourceTree ||
             infrastructure.SourceHash != foundation.SourceHash || target.DeploymentName != foundation.DeploymentName ||
             !Equal(target.DeploymentId, foundation.DeploymentId)))
            throw new ProbeException("target_mismatch");

        var storageName = $"{ExpectedResourceGroup.Replace("-", "", StringComparison.Ordinal)}blob";
        string ResourceId(string type, string name) => $"{target.ResourceGroupId}/providers/{type}/{name}";
        var resources = target.FoundationResources;
        if (!Equal(resources.ClusterId, ResourceId("Microsoft.ContainerService/managedClusters", $"{ExpectedResourceGroup}-aks")) ||
            !Equal(resources.KeyVaultId, ResourceId("Microsoft.KeyVault/vaults", $"{ExpectedResourceGroup}-kv")) ||
            resources.VaultUri != $"https://{ExpectedResourceGroup}-kv.vault.azure.net/" ||
            !Equal(resources.StorageAccountId, ResourceId("Microsoft.Storage/storageAccounts", storageName)) ||
            !Equal(resources.BlobContainerId, ResourceId("Microsoft.Storage/storageAccounts",
                $"{storageName}/blobServices/default/containers/platform-artifacts")) ||
            resources.BlobContainerUri != $"https://{storageName}.blob.core.windows.net/platform-artifacts" ||
            !Equal(resources.PostgresServerId,
                ResourceId("Microsoft.DBforPostgreSQL/flexibleServers", $"{ExpectedResourceGroup}-pg")) ||
            resources.PostgresHost != $"{ExpectedResourceGroup}-pg.postgres.database.azure.com" ||
            !Equal(resources.MonitorWorkspaceResourceId,
                ResourceId("Microsoft.OperationalInsights/workspaces", $"{ExpectedResourceGroup}-law")) ||
            !ValidGuid(resources.MonitorWorkspaceId) ||
            !Equal(resources.AppInsightsResourceId,
                ResourceId("Microsoft.Insights/components", $"{ExpectedResourceGroup}-appi")))
            throw new ProbeException("resource_mismatch");

        var identity = target.FoundationProbeIdentity;
        if (identity.Name != ExpectedServiceAccount ||
            !Equal(identity.ResourceId, ResourceId("Microsoft.ManagedIdentity/userAssignedIdentities",
                $"{ExpectedResourceGroup}-id-foundation-probe")) ||
            identity.Namespace != ExpectedNamespace || identity.ServiceAccount != ExpectedServiceAccount ||
            !ValidGuid(identity.ClientId) || !ValidGuid(identity.PrincipalObjectId) ||
            string.Equals(identity.ClientId, identity.PrincipalObjectId, StringComparison.OrdinalIgnoreCase))
            throw new ProbeException("identity_mismatch");

        if (!Uri.TryCreate(target.AksOidcIssuerUrl, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps || !issuer.IsDefaultPort ||
            issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 ||
            !issuer.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new ProbeException("issuer_mismatch");

        var runtime = target.Runtime;
        if (runtime.DatabaseName is null || !ResourceName.IsMatch(runtime.DatabaseName) ||
            runtime.DatabaseRole != ExpectedRole || runtime.SchemaName != ExpectedSchema ||
            runtime.KeyVaultSecretName != ExpectedSecretName ||
            runtime.KeyVaultSecretVersion is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(runtime.KeyVaultSecretVersion, "^[0-9a-f]{32}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ProbeException("runtime_target_mismatch");
    }

    private static bool Equal(string actual, string expected) =>
        actual is not null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ValidGuid(string? value) => GuidPattern.IsMatch(value ?? "");
}
