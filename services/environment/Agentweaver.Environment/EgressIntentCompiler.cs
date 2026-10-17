using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

/// <summary>Groups compiled outbound network rules by the admitted purpose that requires them.</summary>
public sealed record EgressPurposeGroup(
    NetworkEgressPurpose Purpose,
    ImmutableArray<NetworkEgressRule> Rules);

/// <summary>
/// Immutable network policy selected for one project run, together with the revisions used to authorize it.
/// </summary>
public sealed record CompiledEgressIntent(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    ImmutableArray<NetworkEgressRule> Rules,
    ImmutableArray<EgressPurposeGroup> PurposeGroups,
    bool RequiresL7Mediation);

/// <summary>Explains why an effective run network policy could not be represented safely.</summary>
public sealed record EgressCompilationFailure(string Code, string Message);

/// <summary>Holds either a compiled egress intent or the validation failure that prevented compilation.</summary>
public sealed class EgressCompilationResult
{
    private EgressCompilationResult(CompiledEgressIntent? intent, EgressCompilationFailure? failure) =>
        (Intent, Failure) = (intent, failure);

    public CompiledEgressIntent? Intent { get; }
    public EgressCompilationFailure? Failure { get; }
    public bool IsSuccess => Failure is null;

    /// <summary>Creates a successful result containing the compiled intent.</summary>
    public static EgressCompilationResult Success(CompiledEgressIntent intent) =>
        new(intent ?? throw new ArgumentNullException(nameof(intent)), null);

    /// <summary>Creates a failed result with a stable code and a human-readable reason.</summary>
    public static EgressCompilationResult Fail(string code, string message) =>
        new(null, new EgressCompilationFailure(code, message));
}

/// <summary>
/// Converts the authorized Projects run selection into the narrower policy that the Environment service applies.
/// </summary>
public static class EgressIntentCompiler
{
    private static readonly string[] ProtectedIdentityDestinations =
    [
        "vault.azure.net",
        "*.vault.azure.net",
        "vault.usgovcloudapi.net",
        "*.vault.usgovcloudapi.net",
        "vault.azure.cn",
        "*.vault.azure.cn",
        "vault.microsoftazure.de",
        "*.vault.microsoftazure.de",
        "login.microsoftonline.com",
        "*.login.microsoftonline.com",
        "login.microsoftonline.us",
        "*.login.microsoftonline.us",
        "login.chinacloudapi.cn",
        "*.login.chinacloudapi.cn",
        "login.microsoft.com",
        "*.login.microsoft.com",
        "login.windows.net",
        "*.login.windows.net",
        "sts.windows.net",
        "*.sts.windows.net"
    ];

