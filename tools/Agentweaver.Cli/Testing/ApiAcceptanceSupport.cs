using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Net;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal sealed record AcceptanceManifest(
    JsonObject Root,
    JsonObject Environment,
    JsonObject Pins,
    JsonObject FixturePlan,
    string Journey,
    string InputPath,
    string ManifestDirectory);

internal sealed class AcceptanceFixtureState(string invocationId)
{
    public string InvocationId { get; } = invocationId;
    public string ProjectName { get; } = $"p1-{invocationId.ToLowerInvariant()}";
    public JsonObject? Setup { get; set; }
    public JsonObject? Selection { get; set; }
    public JsonObject? Cleanup { get; set; }
    public List<JsonObject> RequestFailures { get; } = [];
    public bool SetupAttempted { get; set; }
    public bool SetupIndeterminate { get; set; }
    public bool SetupComplete { get; set; }
    public bool CleanupAttempted { get; set; }
}

internal static partial class ApiAcceptanceSupport
{
    private static readonly HashSet<string> Journeys =
        ["gate-answer", "gate-approve", "gate-reject", "message", "journal"];

    private static readonly string[] RequiredReceiptPins =
        ["providerReceipt", "runtimeReceipt", "storageReceipt", "sourceReceipt", "imageReceipt"];

    private static readonly string[] NativeProbeNames =
    [
        "storage-owner-isolation",
        "network-policy-datapath",
        "sandbox-owner-lifecycle",
        "agenthost-image-readiness",
        "numeric-copilot-dispatch-boundaries",
        "checkpoint-restart-duplicate-fence",
    ];

