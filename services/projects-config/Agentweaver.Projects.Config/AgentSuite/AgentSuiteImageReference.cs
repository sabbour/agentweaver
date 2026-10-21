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

    private static readonly Regex Reference = Go.Re(
        "^(" + NamePattern + ")(?::(" + Tag + "))?(?:@(" + DigestPattern + "))?\\z");
    private static readonly Regex AnchoredName = Go.Re("^(?:(" + DomainAndPort + ")/)?(" + RemoteName + ")\\z");
    private static readonly Regex Identifier = Go.Re("^[a-f0-9]{64}\\z");
    private static readonly Regex Sha256Hex = Go.Re("^[a-f0-9]{64}\\z");
    private static readonly Regex CanonicalIpv4 = Go.Re(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z");

    public static string? Validate(string value)
    {
        if (ParseNormalizedNamed(value, out var domain, out var tag, out var digest) is { } parseError)
            return "must be a valid OCI image reference: " + parseError;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || value[..slash] != domain)
            return "must include an explicit registry";
        if (tag.Length != 0)
            return "must not include a tag";
        if (digest.Length == 0 || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            return "must use a sha256 digest";
        return ValidateRegistryAuthority(domain);
    }

    private static string? ParseNormalizedNamed(string value, out string domain, out string tag, out string digest)
    {
        domain = tag = digest = string.Empty;
        if (Identifier.IsMatch(value))
            return "cannot specify 64-byte hexadecimal strings";
        var (splitDomain, remainder) = SplitDockerDomain(value);
        var tagSeparator = remainder.IndexOf(':', StringComparison.Ordinal);
        var remote = tagSeparator > -1 ? remainder[..tagSeparator] : remainder;
        if (remote.ToLowerInvariant() != remote)
            return "repository name must be lowercase";
        var full = splitDomain + "/" + remainder;
        var match = Reference.Match(full);
        if (!match.Success)
            return "invalid reference format";
        var name = AnchoredName.Match(match.Groups[1].Value);
        domain = name.Success ? name.Groups[1].Value : string.Empty;
        var path = name.Success ? name.Groups[2].Value : match.Groups[1].Value;
        if (path.Length > 255)
            return "repository name must not be more than 255 characters";
        tag = match.Groups[2].Value;
        digest = match.Groups[3].Value;
        if (digest.Length != 0)
        {
            // go-digest Parse: only registered algorithms with lowercase hex of the exact length.
            var colon = digest.IndexOf(':', StringComparison.Ordinal);
            var algorithm = digest[..colon];
            var hex = digest[(colon + 1)..];
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
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return ("docker.io", "library/" + name);
        var maybeDomain = name[..slash];
        var maybeRemote = name[(slash + 1)..];
        if (maybeDomain == "localhost")
            return (maybeDomain, maybeRemote);
        if (maybeDomain == "index.docker.io")
            return ("docker.io", maybeRemote.Contains('/', StringComparison.Ordinal) ? maybeRemote : "library/" + maybeRemote);
        if (maybeDomain.IndexOfAny(['.', ':']) >= 0 || maybeDomain.ToLowerInvariant() != maybeDomain)
            return (maybeDomain, maybeRemote);
        return ("docker.io", name);
    }

    private static string? ValidateRegistryAuthority(string authority)
    {
        string host;
        string port;
        if (authority.StartsWith('['))
        {
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
        if (port.Length != 0 && (!ushort.TryParse(port, out var number) || number == 0))
            return "registry port is invalid";
        if (IsIp(host) || host == "localhost")
            return null;
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
        return host.Contains(':', StringComparison.Ordinal) && host.All(c => c == ':' || char.IsAsciiHexDigit(c)) &&
            IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }
}
