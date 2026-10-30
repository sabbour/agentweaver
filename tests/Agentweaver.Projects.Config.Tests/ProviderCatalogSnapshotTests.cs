using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>Checks that the committed provider catalog passes the real loader and matches consumer revisions.</summary>
public sealed class ProviderCatalogSnapshotTests
{
    private const string SourceCommit = "c24d0000fe5fc6163e4f38594c39dff782d3a46d";
    private const string P1Revision = "p1-v1.0.0";

    [Fact]
    public void CheckedInConfigMapMatchesSnapshotAndPassesTheRealCatalogValidator()
    {
        using var snapshot = JsonDocument.Parse(ReadCatalogFile("projects-config-provider-catalog.snapshot.json"));
        using var manifest = JsonDocument.Parse(ReadCatalogFile("projects-config-provider-catalog.json"));
        var snapshotRoot = snapshot.RootElement;
        var configMap = manifest.RootElement;

        Assert.Equal(SourceCommit, snapshotRoot.GetProperty("sourceCommit").GetString());
        Assert.Equal("v1", configMap.GetProperty("apiVersion").GetString());
        Assert.Equal("ConfigMap", configMap.GetProperty("kind").GetString());
        Assert.True(configMap.GetProperty("immutable").GetBoolean());
        var metadata = configMap.GetProperty("metadata");
        Assert.Equal("projects-config-provider-catalog", metadata.GetProperty("name").GetString());
        Assert.Equal("agentweaver", metadata.GetProperty("namespace").GetString());
        Assert.Equal(SourceCommit, metadata.GetProperty("annotations")
            .GetProperty("agentweaver.io/provider-catalog-product-source").GetString());

        var snapshotCatalog = snapshotRoot.GetProperty("ProjectsConfig").GetProperty("ProviderCatalog");
        var flattenedSnapshot = Flatten(snapshotCatalog);
        var configMapData = configMap.GetProperty("data").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
        var flattenedConfigMap = configMapData.ToDictionary(
            entry => entry.Key.Replace("__", ":", StringComparison.Ordinal),
            entry => entry.Value,
            StringComparer.Ordinal);
        Assert.Equal(
            flattenedSnapshot.OrderBy(entry => entry.Key),
            flattenedConfigMap.OrderBy(entry => entry.Key));

        var configurationValues = configMapData.ToDictionary(
            entry => $"ProjectsConfig:ProviderCatalog:{entry.Key.Replace("__", ":", StringComparison.Ordinal)}",
            entry => (string?)entry.Value,
            StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var catalog = ProviderCatalogConfiguration.Load(configuration);
        var registrationIds = Registrations(snapshotCatalog)
            .Select(registration => registration.GetProperty("Id").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "postgres.native-sessions",
                "postgres.native-messaging",
                "postgres.native-memory",
                "agent-sandbox",
                "azure-files-csi",
                "agt.dotnet-yaml",
                "github",
                "copilot.usage-cost",
                "cilium"
            ],
            registrationIds);
        Assert.Equal(8, registrationIds.Count(id => id != "postgres.native-messaging"));

        AssertDefault(catalog, ProviderSeam.Sessions, "postgres.native-sessions");
        AssertDefault(catalog, ProviderSeam.Messaging, "postgres.native-messaging");
        AssertDefault(catalog, ProviderSeam.Memory, "postgres.native-memory");
        AssertDefault(catalog, ProviderSeam.Sandbox, "agent-sandbox");
        AssertDefault(catalog, ProviderSeam.Storage, "azure-files-csi");
        AssertDefault(catalog, ProviderSeam.Policy, "agt.dotnet-yaml");
        AssertDefault(catalog, ProviderSeam.SourceControl, "github");
        Assert.True(catalog.TryGetProvider("cilium", out _));
        Assert.True(catalog.TryGetLayer(NetworkPolicyLayer.L3L4, out _));
        Assert.True(catalog.TryGetMeterSource("copilot.nano_aiu", out var costProvider));
        Assert.Equal("copilot.usage-cost", costProvider);

        Assert.Empty(snapshotCatalog.GetProperty("PermittedOverrides").EnumerateArray());
        Assert.Empty(snapshotCatalog.GetProperty("OrderedSelections").EnumerateArray());
        var networkLayer = Assert.Single(snapshotCatalog.GetProperty("LayerSelections").EnumerateArray());
        Assert.Equal("L3L4", networkLayer.GetProperty("Layer").GetString());
        Assert.Equal("cilium", networkLayer.GetProperty("ProviderId").GetString());
    }

