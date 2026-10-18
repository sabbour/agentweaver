using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Environment.Tests;

/// <summary>Checks the Cilium options reader against the exact immutable deployment ConfigMap.</summary>
public sealed class CiliumEgressConfigurationTests
{
    private const string NamespaceLabel = "k8s:io.kubernetes.pod.namespace";
    private const string ConfigMapPath = "runtime-configs/environment-cilium-config.json";

    /// <summary>Loads all nine shipped selectors with their literal Kubernetes namespace labels intact.</summary>
    [Fact]
    public void ShippedCiliumConfigBindsAndValidatesItsNamespaceSelectors()
    {
        var options = Program.ReadCiliumOptions(CreateConfiguration());

        Assert.Equal("agentweaver", options.Namespace);
        Assert.Equal(9, options.EndpointSelectors.Count);
        foreach (var (service, labels) in options.EndpointSelectors)
        {
            var identity = service.Split('/');
            Assert.Equal(identity[0], labels[NamespaceLabel]);
            Assert.Equal(identity[1], labels["agentweaver.io/workload"]);
        }
    }

    /// <summary>Confirms that preserving literal label keys does not weaken the namespace-pin requirement.</summary>
    [Fact]
    public void ShippedCiliumConfigStillRejectsASelectorWithoutItsNamespacePin()
    {
        var exception = Assert.Throws<CiliumPolicyException>(() =>
            Program.ReadCiliumOptions(CreateConfiguration(removeKnowledgeNamespacePin: true)));

        Assert.Equal("invalid_provider_options", exception.Code);
        Assert.Contains("must pin the target namespace", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects null label values instead of silently widening a service selector.</summary>
    [Fact]
    public void ShippedCiliumConfigRejectsNullValuedSelectorLabels()
    {
        var exception = Assert.Throws<CiliumPolicyException>(() =>
            Program.ReadCiliumOptions(CreateConfiguration(setKnowledgeWorkloadToNull: true)));

        Assert.Equal("invalid_provider_options", exception.Code);
        Assert.Contains("nonempty match labels", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Loads the ConfigMap's exact appsettings value and overlays only Deployment-supplied metadata.</summary>
    private static IConfiguration CreateConfiguration(
        bool removeKnowledgeNamespacePin = false,
        bool setKnowledgeWorkloadToNull = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigMapPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var settingsText = manifest.RootElement
            .GetProperty("data")
            .GetProperty("appsettings.Production.json")
            .GetString() ?? throw new InvalidOperationException("The Cilium ConfigMap has no appsettings value.");
        if (removeKnowledgeNamespacePin)
        {
            var settings = JsonNode.Parse(settingsText)
                ?? throw new InvalidOperationException("The Cilium appsettings value is empty.");
            settings["Environment"]!["NetworkPolicy"]!["Cilium"]!["EndpointSelectors"]!
                ["agentweaver/knowledge"]!.AsObject().Remove(NamespaceLabel);
            settingsText = settings.ToJsonString();
        }
        if (setKnowledgeWorkloadToNull)
        {
            var settings = JsonNode.Parse(settingsText)
                ?? throw new InvalidOperationException("The Cilium appsettings value is empty.");
            settings["Environment"]!["NetworkPolicy"]!["Cilium"]!["EndpointSelectors"]!
                ["agentweaver/knowledge"]!["agentweaver.io/workload"] = null;
            settingsText = settings.ToJsonString();
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(settingsText));
        return new ConfigurationBuilder()
            .AddJsonStream(stream)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Environment:NetworkPolicy:Cilium:AdapterVersion"] = "1.0.0",
                ["Environment:NetworkPolicy:Cilium:OptionsRevision"] = "p1-v1.0.0"
            })
            .Build();
    }
}
