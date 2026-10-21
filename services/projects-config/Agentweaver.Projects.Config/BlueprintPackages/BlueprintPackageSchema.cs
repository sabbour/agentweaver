// Ported without behavior changes from the 0.x Agentweaver.Squad blueprint package code.
// The AgentSuite blueprint converter uses it so 0.x packages are checked by the exact 0.x rules.
using System.Text.Json;
using System.Net;
using System.Net.Sockets;

namespace Agentweaver.Projects.Config.BlueprintPackages;

/// <summary>
/// Schema support for the on-wire manifest. The custom metaschema declares the required
/// Agentweaver vocabulary; this type evaluates each assertion from that vocabulary.
/// </summary>
public static class BlueprintPackageSchema
{
    public const string Id = "https://agentweaver.dev/schemas/blueprint-package-v1.json";
    public const string MetaSchemaId = "https://agentweaver.dev/metaschemas/blueprint-package-v1";
    public const string VocabularyId = "https://agentweaver.dev/vocab/blueprint-package-v1";
    public const string CanonicalDefinitionPathKeyword = "x-agentweaver-canonical-definition-path";
    public const string HttpsRepositoryUriKeyword = "x-agentweaver-https-repository-uri";
    public const string Rfc3339TimestampKeyword = "x-agentweaver-rfc3339-timestamp";
    private const string ResourceName = "Agentweaver.Projects.Config.BlueprintPackages.Schemas.blueprint-package-v1.schema.json";
    private const string MetaSchemaResourceName = "Agentweaver.Projects.Config.BlueprintPackages.Schemas.blueprint-package-v1.meta.schema.json";

    /// <summary>The versioned schema embedded with the validator so callers cannot drift from its grammar.</summary>
    public static string Json { get; } = Load();
    /// <summary>The metaschema that declares the required Agentweaver vocabulary.</summary>
    public static string MetaSchemaJson { get; } = Load(MetaSchemaResourceName);

    /// <summary>Evaluates every Agentweaver-specific assertion declared by the distributed schema.</summary>
    public static void ValidateCustomKeywords(JsonElement manifest, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        // Shape errors are reported by the manifest parser; this pass only checks the custom keywords.
        if (manifest.ValueKind != JsonValueKind.Object) return;

        // Each definition path must be exactly the canonical path for its kind and ID.
        if (manifest.TryGetProperty("definitions", out var definitions) && definitions.ValueKind == JsonValueKind.Array)
        {
            foreach (var definition in definitions.EnumerateArray())
            {
                // Skip malformed entries; the manifest parser already reports them.
                if (definition.ValueKind != JsonValueKind.Object
                    || !definition.TryGetProperty("kind", out var kind)
                    || !definition.TryGetProperty("id", out var id)
                    || !definition.TryGetProperty("path", out var path)
                    || kind.ValueKind != JsonValueKind.String
                    || id.ValueKind != JsonValueKind.String
                    || path.ValueKind != JsonValueKind.String)
                    continue;

                if (!IsCanonicalDefinitionPath(kind.GetString()!, id.GetString()!, path.GetString()!))
                    errors.Add("definition.path is not a canonical definitions-only path.");
            }
        }

        // Provenance strings are optional, but when present they must use the strict formats below.
        if (manifest.TryGetProperty("provenance", out var provenance) && provenance.ValueKind == JsonValueKind.Object)
        {
            if (provenance.TryGetProperty("repository", out var repository)
                && repository.ValueKind == JsonValueKind.String
                && !IsHttpsRepositoryUri(repository.GetString()!))
                errors.Add("provenance.repository must be a strict absolute HTTPS URI.");

            if (provenance.TryGetProperty("created_at", out var createdAt)
                && createdAt.ValueKind == JsonValueKind.String
                && !IsRfc3339Timestamp(createdAt.GetString()!))
                errors.Add("provenance.created_at must use the package RFC 3339 timestamp profile.");
        }
    }

    /// <summary>Returns whether a definition path is the exact path implied by its kind and id.</summary>
    public static bool IsCanonicalDefinitionPath(string kind, string id, string path) =>
        string.Equals(path, kind switch
        {
            "blueprint" => $"definitions/blueprints/{id}.json",
            "role" => $"definitions/roles/{id}.json",
            "workflow" => $"definitions/workflows/{id}.yaml",
            "skill" => $"definitions/skills/{id}.md",
            // An unknown kind has no canonical path, so the comparison fails.
            _ => null,
        }, StringComparison.Ordinal);