    /// <summary>
    /// Verifies runtime inputs use service-named ConfigMaps except for the immutable Broker v2 map.
    /// </summary>
    [Fact]
    public void CatalogRevisionsMatchReleaseDefaultsAndGeneratedRuntimeInputs()
    {
        using var snapshot = JsonDocument.Parse(ReadCatalogFile("projects-config-provider-catalog.snapshot.json"));
        var snapshotCatalog = snapshot.RootElement.GetProperty("ProjectsConfig").GetProperty("ProviderCatalog");
        var catalog = LoadConfigMapCatalog();
        var eventProgram = ReadCatalogFile("EventsAndSessions.Program.cs");
        var knowledgeOptions = ReadCatalogFile("KnowledgeOptions.cs");
        var sandboxOptions = ReadCatalogFile("AgentSandboxOptions.cs");
        var storageOptions = ReadCatalogFile("AzureFilesCsiOptions.cs");
        var environmentProgram = ReadCatalogFile("Environment.Program.cs");
        var orchestratorProgram = ReadCatalogFile("Orchestrator.Program.cs");
        var agtPolicyProvider = ReadCatalogFile("AgtPolicyProvider.cs");
        var costOptions = ReadCatalogFile("CopilotCostProviderOptions.cs");
        var costProviderSource = ReadCatalogFile("CopilotCostProvider.cs");
        var runtimeList = ReadCatalogFile("runtime-list.mjs");
        var contracts = ReadCatalogFile("contracts.md");
        var providerSeams = ReadCatalogFile("provider-seams.md");
        var projectConfigDocs = ReadCatalogFile("projects-config.md");

        Assert.Contains(
            "providerSection[\"OptionsRevision\"] ?? \"native-postgres-v1\"",
            eventProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "messagingSection[\"OptionsRevision\"] ?? \"native-postgres-messaging-v1\"",
            eventProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "section[\"OptionsRevision\"] ?? \"native-postgres-memory-v1\"",
            knowledgeOptions,
            StringComparison.Ordinal);
        Assert.Contains("defaults to `native-postgres-v1`.", contracts, StringComparison.Ordinal);
        Assert.Contains("defaults to `native-postgres-messaging-v1`.", contracts, StringComparison.Ordinal);

        AssertRegistrationRevision(catalog, "postgres.native-sessions", "native-postgres-v1");
        AssertRegistrationRevision(catalog, "postgres.native-messaging", "native-postgres-messaging-v1");
        AssertRegistrationRevision(catalog, "postgres.native-memory", "native-postgres-memory-v1");
        AssertRegistrationRevision(catalog, "cilium", P1Revision);
        var cilium = Registrations(snapshotCatalog)
            .Single(registration => registration.GetProperty("Id").GetString() == "cilium");
        Assert.Equal(
            cilium.GetProperty("AdapterVersion").GetString(),
            ReadRuntimeValue(runtimeList, "Environment__NetworkPolicy__Cilium__AdapterVersion"));
        AssertRuntimeRevisionMatchesInput(
            catalog, runtimeList, "cilium", "Environment__NetworkPolicy__Cilium__OptionsRevision");

        AssertRuntimeRevision(
            catalog,
            projectConfigDocs,
            runtimeList,
            "agent-sandbox",
            "Environment__Sandbox__AgentSandbox__OptionsRevision",
            "Environment:Sandbox:AgentSandbox:OptionsRevision",
            environmentProgram);
        AssertRuntimeRevision(
            catalog,
            projectConfigDocs,
            runtimeList,
            "azure-files-csi",
            "Environment__Storage__AzureFiles__OptionsRevision",
            "Environment:Storage:AzureFiles:OptionsRevision",
            environmentProgram);
        AssertRuntimeRevisionMatchesInput(
            catalog, runtimeList, "agt.dotnet-yaml", "AgentGovernance__Policy__OptionsRevision");
        Assert.Contains(
            "`AgentGovernance__Policy__OptionsRevision` | `p1-v1.0.0`",
            projectConfigDocs,
            StringComparison.Ordinal);

        Assert.Contains(
            "configMapRef: {\n      name: identity ? 'identity-broker-runtime-config-v3' : `${name}-runtime-config`,\n      optional: false,",
            runtimeList.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("'environment'", runtimeList, StringComparison.Ordinal);
        Assert.Contains("'orchestrator'", runtimeList, StringComparison.Ordinal);
        Assert.Contains("'events-and-sessions'", runtimeList, StringComparison.Ordinal);
        Assert.Contains(
            "EventsAndSessions:Cost:Copilot",
            eventProgram,
            StringComparison.Ordinal);
        AssertRegistrationRevision(catalog, "copilot.usage-cost", P1Revision);
        Assert.Contains(
            "`EventsAndSessions__Cost__Copilot__OptionsRevision` | `p1-v1.0.0`",
            projectConfigDocs,
            StringComparison.Ordinal);
        AssertRuntimeRevisionMatchesInput(
            catalog,
            runtimeList,
            "copilot.usage-cost",
            "EventsAndSessions__Cost__Copilot__OptionsRevision");
        Assert.Contains(
            "section[nameof(OptionsRevision)] ?? string.Empty",
            costOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "const string sectionName = \"AgentGovernance:Policy\";",
            orchestratorProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "Required(configuration, sectionName + \":OptionsRevision\")",
            orchestratorProgram,
            StringComparison.Ordinal);

        Assert.Contains(
            "Required(section[\"AdapterVersion\"], \"Environment:NetworkPolicy:Cilium:AdapterVersion\")",
            environmentProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "var ciliumOptions = ReadCiliumOptions(builder.Configuration);",
            environmentProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AdapterVersion\"] ??",
            environmentProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "Coordinator decision: adapter code version shipped in Environment 1.0.0",
            Regex.Replace(projectConfigDocs, @"\s+", " "),
            StringComparison.Ordinal);
        Assert.Contains("CurrentOptionsSchemaVersion = 2", sandboxOptions, StringComparison.Ordinal);
        Assert.Contains("CurrentOptionsSchemaVersion = 1", storageOptions, StringComparison.Ordinal);
        Assert.Contains("OptionsSchemaVersion = 1", agtPolicyProvider, StringComparison.Ordinal);
        Assert.Contains("CurrentOptionsSchemaVersion = 2", costOptions, StringComparison.Ordinal);
        Assert.Contains(
            "`Environment__Sandbox__AgentSandbox__OptionsSchemaVersion` | `2`",
            projectConfigDocs,
            StringComparison.Ordinal);
        Assert.Contains(
            "`Environment__Storage__AzureFiles__OptionsSchemaVersion` | `1`",
            projectConfigDocs,
            StringComparison.Ordinal);
        Assert.Contains(
            "`EventsAndSessions__Cost__Copilot__OptionsSchemaVersion` | `2`",
            projectConfigDocs,
            StringComparison.Ordinal);

        Assert.Contains("The default `agent-sandbox` adapter", providerSeams, StringComparison.Ordinal);
        Assert.Contains("cutover Azure provider uses Azure Files CSI", providerSeams, StringComparison.Ordinal);
        Assert.Contains("Cilium on AKS is the default L3/L4/FQDN adapter", providerSeams, StringComparison.Ordinal);
        Assert.Contains("Native Postgres remains the default.", providerSeams, StringComparison.Ordinal);
        Assert.Contains(
            "| P1 Core and defaults | Native Sessions/Memory, agent-sandbox, Azure Files, Cilium intent, AGT, GitHub, Copilot cost ledger;",
            providerSeams,
            StringComparison.Ordinal);
        Assert.Contains(
            "MeterSource = SdkMeterSources.CopilotNanoAiu",
            costProviderSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const string AiCreditUnit = \"AIC\";",
            costProviderSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public const decimal NanoAiuPerAiCredit = 1_000_000_000m;",
            costOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "CopilotNanoAiu = \"copilot.nano_aiu\"",
            ReadCatalogFile("SdkUsageContracts.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "copies the resolved catalog candidate's `OptionsRevision` into the accepted",
            projectConfigDocs,
            StringComparison.Ordinal);

        foreach (var providerId in new[]
                 {
                     "agent-sandbox",
                     "azure-files-csi",
                     "agt.dotnet-yaml",
                     "github",
                     "copilot.usage-cost"
                 })
        {
            AssertRegistrationRevision(catalog, providerId, P1Revision);
        }

        var sourceControlResolution = new ProviderResolver(catalog).Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet.Create(StringComparer.Ordinal, SourceControlCapabilities.RepositoryRead)));
        Assert.True(sourceControlResolution.IsSuccess, sourceControlResolution.Error?.Message);
        Assert.Equal(P1Revision, sourceControlResolution.Value!.Candidate!.OptionsRevision);
    }

