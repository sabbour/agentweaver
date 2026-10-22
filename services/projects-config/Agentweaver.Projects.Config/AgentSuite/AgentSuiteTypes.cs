using System.Globalization;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// A read-only view of a decoded JSON value with Go zero-value defaults, used after a document passed
/// strict decoding into its Go type (so every present value already has the declared JSON kind).
/// </summary>
internal readonly struct J(JsonNode? node)
{
    public JsonNode? Node => node;

    /// <summary>True when the member exists, including an explicit null.</summary>
    public bool Present => node is not null;

    public bool IsNull => node is null || node.IsNull;

    public J this[string key] => new(node is { Kind: JsonKind.Object } ? node.Get(key) : null);

    // The accessors below return Go zero values (empty string, 0, false, empty list) when the value is
    // missing or of another kind, the same as reading an unset field of a decoded Go struct.
    public string S => node is { Kind: JsonKind.String } ? node.Text! : string.Empty;

    public long I => node is { Kind: JsonKind.Number } && long.TryParse(node.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public bool B => node is { Kind: JsonKind.True };

    public IReadOnlyList<J> A => node is { Kind: JsonKind.Array } ? node.Items!.Select(item => new J(item)).ToList() : [];

    public int Count => node is { Kind: JsonKind.Array } ? node.Items!.Count : 0;

    public IEnumerable<KeyValuePair<string, J>> Members => node is { Kind: JsonKind.Object }
        ? node.Members!.Select(member => new KeyValuePair<string, J>(member.Key, new J(member.Value)))
        : [];

    public bool Has(string key) => this[key].Present;

    /// <summary>Upstream <c>Platform.String()</c>.</summary>
    public string Platform
    {
        get
        {
            // Format is "os/architecture", plus "/variant" only when a variant is set (for example "linux/arm64/v8").
            var variant = this["variant"].S;
            var value = this["os"].S + "/" + this["architecture"].S;
            return variant.Length == 0 ? value : value + "/" + variant;
        }
    }
}

/// <summary>The Go types of the upstream package (types.go, oci.go, execution.go, inference.go, runtime_profile.go).</summary>
internal static class AgentSuiteTypes
{
    public const string SpecVersion = "1.0.0-draft";
    public const string LayoutVersion = "1.0.0";
    public const string MediaTypeArtifact = "application/vnd.agentsuite.suite.v1";
    public const string MediaTypeEmptyConfig = "application/vnd.oci.empty.v1+json";
    public const string MediaTypeContent = "application/vnd.agentsuite.content.v1.tar+gzip";
    public const string MediaTypeSuite = "application/vnd.agentsuite.manifest.v1+json";
    public const string MediaTypeAgent = "application/vnd.agentsuite.agent.v1+json";
    public const string MediaTypePolicy = "application/vnd.agentsuite.policy.v1+json";
    public const string MediaTypePolicyV2 = "application/vnd.agentsuite.policy.v2+json";
    public const string MediaTypeEvaluationIndex = "application/vnd.agentsuite.eval.index.v1+json";
    public const string MediaTypeEvaluationCase = "application/vnd.agentsuite.eval.case.v1+json";
    public const string MediaTypeToolProviderCatalog = "application/vnd.agentsuite.tool.provider.catalog.v1+json";
    public const string MediaTypeToolProvider = "application/vnd.agentsuite.tool.provider.v1+json";
    public const string MediaTypeToolProviderComposition = "application/vnd.agentsuite.tool.provider.composition.v1+json";
    public const string MediaTypeComposition = "application/vnd.agentsuite.composition.v1+json";
    public const string MediaTypeBuildProfile = "application/vnd.agentsuite.build.profile.v1+json";
    public const string MediaTypeRuntimeProfile = "application/vnd.agentsuite.runtime.profile.v1+json";
    public const string MediaTypeInstructions = "text/markdown; charset=utf-8";
    public const string MediaTypeJsonSchema = "application/schema+json";
    public const string OciManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    public const string OciIndexMediaType = "application/vnd.oci.image.index.v1+json";
    public const string ExecutionSharedSandbox = "shared-sandbox";

    // Short names for the Go primitive types, so the struct definitions below read like the Go source.
    private static readonly GoPrim Str = new(GoPrimKind.String);
    private static readonly GoPrim Bool = new(GoPrimKind.Bool);
    private static readonly GoPrim Int = new(GoPrimKind.Int);
    private static readonly GoPrim Int64 = new(GoPrimKind.Int64);
    private static readonly GoPrim Uint32 = new(GoPrimKind.Uint32);
    private static readonly GoRawMessage Raw = new();
    private static readonly GoInterface Any = new();

    private static GoSlice SliceOf(GoType type) => new(type);
    private static GoPointer Ptr(GoType type) => new(type);

    // Each field is declared with its JSON name and Go type, in the same order as the upstream struct.
    public static readonly GoStruct Descriptor = new GoStruct("Descriptor")
        .With("mediaType", Str).With("digest", Str).With("size", Int64).With("annotations", new GoMap(Str));

    // A copy of Descriptor without its custom hook, used to finish decoding after the hook's own checks
    // (like a Go type alias that avoids calling UnmarshalJSON again).
    private static readonly GoStruct DescriptorAlias = Descriptor.Wire("descriptorAlias");

    public static readonly GoStruct Platform = new GoStruct("Platform")
        .With("os", Str).With("architecture", Str).With("variant", Str);

    public static readonly GoStruct Extension = new GoStruct("Extension")
        .With("name", Str).With("critical", Bool).With("digest", Str);

    public static readonly GoStruct ManifestRef = new GoStruct("ManifestRef")
        .With("id", Str).With("version", Str).With("path", Str).With("descriptor", Descriptor);

    public static readonly GoStruct CompositionRef = new GoStruct("CompositionRef")
        .With("runtimeProfile", Str).With("agent", Str).With("platform", Platform).With("path", Str).With("descriptor", Descriptor);

    public static readonly GoStruct ToolProviderCompositionRef = new GoStruct("ToolProviderCompositionRef")
        .With("id", Str).With("version", Str).With("platform", Platform).With("path", Str).With("descriptor", Descriptor);

    public static readonly GoStruct Suite = new GoStruct("Suite")
        .With("schemaVersion", Str).With("mediaType", Str).With("name", Str)
        .With("agents", SliceOf(ManifestRef)).With("toolProviderCatalog", ManifestRef)
        .With("toolProviderCompositions", SliceOf(ToolProviderCompositionRef))
        .With("compositions", SliceOf(CompositionRef)).With("buildProfiles", SliceOf(ManifestRef))
        .With("runtimeProfiles", SliceOf(ManifestRef)).With("capabilities", SliceOf(Str))
        .With("extensions", SliceOf(Extension));

    public static readonly GoStruct FileRef = new GoStruct("FileRef").With("path", Str).With("descriptor", Descriptor);

    public static readonly GoStruct InferenceRequirements = new GoStruct("InferenceRequirements")
        .With("api", Str).With("contextTokens", Int).With("outputTokens", Int).With("streaming", Bool).With("toolCalling", Bool);

    public static readonly GoStruct ModelRequirement = new GoStruct("ModelRequirement")
        .With("protocol", Str).With("model", Str).With("capabilities", Ptr(InferenceRequirements))
        .With("endpointEnv", Str).With("secretRefs", SliceOf(Str));

    public static readonly GoStruct ToolProviderRequirement = new GoStruct("ToolProviderRequirement")
        .With("id", Str).With("version", Str).With("executionMode", Str);

    public static readonly GoStruct AgentInvoke = new GoStruct("AgentInvoke")
        .With("agent", Str).With("maxConcurrent", Int).With("maxDepth", Int);

    public static readonly GoStruct Agent = new GoStruct("Agent")
        .With("schemaVersion", Str).With("mediaType", Str).With("id", Str).With("description", Str)
        .With("instructions", FileRef).With("model", ModelRequirement)
        .With("toolProviders", SliceOf(ToolProviderRequirement)).With("invokes", SliceOf(AgentInvoke))
        .With("evals", SliceOf(FileRef)).With("policy", Ptr(FileRef)).With("extensions", SliceOf(Extension));

    public static readonly GoStruct EvaluationIndex = new GoStruct("EvaluationIndex")
        .With("schemaVersion", Str).With("mediaType", Str).With("agent", Str).With("cases", SliceOf(FileRef))
        .With("limits", new GoStruct("EvaluationLimits").With("maxCases", Int).With("maxCaseBytes", Int));

    public static readonly GoStruct EvaluationAssertion = new GoStruct("EvaluationAssertion")
        .With("id", Str).With("type", Str).With("critical", Ptr(Bool)).With("value", Str).With("pattern", Str)
        .With("tool", Str).With("rubric", Str).With("minScore", Ptr(Int)).With("judgeModel", Str).With("judgeVersion", Str);

    public static readonly GoStruct EvaluationCase = new GoStruct("EvaluationCase")
        .With("schemaVersion", Str).With("mediaType", Str).With("id", Str).With("input", Str)
        .With("runs", Ptr(Int)).With("minPasses", Ptr(Int)).With("assertions", SliceOf(EvaluationAssertion));

    public static readonly GoStruct ToolProviderCatalog = new GoStruct("ToolProviderCatalog")
        .With("schemaVersion", Str).With("mediaType", Str).With("toolProviders", SliceOf(ManifestRef));

    public static readonly GoStruct Tool = new GoStruct("Tool")
        .With("name", Str).With("description", Str).With("inputSchema", FileRef).With("outputSchema", Ptr(FileRef))
        .With("effects", SliceOf(Str)).With("effect", Str).With("approval", Str)
        .With("delegatedAuthorization", Ptr(new GoStruct("DelegatedAuthorization")
            .With("service", Str).With("scope", Str)
            .With("credential", new GoStruct("BrokeredCredential").With("resolution", Str).With("bindingRef", Str))));

    public static readonly GoStruct RemoteToolProvider = new GoStruct("RemoteToolProvider")
        .With("transport", Str).With("endpointRef", Str)
        .With("headers", SliceOf(new GoStruct("RemoteHeader").With("name", Str)
            .With("secretRef", new GoStruct("SecretKeyRef").With("name", Str).With("key", Str))))
        .With("network", SliceOf(new GoStruct("RemoteNetworkRequirement").With("destinationRef", Str)))
        .With("timeouts", new GoStruct("RemoteTimeouts").With("connectMilliseconds", Int).With("requestMilliseconds", Int))
        .With("cancellation", Str).With("connection", Str);

    public static readonly GoStruct InventoryEntry = new GoStruct("InventoryEntry")
        .With("path", Str).With("type", Str).With("mode", Uint32).With("uid", Int).With("gid", Int)
        .With("size", Int64).With("digest", Str).With("linkTarget", Str).With("component", Str);

    public static readonly GoStruct ToolProviderVariant = new GoStruct("ToolProviderVariant")
        .With("platform", Platform).With("variantDigest", Str).With("installRoot", Str).With("relocatable", Bool)
        .With("payloadRoot", Str).With("entrypoint", Str).With("arguments", SliceOf(Str)).With("searchPath", SliceOf(Str))
        .With("environment", SliceOf(new GoStruct("EnvironmentName")
            .With("name", Str).With("required", Bool).With("secret", Bool).With("delivery", Str)))
        .With("writablePaths", SliceOf(Str))
        .With("network", SliceOf(new GoStruct("NetworkAccess").With("scheme", Str).With("host", Str).With("port", Int)))
        .With("runtime", new GoStruct("RuntimeRequirement")
            .With("abi", Str).With("abiVersion", Str).With("cpuBaseline", Str).With("requiredBasePaths", SliceOf(Str)))
        .With("files", SliceOf(InventoryEntry))
        .With("dependencies", SliceOf(new GoStruct("ToolProviderDependency")
            .With("id", Str).With("version", Str).With("variantDigest", Str)))
        .With("sbom", Ptr(Descriptor)).With("provenance", Ptr(Descriptor));

    public static readonly GoStruct ToolProvider = new GoStruct("ToolProvider")
        .With("schemaVersion", Str).With("mediaType", Str).With("id", Str).With("version", Str)
        .With("retained", Ptr(Bool)).With("protocol", Str).With("revision", Str).With("tools", SliceOf(Tool))
        .With("variants", SliceOf(ToolProviderVariant)).With("remote", Ptr(RemoteToolProvider))
        .With("extensions", SliceOf(Extension));

    public static readonly GoStruct PlatformImage = new GoStruct("PlatformImage")
        .With("platform", Platform).With("imageRef", Str).With("image", Descriptor);

    public static readonly GoStruct Composition = new GoStruct("Composition")
        .With("runtimeProfile", Str).With("schemaVersion", Str).With("mediaType", Str).With("agent", Str)
        .With("platform", Platform).With("buildProfile", Str)
        .With("toolProviders", SliceOf(new GoStruct("ResolvedToolProvider")
            .With("id", Str).With("version", Str).With("manifestDigest", Str).With("variantDigest", Str).With("executionMode", Str)));

    public static readonly GoStruct ToolProviderComposition = new GoStruct("ToolProviderComposition")
        .With("schemaVersion", Str).With("mediaType", Str).With("id", Str).With("version", Str)
        .With("manifestDigest", Str).With("platform", Platform).With("variantDigest", Str).With("buildProfile", Str);

    public static readonly GoStruct ExecutionInput = new GoStruct("ExecutionInput")
        .With("name", Str).With("environment", Str).With("secret", Bool);

    public static readonly GoStruct ExecutionContract = new GoStruct("ExecutionContract")
        .With("configuration", Str).With("kind", Str).With("protocol", Str).With("port", Int)
        .With("healthPath", Str).With("inputs", SliceOf(ExecutionInput));

    public static readonly GoStruct BuildProfile = new GoStruct("BuildProfile")
        .With("execution", Ptr(ExecutionContract)).With("schemaVersion", Str).With("mediaType", Str).With("id", Str)
        .With("runtimeBase", SliceOf(PlatformImage)).With("harness", SliceOf(PlatformImage)).With("sourceEpoch", Int64);

    private static readonly GoStruct EvidenceImplementation = new GoStruct("EvidenceImplementation")
        .With("id", Str).With("version", Str).With("digest", Str);

    public static readonly GoStruct RuntimeProfile = new GoStruct("RuntimeProfile")
        .With("schemaVersion", Str).With("mediaType", Str).With("id", Str)
        .With("runtime", EvidenceImplementation).With("harness", EvidenceImplementation).With("renderer", EvidenceImplementation)
        .With("configurationABI", Str).With("imageRequired", Bool);

    public static readonly GoStruct OciLayout = new GoStruct("ociLayout").With("imageLayoutVersion", Str);

    public static readonly GoStruct OciDescriptor = new GoStruct("ociDescriptor")
        .With("mediaType", Str).With("digest", Str).With("size", Int64).With("data", Raw).With("urls", Raw)
        .With("artifactType", Str).With("platform", Ptr(Platform)).With("annotations", new GoMap(Str));

    public static readonly GoStruct OciIndex = new GoStruct("ociIndex")
        .With("schemaVersion", Int).With("mediaType", Str).With("manifests", SliceOf(OciDescriptor))
        .With("annotations", new GoMap(Str));

    public static readonly GoStruct OciManifest = new GoStruct("ociManifest")
        .With("schemaVersion", Int).With("mediaType", Str).With("artifactType", Str).With("config", OciDescriptor)
        .With("layers", SliceOf(OciDescriptor)).With("subject", Ptr(OciDescriptor)).With("annotations", new GoMap(Str));

    /// <summary><c>map[string]json.RawMessage</c>.</summary>
    public static readonly GoMap FieldMap = new(Raw);

    /// <summary><c>any</c>.</summary>
    public static readonly GoInterface AnyValue = Any;

    static AgentSuiteTypes()
    {
        // Hooks act like Go UnmarshalJSON methods: they run extra checks when the decoder reaches that type.
        // They are set here because some hooks refer to struct definitions declared above.
        Descriptor.Hook = DescriptorHook;
        ModelRequirement.Hook = node => WireHook(node, ModelRequirement, ModelRequirementChecks);
        InferenceRequirements.Hook = node => RequiredContract(node, InferenceRequirements, "api", "contextTokens", "outputTokens", "streaming", "toolCalling");
        ExecutionInput.Hook = node => RequiredContract(node, ExecutionInput, "name", "environment", "secret");
        // The execution contract needs its required fields, and an optional "configuration" that is not empty.
        ExecutionContract.Hook = node => RequiredContract(node, ExecutionContract, "kind", "protocol", "port", "healthPath", "inputs")
            ?? (node.Has("configuration") && new J(node)["configuration"].S.Length == 0
                ? GoDecodeError.Of("execution configuration must not be empty when present")
                : null);
        ToolProvider.Hook = node => WireHook(node, ToolProvider, ToolProviderChecks);
        // "execution" may be omitted, but an explicit null is rejected.
        BuildProfile.Hook = node => WireHook(node, BuildProfile, fields =>
            fields.Get("execution") is { IsNull: true } ? "build profile execution must not be null" : null);
        RuntimeProfile.Hook = node => WireHook(node, RuntimeProfile, RuntimeProfileChecks);
        Suite.Hook = node => WireHook(node, Suite, SuiteChecks);
        Composition.Hook = node => WireHook(node, Composition, RuntimeSelector);
        CompositionRef.Hook = node => WireHook(node, CompositionRef, RuntimeSelector);
    }

    /// <summary>Upstream <c>Unmarshal(data, out)</c> (jsoncodec) on a nested value that already passed token checks.</summary>
    public static GoDecodeError? NestedUnmarshal(JsonNode node, GoType type)
    {
        // First check the JSON kind of every field, then run the full Go decode for the remaining errors.
        if (AgentSuiteJson.ValidateJsonType(node, type) is { } fieldError)
            return GoDecodeError.Of(fieldError);
        return GoDecoder.DecodeError(node, type);
    }

    // Upstream hooks also decode into map[string]json.RawMessage, which fails unless the value is an object.
    private static GoDecodeError? FieldMapError(JsonNode node) => node.Kind == JsonKind.Object
        ? null
        : new GoDecodeError { Value = node.GoValueName, TypeName = "map[string]json.RawMessage" };

    // Shared hook shape: decode into the hook-free copy, then decode as a field map, then run type-specific checks.
    // The order matters because Go returns the first error it hits, and error text must match upstream.
    private static GoDecodeError? WireHook(JsonNode node, GoStruct type, Func<JsonNode, string?> checks)
    {
        if (NestedUnmarshal(node, type.Wire("wire")) is { } error)
            return error;
        if (FieldMapError(node) is { } mapError)
            return mapError;
        return checks(node) is { } message ? GoDecodeError.Of(message) : null;
    }

    /// <summary>Upstream <c>decodeRequiredContract</c>.</summary>
    private static GoDecodeError? RequiredContract(JsonNode node, GoStruct type, params string[] required)
    {
        // Same first two steps as WireHook.
        if (NestedUnmarshal(node, type.Wire("wire")) is { } error)
            return error;
        if (FieldMapError(node) is { } mapError)
            return mapError;
        // Then every listed field must be present and not null. Go zero values cannot tell "missing" from "0".
        foreach (var name in required)
        {
            if (node.Get(name) is not { IsNull: false })
                return GoDecodeError.Of($"contract requires explicit non-null {name}");
        }
        return null;
    }

    /// <summary>Upstream <c>Descriptor.UnmarshalJSON</c>.</summary>
    private static GoDecodeError? DescriptorHook(JsonNode node)
    {
        if (FieldMapError(node) is { } mapError)
            return mapError;
        // Reject unknown fields, then missing required fields, then null required fields, in upstream order.
        foreach (var (name, _) in node.Members!)
        {
            if (name is not ("mediaType" or "digest" or "size" or "annotations"))
                return GoDecodeError.Of($"descriptor has unknown field {Go.Quote(name)}");
        }
        string[] required = ["mediaType", "digest", "size"];
        foreach (var name in required)
        {
            if (!node.Has(name))
                return GoDecodeError.Of($"descriptor is missing required field {Go.Quote(name)}");
        }
        foreach (var name in required)
        {
            if (node.Get(name)!.IsNull)
                return GoDecodeError.Of($"descriptor field {Go.Quote(name)} must not be null");
        }
        // "annotations" is optional, but if it is present it must not be null.
        if (node.Get("annotations") is { IsNull: true })
            return GoDecodeError.Of($"descriptor field {Go.Quote("annotations")} must not be null");
        // All custom checks passed; finish with a normal decode into the hook-free copy.
        return GoDecoder.DecodeError(node, DescriptorAlias);
    }

    // A model is either an exact model name or a capability request, never both and never neither.
    private static string? ModelRequirementChecks(JsonNode fields)
    {
        var hasModel = fields.Has("model");
        var hasCapabilities = fields.Has("capabilities");
        if (hasModel == hasCapabilities)
            return "model requires exactly one of model or capabilities";
        if (hasCapabilities && fields.Get("capabilities")!.IsNull)
            return "model capabilities must not be null";
        // Capability requests are resolved by the deployment, so the package may not name an endpoint or secrets.
        if (hasCapabilities)
        {
            foreach (var name in new[] { "endpointEnv", "secretRefs" })
            {
                if (fields.Has(name))
                    return $"capability-based inference forbids {name}";
            }
        }
        return null;
    }

    /// <summary>Upstream <c>requireExtensionCritical</c>.</summary>
    private static string? RequireExtensionCritical(JsonNode? raw)
    {
        // A missing or non-array value is left for the normal type check to report.
        if (raw is null || raw.Kind != JsonKind.Array)
            return null;
        // Every extension must say explicitly whether it is critical, so a reader knows if it may ignore it.
        for (var index = 0; index < raw.Items!.Count; index++)
        {
            var extension = raw.Items[index];
            if (extension.Kind == JsonKind.Object && extension.Get("critical") is not { IsNull: false })
                return $"extension {index} requires non-null critical";
            if (extension.IsNull)
                return $"extension {index} requires non-null critical";
        }
        return null;
    }

    // Upstream tool provider checks: extensions need "critical", "variants" cannot be an empty list,
    // and a tool's "outputSchema" may be omitted but not set to null.
    private static string? ToolProviderChecks(JsonNode fields)
    {
        if (RequireExtensionCritical(fields.Get("extensions")) is { } extensionError)
            return extensionError;
        if (fields.Has("variants") && new J(fields)["variants"].Count == 0)
            return "variants must be a nonempty array";
        if (fields.Get("tools") is { Kind: JsonKind.Array } tools)
        {
            for (var index = 0; index < tools.Items!.Count; index++)
            {
                if (tools.Items[index].Get("outputSchema") is { IsNull: true })
                    return $"tool {index} outputSchema must not be null";
            }
        }
        return null;
    }

    private static string? RuntimeProfileChecks(JsonNode fields)
    {
        // Step 1: every field is required and must not be null.
        foreach (var key in new[] { "schemaVersion", "mediaType", "id", "runtime", "harness", "renderer", "configurationABI", "imageRequired" })
        {
            if (fields.Get(key) is not { IsNull: false })
                return $"runtime profile requires non-null {key}";
        }
        // Step 2: each implementation object needs id, version and digest.
        foreach (var key in new[] { "runtime", "harness", "renderer" })
        {
            if (!ImplementationShape(fields.Get(key)!))
                return $"runtime profile {key} requires id, version and digest";
        }
        // Step 3: check the values (spec version, media type, identifiers, digests). One generic message, as upstream.
        var profile = new J(fields);
        if (profile["schemaVersion"].S != SpecVersion || profile["mediaType"].S != MediaTypeRuntimeProfile ||
            !Go.IdentifierPattern.IsMatch(profile["id"].S) || !Go.EvidenceToken(profile["id"].S) ||
            !Go.IdentifierPattern.IsMatch(profile["configurationABI"].S) ||
            !EvidenceImplementationValid(profile["runtime"]) || !EvidenceImplementationValid(profile["harness"]) ||
            !EvidenceImplementationValid(profile["renderer"]))
            return "invalid runtime profile identity or requirements";
        return null;
    }

    /// <summary>Upstream <c>evidenceShape(data, "implementation")</c>.</summary>
    private static bool ImplementationShape(JsonNode node)
    {
        if (node.Kind != JsonKind.Object)
            return false;
        if (!node.Has("id") || !node.Has("version") || !node.Has("digest"))
            return false;
        // No member may be null, including any extra members.
        return node.Members!.All(member => !member.Value.IsNull);
    }

    /// <summary>Upstream <c>evidenceImplementation</c>.</summary>
    public static bool EvidenceImplementationValid(J value) =>
        Go.EvidenceToken(value["id"].S) && Go.EvidenceToken(value["version"].S) && Go.IsValidDigest(value["digest"].S);

    private static string? SuiteChecks(JsonNode fields)
    {
        if (RequireExtensionCritical(fields.Get("extensions")) is { } extensionError)
            return extensionError;
        // "runtimeProfiles" is optional. When present it must be a nonempty list of complete references.
        if (fields.Get("runtimeProfiles") is { } raw)
        {
            var refs = new J(raw);
            if (raw.IsNull || refs.Count == 0)
                return "runtimeProfiles must be a nonempty array";
            foreach (var reference in refs.A)
            {
                // The path must be a safe relative path inside the content layer.
                if (AgentSuiteContent.ValidateContentPath(reference["path"].S, out _) is { } pathError)
                    return $"runtime profile reference path: {pathError}";
                var descriptor = reference["descriptor"];
                if (!Go.IdentifierPattern.IsMatch(reference["id"].S) || descriptor["mediaType"].S.Length == 0 ||
                    !Go.IsValidDigest(descriptor["digest"].S) || descriptor["size"].I < 0)
                    return "runtimeProfiles requires complete manifest references";
            }
        }
        return null;
    }

    /// <summary>Upstream <c>runtimeSelector</c>.</summary>
    private static string? RuntimeSelector(JsonNode fields)
    {
        if (fields.Get("runtimeProfile") is { } raw)
        {
            // An explicit null decodes to the empty string in Go, which the identifier pattern then checks.
            if (raw.IsNull)
                return Go.IdentifierPattern.IsMatch(string.Empty) ? null : "runtimeProfile must be a nonempty identifier";
            if (raw.Kind != JsonKind.String || !Go.IdentifierPattern.IsMatch(raw.Text!))
                return "runtimeProfile must be a nonempty identifier";
        }
        return null;
    }
}
