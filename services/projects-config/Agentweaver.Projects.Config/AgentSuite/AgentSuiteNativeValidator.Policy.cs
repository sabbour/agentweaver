using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>Policy binding (policy_binding.go, policy/*.go) and evaluation (evaluation.go) checks.</summary>
internal sealed partial class AgentSuiteNativeValidator
{
    private const string PolicySchemaVersion = "agentsuite.policy/v1";
    private const string PolicySchemaVersionV2 = "agentsuite.policy/v2";
    private const string FilesystemDenyWrite = "filesystem/deny-write/v1";
    private const string NetworkEgressAllow = "network/egress-allow/v1";
    private const string PortError = "network port must be an integer from 1 to 65535";

    private static readonly GoPrim PolicyString = new(GoPrimKind.String);

    private static readonly GoStruct FilesystemPolicyWire = new GoStruct("filesystemPolicyWire", "policy")
        .With("version", PolicyString).With("denyWritePaths", new GoSlice(PolicyString))
        .With("requiredCapabilities", new GoSlice(PolicyString));

    private static readonly GoStruct NetworkPolicyWire = new GoStruct("networkPolicyWire", "policy")
        .With("version", PolicyString)
        .With("network", new GoPointer(new GoStruct("networkWire", "policy")
            .With("egressAllow", new GoSlice(new GoStruct("networkEndpointWire", "policy")
                .With("address", PolicyString).With("protocol", PolicyString).With("port", AgentSuiteTypes.AnyValue)))))
        .With("requiredCapabilities", new GoSlice(PolicyString));

    private static readonly Regex CanonicalIpv4 = Go.Re(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z");

    private string? ValidatePolicies()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var errs = new List<string?>();
        foreach (var (id, agent) in Agents)
        {
            var reference = agent["policy"];
            if (reference.IsNull)
                continue;
            var expectedPath = "policies/" + id + ".json";
            if (reference["path"].S != expectedPath)
            {
                errs.Add($"agent {id} policy must be {expectedPath}");
                continue;
            }
            files.Add(expectedPath);
            if (ValidatePolicyDocument(reference) is { } policyError)
                errs.Add($"agent {id} policy: {policyError}");
        }
        foreach (var entry in _content.Entries)
        {
            if (entry.Path != "policies" && !entry.Path.StartsWith("policies/", StringComparison.Ordinal))
                continue;
            if (entry.Path == "policies" && entry.Type == "directory")
                continue;
            if (entry.Type != "file" || !files.Contains(entry.Path))
                errs.Add($"policy path {entry.Path} is orphaned, unreferenced, or not a regular policy file");
        }
        return Go.Join(errs);
    }

    private string? ValidatePolicyDocument(J reference)
    {
        var descriptor = reference["descriptor"];
        if (!Go.IsValidDigest(descriptor["digest"].S))
            return "policy reference digest is invalid";
        if (descriptor["size"].I < 0)
            return "policy reference size is invalid";
        var expectedVersion = descriptor["mediaType"].S switch
        {
            AgentSuiteTypes.MediaTypePolicy => PolicySchemaVersion,
            AgentSuiteTypes.MediaTypePolicyV2 => PolicySchemaVersionV2,
            _ => null,
        };
        if (expectedVersion is null)
            return "policy reference descriptor mediaType must be a supported policy media type";
        if (Data(reference["path"].S, descriptor, out var data) is { } dataError)
            return dataError;
        if (!SizeAndDigestMatch(data, descriptor))
            return "policy document digest mismatch";
        if (DecodeSuitePolicy(data, out var version) is { } decodeError)
            return decodeError;
        if (version != expectedVersion)
            return "policy version does not match descriptor mediaType";
        if (!MatchesCanonical(data, descriptor))
            return "policy document must be stored as canonical JSON";
        return null;
    }

    /// <summary>Upstream <c>policy.DecodeSuitePolicy</c>; returns the error and the decoded version.</summary>
    public static string? DecodeSuitePolicy(byte[] data, out string version)
    {
        version = string.Empty;
        if (DecodeSuiteWire(data, out version, out var policy) is { } wireError)
            return $"decode policy: {wireError}";
        return version == PolicySchemaVersionV2 ? ValidateNetworkProfile(policy) : ValidateFilesystemPolicy(version, policy);
    }

    private static string? PolicyDecodeStrict(byte[] data, GoType type, out J value)
    {
        value = default;
        var trimmed = Go.TrimSpace(Go.Decode(data));
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return "policy JSON must be an object";
        return Decode(data, type, out value);
    }

    private static string? DecodeSuiteWire(byte[] data, out string version, out J policy)
    {
        version = string.Empty;
        policy = default;
        if (PolicyDecodeStrict(data, AgentSuiteTypes.FieldMap, out var fields) is { } fieldsError)
            return fieldsError;
        var rawVersion = fields["version"];
        if (!rawVersion.Present)
            return "policy version: unexpected end of JSON input";
        if (rawVersion.Node!.Kind is not (JsonKind.String or JsonKind.Null))
            return $"policy version: json: cannot unmarshal {rawVersion.Node.GoValueName} into Go value of type string";
        version = rawVersion.S;
        switch (version)
        {
            case PolicySchemaVersion:
                return PolicyDecodeStrict(data, FilesystemPolicyWire, out policy);
            case PolicySchemaVersionV2:
                if (PolicyDecodeStrict(data, NetworkPolicyWire, out policy) is { } wireError)
                    return wireError;
                return NetworkFromWire(policy["network"]);
            default:
                return $"unsupported policy version {Go.Quote(version)}";
        }
    }

    private static string? NetworkFromWire(J network)
    {
        if (network.IsNull)
            return "network is required";
        var egress = network["egressAllow"];
        if (egress.IsNull)
            return "network egressAllow must be a non-null list";
        if (egress.Count > 4096)
            return "too many network egressAllow endpoints";
        var endpoints = egress.A;
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (NetworkPort(endpoints[i]["port"]) is null)
                return $"network endpoint {i}: {PortError}";
        }
        return ValidateNetwork(network);
    }

    /// <summary>Upstream <c>networkPort</c>: a JSON number that is exactly an integer in 1..65535.</summary>
    private static int? NetworkPort(J value)
    {
        if (value.Node is not { Kind: JsonKind.Number } node)
            return null;
        var text = node.Text!;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var approximate) ||
            double.IsInfinity(approximate) || approximate < 1 || approximate > 65535)
            return null;
        var mantissaEnd = text.IndexOfAny(['e', 'E']);
        var mantissa = mantissaEnd < 0 ? text : text[..mantissaEnd];
        var exponent = mantissaEnd < 0 ? 0 : long.Parse(text[(mantissaEnd + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var fraction = dot < 0 ? string.Empty : mantissa[(dot + 1)..];
        var digits = ((dot < 0 ? mantissa : mantissa[..dot]) + fraction).TrimStart('-').TrimEnd('0');
        var trailingZeros = ((dot < 0 ? mantissa : mantissa[..dot]) + fraction).TrimStart('-').Length - digits.Length;
        var scale = exponent - fraction.Length + trailingZeros;
        if (scale < 0)
            return null;
        // The approximate bound above keeps the exact value small.
        var exact = BigInteger.Parse(digits.Length == 0 ? "0" : digits, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)Math.Min(scale, 10));
        return exact < 1 || exact > 65535 ? null : (int)exact;
    }

    private static string? ValidateNetwork(J network)
    {
        if (network.IsNull)
            return "network is required";
        var egress = network["egressAllow"];
        if (egress.IsNull)
            return "network egressAllow must be a non-null list";
        if (egress.Count > 4096)
            return "too many network egressAllow endpoints";
        foreach (var endpoint in egress.A)
        {
            if (!CanonicalIpv4.IsMatch(endpoint["address"].S))
                return "network address must be a canonical IPv4 literal";
            if (endpoint["protocol"].S != "TCP")
                return "network protocol must be TCP";
            if (NetworkPort(endpoint["port"]) is null)
                return PortError;
        }
        return null;
    }

    private static string? ValidateNetworkProfile(J policy)
    {
        if (ValidateNetwork(policy["network"]) is { } networkError)
            return networkError;
        var capabilities = policy["requiredCapabilities"].A;
        if (capabilities.Count != 1 || capabilities[0].S != NetworkEgressAllow)
            return $"requiredCapabilities must be exactly [{Go.Quote(NetworkEgressAllow)}]";
        return null;
    }

    private static string? ValidateFilesystemPolicy(string version, J policy)
    {
        if (version != PolicySchemaVersion)
            return $"unsupported policy version {Go.Quote(version)}";
        var paths = policy["denyWritePaths"];
        if (paths.Count == 0)
            return "denyWritePaths must not be empty";
        if (paths.Count > 4096)
            return "too many denyWritePaths";
        foreach (var value in paths.A.Select(item => item.S))
        {
            if (!Go.ValidString(value))
                return $"path {Go.Quote(value)} must be valid UTF-8";
            if (value.Length == 0 || !value.StartsWith('/') || value.Contains('\0', StringComparison.Ordinal) || Go.CleanPath(value) != value)
                return $"path {Go.Quote(value)} must be clean and absolute";
            if (value == "/")
                return "root deny path is not supported";
        }
        var capabilities = policy["requiredCapabilities"].A;
        if (capabilities.Count == 0)
            return "requiredCapabilities must not be empty";
        if (capabilities.Count != 1 || capabilities[0].S != FilesystemDenyWrite)
            return $"requiredCapabilities must be exactly [{Go.Quote(FilesystemDenyWrite)}]";
        return null;
    }

    private string? ValidateEvaluations()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal) { "evals" };
        var errs = new List<string?>();
        foreach (var (id, agent) in Agents)
        {
            var evals = agent["evals"];
            if (evals.Count > 1)
            {
                errs.Add($"agent {id} evals permits at most one index");
                continue;
            }
            if (evals.Count == 0)
                continue;
            var prefix = "evals/" + id;
            directories.Add(prefix);
            directories.Add(prefix + "/cases");
            var reference = evals.A[0];
            if (reference["path"].S != prefix + "/index.json")
            {
                errs.Add($"agent {id} eval index must be {prefix}/index.json");
                continue;
            }
            files.Add(reference["path"].S);
            if (ValidateEvaluationIndex(id, reference, files) is { } indexError)
                errs.Add($"agent {id} evaluations: {indexError}");
        }
        foreach (var entry in _content.Entries)
        {
            if (entry.Path != "evals" && !entry.Path.StartsWith("evals/", StringComparison.Ordinal))
                continue;
            if (entry.Type == "directory" && directories.Contains(entry.Path))
                continue;
            if (entry.Type != "file" || !files.Contains(entry.Path))
                errs.Add($"eval path {entry.Path} is orphaned, unreferenced, or not a regular evaluation file");
        }
        return Go.Join(errs);
    }

    private string? EvaluationDocument(J reference, string expectedMediaType, GoType type, out byte[] data, out J value)
    {
        data = [];
        value = default;
        var descriptor = reference["descriptor"];
        if (!Go.IsValidDigest(descriptor["digest"].S))
            return "evaluation reference digest is invalid";
        if (descriptor["mediaType"].S != expectedMediaType)
            return $"evaluation reference descriptor mediaType must be {expectedMediaType}";
        if (Data(reference["path"].S, descriptor, out data) is { } dataError)
            return dataError;
        if (!SizeAndDigestMatch(data, descriptor))
            return "evaluation document digest mismatch";
        if (Decode(data, type, out value) is { } decodeError)
            return $"evaluation JSON: {decodeError}";
        if (!MatchesCanonical(data, descriptor))
            return "evaluation document digest mismatch";
        return null;
    }

    private string? ValidateEvaluationIndex(string agent, J reference, HashSet<string> files)
    {
        if (EvaluationDocument(reference, AgentSuiteTypes.MediaTypeEvaluationIndex, AgentSuiteTypes.EvaluationIndex, out _, out var index) is { } indexError)
            return indexError;
        if (index["schemaVersion"].S != AgentSuiteTypes.SpecVersion || index["mediaType"].S != AgentSuiteTypes.MediaTypeEvaluationIndex)
            return "unsupported evaluation index schemaVersion or mediaType";
        if (index["agent"].S != agent)
            return "evaluation index agent does not match owning agent";
        var maxCases = index["limits"]["maxCases"].I;
        var maxCaseBytes = index["limits"]["maxCaseBytes"].I;
        if (maxCases < 1 || maxCases > 128 || maxCaseBytes < 1 || maxCaseBytes > AgentSuiteJson.MaxBytes)
            return "evaluation limits require maxCases in 1..128 and maxCaseBytes in 1..4194304";
        var cases = index["cases"];
        if (cases.Count == 0 || cases.Count > maxCases)
            return "evaluation case count exceeds limits or is empty";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caseRef in cases.A)
        {
            var prefix = "evals/" + agent + "/cases/";
            var path = caseRef["path"].S;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal))
                return "evaluation case path must be inside the owning agent's cases directory";
            if (!files.Add(path))
                return "duplicate evaluation case reference";
            if (EvaluationDocument(caseRef, AgentSuiteTypes.MediaTypeEvaluationCase, AgentSuiteTypes.EvaluationCase, out var data, out var evaluationCase) is { } caseError)
                return caseError;
            if (data.Length > maxCaseBytes)
                return "evaluation case exceeds maxCaseBytes";
            if (evaluationCase["schemaVersion"].S != AgentSuiteTypes.SpecVersion ||
                evaluationCase["mediaType"].S != AgentSuiteTypes.MediaTypeEvaluationCase)
                return "unsupported evaluation case schemaVersion or mediaType";
            var caseId = evaluationCase["id"].S;
            if (!Go.EvaluationIdPattern.IsMatch(caseId) || ids.Contains(caseId))
                return "evaluation case id is invalid or duplicated";
            ids.Add(caseId);
            if (path != prefix + caseId + ".json")
                return "evaluation case filename must match its id";
            if (ValidateEvaluationRunPolicy(evaluationCase) is { } runError)
                return runError;
            var assertions = evaluationCase["assertions"];
            if (Go.TrimSpace(evaluationCase["input"].S).Length == 0 || assertions.Count == 0 || assertions.Count > 1000)
                return "evaluation case requires input and 1..1000 assertions";
            var assertionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assertion in assertions.A)
            {
                var assertionId = assertion["id"].S;
                if (!Go.EvaluationIdPattern.IsMatch(assertionId) || assertionIds.Contains(assertionId))
                    return "evaluation assertion id is invalid or duplicated";
                assertionIds.Add(assertionId);
                if (ValidateEvaluationAssertion(assertion) is { } assertionError)
                    return assertionError;
            }
        }
        return null;
    }

    private static string? ValidateEvaluationRunPolicy(J evaluationCase)
    {
        var runs = evaluationCase["runs"];
        var minPasses = evaluationCase["minPasses"];
        if (runs is { Present: true, IsNull: true } || minPasses is { Present: true, IsNull: true })
            return "evaluation runs and minPasses must not be null";
        if (!runs.Present)
            return minPasses.Present ? "evaluation minPasses requires runs" : null;
        if (runs.I < 1)
            return "evaluation runs must be a positive integer";
        if (!minPasses.Present)
            return null;
        if (minPasses.I < 1 || minPasses.I > runs.I)
            return "evaluation minPasses must be positive and no greater than runs";
        return null;
    }

    /// <summary>
    /// Upstream <c>validateEvaluationAssertion</c>. RE2 syntax is approximated with the .NET
    /// non-backtracking engine, which also rejects backreferences and lookaround.
    /// </summary>
    private static string? ValidateEvaluationAssertion(J assertion)
    {
        var required = new List<string> { "id", "type" };
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "id", "type", "critical" };
        var type = assertion["type"].S;
        switch (type)
        {
            case "contains":
            case "notContains":
                required.Add("value");
                if (!EvaluationSingleLine(assertion["value"].S))
                    return "evaluation assertion value must be nonblank and single-line";
                break;
            case "regex":
                required.Add("pattern");
                var pattern = assertion["pattern"].S;
                if (Go.TrimSpace(pattern).Length == 0 || Go.ByteLength(pattern) > 1024)
                    return "evaluation regex must be nonblank and at most 1024 bytes";
                if (!CompilesAsRe2(pattern))
                    return "evaluation regex must use valid RE2 syntax";
                break;
            case "toolCalled":
            case "toolNotCalled":
                required.Add("tool");
                if (!EvaluationSingleLine(assertion["tool"].S))
                    return "evaluation tool must be nonblank and single-line";
                break;
            case "llmJudge":
                required.AddRange(["rubric", "minScore", "judgeModel", "judgeVersion"]);
                var rubric = assertion["rubric"].S;
                if (Go.TrimSpace(rubric).Length == 0 || Go.ByteLength(rubric) > 4096 ||
                    rubric.EnumerateRunes().Any(rune => Go.IsControl(rune) && rune.Value != '\n' && rune.Value != '\t'))
                    return "evaluation rubric must be nonblank, at most 4096 bytes, and free of controls except newline and tab";
                var minScore = assertion["minScore"];
                if (minScore.IsNull || minScore.I < 0 || minScore.I > 100)
                    return "evaluation minScore must be an integer in 0..100";
                foreach (var value in new[] { assertion["judgeModel"].S, assertion["judgeVersion"].S })
                {
                    var length = Go.ByteLength(value);
                    if (length == 0 || length > 256 || Go.TrimSpace(value).Length == 0 ||
                        value.EnumerateRunes().Any(rune => rune.Value < 0x20 || rune.Value > 0x7e))
                        return "evaluation judge identity must be nonblank printable ASCII of at most 256 bytes";
                }
                break;
            default:
                var critical = assertion["critical"];
                if (!Id(type) || critical.IsNull || critical.B)
                    return "unknown evaluation assertion type requires explicit critical:false";
                required.AddRange(["value", "critical"]);
                if (Go.TrimSpace(assertion["value"].S).Length == 0)
                    return "non-critical evaluation assertion requires a nonblank value";
                break;
        }
        allowed.UnionWith(required);
        foreach (var field in required)
        {
            if (!assertion.Has(field))
                return "evaluation assertion is missing a required field";
        }
        foreach (var (field, value) in assertion.Members)
        {
            if (!allowed.Contains(field) || value.IsNull)
                return "evaluation assertion contains an inapplicable or null field";
        }
        return null;
    }

    private static bool CompilesAsRe2(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool EvaluationSingleLine(string value) =>
        Go.TrimSpace(value).Length != 0 &&
        !value.EnumerateRunes().Any(rune => Go.IsControl(rune) || rune.Value is 0x2028 or 0x2029);
}
