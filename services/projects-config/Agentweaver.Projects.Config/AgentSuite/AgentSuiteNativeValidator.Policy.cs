using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Ported upstream checks for per-agent policy bindings, policy documents, and evaluation
/// (test case) documents. Policies are only validated and retained here; they are not enforced.
/// </summary>
internal sealed partial class AgentSuiteNativeValidator
{
    // Policy v1 is a filesystem deny-write policy; policy v2 is a network egress allow-list.
    private const string PolicySchemaVersion = "agentsuite.policy/v1";
    private const string PolicySchemaVersionV2 = "agentsuite.policy/v2";
    private const string FilesystemDenyWrite = "filesystem/deny-write/v1";
    private const string NetworkEgressAllow = "network/egress-allow/v1";
    private const string PortError = "network port must be an integer from 1 to 65535";

    private static readonly GoPrim PolicyString = new(GoPrimKind.String);

    // Wire shapes copied from the upstream Go structs so strict decoding accepts and rejects
    // exactly the same fields (unknown fields fail, wrong JSON types fail).
    private static readonly GoStruct FilesystemPolicyWire = new GoStruct("filesystemPolicyWire", "policy")
        .With("version", PolicyString).With("denyWritePaths", new GoSlice(PolicyString))
        .With("requiredCapabilities", new GoSlice(PolicyString));

    private static readonly GoStruct NetworkPolicyWire = new GoStruct("networkPolicyWire", "policy")
        .With("version", PolicyString)
        .With("network", new GoPointer(new GoStruct("networkWire", "policy")
            .With("egressAllow", new GoSlice(new GoStruct("networkEndpointWire", "policy")
                // "port" is kept as raw JSON so NetworkPort can apply its exact integer rules.
                .With("address", PolicyString).With("protocol", PolicyString).With("port", AgentSuiteTypes.AnyValue)))))
        .With("requiredCapabilities", new GoSlice(PolicyString));

