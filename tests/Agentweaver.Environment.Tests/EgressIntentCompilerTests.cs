using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class EgressIntentCompilerTests
{
    private static NetworkEgressRule Fqdn(
        string name,
        NetworkEgressPurpose purpose = NetworkEgressPurpose.ModelEndpoint,
        int port = 443,
        EgressProtocol protocol = EgressProtocol.Tcp) =>
        new(purpose, NetworkEgressDestinationKind.Fqdn, name, port, protocol);

    private static NetworkEgressRule Cidr(
        string range,
        NetworkEgressPurpose purpose = NetworkEgressPurpose.SourceControl,
        int port = 443) =>
        new(purpose, NetworkEgressDestinationKind.Cidr, range, port, EgressProtocol.Tcp);

    private static NetworkEgressRule Dns(EgressProtocol protocol) =>
        new(NetworkEgressPurpose.DnsResolver,
            NetworkEgressDestinationKind.KubernetesService,
            "kube-system/kube-dns",
            53,
            protocol);

    private static EffectiveNetworkPolicySelection Selection(
        ImmutableArray<NetworkEgressRule> baseline,
        ImmutableArray<NetworkEgressRule>? narrowing,
        ImmutableArray<NetworkEgressRule> required,
        ImmutableArray<NetworkEgressRule>? allowlist = null) =>
        new(
            "project-a",
            "run-a",
            1,
            2,
            3,
            "selection-rev-1",
            [],
            baseline,
            narrowing,
            required,
            allowlist ?? NetworkEgressRuleSemantics.IntersectSets(baseline, narrowing ?? baseline));

    [Fact]
    public void CompilesPurposeGroupedRulesFromBaselineProjectIntersectionAndRunNeeds()
    {
        var baseline = ImmutableArray.Create(
            Fqdn("*.npmjs.org", NetworkEgressPurpose.PackageRegistry),
            Cidr("203.0.113.0/24"),
            Fqdn("api.example.com"),
            Dns(EgressProtocol.Tcp),
            Dns(EgressProtocol.Udp));
        var narrowing = ImmutableArray.Create(
            Fqdn("registry.npmjs.org", NetworkEgressPurpose.PackageRegistry),
            Cidr("203.0.113.128/25"),
            Fqdn("api.example.com"),
            Dns(EgressProtocol.Tcp),
            Dns(EgressProtocol.Udp));
        var required = narrowing;

        var result = EgressIntentCompiler.Compile(Selection(baseline, narrowing, required));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(required.Length, result.Intent!.Rules.Length);
        Assert.Equal(
            [NetworkEgressPurpose.DnsResolver, NetworkEgressPurpose.ModelEndpoint,
                NetworkEgressPurpose.SourceControl, NetworkEgressPurpose.PackageRegistry],
            result.Intent.PurposeGroups.Select(group => group.Purpose));
        Assert.Contains(result.Intent.Rules, rule =>
            rule.DestinationKind == NetworkEgressDestinationKind.Cidr &&
            rule.Destination == "203.0.113.128/25");
        Assert.DoesNotContain(result.Intent.Rules, rule => rule.Destination == "*.npmjs.org");
    }

    [Fact]
    public void WildcardAndCidrIntersectionAreNarrowingAndNotBroaderThanTheirParents()
    {
        var wildcard = NetworkEgressRuleSemantics.NormalizeSet([Fqdn("*.Example.com.")]).Single();
        var exact = NetworkEgressRuleSemantics.NormalizeSet([Fqdn("api.example.com")]).Single();
        var parentCidr = NetworkEgressRuleSemantics.NormalizeSet([Cidr("203.0.113.99/24")]).Single();
        var childCidr = NetworkEgressRuleSemantics.NormalizeSet([Cidr("203.0.113.128/25")]).Single();

        Assert.True(NetworkEgressRuleSemantics.IsSubset(exact, wildcard));
        Assert.True(NetworkEgressRuleSemantics.IsSubset(childCidr, parentCidr));
        Assert.Equal(exact, NetworkEgressRuleSemantics.Intersect(exact, wildcard));
        Assert.Equal(childCidr, NetworkEgressRuleSemantics.Intersect(parentCidr, childCidr));
        Assert.False(NetworkEgressRuleSemantics.IsSubset(Fqdn("example.com"), wildcard));
        Assert.False(NetworkEgressRuleSemantics.IsSubset(Fqdn("other.test"), wildcard));
        Assert.False(NetworkEgressRuleSemantics.IsSubset(
            Cidr("203.0.114.0/24"),
            parentCidr));
    }

    [Fact]
    public void ProjectWideningAndRunNeedsOutsideTheIntersectionFailClosed()
    {
        var baseline = ImmutableArray.Create(Fqdn("*.example.com"), Dns(EgressProtocol.Tcp), Dns(EgressProtocol.Udp));
        var widening = Selection(
            baseline,
            [Fqdn("*.example.org")],
            [Fqdn("api.example.org")]);
        var outside = Selection(
            baseline,
            [Fqdn("api.example.com")],
            [Fqdn("other.example.com")]);

        Assert.Equal("project_widening", EgressIntentCompiler.Compile(widening).Failure?.Code);
        Assert.Equal("run_need_outside_allowlist", EgressIntentCompiler.Compile(outside).Failure?.Code);
    }

    [Theory]
    [InlineData("https://api.example.com")]
    [InlineData("foo.*.example.com")]
    [InlineData("*.com")]
    public void MalformedOrUnexpressibleFqdnPatternsAreRejected(string pattern)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            NetworkEgressRuleSemantics.NormalizeSet([Fqdn(pattern)]));
        Assert.NotEmpty(exception.Message);
    }

    [Theory]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0")]
    [InlineData("203.0.113.0/24/8")]
    public void MalformedCidrsAreRejected(string cidr)
    {
        Assert.Throws<ArgumentException>(() =>
            NetworkEgressRuleSemantics.NormalizeSet([Cidr(cidr)]));
    }

    [Fact]
    public void PortsProtocolsAndPurposeRemainPartOfTheNarrowingBoundary()
    {
        var allowed = Fqdn("api.example.com", NetworkEgressPurpose.ModelEndpoint, 443);
        Assert.False(NetworkEgressRuleSemantics.IsSubset(
            Fqdn("api.example.com", NetworkEgressPurpose.ModelEndpoint, 8443), allowed));
        Assert.False(NetworkEgressRuleSemantics.IsSubset(
            Fqdn("api.example.com", NetworkEgressPurpose.PackageRegistry, 443), allowed));
        Assert.False(NetworkEgressRuleSemantics.IsSubset(
            Fqdn("api.example.com", NetworkEgressPurpose.ModelEndpoint, 443, EgressProtocol.Udp), allowed));
    }

    [Fact]
    public void FqdnRulesRequireExplicitAdmittedDnsForBothTransports()
    {
        var selection = Selection(
            [Fqdn("api.example.com")],
            null,
            [Fqdn("api.example.com")]);

        var result = EgressIntentCompiler.Compile(selection);

        Assert.Equal("missing_dns_resolver", result.Failure?.Code);
    }

    [Theory]
    [InlineData("vault.azure.net")]
    [InlineData("tenant.vault.azure.net")]
    [InlineData("*.vault.azure.net")]
    [InlineData("*.azure.net")]
    [InlineData("*.usgovcloudapi.net")]
    [InlineData("*.azure.cn")]
    [InlineData("*.microsoftazure.de")]
    [InlineData("login.microsoftonline.com")]
    [InlineData("region.login.microsoftonline.com")]
    [InlineData("*.microsoftonline.com")]
    [InlineData("*.microsoft.com")]
    [InlineData("*.chinacloudapi.cn")]
    [InlineData("login.windows.net")]
    [InlineData("*.windows.net")]
    public void KeyVaultAndEntraAccessOrWildcardAncestorsAreNeverCompiledForAgentHost(string host)
    {
        var rule = Fqdn(host);
        var baseline = ImmutableArray.Create(rule, Dns(EgressProtocol.Tcp), Dns(EgressProtocol.Udp));
        var result = EgressIntentCompiler.Compile(Selection(baseline, null, baseline));
        Assert.Equal("direct_secret_egress_forbidden", result.Failure?.Code);
    }

    [Fact]
    public void PublicHttpsAndRemoteMcpNeedsAreMarkedAsL7Requirements()
    {
        var rule = Fqdn("mcp.example.com", NetworkEgressPurpose.RemoteMcp);
        var baseline = ImmutableArray.Create(rule, Dns(EgressProtocol.Tcp), Dns(EgressProtocol.Udp));
        var result = EgressIntentCompiler.Compile(Selection(baseline, null, baseline));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.True(result.Intent!.RequiresL7Mediation);
    }
}
