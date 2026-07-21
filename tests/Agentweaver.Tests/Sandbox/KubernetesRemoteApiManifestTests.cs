using System.Text.RegularExpressions;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class KubernetesRemoteApiManifestTests
{
    private const string RemoteApiUrl =
        "http://agentweaver-api.agentweaver.svc.cluster.local:8080";

    [Fact]
    public void ApiAndWorkerDeployments_ConfigureRemoteUrlWithoutReplacingExistingApiUrl()
    {
        var api = ReadManifest("api-deployment.yaml");
        var worker = ReadManifest("worker-deployment.yaml");

        EnvironmentValue(api, "Agentweaver__ApiBaseUrl").Should().Be("http://localhost:8080");
        EnvironmentValue(api, "Agentweaver__RemoteApiBaseUrl").Should().Be(RemoteApiUrl);
        EnvironmentValue(worker, "Agentweaver__ApiBaseUrl").Should().Be("http://agentweaver-api:8080");
        EnvironmentValue(worker, "Agentweaver__RemoteApiBaseUrl").Should().Be(RemoteApiUrl);
    }

    [Fact]
    public void AgentHostTemplate_UsesClusterDnsAndNarrowSandboxLabels()
    {
        var template = ReadManifest("sandbox-template-agenthost.yaml");

        template.Should().Contain("dnsPolicy: ClusterFirst");
        template.Should().MatchRegex(@"(?m)^\s+app: agentweaver-agent-host\s*$");
        template.Should().MatchRegex(@"(?m)^\s+agentweaver\.dev/sandbox: ""true""\s*$");
    }

    [Fact]
    public void ApiIngress_AllowsOnlyDoublyLabelledAgentHostPodsOnTcp8080()
    {
        var document = DocumentNamed(
            ReadManifest("networkpolicy-agenthost.yaml"),
            "allow-agenthost-to-api");

        document.Should().MatchRegex(
            @"(?s)podSelector:\s+matchLabels:\s+app: agentweaver-api.*" +
            @"from:\s+- podSelector:\s+matchLabels:\s+" +
            @"app: agentweaver-agent-host\s+agentweaver\.dev/sandbox: ""true"".*" +
            @"ports:\s+- protocol: TCP\s+port: 8080");
        document.Should().NotContain("namespaceSelector");
    }

    [Fact]
    public void AgentHostEgress_ExplicitlyAllowsMcpOnTcp8080()
    {
        var document = DocumentNamed(
            ReadManifest("networkpolicy-agenthost-egress.yaml"),
            "agenthost-egress-allowlist");

        document.Should().Contain("app.kubernetes.io/component: agent-host");
        document.Should().Contain("app: agentweaver-mcp");
        document.Should().Contain("port: 8080");
    }

    [Fact]
    public void ExistingAgentHostPolicies_SelectRenamedAgentHostLabel()
    {
        var files = new[]
        {
            "networkpolicy-agenthost.yaml",
            "networkpolicy-agenthost-api-egress.yaml",
            "networkpolicy-sandbox.yaml",
            "networkpolicy-worker.yaml",
            "cilium-network-policy-sandbox.yaml",
        };

        files.Select(ReadManifest).Should().OnlyContain(
            manifest => !manifest.Contains("app: agentweaver-sandbox", StringComparison.Ordinal)
                && manifest.Contains("app: agentweaver-agent-host", StringComparison.Ordinal));
    }

    private static string EnvironmentValue(string manifest, string variable)
    {
        var match = Regex.Match(
            manifest,
            $@"(?m)^\s+- name: {Regex.Escape(variable)}\s*\r?\n\s+value: (?<value>\S+)\s*$");

        match.Success.Should().BeTrue($"{variable} must be configured");
        return match.Groups["value"].Value;
    }

    private static string DocumentNamed(string manifest, string name) =>
        Regex.Split(manifest, @"(?m)^---\s*$")
            .Single(document => Regex.IsMatch(
                document,
                $@"(?m)^\s+name: {Regex.Escape(name)}\s*$"));

    private static string ReadManifest(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "k8s", "base", fileName));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