    // Dotted IPv4 with no leading zeros, so each address has exactly one accepted spelling.
    private static readonly Regex CanonicalIpv4 = Go.Re(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\z");

    private string? ValidatePolicies()
    {
        // Policy files that some agent references; anything else under policies/ is an error.
        var files = new HashSet<string>(StringComparer.Ordinal);
        // Collect every error instead of stopping at the first, matching upstream errors.Join.
        var errs = new List<string?>();
        foreach (var (id, agent) in Agents)
        {
            // Policy binding is optional per agent.
            var reference = agent["policy"];
            if (reference.IsNull)
                continue;
            // Each agent may only bind its own fixed file, so one policy can't be shared or swapped.
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
        // Second pass over the archive: reject stray, unreferenced, or non-file entries under policies/.
        foreach (var entry in _content.Entries)
        {
            if (entry.Path != "policies" && !entry.Path.StartsWith("policies/", StringComparison.Ordinal))
                continue;
            // The policies/ directory itself is allowed.
            if (entry.Path == "policies" && entry.Type == "directory")
                continue;
            // Symlinks, nested directories, and unreferenced files are all refused.
            if (entry.Type != "file" || !files.Contains(entry.Path))
                errs.Add($"policy path {entry.Path} is orphaned, unreferenced, or not a regular policy file");
        }
        return Go.Join(errs);
    }

    private string? ValidatePolicyDocument(J reference)
    {
        // The descriptor records the expected media type, size, and digest of the policy file.
        var descriptor = reference["descriptor"];
        if (!Go.IsValidDigest(descriptor["digest"].S))
            return "policy reference digest is invalid";
        if (descriptor["size"].I < 0)
            return "policy reference size is invalid";
        // The media type decides which policy schema version the document must declare.
        var expectedVersion = descriptor["mediaType"].S switch
        {
            AgentSuiteTypes.MediaTypePolicy => PolicySchemaVersion,
            AgentSuiteTypes.MediaTypePolicyV2 => PolicySchemaVersionV2,
            _ => null,
        };
        if (expectedVersion is null)
            return "policy reference descriptor mediaType must be a supported policy media type";
        // Read the file bytes from the archive and verify them against the declared size and digest.
        if (Data(reference["path"].S, descriptor, out var data) is { } dataError)
            return dataError;
        if (!SizeAndDigestMatch(data, descriptor))
            return "policy document digest mismatch";
        // Decode and check the policy content, then confirm it matches the declared media type.
        if (DecodeSuitePolicy(data, out var version) is { } decodeError)
            return decodeError;
        if (version != expectedVersion)
            return "policy version does not match descriptor mediaType";
        // The bytes must already be canonical JSON (RFC 8785), so the digest has one meaning.
        if (!MatchesCanonical(data, descriptor))
            return "policy document must be stored as canonical JSON";
        return null;
    }

    /// <summary>Upstream <c>policy.DecodeSuitePolicy</c>; returns the error and the decoded version.</summary>
    public static string? DecodeSuitePolicy(byte[] data, out string version)
    {
        version = string.Empty;
        // Step 1: strict JSON decoding into the shape for the declared version.
        if (DecodeSuiteWire(data, out version, out var policy) is { } wireError)
            return $"decode policy: {wireError}";
        // Step 2: semantic rules for that version (network profile for v2, filesystem for v1).
        return version == PolicySchemaVersionV2 ? ValidateNetworkProfile(policy) : ValidateFilesystemPolicy(version, policy);
    }

    private static string? PolicyDecodeStrict(byte[] data, GoType type, out J value)
    {
        value = default;
        // Go would silently decode "null" into an empty struct, so upstream first requires a JSON
        // object. Checking the first non-space character mirrors that.
        var trimmed = Go.TrimSpace(Go.Decode(data));
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return "policy JSON must be an object";
        return Decode(data, type, out value);
    }

    private static string? DecodeSuiteWire(byte[] data, out string version, out J policy)
    {
        version = string.Empty;
        policy = default;
        // First pass: read only the top-level fields to find "version", which decides the schema.
        if (PolicyDecodeStrict(data, AgentSuiteTypes.FieldMap, out var fields) is { } fieldsError)
            return fieldsError;
        var rawVersion = fields["version"];
        // The error texts below reproduce what Go prints for these cases, so results match upstream.
        if (!rawVersion.Present)
            return "policy version: unexpected end of JSON input";
        // null is allowed here (it decodes to ""), and is then refused as an unsupported version.
        if (rawVersion.Node!.Kind is not (JsonKind.String or JsonKind.Null))
            return $"policy version: json: cannot unmarshal {rawVersion.Node.GoValueName} into Go value of type string";
        version = rawVersion.S;
        // Second pass: decode the whole document strictly with the shape for that version.
        switch (version)
        {
            case PolicySchemaVersion:
                return PolicyDecodeStrict(data, FilesystemPolicyWire, out policy);
            case PolicySchemaVersionV2:
                if (PolicyDecodeStrict(data, NetworkPolicyWire, out policy) is { } wireError)
                    return wireError;
                // v2 needs extra wire checks (null lists, port numbers) before semantic validation.
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
        // Cap list size so a hostile package can't force huge work.
        if (egress.Count > 4096)
            return "too many network egressAllow endpoints";
        // Upstream converts ports while decoding, so port errors include the endpoint index and
        // are reported before the address and protocol checks below.
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
        // Strings like "443" are refused; only real JSON numbers count.
        if (value.Node is not { Kind: JsonKind.Number } node)
            return null;
        // Work from the original number text so values like 443.0 or 4.43e2 are judged exactly,
        // without floating-point rounding.
        var text = node.Text!;
        // Quick range check with a double first; this also bounds the exponent math below.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var approximate) ||
            double.IsInfinity(approximate) || approximate < 1 || approximate > 65535)
            return null;
        // Split the text into mantissa (digits with an optional ".") and decimal exponent.
        var mantissaEnd = text.IndexOfAny(['e', 'E']);
        var mantissa = mantissaEnd < 0 ? text : text[..mantissaEnd];
        var exponent = mantissaEnd < 0 ? 0 : long.Parse(text[(mantissaEnd + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var fraction = dot < 0 ? string.Empty : mantissa[(dot + 1)..];
        // Join integer and fraction digits, dropping trailing zeros (they only shift the scale).
        var digits = ((dot < 0 ? mantissa : mantissa[..dot]) + fraction).TrimStart('-').TrimEnd('0');
        var trailingZeros = ((dot < 0 ? mantissa : mantissa[..dot]) + fraction).TrimStart('-').Length - digits.Length;
        // value = digits * 10^scale. A negative scale means a non-zero fractional part,
        // so the number is not an integer (for example 443.5).
        var scale = exponent - fraction.Length + trailingZeros;
        if (scale < 0)
            return null;
        // The approximate bound above keeps the exact value small, so capping the power at 10 is safe.
        var exact = BigInteger.Parse(digits.Length == 0 ? "0" : digits, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)Math.Min(scale, 10));
        return exact < 1 || exact > 65535 ? null : (int)exact;
    }

    private static string? ValidateNetwork(J network)
    {
        // Semantic rules for the egress allow-list. The list is recorded only; nothing enforces it.
        if (network.IsNull)
            return "network is required";
        var egress = network["egressAllow"];
        if (egress.IsNull)
            return "network egressAllow must be a non-null list";
        if (egress.Count > 4096)
            return "too many network egressAllow endpoints";
        foreach (var endpoint in egress.A)
        {
            // Only exact IPv4 + TCP + port entries; hostnames, IPv6, and UDP are not supported in v2.
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
        // A v2 policy must declare exactly the one runtime capability needed to apply it.
        var capabilities = policy["requiredCapabilities"].A;
        if (capabilities.Count != 1 || capabilities[0].S != NetworkEgressAllow)
            return $"requiredCapabilities must be exactly [{Go.Quote(NetworkEgressAllow)}]";
        return null;
    }

    private static string? ValidateFilesystemPolicy(string version, J policy)
    {
        // Also catches unknown versions when this is reached from the decoder's fallback branch.
        if (version != PolicySchemaVersion)
            return $"unsupported policy version {Go.Quote(version)}";
        var paths = policy["denyWritePaths"];
        // An empty deny list would be a policy that does nothing, so it is refused.
        if (paths.Count == 0)
            return "denyWritePaths must not be empty";
        // Cap list size so a hostile package can't force huge work.
        if (paths.Count > 4096)
            return "too many denyWritePaths";
        foreach (var value in paths.A.Select(item => item.S))
        {
            // Strings with invalid UTF-8 (e.g. lone surrogates) can't be compared reliably.
            if (!Go.ValidString(value))
                return $"path {Go.Quote(value)} must be valid UTF-8";
            // Must be absolute, contain no NUL, and already be normalized (no "..", "//", or
            // trailing "/"), so each path has one unambiguous meaning.
            if (value.Length == 0 || !value.StartsWith('/') || value.Contains('\0', StringComparison.Ordinal) || Go.CleanPath(value) != value)
                return $"path {Go.Quote(value)} must be clean and absolute";
            // Denying writes to "/" would block the whole filesystem.
            if (value == "/")
                return "root deny path is not supported";
        }
        // A v1 policy must declare exactly the one runtime capability needed to apply it.
        var capabilities = policy["requiredCapabilities"].A;
        if (capabilities.Count == 0)
            return "requiredCapabilities must not be empty";
        if (capabilities.Count != 1 || capabilities[0].S != FilesystemDenyWrite)
            return $"requiredCapabilities must be exactly [{Go.Quote(FilesystemDenyWrite)}]";
        return null;
    }

    private string? ValidateEvaluations()
    {
        // Files and directories that the agents' eval indexes account for; anything else is stray.
        // ValidateEvaluationIndex adds each case file to "files" too.
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal) { "evals" };
        var errs = new List<string?>();
        foreach (var (id, agent) in Agents)
        {
            // Evaluations are optional, but an agent may have at most one index.
            var evals = agent["evals"];
            if (evals.Count > 1)
            {
                errs.Add($"agent {id} evals permits at most one index");
                continue;
            }
            if (evals.Count == 0)
                continue;
            // Fixed layout per agent: evals/<id>/index.json plus evals/<id>/cases/<case>.json.
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
        // Second pass over the archive: reject anything under evals/ that no index accounts for.
        foreach (var entry in _content.Entries)
        {
            if (entry.Path != "evals" && !entry.Path.StartsWith("evals/", StringComparison.Ordinal))
                continue;
            if (entry.Type == "directory" && directories.Contains(entry.Path))
                continue;
            // Symlinks, extra directories, and unreferenced files are all refused.
            if (entry.Type != "file" || !files.Contains(entry.Path))
                errs.Add($"eval path {entry.Path} is orphaned, unreferenced, or not a regular evaluation file");
        }
        return Go.Join(errs);
    }

    private string? EvaluationDocument(J reference, string expectedMediaType, GoType type, out byte[] data, out J value)
    {
        data = [];
        value = default;
        // Shared loader for eval index and case files: check the descriptor, read the bytes,
        // verify size and digest, decode strictly, and require canonical JSON.
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
        // Non-canonical bytes are reported as a digest mismatch, which is the upstream wording.
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
        // The index must name the agent that references it, so it can't be reused by another agent.
        if (index["agent"].S != agent)
            return "evaluation index agent does not match owning agent";
        // The index declares its own limits, but they must stay within fixed upper bounds
        // (128 cases, and no case larger than the global JSON size cap).
        var maxCases = index["limits"]["maxCases"].I;
        var maxCaseBytes = index["limits"]["maxCaseBytes"].I;
        if (maxCases < 1 || maxCases > 128 || maxCaseBytes < 1 || maxCaseBytes > AgentSuiteJson.MaxBytes)
            return "evaluation limits require maxCases in 1..128 and maxCaseBytes in 1..4194304";
        var cases = index["cases"];
        if (cases.Count == 0 || cases.Count > maxCases)
            return "evaluation case count exceeds limits or is empty";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Unlike the policy pass, this returns on the first bad case (upstream behavior).
        foreach (var caseRef in cases.A)
        {
            // Case files must live in this agent's own cases directory.
            var prefix = "evals/" + agent + "/cases/";
            var path = caseRef["path"].S;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal))
                return "evaluation case path must be inside the owning agent's cases directory";
            // Adding to the shared set both marks the file as referenced and catches duplicates.
            if (!files.Add(path))
                return "duplicate evaluation case reference";
            if (EvaluationDocument(caseRef, AgentSuiteTypes.MediaTypeEvaluationCase, AgentSuiteTypes.EvaluationCase, out var data, out var evaluationCase) is { } caseError)
                return caseError;
            if (data.Length > maxCaseBytes)
                return "evaluation case exceeds maxCaseBytes";
            if (evaluationCase["schemaVersion"].S != AgentSuiteTypes.SpecVersion ||
                evaluationCase["mediaType"].S != AgentSuiteTypes.MediaTypeEvaluationCase)
                return "unsupported evaluation case schemaVersion or mediaType";
            // Case IDs must be well-formed and unique within this index.
            var caseId = evaluationCase["id"].S;
            if (!Go.EvaluationIdPattern.IsMatch(caseId) || ids.Contains(caseId))
                return "evaluation case id is invalid or duplicated";
            ids.Add(caseId);
            // The file name is derived from the ID, so each case has exactly one location.
            if (path != prefix + caseId + ".json")
                return "evaluation case filename must match its id";
            if (ValidateEvaluationRunPolicy(evaluationCase) is { } runError)
                return runError;
            // A case needs a prompt (input) and a bounded number of checks (assertions).
            var assertions = evaluationCase["assertions"];
            if (Go.TrimSpace(evaluationCase["input"].S).Length == 0 || assertions.Count == 0 || assertions.Count > 1000)
                return "evaluation case requires input and 1..1000 assertions";
            // Assertion IDs only need to be unique within their own case.
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
        // Optional repeat settings: run the case "runs" times and require "minPasses" successes.
        var runs = evaluationCase["runs"];
        var minPasses = evaluationCase["minPasses"];
        // An explicit null is refused; to omit a setting, leave the field out.
        if (runs is { Present: true, IsNull: true } || minPasses is { Present: true, IsNull: true })
            return "evaluation runs and minPasses must not be null";
        // Neither field: single run, fine. minPasses alone is meaningless without runs.
        if (!runs.Present)
            return minPasses.Present ? "evaluation minPasses requires runs" : null;
        if (runs.I < 1)
            return "evaluation runs must be a positive integer";
        if (!minPasses.Present)
            return null;
        // You can't require more passes than there are runs.
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
        // Each assertion type has its own required fields. "required" collects them, and
        // "allowed" is the same list plus the always-optional "critical" flag; any other
        // field is rejected at the end.
        var required = new List<string> { "id", "type" };
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "id", "type", "critical" };
        var type = assertion["type"].S;
        switch (type)
        {
            // Output text must (or must not) contain a fixed string.
            case "contains":
            case "notContains":
                required.Add("value");
                if (!EvaluationSingleLine(assertion["value"].S))
                    return "evaluation assertion value must be nonblank and single-line";
                break;
            // Output must match a regular expression. Size is capped, and only RE2-style
            // syntax (no backtracking features) is accepted so matching stays linear-time.
            case "regex":
                required.Add("pattern");
                var pattern = assertion["pattern"].S;
                if (Go.TrimSpace(pattern).Length == 0 || Go.ByteLength(pattern) > 1024)
                    return "evaluation regex must be nonblank and at most 1024 bytes";
                if (!CompilesAsRe2(pattern))
                    return "evaluation regex must use valid RE2 syntax";
                break;
            // The agent must (or must not) call the named tool.
            case "toolCalled":
            case "toolNotCalled":
                required.Add("tool");
                if (!EvaluationSingleLine(assertion["tool"].S))
                    return "evaluation tool must be nonblank and single-line";
                break;
            // Another model grades the output against a rubric. The judge model and version
            // must be pinned so scores are reproducible.
            case "llmJudge":
                required.AddRange(["rubric", "minScore", "judgeModel", "judgeVersion"]);
                var rubric = assertion["rubric"].S;
                // Rubric may be multi-line (newline and tab allowed) but no other control characters.
                if (Go.TrimSpace(rubric).Length == 0 || Go.ByteLength(rubric) > 4096 ||
                    rubric.EnumerateRunes().Any(rune => Go.IsControl(rune) && rune.Value != '\n' && rune.Value != '\t'))
                    return "evaluation rubric must be nonblank, at most 4096 bytes, and free of controls except newline and tab";
                var minScore = assertion["minScore"];
                if (minScore.IsNull || minScore.I < 0 || minScore.I > 100)
                    return "evaluation minScore must be an integer in 0..100";
                // Judge identity strings: 1..256 bytes of printable ASCII (0x20..0x7E) only.
                foreach (var value in new[] { assertion["judgeModel"].S, assertion["judgeVersion"].S })
                {
                    var length = Go.ByteLength(value);
                    if (length == 0 || length > 256 || Go.TrimSpace(value).Length == 0 ||
                        value.EnumerateRunes().Any(rune => rune.Value < 0x20 || rune.Value > 0x7e))
                        return "evaluation judge identity must be nonblank printable ASCII of at most 256 bytes";
                }
                break;
            // Unknown (future) assertion types are tolerated only when marked critical:false,
            // so a runner that doesn't understand them may safely skip them.
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
        // Every required field for this type must be present...
        foreach (var field in required)
        {
            if (!assertion.Has(field))
                return "evaluation assertion is missing a required field";
        }
        // ...and no field may be null or belong to a different assertion type.
        foreach (var (field, value) in assertion.Members)
        {
            if (!allowed.Contains(field) || value.IsNull)
                return "evaluation assertion contains an inapplicable or null field";
        }
        return null;
    }

    private static bool CompilesAsRe2(string pattern)
    {
        // Compile only to check syntax; the pattern is never run here. The non-backtracking
        // engine refuses the same backtracking-only features RE2 lacks.
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

    // Nonblank and on one line: no control characters (including newlines) and no
    // Unicode line/paragraph separators (U+2028, U+2029).
    private static bool EvaluationSingleLine(string value) =>
        Go.TrimSpace(value).Length != 0 &&
        !value.EnumerateRunes().Any(rune => Go.IsControl(rune) || rune.Value is 0x2028 or 0x2029);
}