    /// <summary>Returns whether text is an absolute, credential-free HTTPS URI in the package profile.</summary>
    public static bool IsHttpsRepositoryUri(string value)
    {
        // Only https URIs, with an upper bound on length.
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || !value.StartsWith("https://", StringComparison.Ordinal))
            return false;

        // The authority starts right after "https://" (8 characters).
        const int authorityStart = 8;
        // Reject backslashes and repeated '?' or '#', which different URI parsers handle differently.
        if (value.IndexOf('\\') >= 0 || !HasAtMostOneDelimiter(value, '?', authorityStart)
            || !HasAtMostOneDelimiter(value, '#', authorityStart))
            return false;

        // A fragment ('#') can't come before the query ('?').
        var queryStart = value.IndexOf('?', authorityStart);
        var fragmentStart = value.IndexOf('#', authorityStart);
        if (queryStart >= 0 && fragmentStart >= 0 && fragmentStart < queryStart)
            return false;

        // The authority ends at the first '/', '?', '#', or the end of the text.
        var pathStart = value.IndexOf('/', authorityStart);
        var authorityEnd = FirstNonNegative(pathStart, queryStart, fragmentStart, value.Length);
        var authority = value[authorityStart..authorityEnd];
        // An '@' means user info (credentials), which is not allowed.
        if (authority.Length == 0 || authority.Contains('@', StringComparison.Ordinal) || !IsValidAuthority(authority))
            return false;

