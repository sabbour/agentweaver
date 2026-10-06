using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

public enum NetworkEgressPurpose
{
    DnsResolver,
    ControlPlane,
    ModelEndpoint,
    SourceControl,
    PackageRegistry,
    RemoteMcp,
    PublicHttps
}

public enum NetworkEgressDestinationKind
{
    Fqdn,
    Cidr,
    KubernetesService
}

public enum EgressProtocol
{
    Tcp,
    Udp
}

public sealed record NetworkEgressRule(
    NetworkEgressPurpose Purpose,
    NetworkEgressDestinationKind DestinationKind,
    string Destination,
    int Port,
    EgressProtocol Protocol);

public static class NetworkEgressRuleSemantics
{
    private static readonly Regex KubernetesName = new(
        "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ImmutableArray<NetworkEgressRule> NormalizeSet(
        IEnumerable<NetworkEgressRule> rules,
        string location = "egress")
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = ImmutableArray.CreateBuilder<NetworkEgressRule>();
        foreach (var rule in rules)
        {
            if (rule is null || !Enum.IsDefined(rule.Purpose) ||
                !Enum.IsDefined(rule.DestinationKind) || !Enum.IsDefined(rule.Protocol) ||
                rule.Port is < 1 or > 65535)
                throw new ArgumentException($"{location} contains an invalid destination, port, protocol, or purpose.");

            var destination = rule.DestinationKind switch
            {
                NetworkEgressDestinationKind.Fqdn => NormalizeFqdn(rule.Destination),
                NetworkEgressDestinationKind.Cidr => NormalizeCidr(rule.Destination),
                NetworkEgressDestinationKind.KubernetesService => NormalizeService(rule.Destination),
                _ => throw new ArgumentException($"{location} contains an unexpressible destination type.")
            };
            if (rule.Purpose == NetworkEgressPurpose.DnsResolver &&
                (rule.DestinationKind != NetworkEgressDestinationKind.KubernetesService ||
                 !string.Equals(destination, "kube-system/kube-dns", StringComparison.Ordinal) ||
                 rule.Port != 53))
                throw new ArgumentException(
                    $"{location} DNS resolver rules must target kube-system/kube-dns on port 53.");
            if (rule.DestinationKind == NetworkEgressDestinationKind.KubernetesService &&
                rule.Purpose == NetworkEgressPurpose.RemoteMcp)
                throw new ArgumentException("Remote MCP egress requires L7 mediation, not a direct service allow.");

            result.Add(rule with { Destination = destination });
        }

