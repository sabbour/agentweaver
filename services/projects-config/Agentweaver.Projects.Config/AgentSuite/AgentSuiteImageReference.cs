using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Port of upstream <c>ValidateImageReference</c> with the distribution/reference v0.6.0
/// grammar it uses. Callers only use pass/fail, so failures return a short reason.
/// </summary>
internal static class AgentSuiteImageReference
{
    // The pattern pieces below rebuild the Docker/OCI reference grammar step by step:
    // [registry[:port]/]repository/path[:tag][@algorithm:hex].
    private const string Alphanumeric = "[a-z0-9]+";
    private const string Separator = "(?:[._]|__|[-]+)";
    private const string DomainNameComponent = "(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9])";
    private const string OptionalPort = "(?::[0-9]+)?";
    // Go \w is ASCII-only.
    private const string Tag = "[0-9A-Za-z_][0-9A-Za-z_.-]{0,127}";
    private const string DigestPattern = "[A-Za-z][A-Za-z0-9]*(?:[-_+.][A-Za-z][A-Za-z0-9]*)*[:][0-9A-Fa-f]{32,}";
    private const string Ipv6Address = @"\[(?:[a-fA-F0-9:]+)\]";
    private const string DomainName = DomainNameComponent + @"(?:\." + DomainNameComponent + ")*";
    private const string DomainAndPort = "(?:" + DomainName + "|" + Ipv6Address + ")" + OptionalPort;
    private const string PathComponent = Alphanumeric + "(?:" + Separator + Alphanumeric + ")*";
    private const string RemoteName = PathComponent + "(?:/" + PathComponent + ")*";
    private const string NamePattern = "(?:" + DomainAndPort + "/)?" + RemoteName;