        // Path, query and fragment may only use RFC 3986 characters or valid percent escapes.
        var pathEnd = FirstNonNegative(queryStart, fragmentStart, value.Length);
        return IsValidUriComponent(value.AsSpan(authorityEnd, pathEnd - authorityEnd))
            && (queryStart < 0 || IsValidUriComponent(value.AsSpan(queryStart + 1, (fragmentStart < 0 ? value.Length : fragmentStart) - queryStart - 1)))
            && (fragmentStart < 0 || IsValidUriComponent(value.AsSpan(fragmentStart + 1)));
    }

    /// <summary>Returns whether text is an exact Gregorian RFC 3339 timestamp in the package profile.</summary>
    public static bool IsRfc3339Timestamp(string value)
    {
        // Fixed layout first: YYYY-MM-DDTHH:MM:SS with an uppercase 'T'.
        if (value is null || value.Length < 20
            || !HasDigits(value, 0, 4) || value[4] != '-' || !HasDigits(value, 5, 2) || value[7] != '-'
            || !HasDigits(value, 8, 2) || value[10] != 'T' || !HasDigits(value, 11, 2) || value[13] != ':'
            || !HasDigits(value, 14, 2) || value[16] != ':' || !HasDigits(value, 17, 2))
            return false;

        // Then check that the date exists and the time is in range. A leap second (60) is rejected.
        var year = ParseDigits(value, 0, 4);
        var month = ParseDigits(value, 5, 2);
        var day = ParseDigits(value, 8, 2);
        var hour = ParseDigits(value, 11, 2);
        var minute = ParseDigits(value, 14, 2);
        var second = ParseDigits(value, 17, 2);
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day is < 1 or > 31
            || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return false;

        // Optional fractional seconds: a '.' followed by at least one digit.
        var offsetIndex = 19;
        if (offsetIndex < value.Length && value[offsetIndex] == '.')
        {
            var fractionStart = ++offsetIndex;
            while (offsetIndex < value.Length && IsDigit(value[offsetIndex])) offsetIndex++;
            if (offsetIndex == fractionStart) return false;
        }

        // The offset is an uppercase 'Z' or +HH:MM / -HH:MM, and it must end the text.
        if (offsetIndex == value.Length - 1 && value[offsetIndex] == 'Z') return true;
        if (offsetIndex + 6 != value.Length || value[offsetIndex] is not ('+' or '-')
            || !HasDigits(value, offsetIndex + 1, 2) || value[offsetIndex + 3] != ':'
            || !HasDigits(value, offsetIndex + 4, 2))
            return false;

        // Offsets range from -14:00 to +14:00.
        var offsetHour = ParseDigits(value, offsetIndex + 1, 2);
        var offsetMinute = ParseDigits(value, offsetIndex + 4, 2);
        return offsetHour < 14 && offsetMinute < 60 || offsetHour == 14 && offsetMinute == 0;
    }

    private static bool IsValidAuthority(string authority)
    {
        string host;
        string? port = null;
        // A bracketed host is an IPv6 literal, optionally followed by :port.
        if (authority[0] == '[')
        {
            var closingBracket = authority.IndexOf(']');
            if (closingBracket <= 1) return false;
            host = authority[1..closingBracket];
            if (closingBracket + 1 < authority.Length)
            {
                if (authority[closingBracket + 1] != ':') return false;
                port = authority[(closingBracket + 2)..];
            }
            // Zone IDs ('%') are not allowed, and the literal must parse as IPv6.
            if (host.Contains('%', StringComparison.Ordinal)
                || !IPAddress.TryParse(host, out var address)
                || address.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
        }
        else
        {
            // Otherwise the host is a DNS name or IPv4 address, with at most one ':' before the port.
            var colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                if (authority.IndexOf(':', colon + 1) >= 0) return false;
                host = authority[..colon];
                port = authority[(colon + 1)..];
            }
            else
            {
                host = authority;
            }
            if (!IsValidDnsOrIpv4Host(host)) return false;
        }
        // The port is optional, but when present it must be valid.
        return port is null || IsValidPort(port);
    }

    private static bool HasAtMostOneDelimiter(string value, char delimiter, int start)
    {
        var first = value.IndexOf(delimiter, start);
        return first < 0 || value.IndexOf(delimiter, first + 1) < 0;
    }

    private static int FirstNonNegative(int first, int second, int fallback)
    {
        // Return the smaller index that was found (not -1), or the fallback when neither was found.
        if (first < 0) return second < 0 ? fallback : second;
        return second < 0 ? first : Math.Min(first, second);
    }

    private static int FirstNonNegative(int first, int second, int third, int fallback) =>
        FirstNonNegative(FirstNonNegative(first, second, fallback), third, fallback);

    private static bool IsValidUriComponent(ReadOnlySpan<char> component)
    {
        for (var index = 0; index < component.Length; index++)
        {
            var character = component[index];
            // A '%' must start a two-digit hex escape.
            if (character == '%')
            {
                if (index + 2 >= component.Length || !IsHex(component[index + 1]) || !IsHex(component[index + 2]))
                    return false;
                index += 2;
                continue;
            }

            // Any other character must be unreserved, a sub-delimiter, ':', '@' or '/'.
            if (!IsUnreserved(character) && !IsSubDelimiter(character) && character is not (':' or '@' or '/'))
                return false;
        }

        return true;
    }

    private static bool IsUnreserved(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '.' or '_' or '~';

    private static bool IsSubDelimiter(char value) => value is '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';

    private static bool IsValidDnsOrIpv4Host(string host)
    {
        if (host.Length is 0 or > 253) return false;
        // Only digits and dots means an IPv4 address, so it must parse as one.
        if (host.All(character => IsDigit(character) || character == '.'))
            return IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork;

        // DNS labels: 1-63 letters, digits or hyphens, starting and ending with a letter or digit.
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
                return false;
            if (label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) return false;
        }
        return true;
    }

    // Port: 1-5 digits, no leading zero, at most 65535.
    private static bool IsValidPort(string port) =>
        port.Length is > 0 and <= 5
        && (port.Length == 1 || port[0] != '0')
        && port.All(IsDigit)
        && int.TryParse(port, out var value)
        && value <= 65535;

    private static bool HasDigits(string value, int start, int length) =>
        start >= 0 && start + length <= value.Length && value.AsSpan(start, length).ToString().All(IsDigit);

    private static int ParseDigits(string value, int start, int length)
    {
        var result = 0;
        for (var index = start; index < start + length; index++)
            result = (result * 10) + value[index] - '0';
        return result;
    }

    private static bool IsDigit(char value) => value is >= '0' and <= '9';
    private static bool IsHex(char value) => IsDigit(value) || value is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string Load(string resourceName = ResourceName)
    {
        // The schema is embedded in the assembly, so a missing resource is a build error, not bad input.
        using var stream = typeof(BlueprintPackageSchema).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded Blueprint package schema: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