        var normalized = result.ToImmutable();
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException($"{location} contains duplicate destinations.");
        return normalized
            .OrderBy(rule => rule.Purpose)
            .ThenBy(rule => rule.DestinationKind)
            .ThenBy(rule => rule.Destination, StringComparer.Ordinal)
            .ThenBy(rule => rule.Protocol)
            .ThenBy(rule => rule.Port)
            .ToImmutableArray();
    }

    public static bool IsSubset(NetworkEgressRule candidate, NetworkEgressRule allowed)
    {
        if (candidate is null || allowed is null ||
            !Enum.IsDefined(candidate.Purpose) ||
            !Enum.IsDefined(allowed.Purpose) ||
            !Enum.IsDefined(candidate.DestinationKind) ||
            !Enum.IsDefined(allowed.DestinationKind) ||
            !Enum.IsDefined(candidate.Protocol) ||
            !Enum.IsDefined(allowed.Protocol) ||
            candidate.Port is < 1 or > 65535 ||
            allowed.Port is < 1 or > 65535 ||
            candidate.Purpose != allowed.Purpose ||
            candidate.DestinationKind != allowed.DestinationKind ||
            candidate.Port != allowed.Port ||
            candidate.Protocol != allowed.Protocol)
            return false;

        try
        {
            var candidateDestination = NormalizeDestination(candidate.DestinationKind, candidate.Destination);
            var allowedDestination = NormalizeDestination(allowed.DestinationKind, allowed.Destination);
            return candidate.DestinationKind switch
            {
                NetworkEgressDestinationKind.Fqdn =>
                    FqdnIsSubset(candidateDestination, allowedDestination),
                NetworkEgressDestinationKind.Cidr =>
                    CidrIsSubset(candidateDestination, allowedDestination),
                NetworkEgressDestinationKind.KubernetesService =>
                    string.Equals(candidateDestination, allowedDestination, StringComparison.Ordinal),
                _ => false
            };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static NetworkEgressRule? Intersect(
        NetworkEgressRule left,
        NetworkEgressRule right)
    {
        if (IsSubset(left, right)) return left;
        if (IsSubset(right, left)) return right;
        return null;
    }

    public static ImmutableArray<NetworkEgressRule> IntersectSets(
        IEnumerable<NetworkEgressRule> left,
        IEnumerable<NetworkEgressRule> right)
    {
        var first = NormalizeSet(left);
        var second = NormalizeSet(right);
        return NormalizeSet(first
            .SelectMany(leftRule => second
                .Select(rightRule => Intersect(leftRule, rightRule))
                .OfType<NetworkEgressRule>())
            .Distinct());
    }

    public static bool IsContainedBy(
        NetworkEgressRule candidate,
        IEnumerable<NetworkEgressRule> allowed) =>
        allowed.Any(rule => IsSubset(candidate, rule));

    private static string NormalizeFqdn(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') ||
            value.Contains('\\') || value.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("FQDN egress destinations must be host names or a single-label wildcard pattern.");

        var host = value.Trim();
        if (host.EndsWith("..", StringComparison.Ordinal))
            throw new ArgumentException("FQDN egress destination is malformed.");
        if (host.EndsWith(".", StringComparison.Ordinal))
            host = host[..^1];
        host = host.ToLowerInvariant();
        var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
        if (host.Contains('*') && (!wildcard || host.IndexOf('*', 1) >= 0))
            throw new ArgumentException("FQDN wildcards are supported only as a leading '*.' label.");
        var suffix = wildcard ? host[2..] : host;
        try
        {
            suffix = new IdnMapping().GetAscii(suffix).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("FQDN egress destination is malformed.");
        }

        if (suffix.Length > 253 || !suffix.Contains('.', StringComparison.Ordinal) ||
            Uri.CheckHostName(suffix) != UriHostNameType.Dns ||
            IPAddress.TryParse(suffix, out _) ||
            suffix.Split('.').Any(label => label.Length is < 1 or > 63))
            throw new ArgumentException("FQDN egress destination is malformed or is an IP address.");
        if (wildcard && suffix.Split('.').Length < 2)
            throw new ArgumentException("FQDN wildcard patterns must name a registrable multi-label suffix.");
        return wildcard ? $"*.{suffix}" : suffix;
    }

    private static string NormalizeCidr(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("CIDR egress destination is malformed.");
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            throw new ArgumentException("CIDR egress destination must include a valid network prefix.");
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            throw new ArgumentException("Scoped IPv6 addresses are not valid egress CIDRs.");
        var bytes = address.GetAddressBytes();
        var maximum = bytes.Length * 8;
        if (prefix < 1 || prefix > maximum)
            throw new ArgumentException("CIDR prefix is outside the address-family range.");
        for (var i = 0; i < bytes.Length; i++)
        {
            var remaining = prefix - (i * 8);
            var mask = remaining >= 8 ? byte.MaxValue : remaining <= 0 ? (byte)0 : (byte)(0xff << (8 - remaining));
            bytes[i] &= mask;
        }
        return $"{new IPAddress(bytes)}/{prefix.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeService(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Kubernetes service destination must be namespace/name.");
        var parts = value.Trim().ToLowerInvariant().Split('/');
        if (parts.Length != 2 || !KubernetesName.IsMatch(parts[0]) || !KubernetesName.IsMatch(parts[1]))
            throw new ArgumentException("Kubernetes service destination must be a valid namespace/name pair.");
        return $"{parts[0]}/{parts[1]}";
    }

    private static string NormalizeDestination(NetworkEgressDestinationKind kind, string value) =>
        kind switch
        {
            NetworkEgressDestinationKind.Fqdn => NormalizeFqdn(value),
            NetworkEgressDestinationKind.Cidr => NormalizeCidr(value),
            NetworkEgressDestinationKind.KubernetesService => NormalizeService(value),
            _ => throw new ArgumentException("Egress destination kind is not expressible.")
        };

    private static bool FqdnIsSubset(string candidate, string allowed)
    {
        if (string.Equals(candidate, allowed, StringComparison.Ordinal)) return true;
        if (!allowed.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = allowed[2..];
        if (candidate.StartsWith("*.", StringComparison.Ordinal))
            return string.Equals(candidate[2..], suffix, StringComparison.Ordinal);
        var suffixWithSeparator = $".{suffix}";
        if (!candidate.EndsWith(suffixWithSeparator, StringComparison.Ordinal))
            return false;
        var label = candidate[..^suffixWithSeparator.Length];
        return label.Length > 0 && !label.Contains('.');
    }

    private static bool CidrIsSubset(string candidate, string allowed)
    {
        var child = ParseCidr(candidate);
        var parent = ParseCidr(allowed);
        if (child.Address.AddressFamily != parent.Address.AddressFamily ||
            child.Prefix < parent.Prefix)
            return false;
        var childBytes = child.Address.GetAddressBytes();
        var parentBytes = parent.Address.GetAddressBytes();
        var fullBytes = parent.Prefix / 8;
        var remainingBits = parent.Prefix % 8;
        for (var i = 0; i < fullBytes; i++)
            if (childBytes[i] != parentBytes[i]) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (childBytes[fullBytes] & mask) == (parentBytes[fullBytes] & mask);
    }

    private static (IPAddress Address, int Prefix) ParseCidr(string value)
    {
        var parts = value.Split('/');
        _ = IPAddress.TryParse(parts[0], out var address);
        _ = int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix);
        return (address!, prefix);
    }
}