    private static readonly Dictionary<string, string> NativeProbeReasons = new(StringComparer.Ordinal)
    {
        ["storage-owner-isolation"] = "The existing Storage owner lifecycle is a direct Environment contract; no admitted surface runner observes a mounted cross-owner denial and retention receipt.",
        ["network-policy-datapath"] = "Cilium policy readback does not prove datapath enforcement, and the source adapter reports DatapathEnforcementVerified=false.",
        ["sandbox-owner-lifecycle"] = "The provider-owned Environment lifecycle and exact native absence/reclaim receipts are not exposed by the admitted Gateway clients.",
        ["agenthost-image-readiness"] = "No exact provider-owned Host Pod, readiness-phase receipt, or installed positive phase-budget set is available through the admitted surface clients.",
        ["numeric-copilot-dispatch-boundaries"] = "A usage read is not an at/beyond-boundary dispatch probe; Gateway has no dispatch route for the admitted harness client, and no live boundary receipt exists.",
        ["checkpoint-restart-duplicate-fence"] = "The duplicate-fence probe needs a Host replacement with a fresh lease and retained workspace; no verified old/new lease and duplicate-fence receipts exist.",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex SensitiveName = SensitiveNamePattern();
    private static readonly Regex SensitiveDescriptor = new(
        @"(?:^|[ _-])(?:authorization|cookie|token|credential|secret|password|kubeconfig|storagestate|signedurl)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SensitiveKeyDescriptor = new(
        @"(?:^|[ _-])(?:api|execution|provider)[ _-]?key$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StandaloneSensitiveDescriptor = new(
        @"^(?:api|provider)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DescriptorKey = new(
        @"^(?:descriptor|header|key|label|name|type)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DescriptorValueKey = new(
        @"^(?:val|value|values)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(
        "https?://[^\\s\"'\\\\]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(
        "((?:authorization|cookie|token|credentials?|api[_-]?key|execution[_-]?key|provider[_-]?key|secret|password)\\s*[:=]\\s*[\"']?)(?:Bearer\\s+)?[^\"',;\\s}\\]]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretCanary = new(
        @"\b(?:(?:credential|secret|token|bearer)[_-]?canary|canary[_-]?(?:credential|secret|token|bearer))(?:[-_][A-Za-z0-9]+)*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TokenCountName = new(
        @"^(?:input|output|cached|cacheWrite|reasoning|maxPrompt|prompt|total)Tokens$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BearerPattern = BearerTokenPattern();
    private static readonly Regex JwtPattern = JwtTokenPattern();
    private static readonly Regex GitHubTokenPattern = GitHubTokenValuePattern();

    public static async Task<AcceptanceManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(path);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("The P1 input manifest is missing or malformed JSON.");
        }

        JsonObject root = RequireObject(node, "The P1 runner input must be a JSON object.");
        RejectSecrets(root);
        if (GetString(root, "executionIntent") != "execute")
        {
            throw new InvalidDataException("P1 runner input must explicitly set executionIntent to \"execute\".");
        }

        JsonObject environment = RequireObject(root["environment"], "The direct Environment target is required.");
        ValidateGatewayTarget(RequireString(environment, "baseUrl", "Gateway origin"));
        foreach (string field in new[] { "issuer", "audience", "actorId", "tenantId" })
        {
            RequireString(environment, field, $"Environment {field}");
        }

        JsonObject pins = RequireObject(root["pins"], "Pinned P1 environment receipts are required.");
        string context = RequireString(pins, "context", "Kubernetes context");
        string namespaceName = RequireString(pins, "namespace", "Kubernetes namespace");
        if (!Regex.IsMatch(context, "^[A-Za-z0-9._:/@-]{1,128}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(namespaceName, "^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("The exact Kubernetes context or namespace is invalid.");
        }

        foreach (string name in RequiredReceiptPins)
        {
            JsonObject receipt = RequireObject(pins[name], $"A pinned {name} is required.");
            if (receipt.Count == 0)
            {
                throw new InvalidDataException($"A pinned {name} is required.");
            }
        }

        ValidatePins(pins);
        JsonObject evidence = RequireObject(root["deploymentEvidence"], "Exact source deployment evidence paths are required.");
        string[] requiredPaths = ["publicationPath", "manifestPath", "agentHostArchivePath"];
        string[] allowedPaths = [.. requiredPaths, "integrationDeploymentReceiptPath", "releaseTag"];
        RequireExactKeys(evidence, allowedPaths.Where(evidence.ContainsKey).ToArray(), "deployment evidence");
        foreach (string name in requiredPaths)
        {
            RequireString(evidence, name, $"deployment evidence {name}");
        }

        if (evidence.ContainsKey("integrationDeploymentReceiptPath"))
        {
            RequireString(evidence, "integrationDeploymentReceiptPath", "deployment evidence integrationDeploymentReceiptPath");
        }
        if (evidence.ContainsKey("releaseTag")
            && !Regex.IsMatch(RequireString(evidence, "releaseTag", "deployment evidence releaseTag"),
                "^v\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("The deployment evidence releaseTag must be an exact versioned release tag.");
        }

        string journey = RequireString(root, "journey", "supported P1 journey");
        if (!Journeys.Contains(journey))
        {
            throw new InvalidDataException("A supported P1 journey is required.");
        }

        JsonObject fixturePlan = RequireObject(root["fixturePlan"], "An explicit runner fixture plan is required.");
        ValidateFixturePlan(fixturePlan, environment, journey, pins);
        return new(root, environment, pins, fixturePlan, journey, fullPath, Path.GetDirectoryName(fullPath)!);
    }

    public static void ValidateGatewayTarget(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host)))
        {
            throw new InvalidDataException("The P1 Gateway target must be an exact HTTPS origin (HTTP is allowed only for loopback tests).");
        }
    }

    private static bool IsLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static void ValidatePins(JsonObject pins)
    {
        JsonObject provider = (JsonObject)pins["providerReceipt"]!;
        RequireExactKeys(provider, ["configMapName", "sourceCommit", "uid", "resourceVersion"], "provider catalog receipt pin");
        RequireString(provider, "configMapName", "provider catalog ConfigMap name");
        RequireString(provider, "uid", "provider catalog ConfigMap UID");
        RequireString(provider, "resourceVersion", "provider catalog resource version");
        RequireSha(provider["sourceCommit"]?.GetValue<string>(), "A pinned provider catalog source commit is required.");

        JsonObject source = (JsonObject)pins["sourceReceipt"]!;
        RequireExactKeys(source, ["sourceSha", "sourceTree"], "source receipt pin");
        RequireSha(source["sourceSha"]?.GetValue<string>(), "The source receipt must pin an exact Git commit and tree.");
        RequireSha(source["sourceTree"]?.GetValue<string>(), "The source receipt must pin an exact Git commit and tree.");

        JsonObject image = (JsonObject)pins["imageReceipt"]!;
        RequireExactKeys(image,
            ["registryImage", "registryDigest", "archiveSha256", "manifestDigest", "configDigest", "compressedPullBytes"],
            "AgentHost image receipt pin");
        string reference = RequireString(image, "registryImage", "immutable AgentHost registry image");
        Match match = Regex.Match(reference, "^ghcr\\.io/[a-z0-9._/-]+@(sha256:[a-f0-9]{64})$", RegexOptions.CultureInvariant);
        if (!match.Success || GetString(image, "registryDigest") != match.Groups[1].Value
            || !IsSha256(GetString(image, "archiveSha256"))
            || !Regex.IsMatch(GetString(image, "manifestDigest") ?? "", "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(GetString(image, "configDigest") ?? "", "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)
            || !TryLong(image["compressedPullBytes"], out long bytes) || bytes < 1)
        {
            throw new InvalidDataException("The AgentHost image receipt must pin its immutable registry, archive, OCI, config, and byte identities.");
        }
    }

    private static void ValidateFixturePlan(JsonObject plan, JsonObject environment, string journey, JsonObject pins)
    {
        if (pins.ContainsKey("ownerFences") || pins.ContainsKey("fixtureScope"))
        {
            throw new InvalidDataException("Runner plans cannot contain caller-supplied owner fences or fixture receipts.");
        }

        string[] fixtureKeys = plan.ContainsKey("deniedActor")
            ? ["expectedPlatformRuntimeRevision", "selectionContext", "gate", "message", "deniedActor"]
            : ["expectedPlatformRuntimeRevision", "selectionContext", "gate", "message"];
        RequireExactKeys(plan, fixtureKeys, "runner fixture plan");
        if (!TryLong(plan["expectedPlatformRuntimeRevision"], out long revision) || revision < 1)
        {
            throw new InvalidDataException("A positive expected platform runtime revision is required in the runner fixture plan.");
        }

        JsonObject context = RequireObject(plan["selectionContext"], "The run selection context is required.");
        RequireExactKeys(context,
            ["revision", "availableModelSelectionReferences", "providerRequirements", "requiredEgress"],
            "run selection context");
        RequireString(context, "revision", "run selection context revision");
        foreach (string name in new[] { "availableModelSelectionReferences", "providerRequirements", "requiredEgress" })
        {
            if (context[name] is not JsonArray array)
            {
                throw new InvalidDataException($"The run selection {name} must be an array.");
            }
            if (name == "availableModelSelectionReferences"
                ? array.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text))
                : array.Any(item => item is not JsonObject))
            {
                throw new InvalidDataException("The run selection context contains an invalid reference or requirement.");
            }
        }

        if (plan.ContainsKey("deniedActor"))
        {
            JsonObject denied = RequireObject(plan["deniedActor"], "The denied actor is invalid.");
            RequireExactKeys(denied, ["actorId", "tenantId"], "denied actor");
            string actor = RequireString(denied, "actorId", "denied actor ID");
            string tenant = RequireString(denied, "tenantId", "denied actor tenant ID");
            if (actor == GetString(environment, "actorId") && tenant == GetString(environment, "tenantId"))
            {
                throw new InvalidDataException("The denied actor must be different from the selected P1 actor.");
            }
        }

        if (journey.StartsWith("gate-", StringComparison.Ordinal))
        {
            JsonObject gate = RequireObject(plan["gate"], "The gate plan is required for a gate journey.");
            RequireExactKeys(gate, ["prompt", "allowedChoices", "allowsFreeform", "answer"], "gate plan");
            RequireString(gate, "prompt", "gate prompt");
            RequireString(gate, "answer", "gate answer");
            if (gate["allowedChoices"] is not JsonArray choices
                || choices.Any(item => item is not JsonValue choice || !choice.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text))
                || gate["allowsFreeform"] is not JsonValue freeformNode || !freeformNode.TryGetValue<bool>(out bool freeform))
            {
                throw new InvalidDataException("The gate plan must contain exact choices, freeform behavior, and an answer.");
            }
            string answer = GetString(gate, "answer")!;
            if (journey == "gate-answer" && !choices.Any(item => item!.GetValue<string>() == answer) && !freeform)
            {
                throw new InvalidDataException("The planned gate does not allow its exact answer.");
            }
        }
        else if (plan["gate"] is not null)
        {
            throw new InvalidDataException("A gate plan is only valid for a gate journey.");
        }

        if (journey == "message")
        {
            JsonObject message = RequireObject(plan["message"], "The message plan is required.");
            RequireExactKeys(message, ["text"], "message plan");
            RequireString(message, "text", "addressed-message text");
        }
        else if (plan["message"] is not null)
        {
            throw new InvalidDataException("A message plan is only valid for the message journey.");
        }
    }

    private static void RequireSha(string? value, string error)
    {
        if (!Regex.IsMatch(value ?? "", "^[a-f0-9]{40}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException(error);
        }
    }

    private static bool IsSha256(string? value) =>
        Regex.IsMatch(value ?? "", "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public static JsonObject RequireObject(JsonNode? node, string error) =>
        node is JsonObject value ? value : throw new InvalidDataException(error);

    public static string RequireString(JsonObject value, string name, string label)
    {
        string? text = GetString(value, name);
        return !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidDataException($"An explicit {label} is required.");
    }

    public static string? GetString(JsonObject value, string name) =>
        value[name] is JsonValue json && json.TryGetValue<string>(out string? text) ? text : null;

    public static bool TryLong(JsonNode? value, out long result)
    {
        if (value is JsonValue json && json.TryGetValue<long>(out result))
        {
            return true;
        }
        result = default;
        return false;
    }

    public static void RequireExactKeys(JsonObject value, IReadOnlyCollection<string> keys, string label)
    {
        if (value.Count != keys.Count || value.Any(pair => !keys.Contains(pair.Key, StringComparer.Ordinal)))
        {
            throw new InvalidDataException($"The {label} must contain exactly: {string.Join(", ", keys)}.");
        }
    }

    private static void RejectSecrets(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (Regex.IsMatch(key, "^(?:accessToken|authorization|connectionString|password|secret|setupToken|token)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    throw new InvalidDataException("Credentials must not be included in P1 runner input.");
                }
                RejectSecrets(value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                RejectSecrets(item);
            }
        }
    }

    public static JsonNode? Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            bool descriptorSensitive = obj.Any(pair =>
                DescriptorKey.IsMatch(pair.Key)
                && pair.Value is JsonValue descriptorValue
                && descriptorValue.TryGetValue<string>(out string? descriptor)
                && IsSensitiveDescriptor(descriptor));
            JsonObject result = [];
            foreach ((string key, JsonNode? value) in obj)
            {
                result[key] = ShouldRedactProperty(key, value)
                    || descriptorSensitive && DescriptorValueKey.IsMatch(key)
                    ? RedactSensitiveValue(value)
                    : Redact(value);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            if (array.Count == 2
                && array[0] is JsonValue descriptorValue
                && descriptorValue.TryGetValue<string>(out string? descriptor)
                && IsSensitiveDescriptor(descriptor))
            {
                return new JsonArray(array[0]?.DeepClone(), RedactSensitiveValue(array[1]));
            }
            JsonArray result = [];
            foreach (JsonNode? value in array)
            {
                result.Add(Redact(value));
            }
            return result;
        }
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            {
                try
                {
                    return Redact(JsonNode.Parse(text));
                }
                catch
                {
                }
            }
            return JsonValue.Create(RedactText(text));
        }
        return node?.DeepClone();
    }

    private static bool ShouldRedactProperty(string key, JsonNode? value) =>
        SensitiveName.IsMatch(key)
        && !(TokenCountName.IsMatch(key)
            && value is JsonValue count
            && count.TryGetValue<double>(out double number)
            && double.IsFinite(number)
            && number >= 0);

    private static bool IsSensitiveDescriptor(string value)
    {
        string descriptor = value.Trim();
        return SensitiveDescriptor.IsMatch(descriptor)
            || SensitiveKeyDescriptor.IsMatch(descriptor)
            || StandaloneSensitiveDescriptor.IsMatch(descriptor);
    }

    private static JsonNode? RedactSensitiveValue(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            JsonArray result = [];
            foreach (JsonNode? item in array)
                result.Add(RedactSensitiveValue(item));
            return result;
        }
        if (node is JsonObject obj)
        {
            JsonObject result = [];
            foreach ((string key, JsonNode? value) in obj)
                result[key] = RedactSensitiveValue(value);
            return result;
        }
        return JsonValue.Create("[REDACTED]");
    }

    public static string RedactText(string text) =>
        GitHubTokenPattern.Replace(
            SecretCanary.Replace(
                SecretAssignment.Replace(
                    JwtPattern.Replace(
                        BearerPattern.Replace(UrlPattern.Replace(text, SanitizeUrl), "******"),
                        "[REDACTED_JWT]"),
                    "$1[REDACTED]"),
                "[REDACTED_CANARY]"),
            "[REDACTED_GITHUB_TOKEN]");

    private static string SanitizeUrl(Match match)
    {
        if (Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri))
            return $"{uri.GetLeftPart(UriPartial.Authority)}{uri.AbsolutePath}";
        return "[REDACTED_URL]";
    }

    public static string CanonicalJson(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JavaScriptString(pair.Key) + ":" + CanonicalJson(pair.Value))) + "}";
        }
        if (node is JsonArray array)
        {
            return "[" + string.Join(",", array.Select(CanonicalJson)) + "]";
        }
        if (node is null)
            return "null";
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
            return JavaScriptString(text);
        return JavaScriptScalar(node);
    }

    public static string JavaScriptJson(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var properties = obj.Select((pair, ordinal) =>
            {
                uint? index = TryJavaScriptIndex(pair.Key, out uint parsed) ? parsed : null;
                return (pair, ordinal, index);
            }).OrderBy(item => item.index is null ? 1 : 0)
                .ThenBy(item => item.index)
                .ThenBy(item => item.ordinal);
            return "{" + string.Join(",", properties.Select(item =>
                JavaScriptString(item.pair.Key) + ":" + JavaScriptJson(item.pair.Value))) + "}";
        }
        if (node is JsonArray array)
            return "[" + string.Join(",", array.Select(JavaScriptJson)) + "]";
        if (node is null)
            return "null";
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
            return JavaScriptString(text);
        return JavaScriptScalar(node);
    }

    private static bool TryJavaScriptIndex(string key, out uint index)
    {
        return uint.TryParse(key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out index)
            && index != uint.MaxValue
            && index.ToString(System.Globalization.CultureInfo.InvariantCulture) == key;
    }

    private static string JavaScriptScalar(JsonNode node)
    {
        string scalar = node.ToJsonString(JsonOptions);
        if (scalar is "true" or "false" or "null")
            return scalar;
        return double.TryParse(scalar, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double number)
            ? JavaScriptNumber(number)
            : scalar;
    }

    private static string JavaScriptString(string value) =>
        JsonSerializer.Serialize(value, JsonOptions)
            .Replace("\\u2028", "\u2028", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u2029", "\u2029", StringComparison.OrdinalIgnoreCase);

    private static string JavaScriptNumber(double number)
    {
        if (!double.IsFinite(number) || number == 0)
            return number == 0 ? "0" : "null";
        string text = number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        int exponentIndex = text.IndexOfAny(['e', 'E']);
        if (exponentIndex < 0)
            return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;

        string mantissa = text[..exponentIndex];
        int exponent = int.Parse(text[(exponentIndex + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (exponent is >= -6 and < 21)
        {
            bool negative = mantissa.Length > 0 && mantissa[0] == '-';
            string unsigned = negative ? mantissa[1..] : mantissa;
            string[] parts = unsigned.Split('.', 2);
            string digits = string.Concat(parts);
            int point = parts[0].Length + exponent;
            string plain = point <= 0
                ? "0." + new string('0', -point) + digits
                : point >= digits.Length
                    ? digits + new string('0', point - digits.Length)
                    : digits[..point] + "." + digits[point..];
            return negative ? "-" + plain : plain;
        }

        string normalizedMantissa = mantissa.EndsWith(".0", StringComparison.Ordinal) ? mantissa[..^2] : mantissa;
        return normalizedMantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string EvidenceJson(JsonNode? node) => JavaScriptJson(node);

    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static string StableUuid(string invocationId, string purpose)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{invocationId}:{purpose}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        string value = Convert.ToHexStringLower(bytes);
        return $"{value[..8]}-{value[8..12]}-{value[12..16]}-{value[16..20]}-{value[20..]}";
    }

    public static JsonObject NewRecord(string kind, object value)
    {
        JsonObject record = JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Acceptance evidence record was not an object.");
        record["kind"] = kind;
        return record;
    }

    public static string ProjectName(string invocationId) => $"p1-{invocationId.ToLowerInvariant()}";

    public static string? ValueString(JsonNode? node) =>
        node is JsonValue json && json.TryGetValue<string>(out string? value) ? value : null;

    public static bool IsUuid(string? value) =>
        Guid.TryParseExact(value, "D", out _) && value?.Length == 36;

    public static JsonObject CloneObject(JsonObject source) => (JsonObject)source.DeepClone();

    public static JsonObject Object(params (string Name, JsonNode? Value)[] properties)
    {
        JsonObject obj = [];
        foreach ((string name, JsonNode? value) in properties)
        {
            obj[name] = value?.DeepClone();
        }
        return obj;
    }

    public static JsonObject FromObject(object value) =>
        JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject
        ?? throw new InvalidOperationException("Expected a JSON object.");

    public static void AddStep(List<JsonObject> steps, string id, string status, params (string Name, JsonNode? Value)[] details)
    {
        JsonObject step = new()
        {
            ["id"] = id,
            ["status"] = status,
        };
        foreach ((string name, JsonNode? value) in details)
        {
            step[name] = value?.DeepClone();
        }
        steps.Add(step);
    }

    public static JsonArray ToArray(IEnumerable<JsonObject> objects)
    {
        JsonArray array = [];
        foreach (JsonObject value in objects)
        {
            array.Add(value.DeepClone());
        }
        return array;
    }

    public static JsonObject Result(
        string surfaceStatus,
        JsonObject? sourceEvidence,
        IEnumerable<JsonObject> steps,
        JsonObject observations,
        string? failure,
        IReadOnlyList<JsonObject> suppliedNativeProbes,
        AcceptanceManifest? manifest = null)
    {
        JsonArray probes = [];
        probes.Add(new JsonObject
        {
            ["id"] = "service-deployment-source-binding",
            ["required"] = true,
            ["status"] = sourceEvidence?["services"]?["status"]?.GetValue<string>() == "passed" ? "passed" : "blocked",
            ["reason"] = sourceEvidence?["services"]?["status"]?.GetValue<string>() == "passed"
                ? null
                : "Actual service Deployment/Pod identities and registry source labels were not verified.",
        });
        probes.Add(new JsonObject
        {
            ["id"] = "agenthost-image-measurement",
            ["required"] = true,
            ["status"] = sourceEvidence?["agentHostImageMeasurement"]?["status"]?.GetValue<string>() == "passed" ? "passed" : "blocked",
            ["reason"] = sourceEvidence?["agentHostImageMeasurement"]?["status"]?.GetValue<string>() == "passed"
                ? null
                : "The source-bound AgentHost OCI image was not measured with the existing image tool.",
        });

        Dictionary<string, JsonObject> native = [];
        foreach (JsonObject probe in suppliedNativeProbes)
        {
            native[GetString(probe, "id") ?? ""] = CloneObject(probe);
        }
        foreach (string name in NativeProbeNames)
        {
            if (native.TryGetValue(name, out JsonObject? supplied))
            {
                probes.Add(supplied);
            }
            else
            {
                probes.Add(new JsonObject
                {
                    ["id"] = name,
                    ["required"] = true,
                    ["status"] = "blocked",
                    ["reason"] = NativeProbeReasons[name],
                });
            }
        }

        bool failedProbe = probes.Any(item => GetString((JsonObject)item!, "status") == "failed");
        bool blockedProbe = probes.Any(item => GetString((JsonObject)item!, "status") != "passed");
        // Blocked means required evidence is absent; failed means the journey or an observed probe violated its contract.
        bool failed = surfaceStatus == "failed" || failedProbe;
        string fullStatus = failed ? "failed" : blockedProbe || surfaceStatus == "blocked" ? "blocked" : "passed";
        JsonObject result = new()
        {
            ["kind"] = "p1-acceptance-result",
            ["schemaVersion"] = 1,
            ["surface"] = "api",
            ["surfaceStatus"] = surfaceStatus,
            ["fullP1Status"] = fullStatus,
            ["exitCode"] = fullStatus switch { "passed" => 0, "failed" => 1, _ => 2 },
            ["sourceEvidence"] = sourceEvidence is null ? null : Redact(sourceEvidence),
            ["steps"] = ToArray(steps),
            ["observations"] = Redact(observations),
            ["probes"] = probes,
        };
        if (!string.IsNullOrWhiteSpace(failure))
        {
            result["failure"] = new JsonObject { ["message"] = RedactText(failure) };
        }

        AddSessionMaterialBlocker(result, manifest);
        return result;
    }

    private static void AddSessionMaterialBlocker(JsonObject result, AcceptanceManifest? manifest)
    {
        JsonObject observations = (JsonObject)result["observations"]!;
        string[] blockers =
        [
            "The authorized P1 client exposes Gateway operations, while session-material routes are internal Events owner routes requiring runtime authorization that this identity provider does not issue.",
            "The runner-owned fixture permits only its selected P1 surface journey; it has no approved session-material fixture creation or bounded material cleanup boundary.",
            "The authorized P1 flow has no native Host replacement boundary or receipt for compatible cache restore, journal rebuild, or proving that recovery did not invoke a model turn or repeat external effects.",
            "The authorized P1 flow has no supported fault hook for current-authority revocation, stale fence or registration, duplicate or changed retry, or missing required turn material.",
        ];
        JsonObject? sourceEvidence = result["sourceEvidence"] as JsonObject;
        JsonObject environment = manifest?.Environment ?? [];
        JsonObject fixturePlan = manifest?.FixturePlan ?? [];
        JsonObject? identity = new()
        {
            ["actorId"] = GetString(environment, "actorId"),
            ["tenantId"] = GetString(environment, "tenantId"),
            ["issuer"] = GetString(environment, "issuer"),
            ["audience"] = GetString(environment, "audience"),
            ["projectId"] = observations["fixtureSetup"]?["setup"]?["projectId"]?.DeepClone(),
            ["runId"] = observations["fixtureSetup"]?["selection"]?["runId"]?.DeepClone(),
            ["rootSessionId"] = observations["fixtureSetup"]?["rootSessionId"]?.DeepClone(),
            ["sessionId"] = observations["fixtureSetup"]?["sessionId"]?.DeepClone(),
            ["executionFence"] = observations["fixtureSetup"]?["executionFence"]?.DeepClone(),
            ["fixtureId"] = null,
            ["invocationId"] = observations["fixtureSetup"]?["invocationId"]?.DeepClone(),
            ["allowedJourneys"] = new JsonArray("journal"),
            ["platformRuntimeRevision"] = fixturePlan["expectedPlatformRuntimeRevision"]?.DeepClone(),
            ["selectionContextRevision"] = fixturePlan["selectionContext"]?["revision"]?.DeepClone(),
            ["runtimeInstanceId"] = null,
            ["registrationRevision"] = null,
        };
        JsonArray ownerChecks = [];
        foreach (string name in new[] { "current-authority-revocation", "stale-fence-or-registration", "duplicate-and-changed-retry", "missing-required-turn-material" })
        {
            ownerChecks.Add(new JsonObject { ["name"] = name, ["status"] = "blocked", ["reason"] = blockers[3] });
        }
        JsonArray blockersArray = [];
        foreach (string blocker in blockers) blockersArray.Add(blocker);
        observations["sessionMaterialAcceptance"] = new JsonObject
        {
            ["kind"] = "p1-session-material-acceptance",
            ["schemaVersion"] = 1,
            ["status"] = "blocked",
            ["evidenceClass"] = "deployed-runtime",
            ["sourceEvidence"] = sourceEvidence?.DeepClone(),
            ["identity"] = identity,
            ["turn"] = new JsonObject
            {
                ["status"] = "blocked",
                ["request"] = new JsonObject { ["status"] = "not-sent", ["method"] = "POST", ["route"] = "/internal/sessions/{sessionId}/material", ["materialKind"] = "TurnContent", ["redactedBody"] = null },
                ["result"] = new JsonObject { ["status"] = "not-observed", ["redactedBody"] = null },
                ["acknowledgment"] = new JsonObject { ["status"] = "not-observed", ["redactedReceipt"] = null },
                ["event"] = new JsonObject { ["eventId"] = null, ["committedPosition"] = null, ["cursor"] = null },
            },
            ["ownerBoundaryChecks"] = ownerChecks,
            ["recovery"] = new JsonObject
            {
                ["status"] = "blocked",
                ["compatibleReplacementRuntime"] = new JsonObject { ["status"] = "not-attempted", ["registrationRevision"] = null },
                ["optionalSdkCache"] = new JsonObject
                {
                    ["absent"] = new JsonObject { ["status"] = "not-probed", ["expectedDisposition"] = "rebuild-from-retained-turns" },
                    ["expired"] = new JsonObject { ["status"] = "not-probed", ["expectedDisposition"] = "rebuild-from-retained-turns" },
                },
                ["requiredTurnMaterial"] = new JsonObject
                {
                    ["missing"] = new JsonObject { ["status"] = "not-probed", ["expectedDisposition"] = "turn-failure" },
                    ["expired"] = new JsonObject { ["status"] = "not-probed", ["expectedDisposition"] = "turn-failure" },
                },
                ["modelTurnDuringRebuild"] = new JsonObject { ["status"] = "not-observed", ["count"] = null },
                ["repeatedExternalEffects"] = new JsonObject { ["status"] = "not-observed" },
                ["reason"] = blockers[2],
            },
            ["controlledEvidence"] = new JsonObject
            {
                ["status"] = "not-executed",
                ["evidenceClass"] = "controlled-source",
                ["tests"] = ControlledSessionMaterialTests(),
            },
            ["cleanup"] = new JsonObject
            {
                ["status"] = "not-started",
                ["target"] = null,
                ["reason"] = "No session-material fixture was created; the existing P1 fixture cleanup remains separately recorded.",
            },
            ["blockers"] = blockersArray,
        };
        if (GetString(result, "fullP1Status") != "failed")
        {
            result["fullP1Status"] = "blocked";
            result["exitCode"] = 2;
        }
    }

    private static JsonArray ControlledSessionMaterialTests() =>
    [
        new JsonObject
        {
            ["source"] = "tests/Agentweaver.Identity.Broker.Tests/RuntimeRegistrationOwnerIntegrationTests.cs",
            ["test"] = "ProjectsConfigBrokerAuthorizationTests.AzureByokNativeAccountingLostReplyReplaysWithFreshTotalsAndRejectsStaleProof",
            ["covers"] = new JsonArray("typed-turn-acknowledgment", "material-readback", "exact-retry", "stale-fence", "authority-revocation"),
        },
        new JsonObject
        {
            ["source"] = "tests/Agentweaver.Identity.Broker.Tests/RuntimeSessionRecoveryTests.cs",
            ["test"] = "RuntimeSessionRecoveryTests.ActualSdkFilesystemCallbacksCaptureOpaqueFilesAndResumeTheSameLogicalSession",
            ["covers"] = new JsonArray("compatible-replacement-cache-restore"),
        },
        new JsonObject
        {
            ["source"] = "tests/Agentweaver.Identity.Broker.Tests/RuntimeSessionRecoveryTests.cs",
            ["test"] = "RuntimeSessionRecoveryTests.LostOrIncompatibleCacheRebuildsNativeContextWithoutReplayingATurn",
            ["covers"] = new JsonArray("optional-cache-rebuild", "no-model-turn-replay"),
        },
        new JsonObject
        {
            ["source"] = "tests/Agentweaver.EventsAndSessions.Tests/SessionMaterialPostgresTests.cs",
            ["test"] = "SessionMaterialPostgresTests.ExactMaterialRetriesAndRestartReturnOriginalRecordedAcknowledgment",
            ["covers"] = new JsonArray("exact-duplicate-retry", "changed-payload-rejection", "restart-acknowledgment"),
        },
        new JsonObject
        {
            ["source"] = "tests/Agentweaver.EventsAndSessions.Tests/SessionMaterialPostgresTests.cs",
            ["test"] = "SessionMaterialPostgresTests.ExpiredRequiredTurnsFailAndOnlyExpiredOptionalCacheMapsToNotFound",
            ["covers"] = new JsonArray("required-turn-expiry", "optional-cache-expiry"),
        },
        new JsonObject
        {
            ["source"] = "tests/Agentweaver.EventsAndSessions.Tests/SessionMaterialFailureRecoveryTests.cs",
            ["test"] = "SessionMaterialEndpointRetentionTests.OnlyOptionalCacheAbsenceAndExpiryBecomeNotFound",
            ["covers"] = new JsonArray("optional-cache-absence", "required-material-failure"),
        },
    ];

    public static JsonObject SummarizeFixture(AcceptanceFixtureState state, string? rootSessionId = null, string? sessionId = null, long? executionFence = null)
    {
        JsonObject setup = new()
        {
            ["invocationId"] = state.InvocationId,
            ["projectName"] = state.ProjectName,
            ["setup"] = state.Setup?.DeepClone(),
            ["requestFailures"] = ToArray(state.RequestFailures),
            ["diagnosis"] = state.SetupIndeterminate || GetString(state.Cleanup ?? [], "state") == "indeterminate"
                ? new JsonObject
                {
                    ["state"] = "required",
                    ["projectName"] = state.ProjectName,
                    ["reason"] = "Find the project with this name through listProjects and review it and the setup actor's effective authority through getAuthorizationContext. Reconcile this invocation before taking further action.",
                }
                : null,
            ["selection"] = state.Selection?.DeepClone(),
            ["nativeFence"] = new JsonObject { ["status"] = "not-started", ["reason"] = "This setup path never invokes an Orchestrator dispatch operation." },
            ["cleanup"] = state.Cleanup?.DeepClone() ?? new JsonObject { ["state"] = "not-started" },
        };
        if (rootSessionId is not null) setup["rootSessionId"] = rootSessionId;
        if (sessionId is not null) setup["sessionId"] = sessionId;
        if (executionFence is not null) setup["executionFence"] = executionFence.Value;
        return (JsonObject)Redact(setup)!;
    }

    public static JsonObject CreateNativeProbeEnvelope(JsonObject envelope)
    {
        string canonical = CanonicalJson(envelope);
        string hash = Sha256Hex(Encoding.UTF8.GetBytes(canonical));
        JsonObject result = CloneObject(envelope);
        result["receiptSha256"] = hash;
        return result;
    }

    public static List<JsonObject> ValidateNativeEnvelopes(JsonObject? native, JsonObject? recovery)
    {
        List<JsonObject> envelopes = [];
        if (native is not null) envelopes.Add(ValidateNativeEnvelope(native, "p1-native-acceptance"));
        if (recovery is not null) envelopes.Add(ValidateNativeEnvelope(recovery, "p1-dispatch-recovery-acceptance"));
        if (envelopes.Count > 2)
        {
            throw new InvalidDataException("Too many P1 native probe envelopes were provided.");
        }

        List<JsonObject> rows = [];
        foreach (JsonObject envelope in envelopes)
        {
            JsonArray probes = (JsonArray)envelope["probes"]!;
            foreach (JsonObject probe in probes.Cast<JsonObject>())
            {
                string name = GetString(probe, "name")!;
                rows.Add(new JsonObject
                {
                    ["id"] = name,
                    ["required"] = true,
                    ["status"] = GetString(probe, "status"),
                    ["startedAt"] = GetString(envelope, "startedAt"),
                    ["completedAt"] = GetString(envelope, "completedAt"),
                    ["reason"] = GetString(probe, "reason"),
                    ["code"] = GetString(probe, "code"),
                    ["evidence"] = Redact(probe["evidence"]),
                    ["cleanup"] = Redact(probe["cleanup"]),
                });
            }
        }
        return rows;
    }

    private static JsonObject ValidateNativeEnvelope(JsonObject envelope, string expectedKind)
    {
        if (GetString(envelope, "kind") != expectedKind || TryLong(envelope["schemaVersion"], out long version) && version != 1
            || !TryLong(envelope["schemaVersion"], out version) || envelope["probes"] is not JsonArray probes
            || !new[] { "passed", "failed", "blocked" }.Contains(GetString(envelope, "overallStatus"), StringComparer.Ordinal)
            || !DateTimeOffset.TryParse(GetString(envelope, "startedAt"), out DateTimeOffset started)
            || !DateTimeOffset.TryParse(GetString(envelope, "completedAt"), out DateTimeOffset completed)
            || completed < started)
        {
            throw new InvalidDataException($"The {expectedKind} envelope is invalid or duplicated.");
        }

        string[] expectedNames = expectedKind == "p1-native-acceptance"
            ? NativeProbeNames[..4]
            : NativeProbeNames[4..];
        JsonObject unsigned = CloneObject(envelope);
        string? suppliedHash = GetString(unsigned, "receiptSha256");
        unsigned.Remove("receiptSha256");
        if (!IsSha256(suppliedHash)
            || !string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson(unsigned))), suppliedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A native P1 probe envelope checksum does not match its contents.");
        }
        if (probes.Count != expectedNames.Length)
        {
            throw new InvalidDataException($"The {expectedKind} envelope is missing a required P1 probe.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonNode? node in probes)
        {
            JsonObject probe = RequireObject(node, "A native P1 probe result is invalid.");
            string name = RequireString(probe, "name", "native P1 probe name");
            if (!expectedNames.Contains(name, StringComparer.Ordinal) || !names.Add(name)
                || !new[] { "passed", "failed", "blocked" }.Contains(GetString(probe, "status"), StringComparer.Ordinal)
                || probe["evidence"] is not JsonObject evidence || probe["cleanup"] is not JsonObject cleanup)
            {
                throw new InvalidDataException("A native P1 probe result is invalid or duplicated.");
            }

            if (GetString(probe, "status") == "passed")
            {
                bool cleanupPassed = expectedKind == "p1-native-acceptance"
                    ? (GetString(cleanup, "status") is "complete" or "not-required")
                        && cleanup["actions"] is JsonArray actions
                        && (GetString(cleanup, "status") != "not-required" || actions.Count == 0)
                        && actions.All(action => GetString(action as JsonObject ?? [], "status") == "complete")
                    : GetString(cleanup, "status") == "not-started"
                        && cleanup["createdResourceIds"] is JsonArray ids && ids.Count == 0;
                if (evidence.Count == 0 || !cleanupPassed)
                {
                    throw new InvalidDataException("A passed native P1 probe lacks evidence or confirmed cleanup.");
                }
            }
        }

        string computed = probes.Any(node => GetString((JsonObject)node!, "status") == "failed")
            ? "failed"
            : probes.Any(node => GetString((JsonObject)node!, "status") == "blocked") ? "blocked" : "passed";
        if (GetString(envelope, "overallStatus") != computed)
        {
            throw new InvalidDataException($"The {expectedKind} overall status does not match its probe results.");
        }
        return CloneObject(envelope);
    }

    public static string ExecutionPlanHash(JsonObject validated, IReadOnlyList<JsonObject> probes, IReadOnlyDictionary<string, string> proofHashes)
    {
        JsonObject input = CloneObject(validated);
        input.Remove("executionIntent");
        JsonObject evidence = (JsonObject)input["deploymentEvidence"]!;
        // Resolve local paths only to hash proof bytes; replace them before canonical hashing so checkout paths are not identity.
        foreach ((string name, string hash) in proofHashes)
        {
            evidence[name] = hash;
        }
        JsonObject plan = new()
        {
            ["validated"] = input,
            ["nativeProbeResults"] = ToArray(probes),
        };
        return Sha256Hex(Encoding.UTF8.GetBytes(CanonicalJson(plan)));
    }

    public static async Task AppendEvidenceAsync(string path, JsonNode record, CancellationToken ct)
    {
        string line = EvidenceJson(Redact(record)) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        await using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static JsonObject SetupInvocation(string invocationId, string planHash) => new()
    {
        ["kind"] = "p1-setup-invocation",
        ["schemaVersion"] = 2,
        ["invocationId"] = invocationId,
        ["executionPlanSha256"] = planHash,
        ["surface"] = "api",
    };

    public static IReadOnlyList<JsonObject> NativeProbeRows(JsonObject? native, JsonObject? recovery) =>
        ValidateNativeEnvelopes(native, recovery);

    public static string PathIdentity(string path) =>
        OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);

    [GeneratedRegex(@"authorization|cookie|token|credential|api[ _-]?key|execution[ _-]?key|provider[ _-]?key|secret|password|kubeconfig|storagestate|signedurl", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtTokenPattern();

    [GeneratedRegex(@"\bgh(?:p|o|u|s|r)_[A-Za-z0-9_]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenValuePattern();
}
