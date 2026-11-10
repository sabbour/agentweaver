using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Abstractions;

/// <summary>Platform-wide outbound rule for all AgentHost workloads.</summary>
public enum PlatformEgressMode { PublicHttps, Allowlist }

/// <summary>One exact DNS name on TCP 443 that AgentHost workloads can reach.</summary>
public sealed record PlatformEgressDestination(string Host, int Port);

/// <summary>Desired platform egress policy that a provider applies to all AgentHost workloads.</summary>
/// <param name="Generation">Positive generation that increases on every desired-policy change.</param>
/// <param name="Mode">The platform egress mode.</param>
/// <param name="Destinations">Sorted, unique, validated destinations. Always empty for PublicHttps.</param>
/// <param name="IntentHash">SHA-256 of the mode and destinations.</param>
/// <param name="SourceHash">
/// Opaque hash of the admitted policy source. The provider stores it and returns it in the status;
/// it does not interpret it.
/// </param>
public sealed record PlatformEgressPolicy(
    long Generation,
    PlatformEgressMode Mode,
    ImmutableArray<PlatformEgressDestination> Destinations,
    string IntentHash,
    string SourceHash)
{
    /// <summary>Creates a policy with sorted unique destinations and its intent hash.</summary>
    public static PlatformEgressPolicy Create(
        long generation,
        PlatformEgressMode mode,
        IEnumerable<PlatformEgressDestination> destinations,
        string sourceHash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        if (!PlatformEgressPolicySnapshot.IsSha256(sourceHash))
            throw new ArgumentException("The source hash must be 64 lowercase hexadecimal characters.", nameof(sourceHash));
        var canonical = Canonicalize(destinations);
        if (mode == PlatformEgressMode.PublicHttps)
            canonical = [];
        return new(generation, mode, canonical, ComputeIntentHash(mode, canonical), sourceHash);
    }

    /// <summary>
    /// Hashes the mode and the sorted unique destinations. The generation is not part of the hash.
    /// PublicHttps does not use hosts, so its hash ignores them and the provider can verify it on readback.
    /// </summary>
    public static string ComputeIntentHash(
        PlatformEgressMode mode,
        IEnumerable<PlatformEgressDestination> destinations)
    {
        var material = new StringBuilder("agentweaver.platform-egress.v1\n")
            .Append(mode.ToString()).Append('\n');
        foreach (var destination in mode == PlatformEgressMode.PublicHttps ? [] : Canonicalize(destinations))
            material.Append(destination.Host).Append(':')
                .Append(destination.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    private static ImmutableArray<PlatformEgressDestination> Canonicalize(
        IEnumerable<PlatformEgressDestination> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var result = destinations.Distinct()
            .OrderBy(destination => destination.Host, StringComparer.Ordinal)
            .ThenBy(destination => destination.Port)
            .ToImmutableArray();
        foreach (var destination in result)
            if (PlatformEgressHostValidator.Validate(destination.Host, destination.Port) is { } error)
                throw new ArgumentException(error.Message, nameof(destinations));
        return result;
    }
}

/// <summary>Accepted means the provider control plane stored the generation; Enforced means it has datapath proof.</summary>
public enum PlatformEgressApplyState { Accepted, Enforced, Failed }

/// <summary>Provider-reported state of the applied platform egress policy.</summary>
/// <param name="Generation">Applied generation, or 0 when no platform policy is applied.</param>
/// <param name="IntentHash">Applied intent hash, or null when no platform policy is applied.</param>
/// <param name="SourceHash">Source hash stored with the applied generation, or null.</param>
/// <param name="State">Accepted or Enforced only when the readback matches the applied generation exactly.</param>
/// <param name="ObservedAt">Time of the readback.</param>
/// <param name="Message">Stable reason code when the state is Failed.</param>
public sealed record PlatformEgressStatus(
    long Generation,
    string? IntentHash,
    string? SourceHash,
    PlatformEgressApplyState State,
    DateTimeOffset ObservedAt,
    string? Message)
{
    /// <summary>True when the provider reports this exact intent and source as Accepted or Enforced.</summary>
    public bool Matches(string intentHash, string sourceHash) =>
        State is PlatformEgressApplyState.Accepted or PlatformEgressApplyState.Enforced &&
        IntentHash == intentHash &&
        SourceHash == sourceHash;
}

/// <summary>
/// Admitted platform egress policy from the one canonical policy source, compiled to a mode and exact hosts.
/// </summary>
/// <param name="SourceId">Identifier of the policy source implementation.</param>
/// <param name="SourceRevision">Revision of the canonical policy document.</param>
/// <param name="SourceHash">SHA-256 of the canonical policy content, as 64 lowercase hexadecimal characters.</param>
/// <param name="CompilerVersion">Version of the compiler that produced the mode and destinations.</param>
/// <param name="Checkpoint">Admission checkpoint that approved this snapshot.</param>
/// <param name="Mode">Compiled platform egress mode.</param>
/// <param name="Destinations">Compiled exact destinations, including release-managed hosts.</param>
public sealed record PlatformEgressPolicySnapshot(
    string SourceId,
    string SourceRevision,
    string SourceHash,
    string CompilerVersion,
    string Checkpoint,
    PlatformEgressMode Mode,
    ImmutableArray<PlatformEgressDestination> Destinations)
{
    /// <summary>Returns null when the snapshot is complete; otherwise returns the reason.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceId) || string.IsNullOrWhiteSpace(SourceRevision) ||
            string.IsNullOrWhiteSpace(CompilerVersion) || string.IsNullOrWhiteSpace(Checkpoint) ||
            !IsSha256(SourceHash) || !Enum.IsDefined(Mode) || Destinations.IsDefault)
            return "The policy source returned an incomplete snapshot.";
        foreach (var destination in Destinations)
            if (destination is null)
                return "The policy source returned an empty destination.";
            else if (PlatformEgressHostValidator.Validate(destination.Host, destination.Port) is { } error)
                return $"{destination.Host}:{destination.Port}: {error.Code}";
        return null;
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}

/// <summary>
/// Reads the admitted platform egress policy from the one canonical policy source.
/// </summary>
/// <remarks>
/// Return null when no admitted policy exists or the source cannot be read. Callers then fail closed:
/// they do not apply or remove provider rules, and they do not use another source.
/// </remarks>
public interface IPlatformEgressPolicySource
{
    /// <summary>Returns the current admitted snapshot, or null when none is available.</summary>
    Task<PlatformEgressPolicySnapshot?> GetAdmittedAsync(CancellationToken cancellationToken);
}

/// <summary>Provider verdict for one observed outbound flow.</summary>
public enum EgressFlowVerdict { Allowed, Denied }

/// <summary>One observed outbound flow, or an aggregate of equal flows, from an AgentHost workload.</summary>
public sealed record EgressFlowObservation(
    string? Host,
    string? Address,
    int Port,
    EgressFlowVerdict Verdict,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long Count,
    string? WorkloadRef);

/// <summary>CA certificates that AgentHost must trust when a provider inspects TLS.</summary>
public sealed record EgressTrustBundle(string Version, string PemCertificates);

/// <summary>Applies the platform egress policy for all AgentHost workloads on the L3L4 network-policy layer.</summary>
/// <remarks>
/// A provider gets only the mode, the host list, the generation, and an opaque source hash. It never sees
/// users, projects, model providers, requests, or audit data. Every provider must pass the conformance kit.
/// </remarks>
public interface IPlatformEgressProvider
{
    /// <summary>Descriptor for the selected <see cref="ProviderSeam.NetworkPolicy"/> L3L4 provider.</summary>
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Stores the policy in the provider control plane. When the descriptor cannot apply the mode,
    /// returns Failed with <c>capability_unavailable</c> and changes nothing.
    /// </summary>
    Task<PlatformEgressStatus> ApplyAsync(PlatformEgressPolicy policy, CancellationToken cancellationToken);

    /// <summary>Reads back the applied generation, intent hash, and state.</summary>
    Task<PlatformEgressStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Removes everything this provider applied for the platform policy.</summary>
    Task RemoveAsync(CancellationToken cancellationToken);

    /// <summary>Reads observed flows. Available only with <see cref="PlatformEgressCapabilities.BlockedAttempts"/>.</summary>
    IAsyncEnumerable<EgressFlowObservation> ReadFlowsAsync(
        string? cursor,
        bool includeAllowed,
        CancellationToken cancellationToken);

    /// <summary>Returns the TLS trust bundle. Available only with <see cref="PlatformEgressCapabilities.TlsTrust"/>.</summary>
    Task<EgressTrustBundle?> GetTrustBundleAsync(CancellationToken cancellationToken);
}

/// <summary>Capability names that a platform egress provider advertises in its descriptor.</summary>
public static class PlatformEgressCapabilities
{
    /// <summary>Required. Denies the excluded ranges in both modes.</summary>
    public const string PrivateRangeExclusion = "egress.private-range-exclusion";
    /// <summary>Required. Reports generation and intent hash with state Accepted or Enforced.</summary>
    public const string Status = "egress.status";
    /// <summary>Required for <see cref="PlatformEgressMode.PublicHttps"/>.</summary>
    public const string PublicHttps = "egress.public-https";
    /// <summary>Required for <see cref="PlatformEgressMode.Allowlist"/>.</summary>
    public const string ExactHost = "egress.exact-host";
    /// <summary>Optional. Returns denied flows.</summary>
    public const string BlockedAttempts = "egress.blocked-attempts";
    /// <summary>Optional. Also returns allowed flows.</summary>
    public const string AllowedFlows = "egress.allowed-flows";
    /// <summary>Optional. Each flow maps to exactly one run.</summary>
    public const string RunAttribution = "egress.run-attribution";
    /// <summary>Optional. The provider can report Enforced.</summary>
    public const string EnforcedStatus = "egress.enforced-status";
    /// <summary>Required when the provider inspects TLS.</summary>
    public const string TlsTrust = "egress.tls-trust";

    /// <summary>Returns the capability that a provider needs to apply the mode.</summary>
    public static string ForMode(PlatformEgressMode mode) => mode switch
    {
        PlatformEgressMode.PublicHttps => PublicHttps,
        PlatformEgressMode.Allowlist => ExactHost,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Checks the required capabilities and the capability for the mode.</summary>
    public static bool CanApply(IReadOnlySet<string> advertised, PlatformEgressMode mode)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        return advertised.Contains(PrivateRangeExclusion) &&
            advertised.Contains(Status) &&
            advertised.Contains(ForMode(mode));
    }
}

/// <summary>Validation failure for a platform egress destination.</summary>
public sealed record PlatformEgressValidationError(string Code, string Message);

/// <summary>The one host rule for platform egress destinations. Policy sources and providers use it.</summary>
public static class PlatformEgressHostValidator
{
    /// <summary>The only allowed destination port.</summary>
    public const int HttpsPort = 443;

    private static readonly string[] ReservedSuffixes =
        [".local", ".localhost", ".internal", ".svc", ".cluster.local", ".localdomain"];

    // Same Key Vault and Entra domains that EgressIntentCompiler rejects for per-run egress.
    private static readonly string[] ProtectedDomains =
    [
        "vault.azure.net",
        "vault.usgovcloudapi.net",
        "vault.azure.cn",
        "vault.microsoftazure.de",
        "login.microsoftonline.com",
        "login.microsoftonline.us",
        "login.chinacloudapi.cn",
        "login.microsoft.com",
        "login.windows.net",
        "sts.windows.net",
    ];

    /// <summary>Returns null when the host and port are allowed; otherwise returns the reason.</summary>
    public static PlatformEgressValidationError? Validate(string? host, int port)
    {
        if (port != HttpsPort)
            return new("destination_port_not_allowed", "Only TCP port 443 is allowed.");
        if (string.IsNullOrEmpty(host) || host.Length > 253)
            return Invalid("The host must be a DNS name of 1 to 253 characters.");
        if (host.EndsWith('.'))
            return Invalid("The host must not end with a dot.");
        if (IPAddress.TryParse(host, out _))
            return Invalid("The host must be a DNS name, not an IP address.");
        var labels = host.Split('.');
        if (labels.Length < 2)
            return Invalid("The host must have at least two labels.");
        foreach (var label in labels)
            if (!IsLabel(label))
                return Invalid(
                    "Each host label must have 1 to 63 lowercase letters, digits, or hyphens, and must not start or end with a hyphen.");
        if (labels[^1].All(char.IsAsciiDigit))
            return Invalid("The last host label must not be numeric.");
        if (ReservedSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)))
            return new("destination_host_reserved", "The host is a local or cluster-internal name.");
        if (ProtectedDomains.Any(domain =>
                host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)))
            return new(
                "destination_host_protected",
                "AgentHost egress cannot directly reach Key Vault or Entra identity endpoints.");
        return null;
    }

    private static PlatformEgressValidationError Invalid(string message) => new("destination_host_invalid", message);

    private static bool IsLabel(string label) =>
        label.Length is >= 1 and <= 63 &&
        label[0] != '-' &&
        label[^1] != '-' &&
        label.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
}