public enum ProjectAuthorityResourceType
{
    Platform,
    Tenant,
    Project
}

public enum ProjectAuthorizationPermission
{
    ReadProjects,
    WriteProjects,
    CreateProjects,
    ReadRunSelection,
    AcceptRunSelection,
    ReadPlatformRuntimeDefaults,
    WritePlatformRuntimeDefaults
}

public sealed record ProjectAuthorizationPermissionGrant(
    ProjectAuthorizationPermission Permission,
    long RoleRevision);

public sealed record EffectiveProjectAuthorization(
    ProjectAuthorityResourceType ResourceType,
    string ResourceId,
    ImmutableArray<ProjectAuthorizationPermissionGrant> Permissions);

public sealed record ProjectAuthorizationContextResponse(
    int ContractVersion,
    string Issuer,
    string ActorId,
    string TenantId,
    long MembershipRevision,
    string? BoundProjectId,
    string? BoundRunId,
    ImmutableArray<EffectiveProjectAuthorization> EffectiveAuthority);

public static class ProjectAuthorizationContextContract
{
    public const int CurrentVersion = 1;
    public const string TenantSelectorHeader = "X-Agentweaver-Tenant";
}

public sealed record EffectiveProviderCandidate(
    ProviderSeam Seam,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    ProviderHostingPattern Hosting,
    ImmutableArray<string> AdvertisedCapabilities,
    ImmutableArray<string> RequiredCapabilities,
    NetworkPolicyLayer? Layer = null);

public sealed record EffectiveProviderSelection(
    ProviderCardinality Cardinality,
    ProviderSeam Seam,
    ImmutableArray<EffectiveProviderCandidate> Candidates,
    string? MeterSource = null);

public sealed record EffectiveNetworkPolicySelection(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    ImmutableArray<EffectiveProviderSelection> Providers,
    ImmutableArray<NetworkEgressRule> EgressBaseline,
    ImmutableArray<NetworkEgressRule>? ProjectEgressNarrowing,
    ImmutableArray<NetworkEgressRule> RequiredEgress,
    ImmutableArray<NetworkEgressRule> EgressAllowlist);
