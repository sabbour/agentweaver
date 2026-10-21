namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// Ports the upstream <c>validator</c> (validate.go, policy_binding.go, evaluation.go, runtime_profile.go).
/// Go maps are replaced by insertion-ordered dictionaries; the caller sorts the final error lines, so map
/// iteration order never changes the joined result.
/// </summary>
internal sealed partial class AgentSuiteNativeValidator
{
    private readonly ContentSet _content;
    private readonly Dictionary<string, string> _mediaTypes = new(StringComparer.Ordinal);

    private AgentSuiteNativeValidator(ContentSet content, byte[] suiteBytes, J suite)
    {
        _content = content;
        SuiteBytes = suiteBytes;
        Suite = suite;
    }

    public byte[] SuiteBytes { get; }
    public J Suite { get; }
    public Dictionary<string, J> Agents { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AgentDigests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, J> ToolProviders { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ToolProviderDigests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, J> ToolProviderCompositions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, J> Compositions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, J> Builds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BuildDigests { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, J> Runtimes { get; } = new(StringComparer.Ordinal);

    /// <summary>Content path to the media type of the descriptor that references it.</summary>
    public IReadOnlyDictionary<string, string> MediaTypes => _mediaTypes;

    /// <summary>Upstream <c>validateContentGraph</c>; returns the joined Go error text or null.</summary>
    public static string? Validate(ContentSet content, out AgentSuiteNativeValidator? validator)
    {
        validator = null;
        if (content.Data("agentsuite.json", out var rawSuite) is { } dataError)
            return dataError;
        if (Decode(rawSuite, AgentSuiteTypes.Suite, out var suite) is { } suiteError)
            return $"agentsuite.json: {suiteError}";
        var v = new AgentSuiteNativeValidator(content, rawSuite, suite);
        v.Note("agentsuite.json", AgentSuiteTypes.MediaTypeSuite);
        var error = Go.Join([
            v.ValidateSuite(),
            v.LoadAgents(),
            v.ValidatePolicies(),
            v.ValidateEvaluations(),
            v.LoadToolProviders(),
            v.LoadBuildProfiles(),
            v.LoadRuntimeProfiles(),
            v.LoadToolProviderCompositions(),
            v.LoadCompositions(),
            v.ValidateReferences(),
        ]);
        if (error is not null)
            return error;
        validator = v;
        return null;
    }

    private void Note(string path, string mediaType) => _mediaTypes.TryAdd(path, mediaType);

    private string? Data(string path, J descriptor, out byte[] data)
    {
        var error = _content.Data(path, out data);
        if (error is null)
            Note(path, descriptor["mediaType"].S);
        return error;
    }

    private static string? Decode(byte[] data, GoType type, out J value)
    {
        var error = AgentSuiteJson.Unmarshal(data, type, out var node);
        value = new J(error is null ? node : null);
        return error;
    }

    private static bool MatchesCanonical(byte[] data, J descriptor) =>
        AgentSuiteJson.MatchesCanonical(data, descriptor["digest"].S, descriptor["size"].I);

    private static bool SizeAndDigestMatch(byte[] data, J descriptor) =>
        data.LongLength == descriptor["size"].I && Go.DigestBytes(data) == descriptor["digest"].S;

    private static bool SamePlatform(J left, J right) =>
        left["os"].S == right["os"].S && left["architecture"].S == right["architecture"].S && left["variant"].S == right["variant"].S;

    /// <summary>Upstream <c>compositionKey</c>.</summary>
    public static string CompositionKey(string agent, J platform, string profile)
    {
        var key = agent + "@" + platform.Platform;
        return profile.Length == 0 ? key : key + "@" + profile;
    }

    private static bool Id(string value) => Go.IdentifierPattern.IsMatch(value);

    private static bool Version(string value) => Go.VersionPattern.IsMatch(value);

    private string? ValidateSuite()
    {
        var errs = new List<string?>();
        if (Suite["schemaVersion"].S != AgentSuiteTypes.SpecVersion)
            errs.Add($"agentsuite.json schemaVersion must be {AgentSuiteTypes.SpecVersion}");
        if (Suite["mediaType"].S != AgentSuiteTypes.MediaTypeSuite)
            errs.Add($"agentsuite.json mediaType must be {AgentSuiteTypes.MediaTypeSuite}");
        if (!Id(Suite["name"].S))
            errs.Add("agentsuite.json name is invalid");
        if (Suite["agents"].Count == 0)
            errs.Add("agentsuite.json must contain at least one agent");
        if (Suite["buildProfiles"].Count == 0)
            errs.Add("agentsuite.json must contain at least one build profile");
        errs.Add(ValidateExtensions(Suite["extensions"]));
        return Go.Join(errs);
    }

    private string? LoadBuildProfiles()
    {
        var errs = new List<string?>();
        foreach (var reference in Suite["buildProfiles"].A)
        {
            var id = reference["id"].S;
            if (!Id(id))
            {
                errs.Add($"build profile reference {Go.Quote(id)} has invalid id");
                continue;
            }
            if (Builds.ContainsKey(id))
            {
                errs.Add($"duplicate build profile {Go.Quote(id)}");
                continue;
            }
            var descriptor = reference["descriptor"];
            if (Data(reference["path"].S, descriptor, out var data) is { } dataError)
            {
                errs.Add($"build profile {id}: {dataError}");
                continue;
            }
            if (descriptor["mediaType"].S != AgentSuiteTypes.MediaTypeBuildProfile || !MatchesCanonical(data, descriptor))
            {
                errs.Add($"build profile {id} digest mismatch");
                continue;
            }
            if (Decode(data, AgentSuiteTypes.BuildProfile, out var profile) is { } decodeError)
            {
                errs.Add($"build profile {id}: {decodeError}");
                continue;
            }
            if (ValidateBuildProfile(profile) is { } profileError)
            {
                errs.Add($"build profile {id}: {profileError}");
                continue;
            }
            if (profile["id"].S != id)
            {
                errs.Add($"build profile reference {id} points to {profile["id"].S}");
                continue;
            }
            Builds[id] = profile;
            BuildDigests[id] = descriptor["digest"].S;
        }
        return Go.Join(errs);
    }

    private string? LoadAgents()
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var errs = new List<string?>();
        foreach (var reference in Suite["agents"].A)
        {
            var id = reference["id"].S;
            var path = reference["path"].S;
            if (!Id(id))
            {
                errs.Add($"agent reference {Go.Quote(id)} has invalid id");
                continue;
            }
            if (Agents.ContainsKey(id))
            {
                errs.Add($"duplicate agent reference {Go.Quote(id)}");
                continue;
            }
            if (!seenPaths.Add(path))
            {
                errs.Add($"agent path {Go.Quote(path)} is referenced more than once");
                continue;
            }
            var descriptor = reference["descriptor"];
            if (Data(path, descriptor, out var data) is { } dataError)
            {
                errs.Add($"agent {id}: {dataError}");
                continue;
            }
            if (descriptor["mediaType"].S != AgentSuiteTypes.MediaTypeAgent || !MatchesCanonical(data, descriptor))
            {
                errs.Add($"agent {id} manifest digest mismatch");
                continue;
            }
            if (Decode(data, AgentSuiteTypes.Agent, out var agent) is { } decodeError)
            {
                errs.Add($"agent {id}: {decodeError}");
                continue;
            }
            if (agent["policy"] is { Present: true, IsNull: true })
            {
                errs.Add($"agent {id} policy must not be null");
                continue;
            }
            if (agent["evals"] is { Present: true, IsNull: true })
            {
                errs.Add($"agent {id} evals must be an array");
                continue;
            }
            if (ValidateAgent(agent) is { } agentError)
            {
                errs.Add($"agent {id}: {agentError}");
                continue;
            }
            if (agent["id"].S != id)
            {
                errs.Add($"agent reference {id} points to manifest for {agent["id"].S}");
                continue;
            }
            var instructions = agent["instructions"];
            var instructionsPath = instructions["path"].S;
            var entry = _content.Get(instructionsPath);
            if (entry is null || entry.Type != "file" || entry.Digest != instructions["descriptor"]["digest"].S ||
                entry.Size != instructions["descriptor"]["size"].I)
            {
                errs.Add($"agent {id} instructions do not match {instructionsPath}");
                continue;
            }
            Note(instructionsPath, instructions["descriptor"]["mediaType"].S);
            Agents[id] = agent;
            AgentDigests[id] = descriptor["digest"].S;
        }
        return Go.Join(errs);
    }

    private string? LoadToolProviders()
    {
        var catalogRef = Suite["toolProviderCatalog"];
        var catalogDescriptor = catalogRef["descriptor"];
        if (Data(catalogRef["path"].S, catalogDescriptor, out var data) is { } dataError)
            return $"tool provider catalog: {dataError}";
        if (catalogDescriptor["mediaType"].S != AgentSuiteTypes.MediaTypeToolProviderCatalog || !MatchesCanonical(data, catalogDescriptor))
            return "tool provider catalog digest mismatch";
        if (Decode(data, AgentSuiteTypes.ToolProviderCatalog, out var catalog) is { } decodeError)
            return $"tool provider catalog: {decodeError}";
        if (catalog["schemaVersion"].S != AgentSuiteTypes.SpecVersion || catalog["mediaType"].S != AgentSuiteTypes.MediaTypeToolProviderCatalog)
            return "tool provider catalog has unsupported schemaVersion or mediaType";
        var errs = new List<string?>();
        foreach (var reference in catalog["toolProviders"].A)
        {
            var id = reference["id"].S;
            var version = reference["version"].S;
            var key = id + "@" + version;
            if (!Id(id) || !Version(version))
            {
                errs.Add($"tool provider reference {Go.Quote(key)} has invalid identity or exact version");
                continue;
            }
            if (ToolProviders.ContainsKey(key))
            {
                errs.Add($"duplicate tool provider {key}");
                continue;
            }
            var descriptor = reference["descriptor"];
            if (Data(reference["path"].S, descriptor, out var raw) is { } rawError)
            {
                errs.Add($"tool provider {key}: {rawError}");
                continue;
            }
            if (descriptor["mediaType"].S != AgentSuiteTypes.MediaTypeToolProvider || !MatchesCanonical(raw, descriptor))
            {
                errs.Add($"tool provider {key} manifest digest mismatch");
                continue;
            }
            if (Decode(raw, AgentSuiteTypes.ToolProvider, out var provider) is { } providerDecodeError)
            {
                errs.Add($"tool provider {key}: {providerDecodeError}");
                continue;
            }
            if (ValidateToolProvider(provider, raw) is { } providerError)
            {
                errs.Add($"tool provider {key}: {providerError}");
                continue;
            }
            if (provider["id"].S != id || provider["version"].S != version)
            {
                errs.Add($"tool provider reference {key} points to {provider["id"].S}@{provider["version"].S}");
                continue;
            }
            ToolProviders[key] = provider;
            ToolProviderDigests[key] = descriptor["digest"].S;
        }
        return Go.Join(errs);
    }

    private string? LoadToolProviderCompositions()
    {
        var errs = new List<string?>();
        foreach (var reference in Suite["toolProviderCompositions"].A)
        {
            var key = reference["id"].S + "@" + reference["version"].S + "@" + reference["platform"].Platform;
            if (ToolProviderCompositions.ContainsKey(key))
            {
                errs.Add($"duplicate tool provider composition {key}");
                continue;
            }
            var descriptor = reference["descriptor"];
            if (Data(reference["path"].S, descriptor, out var data) is { } dataError)
            {
                errs.Add($"tool provider composition {key}: {dataError}");
                continue;
            }
            if (descriptor["mediaType"].S != AgentSuiteTypes.MediaTypeToolProviderComposition || !MatchesCanonical(data, descriptor))
            {
                errs.Add($"tool provider composition {key} digest mismatch");
                continue;
            }
            if (Decode(data, AgentSuiteTypes.ToolProviderComposition, out var composition) is { } decodeError)
            {
                errs.Add($"tool provider composition {key}: {decodeError}");
                continue;
            }
            if (ValidateToolProviderComposition(composition) is { } compositionError)
            {
                errs.Add($"tool provider composition {key}: {compositionError}");
                continue;
            }
            if (composition["id"].S != reference["id"].S || composition["version"].S != reference["version"].S ||
                !SamePlatform(composition["platform"], reference["platform"]))
            {
                errs.Add($"tool provider composition {key} identity does not match its reference");
                continue;
            }
            var providerKey = composition["id"].S + "@" + composition["version"].S;
            if (!ToolProviders.TryGetValue(providerKey, out var provider) ||
                composition["manifestDigest"].S != ToolProviderDigests.GetValueOrDefault(providerKey, string.Empty))
            {
                errs.Add($"tool provider composition {key} references a missing or stale ToolProvider manifest");
                continue;
            }
            var (variant, matches) = ExactVariant(provider["variants"], composition["platform"]);
            if (matches != 1 || composition["variantDigest"].S != variant["variantDigest"].S)
            {
                errs.Add($"tool provider composition {key} must select exactly one matching variant");
                continue;
            }
            var buildProfile = composition["buildProfile"].S;
            if (!Builds.TryGetValue(buildProfile, out var profile) || !ProfileSupportsToolProviderPlatform(profile, composition["platform"]))
            {
                errs.Add($"tool provider composition {key} build profile {Go.Quote(buildProfile)} does not support {composition["platform"].Platform}");
                continue;
            }
            var destinations = new Dictionary<string, J>(StringComparer.Ordinal);
            if (BundleClosure(providerKey, variant, out var closure) is { } closureError)
            {
                errs.Add($"tool provider composition {key}: {closureError}");
                continue;
            }
            foreach (var (_, bundleVariant) in closure)
            {
                if (AddVariantDestinations(destinations, bundleVariant) is { } destinationError)
                    errs.Add($"tool provider composition {key}: {destinationError}");
            }
            ToolProviderCompositions[key] = composition;
        }
        return Go.Join(errs);
    }

    private string? LoadCompositions()
    {
        var errs = new List<string?>();
        var profiled = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var reference in Suite["compositions"].A)
        {
            var agent = reference["agent"].S;
            var platform = reference["platform"];
            var runtimeProfile = reference["runtimeProfile"].S;
            var tuple = CompositionKey(agent, platform, string.Empty);
            if (profiled.TryGetValue(tuple, out var previous) && previous != (runtimeProfile.Length != 0))
                errs.Add($"composition {tuple} mixes legacy and runtime profile selections");
            profiled[tuple] = runtimeProfile.Length != 0;
            var key = CompositionKey(agent, platform, runtimeProfile);
            if (Compositions.ContainsKey(key))
            {
                errs.Add($"duplicate composition {key}");
                continue;
            }
            var descriptor = reference["descriptor"];
            if (Data(reference["path"].S, descriptor, out var data) is { } dataError)
            {
                errs.Add($"composition {key}: {dataError}");
                continue;
            }
            if (descriptor["mediaType"].S != AgentSuiteTypes.MediaTypeComposition || !MatchesCanonical(data, descriptor))
            {
                errs.Add($"composition {key} digest mismatch");
                continue;
            }
            if (Decode(data, AgentSuiteTypes.Composition, out var composition) is { } decodeError)
            {
                errs.Add($"composition {key}: {decodeError}");
                continue;
            }
            if (ValidateComposition(composition) is { } compositionError)
            {
                errs.Add($"composition {key}: {compositionError}");
                continue;
            }
            if (composition["agent"].S != agent || !SamePlatform(composition["platform"], platform) ||
                composition["runtimeProfile"].S != runtimeProfile)
            {
                errs.Add($"composition {key} identity does not match its reference");
                continue;
            }
            var buildProfile = composition["buildProfile"].S;
            if (!Builds.TryGetValue(buildProfile, out var profile) || !ProfileSupportsPlatform(profile, composition["platform"]))
            {
                errs.Add($"composition {key} build profile {Go.Quote(buildProfile)} does not support {composition["platform"].Platform}");
                continue;
            }
            var selectedRuntime = composition["runtimeProfile"].S;
            if (selectedRuntime.Length != 0)
            {
                if (!Runtimes.TryGetValue(selectedRuntime, out var runtime))
                {
                    errs.Add($"composition {key} references unknown runtime profile {selectedRuntime}");
                    continue;
                }
                foreach (var image in profile["harness"].A)
                {
                    if (SamePlatform(image["platform"], composition["platform"]) &&
                        image["image"]["digest"].S != runtime["harness"]["digest"].S)
                        errs.Add($"composition {key} runtime profile harness digest mismatch");
                }
            }
            Compositions[key] = composition;
        }
        return Go.Join(errs);
    }

    private string? LoadRuntimeProfiles()
    {
        var errs = new List<string?>();
        foreach (var reference in Suite["runtimeProfiles"].A)
        {
            var id = reference["id"].S;
            if (Runtimes.ContainsKey(id))
            {
                errs.Add($"duplicate runtime profile {Go.Quote(id)}");
                continue;
            }
            var descriptor = reference["descriptor"];
            if (Data(reference["path"].S, descriptor, out var data) is { } dataError)
            {
                errs.Add($"runtime profile {id}: {dataError}");
                continue;
            }
            if (descriptor["mediaType"].S != AgentSuiteTypes.MediaTypeRuntimeProfile || !MatchesCanonical(data, descriptor))
            {
                errs.Add($"runtime profile {id} digest mismatch");
                continue;
            }
            if (Decode(data, AgentSuiteTypes.RuntimeProfile, out var profile) is { } decodeError)
            {
                errs.Add($"runtime profile {id}: {decodeError}");
                continue;
            }
            if (profile["id"].S != id)
            {
                errs.Add($"runtime profile reference {id} points to {profile["id"].S}");
                continue;
            }
            Runtimes[id] = profile;
        }
        return Go.Join(errs);
    }

    private string? ValidateReferences()
    {
        var errs = new List<string?>();
        foreach (var (providerKey, provider) in ToolProviders)
        {
            foreach (var variant in provider["variants"].A)
                errs.Add(BundleClosure(providerKey, variant, out _));
        }
        foreach (var (agentId, agent) in Agents)
        {
            foreach (var invoke in agent["invokes"].A)
            {
                if (!Agents.ContainsKey(invoke["agent"].S))
                    errs.Add($"agent {agentId} invokes unknown agent {invoke["agent"].S}");
            }
            foreach (var (key, composition) in Compositions)
            {
                if (composition["agent"].S != agentId)
                    continue;
                var destinations = new Dictionary<string, J>(StringComparer.Ordinal);
                var requirements = new Dictionary<string, J>(StringComparer.Ordinal);
                foreach (var requirement in agent["toolProviders"].A)
                    requirements[requirement["id"].S + "@" + requirement["version"].S] = requirement;
                if (requirements.Count != composition["toolProviders"].Count)
                {
                    errs.Add($"composition {key} does not resolve every agent requirement exactly once");
                    continue;
                }
                foreach (var resolved in composition["toolProviders"].A)
                {
                    var providerKey = resolved["id"].S + "@" + resolved["version"].S;
                    if (!requirements.TryGetValue(providerKey, out var requirement))
                    {
                        errs.Add($"composition {key} contains undeclared tool provider {providerKey}");
                        continue;
                    }
                    if (!ToolProviders.TryGetValue(providerKey, out var provider))
                    {
                        errs.Add($"composition {key} contains missing tool provider {providerKey}");
                        continue;
                    }
                    if (resolved["manifestDigest"].S != ToolProviderDigests.GetValueOrDefault(providerKey, string.Empty) ||
                        resolved["executionMode"].S != requirement["executionMode"].S)
                        errs.Add($"composition {key} has stale resolution for {providerKey}");
                    var (variant, matches) = ExactVariant(provider["variants"], composition["platform"]);
                    if (matches != 1 || resolved["variantDigest"].S != variant["variantDigest"].S)
                    {
                        errs.Add($"composition {key} must select exactly one matching variant for {providerKey}");
                        continue;
                    }
                    if (BundleClosure(providerKey, variant, out var closure) is { } closureError)
                    {
                        errs.Add($"composition {key}: {closureError}");
                        continue;
                    }
                    foreach (var (_, bundleVariant) in closure)
                    {
                        if (AddVariantDestinations(destinations, bundleVariant) is { } destinationError)
                            errs.Add($"composition {key}: {destinationError}");
                    }
                }
            }
            if (!Compositions.Values.Any(composition => composition["agent"].S == agentId))
                errs.Add($"agent {agentId} has no platform composition");
        }
        foreach (var (key, composition) in Compositions)
        {
            if (!Agents.ContainsKey(composition["agent"].S))
                errs.Add($"composition {key} references unknown agent {composition["agent"].S}");
        }
        errs.Add(ValidateToolProviderInboundReferences());
        if (!Suite["capabilities"].A.Select(value => value.S).SequenceEqual(DerivedCapabilities(ToolProviders.Values), StringComparer.Ordinal))
            errs.Add("suite capabilities must exactly equal capabilities derived from its tool provider declarations");
        return Go.Join(errs);
    }

    private string? ValidateToolProviderInboundReferences()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in Agents.Values)
        {
            foreach (var requirement in agent["toolProviders"].A)
                referenced.Add(requirement["id"].S + "@" + requirement["version"].S);
        }
        foreach (var composition in ToolProviderCompositions.Values)
            referenced.Add(composition["id"].S + "@" + composition["version"].S);
        foreach (var provider in ToolProviders.Values)
        {
            foreach (var variant in provider["variants"].A)
            {
                foreach (var dependency in variant["dependencies"].A)
                    referenced.Add(dependency["id"].S + "@" + dependency["version"].S);
            }
        }
        var errs = new List<string?>();
        foreach (var (key, provider) in ToolProviders)
        {
            if (referenced.Contains(key))
                continue;
            if (provider["retained"].IsNull || !provider["retained"].B)
                errs.Add($"tool provider {key} has no inward reference and must declare retained true to remain in the catalog");
        }
        return Go.Join(errs);
    }

    /// <summary>Upstream <c>derivedCapabilities</c>.</summary>
    public static IReadOnlyList<string> DerivedCapabilities(IEnumerable<J> providers) =>
        providers.Any(provider => provider["variants"].Count > 0) ? ["bundled-stdio-mcp"] : [];

    private static string? ValidateAgent(J agent)
    {
        var errs = new List<string?>();
        if (agent["schemaVersion"].S != AgentSuiteTypes.SpecVersion || agent["mediaType"].S != AgentSuiteTypes.MediaTypeAgent)
            errs.Add("unsupported schemaVersion or mediaType");
        if (!Id(agent["id"].S))
            errs.Add("id is invalid");
        var instructions = agent["instructions"];
        if (AgentSuiteContent.ValidateContentPath(instructions["path"].S, out _) is not null ||
            !Go.IsValidDigest(instructions["descriptor"]["digest"].S) ||
            instructions["descriptor"]["mediaType"].S != AgentSuiteTypes.MediaTypeInstructions)
            errs.Add("instructions reference is invalid");
        var model = agent["model"];
        var hasCapabilities = !model["capabilities"].IsNull;
        if (model["protocol"].S.Length == 0 || (model["model"].S.Length == 0) == !hasCapabilities)
            errs.Add("model requires a protocol and exactly one of model or capabilities");
        var endpointEnv = model["endpointEnv"].S;
        if (hasCapabilities)
        {
            errs.Add(ValidateInferenceRequirements(model["capabilities"]));
            if (model["protocol"].S != "openai-compatible" || endpointEnv.Length != 0 || model["secretRefs"].Count != 0)
                errs.Add("capability-based inference requires deployment-owned connection bindings");
        }
        if (endpointEnv.Length != 0 && (!Go.EnvPattern.IsMatch(endpointEnv) || UnsafeInjectionEnv(endpointEnv)))
            errs.Add("model endpointEnv is invalid");
        errs.Add(ValidateIdentifierList("model secretRefs", model["secretRefs"].A.Select(value => value.S)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in agent["toolProviders"].A)
        {
            var key = provider["id"].S + "@" + provider["version"].S;
            if (!Id(provider["id"].S) || !Version(provider["version"].S))
                errs.Add($"tool provider requirement {key} is not an exact identity");
            if (!seen.Add(key))
                errs.Add($"duplicate tool provider requirement {key}");
            if (provider["executionMode"].S != AgentSuiteTypes.ExecutionSharedSandbox)
                errs.Add($"tool provider requirement {key} executionMode must be {Go.Quote(AgentSuiteTypes.ExecutionSharedSandbox)}");
        }
        var invokes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invoke in agent["invokes"].A)
        {
            var target = invoke["agent"].S;
            if (!Id(target) || target == agent["id"].S || invokes.Contains(target))
                errs.Add($"invoked agent {Go.Quote(target)} is invalid or duplicated");
            invokes.Add(target);
            if (invoke["maxConcurrent"].I <= 0 || invoke["maxDepth"].I <= 0)
                errs.Add($"invoked agent {Go.Quote(target)} requires positive maxConcurrent and maxDepth");
        }
        errs.Add(ValidateExtensions(agent["extensions"]));
        return Go.Join(errs);
    }

    /// <summary>Upstream <c>InferenceRequirements.Validate</c>.</summary>
    private static string? ValidateInferenceRequirements(J requirements)
    {
        if (requirements["api"].S != "openai-chat-completions-v1")
            return "unsupported inference API; expected openai-chat-completions-v1";
        var context = requirements["contextTokens"].I;
        var output = requirements["outputTokens"].I;
        if (context < 1 || output < 1 || output > context || context > 10_000_000)
            return "inference token limits must be positive, bounded, and output must fit context";
        return null;
    }

    private static string? ValidateIdentifierList(string name, IEnumerable<string> values)
    {
        var errs = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!Id(value) || seen.Contains(value))
                errs.Add($"{name} contains invalid or duplicate identifier {Go.Quote(value)}");
            seen.Add(value);
        }
        return Go.Join(errs);
    }

    /// <summary>Upstream <c>unsafeInjectionEnv</c>.</summary>
    public static bool UnsafeInjectionEnv(string name) =>
        name.StartsWith("LD_", StringComparison.Ordinal) || name is "PYTHONPATH" or "NODE_OPTIONS" or "BASH_ENV";

    private static string? ValidateExtensions(J extensions)
    {
        var errs = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions.A)
        {
            var name = extension["name"].S;
            if (!Go.CapabilityPattern.IsMatch(name) || seen.Contains(name))
                errs.Add($"extension {Go.Quote(name)} is invalid or duplicated");
            seen.Add(name);
            if (extension["critical"].B)
                errs.Add($"unknown critical extension {Go.Quote(name)} is unsupported");
            var digest = extension["digest"].S;
            if (digest.Length != 0 && !Go.IsValidDigest(digest))
                errs.Add($"extension {Go.Quote(name)} digest is invalid");
        }
        return Go.Join(errs);
    }

    private static string? ValidateComposition(J composition)
    {
        var errs = new List<string?>();
        if (composition["schemaVersion"].S != AgentSuiteTypes.SpecVersion || composition["mediaType"].S != AgentSuiteTypes.MediaTypeComposition)
            errs.Add("unsupported schemaVersion or mediaType");
        if (!Id(composition["agent"].S))
            errs.Add("agent id is invalid");
        if (!Id(composition["buildProfile"].S))
            errs.Add("buildProfile is invalid");
        errs.Add(ValidatePlatform(composition["platform"]));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string previousId = string.Empty, previousVersion = string.Empty;
        var index = 0;
        foreach (var provider in composition["toolProviders"].A)
        {
            var id = provider["id"].S;
            var version = provider["version"].S;
            var key = id + "@" + version;
            if (index > 0 && (Go.Compare(id, previousId) < 0 || id == previousId && Go.Compare(version, previousVersion) <= 0))
                errs.Add("resolved tool providers must be sorted by id and version");
            if (seen.Contains(key) || !Id(id) || !Version(version) || !Go.IsValidDigest(provider["manifestDigest"].S) ||
                !Go.IsValidDigest(provider["variantDigest"].S) || provider["executionMode"].S != AgentSuiteTypes.ExecutionSharedSandbox)
                errs.Add($"resolved tool provider {key} is invalid or duplicated");
            seen.Add(key);
            previousId = id;
            previousVersion = version;
            index++;
        }
        return Go.Join(errs);
    }

    private static string? ValidateToolProviderComposition(J composition)
    {
        var errs = new List<string?>();
        if (composition["schemaVersion"].S != AgentSuiteTypes.SpecVersion ||
            composition["mediaType"].S != AgentSuiteTypes.MediaTypeToolProviderComposition)
            errs.Add("unsupported schemaVersion or mediaType");
        if (!Id(composition["id"].S) || !Version(composition["version"].S))
            errs.Add("tool provider identity is invalid");
        if (!Go.IsValidDigest(composition["manifestDigest"].S) || !Go.IsValidDigest(composition["variantDigest"].S))
            errs.Add("tool provider manifest or variant digest is invalid");
        if (!Id(composition["buildProfile"].S))
            errs.Add("buildProfile is invalid");
        errs.Add(ValidatePlatform(composition["platform"]));
        return Go.Join(errs);
    }

    private static string? ValidateBuildProfile(J profile)
    {
        var errs = new List<string?>();
        if (!profile["execution"].IsNull)
            errs.Add(ValidateExecutionContract(profile["execution"]));
        if (profile["schemaVersion"].S != AgentSuiteTypes.SpecVersion || profile["mediaType"].S != AgentSuiteTypes.MediaTypeBuildProfile)
            errs.Add("unsupported schemaVersion or mediaType");
        if (!Id(profile["id"].S))
            errs.Add("id is invalid");
        if (profile["sourceEpoch"].I <= 0)
            errs.Add("sourceEpoch must be a positive Unix timestamp");
        errs.Add(ValidatePlatformImages("runtimeBase", profile["runtimeBase"]));
        errs.Add(ValidatePlatformImages("harness", profile["harness"]));
        return Go.Join(errs);
    }

    /// <summary>Upstream <c>ValidateExecutionContract</c> (execution.go).</summary>
    private static string? ValidateExecutionContract(J contract)
    {
        const string mounted = "agentkit-v0-mounted-v1";
        var configuration = contract["configuration"].S;
        if (configuration.Length != 0 && configuration != mounted)
            return "unsupported execution configuration ABI";
        var port = contract["port"].I;
        var healthPath = contract["healthPath"].S;
        if (configuration == mounted && (port != 8080 || healthPath != "/healthz"))
            return "mounted AgentKit configuration requires port 8080 and /healthz";
        if (contract["kind"].S != "http-v1")
            return $"unsupported execution contract {Go.Quote(contract["kind"].S)}";
        if (contract["protocol"].S != "openai-chat-v1")
            return "HTTP execution requires openai-chat-v1";
        if (port < 1024 || port > 65535)
            return "execution port must be between 1024 and 65535";
        if (!healthPath.StartsWith('/') || Go.CleanPath(healthPath) != healthPath || healthPath.IndexOfAny(['?', '#', '\\', '\r', '\n']) >= 0)
            return "healthPath must be a normalized HTTP path";
        var names = new HashSet<string>(StringComparer.Ordinal);
        var envs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in contract["inputs"].A)
        {
            var name = input["name"].S;
            var environment = input["environment"].S;
            if (!Id(name) || names.Contains(name) || !Go.EnvPattern.IsMatch(environment) || envs.Contains(environment) ||
                UnsafeInjectionEnv(environment) || environment is "PATH" or "HOME" or "AGENTKIT_PROTOCOL" or "AGENTKIT_PORT")
                return "execution inputs must be valid, unique and non-injecting";
            names.Add(name);
            envs.Add(environment);
        }
        return null;
    }

    private static string? ValidatePlatformImages(string name, J images)
    {
        if (images.Count == 0)
            return $"{name} must contain at least one pinned platform image";
        var errs = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in images.A)
        {
            var key = image["platform"].Platform;
            if (ValidatePlatform(image["platform"]) is { } platformError)
                errs.Add($"{name} {key}: {platformError}");
            if (seen.Contains(key))
                errs.Add($"{name} contains duplicate platform {key}");
            seen.Add(key);
            var imageRef = image["imageRef"].S;
            var descriptor = image["image"];
            if (AgentSuiteImageReference.Validate(imageRef) is not null)
                errs.Add($"{name} {key} imageRef must be a registry-qualified, digest-addressed OCI image reference");
            else if (!imageRef.EndsWith("@" + descriptor["digest"].S, StringComparison.Ordinal))
                errs.Add($"{name} {key} imageRef digest must match image descriptor");
            if (descriptor["mediaType"].S != AgentSuiteTypes.OciManifestMediaType || !Go.IsValidDigest(descriptor["digest"].S) ||
                descriptor["size"].I <= 0)
                errs.Add($"{name} {key} image descriptor must pin an OCI image manifest");
        }
        return Go.Join(errs);
    }

    private static bool ProfileSupportsPlatform(J profile, J platform) =>
        profile["runtimeBase"].A.Any(image => SamePlatform(image["platform"], platform)) &&
        profile["harness"].A.Any(image => SamePlatform(image["platform"], platform));

    private static bool ProfileSupportsToolProviderPlatform(J profile, J platform) =>
        profile["runtimeBase"].A.Any(image => SamePlatform(image["platform"], platform));

    /// <summary>Upstream <c>validatePlatform</c>.</summary>
    private static string? ValidatePlatform(J platform)
    {
        if (platform["os"].S != "linux")
            return $"the draft supports exact Linux tool provider platforms only, got {platform.Platform}";
        if (platform["architecture"].S is not ("amd64" or "arm64"))
            return $"the draft supports linux/amd64 and linux/arm64 tool providers, got {platform.Platform}";
        if (platform["variant"].S.Length != 0)
            return $"the draft requires an exact platform without OCI variant, got {platform.Platform}";
        return null;
    }

    /// <summary>Upstream <c>exactVariant</c>: the last matching variant and the match count.</summary>
    private static (J Variant, int Count) ExactVariant(J variants, J platform)
    {
        var selected = default(J);
        var count = 0;
        foreach (var variant in variants.A)
        {
            if (SamePlatform(variant["platform"], platform))
            {
                selected = variant;
                count++;
            }
        }
        return (selected, count);
    }
}
