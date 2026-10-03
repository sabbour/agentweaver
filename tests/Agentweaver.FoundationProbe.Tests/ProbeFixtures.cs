using Agentweaver.FoundationProbe;

namespace Agentweaver.FoundationProbe.Tests;

internal static class ProbeFixtures
{
    public static ProbeSource Source { get; } = new(new string('a', 40), new string('b', 40), new string('c', 64));

    public static ProbeTarget Target() => new()
    {
        SourceSha = Source.Sha,
        SourceTree = Source.Tree,
        SourceHash = Source.InfrastructureHash,
        SubscriptionId = "11111111-1111-1111-1111-111111111111",
        TenantId = "22222222-2222-2222-2222-222222222222",
        ResourceGroup = "aw-v1-p0",
        ResourceGroupId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0",
        DeploymentName = $"aw-v1-p0-{Source.Sha[..12]}",
        DeploymentId =
            $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.Resources/deployments/aw-v1-p0-{Source.Sha[..12]}",
        AksOidcIssuerUrl = "https://eastus.oic.prod-aks.azure.com/22222222-2222-2222-2222-222222222222/cluster-id/",
        FoundationProbeIdentity = new ProbeIdentity
        {
            Name = "foundation-probe",
            ResourceId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aw-v1-p0-id-foundation-probe",
            ClientId = "33333333-3333-3333-3333-333333333333",
            PrincipalObjectId = "44444444-4444-4444-4444-444444444444",
            Namespace = "agentweaver-v1-p0",
            ServiceAccount = "foundation-probe",
        },
        FoundationResources = new ProbeResources
        {
            ClusterId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.ContainerService/managedClusters/aw-v1-p0-aks",
            KeyVaultId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.KeyVault/vaults/aw-v1-p0-kv",
            VaultUri = "https://aw-v1-p0-kv.vault.azure.net/",
            StorageAccountId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.Storage/storageAccounts/awv1p0blob",
            BlobContainerId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.Storage/storageAccounts/awv1p0blob/blobServices/default/containers/platform-artifacts",
            BlobContainerUri = "https://awv1p0blob.blob.core.windows.net/platform-artifacts",
            PostgresServerId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.DBforPostgreSQL/flexibleServers/aw-v1-p0-pg",
            PostgresHost = "aw-v1-p0-pg.postgres.database.azure.com",
            MonitorWorkspaceResourceId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.OperationalInsights/workspaces/aw-v1-p0-law",
            MonitorWorkspaceId = "55555555-5555-5555-5555-555555555555",
            AppInsightsResourceId =
                "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/aw-v1-p0/providers/Microsoft.Insights/components/aw-v1-p0-appi",
        },
        Runtime = new ProbeRuntime
        {
            DatabaseName = "agentweaver",
            DatabaseRole = "foundation_probe_runtime",
            SchemaName = "foundation_probe",
            KeyVaultSecretName = "foundation-probe",
            KeyVaultSecretVersion = new string('d', 32),
        },
    };

    public static ProbeIdentityEvidence Identity(ProbeTarget target) => new(
        target.AksOidcIssuerUrl,
        "system:serviceaccount:agentweaver-v1-p0:foundation-probe",
        "api://AzureADTokenExchange");

    public static MonitorConfigurationEvidence MonitorConfiguration(ProbeTarget target) =>
        ProbeMonitorConfiguration.Validate(
            "InstrumentationKey=66666666-6666-6666-6666-666666666666;" +
            "IngestionEndpoint=https://eastus-1.in.applicationinsights.azure.com/",
            target);

    public static string Token(
        string? issuer = null,
        string? subject = null,
        string audience = "api://AzureADTokenExchange")
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer ?? Target().AksOidcIssuerUrl,
            sub = subject ?? "system:serviceaccount:agentweaver-v1-p0:foundation-probe",
            aud = audience,
        });
        return $"eyJhbGciOiJub25lIn0.{Base64Url(payload)}.signature";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