/// <summary>Address ranges that AgentHost egress never reaches, in both modes.</summary>
public static class PlatformEgressExcludedRanges
{
    /// <summary>The fixed ranges. Install configuration adds the cluster pod and service ranges.</summary>
    public static ImmutableArray<string> Fixed { get; } =
    [
        "0.0.0.0/8",
        "10.0.0.0/8",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "224.0.0.0/4",
        "::1/128",
        "fc00::/7",
        "fe80::/10",
        "ff00::/8",
    ];

    /// <summary>Parses the fixed ranges and the extra ranges, and fails for an invalid range.</summary>
    public static ImmutableArray<IPNetwork> Create(IEnumerable<string> additionalRanges)
    {
        ArgumentNullException.ThrowIfNull(additionalRanges);
        return Fixed.Concat(additionalRanges).Select(range =>
            IPNetwork.TryParse(range, out var network) && network.ToString() == range
                ? network
                : throw new ArgumentException(
                    $"'{range}' is not a canonical CIDR range.", nameof(additionalRanges)))
            .ToImmutableArray();
    }

    /// <summary>Checks one address. IPv4-mapped IPv6 addresses are checked as IPv4.</summary>
    public static bool Contains(IEnumerable<IPNetwork> ranges, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return ranges.Any(range => range.Contains(address));
    }
}