    /// <summary>
    /// Validates policy revisions and rule containment, then compiles only the admitted run needs.
    /// Protected identity destinations and incomplete DNS requirements are rejected rather than widened.
    /// </summary>
    /// <param name="selection">The immutable, authorized policy selection for the run.</param>
    /// <returns>A compiled intent, or a coded failure when the selection is malformed or unsafe.</returns>
    public static EgressCompilationResult Compile(EffectiveNetworkPolicySelection selection)
    {
        if (selection is null ||
            string.IsNullOrWhiteSpace(selection.ProjectId) ||
            string.IsNullOrWhiteSpace(selection.RunId) ||
            string.IsNullOrWhiteSpace(selection.ContextRevision) ||
            selection.ProjectRevision < 1 ||
            selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 ||
            selection.EgressBaseline.IsDefault ||
            selection.ProjectEgressNarrowing is { IsDefault: true } ||
            selection.RequiredEgress.IsDefault ||
            selection.EgressAllowlist.IsDefault ||
            selection.Providers.IsDefault ||
            selection.Providers.Any(provider => provider is null ||
                !Enum.IsDefined(provider.Cardinality) ||
                !Enum.IsDefined(provider.Seam) ||
                provider.Candidates.IsDefault ||
                provider.Candidates.Any(candidate => candidate is null ||
                    !Enum.IsDefined(candidate.Seam) ||
                    !Enum.IsDefined(candidate.Hosting) ||
                    (candidate.Layer is { } layer && !Enum.IsDefined(layer)) ||
                    candidate.AdvertisedCapabilities.IsDefault ||
                    candidate.RequiredCapabilities.IsDefault)))
            return EgressCompilationResult.Fail(
                "invalid_selection",
                "The authorized run selection is incomplete or malformed.");

        ImmutableArray<NetworkEgressRule> baseline;
        ImmutableArray<NetworkEgressRule> narrowing;
        ImmutableArray<NetworkEgressRule> needs;
        ImmutableArray<NetworkEgressRule> reportedAllowlist;
        try
        {
            baseline = NetworkEgressRuleSemantics.NormalizeSet(selection.EgressBaseline, "platform baseline");
            narrowing = selection.ProjectEgressNarrowing is { } projectRules
                ? NetworkEgressRuleSemantics.NormalizeSet(projectRules, "project narrowing")
                : baseline;
            needs = NetworkEgressRuleSemantics.NormalizeSet(selection.RequiredEgress, "admitted run needs");
            reportedAllowlist = NetworkEgressRuleSemantics.NormalizeSet(
                selection.EgressAllowlist, "effective Projects allowlist");
        }
        catch (ArgumentException exception)
        {
            return EgressCompilationResult.Fail("invalid_egress_rule", exception.Message);
        }

        if (narrowing.Any(rule => !NetworkEgressRuleSemantics.IsContainedBy(rule, baseline)))
            return EgressCompilationResult.Fail(
                "project_widening",
                "Project egress narrowing contains a destination outside the platform baseline.");

        var effectiveAllowlist = NetworkEgressRuleSemantics.IntersectSets(baseline, narrowing);
        if (!effectiveAllowlist.SequenceEqual(reportedAllowlist))
            return EgressCompilationResult.Fail(
                "selection_egress_mismatch",
                "The immutable Projects selection does not match its baseline and project narrowing.");

        if (needs.Any(rule => !NetworkEgressRuleSemantics.IsContainedBy(rule, effectiveAllowlist)))
            return EgressCompilationResult.Fail(
                "run_need_outside_allowlist",
                "An admitted run need is outside the effective platform and project egress allowlist.");

        var rules = NetworkEgressRuleSemantics.IntersectSets(effectiveAllowlist, needs);
        if (rules.Length != needs.Length)
            return EgressCompilationResult.Fail(
                "unexpressible_intersection",
                "The admitted egress requirements cannot be represented by the effective allowlist.");

        if (rules.Any(rule => rule.Purpose == NetworkEgressPurpose.DnsResolver) &&
            (!rules.Contains(DnsRule(EgressProtocol.Tcp)) ||
             !rules.Contains(DnsRule(EgressProtocol.Udp))))
            return EgressCompilationResult.Fail(
                "incomplete_dns",
                "DNS egress requires both TCP/53 and UDP/53 to kube-system/kube-dns.");

        if (rules.Any(rule => rule.DestinationKind == NetworkEgressDestinationKind.Fqdn) &&
            (!rules.Contains(DnsRule(EgressProtocol.Tcp)) ||
             !rules.Contains(DnsRule(EgressProtocol.Udp))))
            return EgressCompilationResult.Fail(
                "missing_dns_resolver",
                "FQDN egress requires admitted TCP/53 and UDP/53 DNS resolver rules.");

        if (rules.Any(CanReachProtectedIdentityDestination))
            return EgressCompilationResult.Fail(
                "direct_secret_egress_forbidden",
                "AgentHost egress cannot directly reach Key Vault or Entra identity endpoints.");

        var groups = rules
            .GroupBy(rule => rule.Purpose)
            .OrderBy(group => group.Key)
            .Select(group => new EgressPurposeGroup(group.Key, group.ToImmutableArray()))
            .ToImmutableArray();

        return EgressCompilationResult.Success(new CompiledEgressIntent(
            selection.ProjectId,
            selection.RunId,
            selection.ProjectRevision,
            selection.ProjectConfigurationRevision,
            selection.PlatformRuntimeRevision,
            selection.ContextRevision,
            rules,
            groups,
            rules.Any(rule => rule.Purpose is
                NetworkEgressPurpose.RemoteMcp or NetworkEgressPurpose.PublicHttps)));
    }

    /// <summary>Hashes the canonical policy identity and selected Kubernetes scope for policy pinning.</summary>
    /// <param name="intent">The compiled policy and its authorization revisions.</param>
    /// <param name="selector">The namespace and labels that scope the policy.</param>
    /// <returns>A lowercase SHA-256 digest suitable for comparing policy versions.</returns>
    public static string Hash(CompiledEgressIntent intent, EnvironmentEgressSelector selector)
    {
        var material = string.Join('\n',
            selector.Namespace,
            selector.EnvironmentLabel,
            selector.OwnerLabel,
            intent.ProjectId,
            intent.RunId,
            intent.ProjectRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            intent.ProjectConfigurationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            intent.PlatformRuntimeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            intent.ContextRevision,
            JsonSerializer.Serialize(intent.Rules));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>Builds one of the required kube-dns TCP or UDP port-53 rules.</summary>
    private static NetworkEgressRule DnsRule(EgressProtocol protocol) =>
        new(NetworkEgressPurpose.DnsResolver,
            NetworkEgressDestinationKind.KubernetesService,
            "kube-system/kube-dns",
            53,
            protocol);

    /// <summary>Checks whether an FQDN rule overlaps a Key Vault or identity-provider destination.</summary>
    private static bool CanReachProtectedIdentityDestination(NetworkEgressRule rule)
    {
        if (rule.DestinationKind != NetworkEgressDestinationKind.Fqdn)
            return false;
        return ProtectedIdentityDestinations.Any(destination =>
        {
            var protectedRule = rule with { Destination = destination };
            return NetworkEgressRuleSemantics.IsSubset(protectedRule, rule) ||
                NetworkEgressRuleSemantics.IsSubset(rule, protectedRule);
        });
    }
}