    private static void AssertRuntimeRevision(
        ProviderCatalog catalog,
        string documentation,
        string runtimeList,
        string providerId,
        string runtimeKey,
        string configurationPath,
        string source)
    {
        AssertRegistrationRevision(catalog, providerId, P1Revision);
        AssertRuntimeRevisionMatchesInput(catalog, runtimeList, providerId, runtimeKey);
        Assert.Contains($"`{runtimeKey}` | `{P1Revision}`", documentation, StringComparison.Ordinal);
        Assert.Contains(
            $"Required(section[\"OptionsRevision\"], \"{configurationPath}\")",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertRuntimeRevisionMatchesInput(
        ProviderCatalog catalog,
        string runtimeList,
        string providerId,
        string runtimeKey)
    {
        Assert.True(catalog.TryGetProvider(providerId, out var registration));
        Assert.Equal(registration!.OptionsRevision, ReadRuntimeValue(runtimeList, runtimeKey));
    }

    private static string ReadRuntimeValue(string runtimeList, string runtimeKey)
    {
        var match = Regex.Match(
            runtimeList,
            $@"name: '{Regex.Escape(runtimeKey)}',\s*value: '([^']+)'");
        Assert.True(match.Success, $"The bootstrap List must supply {runtimeKey}.");
        return match.Groups[1].Value;
    }

    private static ProviderCatalog LoadConfigMapCatalog()
    {
        using var manifest = JsonDocument.Parse(ReadCatalogFile("projects-config-provider-catalog.json"));
        var values = manifest.RootElement.GetProperty("data").EnumerateObject()
            .ToDictionary(
                property => $"ProjectsConfig:ProviderCatalog:{property.Name.Replace("__", ":", StringComparison.Ordinal)}",
                property => (string?)property.Value.GetString(),
                StringComparer.Ordinal);
        return ProviderCatalogConfiguration.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build());
    }

    private static void AssertDefault(ProviderCatalog catalog, ProviderSeam seam, string expectedId)
    {
        Assert.True(catalog.TryGetDefault(seam, out var providerId));
        Assert.Equal(expectedId, providerId);
        Assert.True(catalog.TryGetProvider(expectedId, out _));
    }

    private static void AssertRegistrationRevision(ProviderCatalog catalog, string id, string expectedRevision)
    {
        Assert.True(catalog.TryGetProvider(id, out var registration));
        Assert.Equal(expectedRevision, registration!.OptionsRevision);
    }

    private static IEnumerable<JsonElement> Registrations(JsonElement catalog) =>
        catalog.GetProperty("Registrations").EnumerateArray();

    private static Dictionary<string, string> Flatten(JsonElement element, string prefix = "")
    {
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Merge(flattened, Flatten(property.Value, Join(prefix, property.Name)));
                break;
            case JsonValueKind.Array:
                for (var index = 0; index < element.GetArrayLength(); index++)
                    Merge(flattened, Flatten(element[index], Join(prefix, index.ToString())));
                break;
            case JsonValueKind.String:
                flattened.Add(prefix, element.GetString()!);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                flattened.Add(prefix, element.GetRawText());
                break;
        }
        return flattened;
    }

    private static void Merge(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (var entry in source)
            target.Add(entry.Key, entry.Value);
    }

    private static string Join(string prefix, string segment) =>
        string.IsNullOrEmpty(prefix) ? segment : $"{prefix}:{segment}";

    private static string ReadCatalogFile(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "provider-catalog", name));
}
