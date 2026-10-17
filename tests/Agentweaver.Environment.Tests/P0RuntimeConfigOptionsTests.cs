using System.Globalization;
using System.Text.Json;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class P0RuntimeConfigOptionsTests
{
    [Fact]
    public void ApprovedP0ProviderSizingPassesTheProviderValidators()
    {
        var configPath = Path.Combine(
            AppContext.BaseDirectory,
            "runtime-configs",
            "environment-runtime-config.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(configPath));
        var data = manifest.RootElement.GetProperty("data");
        string Value(string key) => data.GetProperty(key).GetString()!;
        int Integer(string key) => int.Parse(Value(key), CultureInfo.InvariantCulture);

        // The bootstrap List supplies the image receipt fields independently of this ConfigMap.
        var sandbox = new AgentSandboxOptions(
            Integer("Environment__Sandbox__AgentSandbox__OptionsSchemaVersion"),
            "p1-v1.0.0",
            Value("Environment__Sandbox__AgentSandbox__Namespace"),
            Value("Environment__Sandbox__AgentSandbox__WorkspaceStorageProviderId"),
            $"ghcr.io/agentweaver/agenthost@sha256:{new string('a', 64)}",
            "linux/amd64",
            1,
            Value("Environment__Sandbox__AgentSandbox__RuntimeClassName"),
            Value("Environment__Sandbox__AgentSandbox__ExpectedRuntimeHandler"),
            Value("Environment__Sandbox__AgentSandbox__CpuRequest"),
            Value("Environment__Sandbox__AgentSandbox__MemoryRequest"),
            Integer("Environment__Sandbox__AgentSandbox__ReconciliationTimeoutSeconds"),
            Integer("Environment__Sandbox__AgentSandbox__PollIntervalMilliseconds"),
            new AgentSandboxStartupBudgets(
                Integer("Environment__Sandbox__AgentSandbox__StartupBudgets__ScheduledSeconds"),
                Integer("Environment__Sandbox__AgentSandbox__StartupBudgets__ImageReadySeconds"),
                Integer("Environment__Sandbox__AgentSandbox__StartupBudgets__StartedSeconds"),
                Integer("Environment__Sandbox__AgentSandbox__StartupBudgets__ConfiguredSeconds"),
                Integer("Environment__Sandbox__AgentSandbox__StartupBudgets__ReadySeconds"),
                Integer("Environment__Sandbox__AgentSandbox__StartupBudgets__TotalSeconds")))
            .Validate();

        var storage = new AzureFilesCsiOptions(
            Integer("Environment__Storage__AzureFiles__OptionsSchemaVersion"),
            "p1-v1.0.0",
            Value("Environment__Storage__AzureFiles__Namespace"),
            Value("Environment__Storage__AzureFiles__StorageClassName"),
            long.Parse(
                Value("Environment__Storage__AzureFiles__MaximumCapacityGiB"),
                CultureInfo.InvariantCulture),
            Integer("Environment__Storage__AzureFiles__ProvisioningTimeoutSeconds"),
            Integer("Environment__Storage__AzureFiles__PollIntervalMilliseconds"))
            .Validate();

        Assert.Equal("1", sandbox.CpuRequest);
        Assert.Equal("2Gi", sandbox.MemoryRequest);
        Assert.Equal(100, storage.MaximumCapacityGiB);
    }
}