    // Full reference: group 1 = name, group 2 = tag, group 3 = digest.
    private static readonly Regex Reference = Go.Re(
        "^(" + NamePattern + ")(?::(" + Tag + "))?(?:@(" + DigestPattern + "))?\\z");
    // Splits a name into group 1 = registry domain (optional) and group 2 = repository path.
    private static readonly Regex AnchoredName = Go.Re("^(?:(" + DomainAndPort + ")/)?(" + RemoteName + ")\\z");
    // A bare 64-character hex string looks like an image ID, which upstream refuses as a name.
    private static readonly Regex Identifier = Go.Re("^[a-f0-9]{64}\\z");
    private static readonly Regex Sha256Hex = Go.Re("^[a-f0-9]{64}\\z");
    // Dotted IPv4 with no leading zeros, so each address has exactly one accepted spelling.
    private static readonly Regex CanonicalIpv4 = Go.Re(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z");

    public static string? Validate(string value)
    {
        // First make sure the text is a well-formed reference at all.
        if (ParseNormalizedNamed(value, out var domain, out var tag, out var digest) is { } parseError)
            return "must be a valid OCI image reference: " + parseError;
        // Parsing fills in "docker.io" when no registry is written. Requiring the original text
        // to start with the parsed domain rejects that implicit default registry.
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || value[..slash] != domain)
            return "must include an explicit registry";
        // Tags are mutable, so only an immutable sha256 digest pins the exact image.
        if (tag.Length != 0)
            return "must not include a tag";
        if (digest.Length == 0 || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            return "must use a sha256 digest";
        // Finally check the registry host and port more strictly than the grammar does.
        return ValidateRegistryAuthority(domain);
    }

    private static string? ParseNormalizedNamed(string value, out string domain, out string tag, out string digest)
    {
        domain = tag = digest = string.Empty;
        // Mirrors upstream: a plain image ID is not a name.
        if (Identifier.IsMatch(value))
            return "cannot specify 64-byte hexadecimal strings";
        // Separate the registry from the rest, adding Docker Hub defaults when needed.
        var (splitDomain, remainder) = SplitDockerDomain(value);
        // Only the repository part (before any ":tag") must be lowercase; tags may use capitals.
        var tagSeparator = remainder.IndexOf(':', StringComparison.Ordinal);
        var remote = tagSeparator > -1 ? remainder[..tagSeparator] : remainder;
        if (remote.ToLowerInvariant() != remote)
            return "repository name must be lowercase";
        // Match the normalized "domain/remainder" form against the full grammar.
        var full = splitDomain + "/" + remainder;
        var match = Reference.Match(full);
        if (!match.Success)
            return "invalid reference format";
        // Split the matched name again to get the domain and the repository path separately.
        var name = AnchoredName.Match(match.Groups[1].Value);
        domain = name.Success ? name.Groups[1].Value : string.Empty;
        var path = name.Success ? name.Groups[2].Value : match.Groups[1].Value;
        // Upstream limit on repository path length (registry excluded).
        if (path.Length > 255)
            return "repository name must not be more than 255 characters";
        // Empty group values mean the tag or digest was not present.
        tag = match.Groups[2].Value;
        digest = match.Groups[3].Value;
        if (digest.Length != 0)
        {
            // The grammar accepts any "algorithm:hex" shape. Upstream then also requires a known
            // algorithm with lowercase hex of exactly that algorithm's length; mirror that here.
            var colon = digest.IndexOf(':', StringComparison.Ordinal);
            var algorithm = digest[..colon];
            var hex = digest[(colon + 1)..];
            // Hex lengths: sha256 = 64, sha384 = 96, sha512 = 128 characters.
            var valid = algorithm switch
            {
                "sha256" => Sha256Hex.IsMatch(hex),
                "sha384" => hex.Length == 96 && hex.All(IsLowerHex),
                "sha512" => hex.Length == 128 && hex.All(IsLowerHex),
                _ => false,
            };
            if (!valid)
                return "invalid digest format";
        }
        return null;
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static (string Domain, string Remote) SplitDockerDomain(string name)
    {
        // Docker naming rules, mirrored from upstream.
        // No slash ("ubuntu"): an official Docker Hub image, stored under "library/".
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return ("docker.io", "library/" + name);
        var maybeDomain = name[..slash];
        var maybeRemote = name[(slash + 1)..];
        // "localhost" is always a registry even without a dot or port.
        if (maybeDomain == "localhost")
            return (maybeDomain, maybeRemote);
        // The legacy Docker Hub name is rewritten to "docker.io".
        if (maybeDomain == "index.docker.io")
            return ("docker.io", maybeRemote.Contains('/', StringComparison.Ordinal) ? maybeRemote : "library/" + maybeRemote);
        // The first segment counts as a registry only if it has a dot, a port colon, or capitals
        // (repository names can't have capitals). Otherwise it is a Docker Hub user, e.g. "user/app".
        if (maybeDomain.IndexOfAny(['.', ':']) >= 0 || maybeDomain.ToLowerInvariant() != maybeDomain)
            return (maybeDomain, maybeRemote);
        return ("docker.io", name);
    }

    private static string? ValidateRegistryAuthority(string authority)
    {
        string host;
        string port;
        // Split host and port. IPv6 hosts are written in brackets ("[::1]:5000") because
        // the address itself contains colons.
        if (authority.StartsWith('['))
        {
            // The grammar already guaranteed a closing bracket.
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            host = authority[1..close];
            port = authority[(close + 1)..].TrimStart(':');
        }
        else
        {
            var colon = authority.IndexOf(':', StringComparison.Ordinal);
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? string.Empty : authority[(colon + 1)..];
        }
        if (host.Length == 0)
            return "registry authority is invalid";
        // The grammar allows any digits; a real TCP port must be 1..65535.
        if (port.Length != 0 && (!ushort.TryParse(port, out var number) || number == 0))
            return "registry port is invalid";
        // IP literals and localhost need no DNS hostname checks.
        if (IsIp(host) || host == "localhost")
            return null;
        // DNS limits: whole name at most 253 characters, each dot-separated label 1..63
        // characters, letters/digits/hyphens only, and no hyphen at either end of a label.
        if (host.Length > 253)
            return "registry hostname is invalid";
        foreach (var label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || !char.IsAsciiLetterOrDigit(label[0]) ||
                !char.IsAsciiLetterOrDigit(label[^1]))
                return "registry hostname is invalid";
            for (var i = 1; i < label.Length - 1; i++)
            {
                if (!char.IsAsciiLetterOrDigit(label[i]) && label[i] != '-')
                    return "registry hostname is invalid";
            }
        }
        return null;
    }

    private static bool IsIp(string host)
    {
        if (CanonicalIpv4.IsMatch(host))
            return true;
        // IPv6: pre-filter to only colons and hex digits, so .NET forms such as zone IDs
        // ("%eth0") or embedded IPv4 are not accepted, then let IPAddress confirm it parses.
        return host.Contains(':', StringComparison.Ordinal) && host.All(c => c == ':' || char.IsAsciiHexDigit(c)) &&
            IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }
}
