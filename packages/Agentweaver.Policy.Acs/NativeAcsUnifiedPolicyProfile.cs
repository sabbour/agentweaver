using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Policy.Acs;

public static class NativeAcsUnifiedPolicyProfile
{
    public const string ProfileId = "composable-domains-v2";
    public const string ModelUsePolicyId = "agentweaver_model_use";
    public const string GuardrailsPolicyId = "agentweaver_guardrails";
    public const string SelectorPolicyId = "agentweaver_domains_v2";

    internal const string ModelUseModulePath = "policy/model-use.rego";
    internal const string ModelUseDataPath = "policy/model_use/data.json";
    internal const string GuardrailsModulePath = "policy/guardrails.rego";
    internal const string GuardrailsDataPath = "policy/guardrails/data.json";
    internal const string SelectorModulePath = "policy/domains.rego";

    private const string ManifestResource = "Agentweaver.Policy.Acs.ComposableDomainsV2.manifest.yaml";
    private const string SelectorModuleResource = "Agentweaver.Policy.Acs.ComposableDomainsV2.policy.domains.rego";
    private const string ModelUseModuleResource = "Agentweaver.Policy.Acs.ComposableDomainsV2.policy.model-use.rego";
    private const string GuardrailsModuleResource = "Agentweaver.Policy.Acs.ComposableDomainsV2.policy.guardrails.rego";
    private const string ActionsModuleResource = "Agentweaver.Policy.Acs.ComposableDomainsV1.policy.actions.rego";
    private const string NetworkModuleResource = "Agentweaver.Policy.Acs.FiniteNetworkV1.policy.network.rego";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] ManifestBytes = ReadAsset(ManifestResource);
    private static readonly byte[] ActionsModuleBytes = ReadAsset(ActionsModuleResource);
    private static readonly byte[] NetworkModuleBytes = ReadAsset(NetworkModuleResource);
    private static readonly byte[] SelectorModuleBytes = ReadAsset(SelectorModuleResource);
    private static readonly byte[] ModelUseModuleBytes = ReadAsset(ModelUseModuleResource);
    private static readonly byte[] GuardrailsModuleBytes = ReadAsset(GuardrailsModuleResource);

    public static bool IsCandidate(NativeAcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.Files.IsDefault &&
            artifact.Files.Any(file => file?.RelativePath is ModelUseModulePath or
                ModelUseDataPath or GuardrailsModulePath or GuardrailsDataPath))
            return true;

        try
        {
            return !artifact.ManifestBytes.IsDefaultOrEmpty &&
                StrictUtf8.GetString(artifact.ManifestBytes.AsSpan())
                    .Contains(SelectorPolicyId, StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static NativeAcsUnifiedPolicyInspection Inspect(NativeAcsArtifact artifact) =>
        InspectCore(artifact, projectScope: false);

    public static NativeAcsUnifiedPolicyInspection InspectProject(NativeAcsArtifact artifact) =>
        InspectCore(artifact, projectScope: true);

    private static NativeAcsUnifiedPolicyInspection InspectCore(NativeAcsArtifact artifact, bool projectScope)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var diagnostics = ImmutableArray.CreateBuilder<NativeAcsDiagnostic>();

        if (artifact.ManifestBytes.IsDefaultOrEmpty ||
            artifact.ManifestBytes.Length > NativeAcsArtifactValidator.MaximumManifestBytes ||
            !artifact.ManifestBytes.AsSpan().SequenceEqual(ManifestBytes))
            return Invalid(diagnostics, "unified_manifest",
                "The unified ACS manifest does not match the reviewed profile.");

        if (artifact.Files.IsDefault || artifact.Files.Length != (projectScope ? 8 : 9))
            return Invalid(diagnostics, "unified_file_closure",
                "The unified ACS artifact must contain all reviewed domain files.");

        var files = new Dictionary<string, NativeAcsArtifactFile>(StringComparer.Ordinal);
        long totalBytes = artifact.ManifestBytes.Length;
        foreach (var file in artifact.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.RelativePath) ||
                file.Bytes.IsDefaultOrEmpty ||
                file.Bytes.Length > NativeAcsArtifactValidator.MaximumFileBytes ||
                !files.TryAdd(file.RelativePath, file))
                return Invalid(diagnostics, "unified_file_closure",
                    "The unified ACS artifact contains an invalid or duplicate file.");
            totalBytes += file.Bytes.Length;
        }

        if (totalBytes > NativeAcsArtifactValidator.MaximumArtifactBytes ||
            !files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
            [
                NativeAcsComposableDomainsProfile.ActionsModulePath,
                NativeAcsComposableDomainsProfile.ActionsDataPath,
                NativeAcsComposableDomainsProfile.NetworkModulePath,
                SelectorModulePath,
                ModelUseModulePath,
                ModelUseDataPath,
                GuardrailsModulePath,
                GuardrailsDataPath,
                .. projectScope ? [] : new[] { NativeAcsComposableDomainsProfile.NetworkDataPath }
            ]))
            return Invalid(diagnostics, "unified_file_closure",
                "The unified ACS artifact must contain only the reviewed domain files.");

        if (!Matches(files[NativeAcsComposableDomainsProfile.ActionsModulePath], ActionsModuleBytes) ||
            !Matches(files[NativeAcsComposableDomainsProfile.NetworkModulePath], NetworkModuleBytes) ||
            !Matches(files[SelectorModulePath], SelectorModuleBytes) ||
            !Matches(files[ModelUseModulePath], ModelUseModuleBytes) ||
            !Matches(files[GuardrailsModulePath], GuardrailsModuleBytes))
            return Invalid(diagnostics, "unified_module",
                "Unified ACS Rego modules must match the reviewed templates.");

        try
        {
            if (!string.Equals(
                    NativeAcsArtifactValidator.ComputeContentSha256(artifact),
                    artifact.ContentSha256,
                    StringComparison.Ordinal))
                return Invalid(diagnostics, "artifact_hash",
                    "The ACS artifact hash does not match all supplied bytes.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Invalid(diagnostics, "artifact_hash",
                "The ACS artifact hash could not be verified.");
        }

        var actionsResult = NativeAcsComposableDomainsProfile.ValidateActionsData(
            files[NativeAcsComposableDomainsProfile.ActionsDataPath].Bytes);
        if (!actionsResult.IsValid)
            return Invalid(diagnostics, actionsResult.Code,
                "The actions domain data does not match its closed schema.",
                NativeAcsComposableDomainsProfile.ActionsDataPath);

        NativeAcsFiniteNetworkPolicy? networkPolicy = null;
        if (!projectScope)
        {
            try
            {
                var network = NativeAcsFiniteNetworkProfile.CreateArtifact(
                    StrictUtf8.GetString(files[NativeAcsComposableDomainsProfile.NetworkDataPath].Bytes.AsSpan()));
                var networkResult = NativeAcsFiniteNetworkProfile.Validate(network);
                if (!networkResult.IsValid || networkResult.Policy is null)
                    return new(false, [], null, null, null, networkResult.Diagnostics);
                networkPolicy = networkResult.Policy;
            }
            catch (DecoderFallbackException)
            {
                return Invalid(diagnostics, "network_data_encoding",
                    "The network domain data must use valid UTF-8.",
                    NativeAcsComposableDomainsProfile.NetworkDataPath);
            }
        }

        if (!TryReadModelUse(
                files[ModelUseDataPath].Bytes, out var modelUsePolicy, out var modelCode))
            return Invalid(diagnostics, modelCode,
                "The model-use domain data does not match its closed schema.",
                ModelUseDataPath);
        if (!TryReadGuardrails(
                files[GuardrailsDataPath].Bytes, out var guardrailsPolicy, out var guardrailsCode))
            return Invalid(diagnostics, guardrailsCode,
                "The Guardrails domain data does not match its closed schema.",
                GuardrailsDataPath);

        return new(
            true,
            projectScope
                ? [NativeAcsDomainId.Actions, NativeAcsDomainId.ModelUse, NativeAcsDomainId.Guardrails]
                : [NativeAcsDomainId.Actions, NativeAcsDomainId.PlatformNetwork,
                    NativeAcsDomainId.ModelUse, NativeAcsDomainId.Guardrails],
            networkPolicy,
            modelUsePolicy,
            guardrailsPolicy,
            []);
    }

    public static NativeAcsArtifact CreateArtifact(
        string actionsDataJson,
        string networkDataJson,
        string modelUseDataJson,
        string guardrailsDataJson)
    {
        ArgumentNullException.ThrowIfNull(networkDataJson);
        return CreateArtifactCore(actionsDataJson, networkDataJson, modelUseDataJson, guardrailsDataJson);
    }

    public static NativeAcsArtifact CreateProjectArtifact(
        string actionsDataJson,
        string modelUseDataJson,
        string guardrailsDataJson) =>
        CreateArtifactCore(actionsDataJson, null, modelUseDataJson, guardrailsDataJson);

    private static NativeAcsArtifact CreateArtifactCore(
        string actionsDataJson,
        string? networkDataJson,
        string modelUseDataJson,
        string guardrailsDataJson)
    {
        ArgumentNullException.ThrowIfNull(actionsDataJson);
        ArgumentNullException.ThrowIfNull(modelUseDataJson);
        ArgumentNullException.ThrowIfNull(guardrailsDataJson);
        var artifact = new NativeAcsArtifact(
            ManifestBytes.ToImmutableArray(),
            [
                new NativeAcsArtifactFile(
                    NativeAcsComposableDomainsProfile.ActionsModulePath,
                    ActionsModuleBytes.ToImmutableArray()),
                new NativeAcsArtifactFile(
                    NativeAcsComposableDomainsProfile.ActionsDataPath,
                    StrictUtf8.GetBytes(actionsDataJson).ToImmutableArray()),
                new NativeAcsArtifactFile(
                    SelectorModulePath,
                    SelectorModuleBytes.ToImmutableArray()),
                new NativeAcsArtifactFile(
                    NativeAcsComposableDomainsProfile.NetworkModulePath,
                    NetworkModuleBytes.ToImmutableArray()),
                .. networkDataJson is null ? [] : new[]
                {
                    new NativeAcsArtifactFile(
                        NativeAcsComposableDomainsProfile.NetworkDataPath,
                        StrictUtf8.GetBytes(networkDataJson).ToImmutableArray())
                },
                new NativeAcsArtifactFile(
                    ModelUseModulePath,
                    ModelUseModuleBytes.ToImmutableArray()),
                new NativeAcsArtifactFile(
                    ModelUseDataPath,
                    StrictUtf8.GetBytes(modelUseDataJson).ToImmutableArray()),
                new NativeAcsArtifactFile(
                    GuardrailsModulePath,
                    GuardrailsModuleBytes.ToImmutableArray()),
                new NativeAcsArtifactFile(
                    GuardrailsDataPath,
                    StrictUtf8.GetBytes(guardrailsDataJson).ToImmutableArray())
            ],
            string.Empty);
        return artifact with
        {
            ContentSha256 = NativeAcsArtifactValidator.ComputeContentSha256(artifact)
        };
    }

    public static NativeAcsDomainChangeClassification ClassifyChanges(
        NativeAcsArtifact before,
        NativeAcsArtifact after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var beforeInspection = Inspect(before);
        var afterInspection = Inspect(after);
        if (!beforeInspection.IsValid)
            return new(false, [], beforeInspection.Diagnostics);
        if (!afterInspection.IsValid)
            return new(false, [], afterInspection.Diagnostics);

        var beforeFiles = before.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var afterFiles = after.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var changed = ImmutableArray.CreateBuilder<NativeAcsDomainId>();
        AddChange(NativeAcsDomainId.Actions, NativeAcsComposableDomainsProfile.ActionsDataPath);
        AddChange(NativeAcsDomainId.PlatformNetwork, NativeAcsComposableDomainsProfile.NetworkDataPath);
        AddChange(NativeAcsDomainId.ModelUse, ModelUseDataPath);
        AddChange(NativeAcsDomainId.Guardrails, GuardrailsDataPath);
        return new(true, changed.ToImmutable(), []);

        void AddChange(NativeAcsDomainId domain, string path)
        {
            if (!beforeFiles[path].Bytes.AsSpan().SequenceEqual(afterFiles[path].Bytes.AsSpan()))
                changed.Add(domain);
        }
    }

    internal static string GetPolicyId(NativeAcsDomainId domain) => domain switch
    {
        NativeAcsDomainId.Actions => NativeAcsComposableDomainsProfile.ActionsPolicyId,
        NativeAcsDomainId.PlatformNetwork => NativeAcsComposableDomainsProfile.NetworkPolicyId,
        NativeAcsDomainId.ModelUse => ModelUsePolicyId,
        NativeAcsDomainId.Guardrails => GuardrailsPolicyId,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    private static bool TryReadModelUse(
        ImmutableArray<byte> bytes,
        out NativeAcsModelUsePolicy? policy,
        out string failureCode)
    {
        policy = null;
        failureCode = "model_use_data_json";
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(),
                new JsonDocumentOptions { MaxDepth = 32 });
            if (HasDuplicateProperties(document.RootElement))
            {
                failureCode = "model_use_data_duplicate_key";
                return false;
            }

            var root = document.RootElement;
            if (!HasProperties(root, "rules") ||
                !root.TryGetProperty("rules", out var rulesElement) ||
                rulesElement.ValueKind != JsonValueKind.Array ||
                rulesElement.GetArrayLength() > 256)
            {
                failureCode = "model_use_data_shape";
                return false;
            }

            var rules = ImmutableArray.CreateBuilder<NativeAcsModelUsePolicyRule>();
            var tuples = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                if (!HasProperties(ruleElement,
                        "effect", "model_selection_reference", "model_id", "source_mode",
                        "provider_type", "provider_id", "configuration_revision", "configuration_hash") ||
                    !TryString(ruleElement, "effect", out var effect) ||
                    effect is not ("allow" or "deny") ||
                    !TryString(ruleElement, "model_selection_reference", out var reference) ||
                    !TryString(ruleElement, "model_id", out var modelId) ||
                    !TryString(ruleElement, "source_mode", out var sourceMode) ||
                    sourceMode is not ("hostedCopilot" or "byok") ||
                    !TryNullableString(ruleElement, "provider_type", out var providerType) ||
                    !TryNullableString(ruleElement, "provider_id", out var providerId) ||
                    !TryString(ruleElement, "configuration_revision", out var configurationRevision) ||
                    !TryString(ruleElement, "configuration_hash", out var configurationHash) ||
                    !IsIdentifier(reference) ||
                    !IsIdentifier(modelId) ||
                    !IsIdentifier(configurationRevision) ||
                    !IsSha256(configurationHash) ||
                    (sourceMode == "hostedCopilot"
                        ? providerType is not null || providerId is not null
                        : providerType is not ("azure" or "openai" or "anthropic")) ||
                    providerId is not null && !IsIdentifier(providerId))
                {
                    failureCode = "model_use_data_rule";
                    return false;
                }

                var rule = new NativeAcsModelUsePolicyRule(
                    effect, reference, modelId, sourceMode, providerType, providerId,
                    configurationRevision, configurationHash);
                var tuple = string.Join('\0',
                    effect, reference, modelId, sourceMode, providerType ?? string.Empty,
                    providerId ?? string.Empty, configurationRevision, configurationHash);
                if (!tuples.Add(tuple))
                {
                    failureCode = "model_use_data_duplicate_rule";
                    return false;
                }
                rules.Add(rule);
            }

            policy = new NativeAcsModelUsePolicy(rules.ToImmutable());
            failureCode = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadGuardrails(
        ImmutableArray<byte> bytes,
        out NativeAcsGuardrailsPolicy? policy,
        out string failureCode)
    {
        policy = null;
        failureCode = "guardrails_data_json";
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(),
                new JsonDocumentOptions { MaxDepth = 32 });
            if (HasDuplicateProperties(document.RootElement))
            {
                failureCode = "guardrails_data_duplicate_key";
                return false;
            }

            var root = document.RootElement;
            if (!HasProperties(root, "required_checks", "blocked_findings") ||
                !root.TryGetProperty("required_checks", out var checksElement) ||
                checksElement.ValueKind != JsonValueKind.Array ||
                checksElement.GetArrayLength() > 16 ||
                !root.TryGetProperty("blocked_findings", out var findingsElement) ||
                findingsElement.ValueKind != JsonValueKind.Array ||
                findingsElement.GetArrayLength() > 48)
            {
                failureCode = "guardrails_data_shape";
                return false;
            }

            var checks = ImmutableArray.CreateBuilder<NativeAcsGuardrailsRequiredCheck>();
            var checkIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var checkElement in checksElement.EnumerateArray())
            {
                if (!HasProperties(checkElement,
                        "provider_id", "adapter_version", "options_schema_version", "options_revision") ||
                    !TryString(checkElement, "provider_id", out var providerId) ||
                    !TryString(checkElement, "adapter_version", out var adapterVersion) ||
                    !checkElement.TryGetProperty("options_schema_version", out var schemaVersion) ||
                    schemaVersion.ValueKind != JsonValueKind.Number ||
                    !schemaVersion.TryGetInt32(out var schemaVersionValue) ||
                    schemaVersionValue < 1 ||
                    !TryString(checkElement, "options_revision", out var optionsRevision) ||
                    !IsIdentifier(providerId) ||
                    !Version.TryParse(adapterVersion, out var parsedVersion) ||
                    parsedVersion.ToString() != adapterVersion ||
                    !IsIdentifier(optionsRevision) ||
                    !checkIds.Add(providerId))
                {
                    failureCode = "guardrails_data_check";
                    return false;
                }
                checks.Add(new NativeAcsGuardrailsRequiredCheck(
                    providerId, adapterVersion, schemaVersionValue, optionsRevision));
            }

            var findings = ImmutableArray.CreateBuilder<NativeAcsGuardrailsBlockedFinding>();
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var findingElement in findingsElement.EnumerateArray())
            {
                if (!HasProperties(findingElement, "check_id", "code", "severity") ||
                    !TryString(findingElement, "check_id", out var checkId) ||
                    !TryString(findingElement, "code", out var code) ||
                    !TryString(findingElement, "severity", out var severity) ||
                    !checkIds.Contains(checkId) ||
                    !IsIdentifier(code) ||
                    severity is not ("low" or "medium" or "high" or "critical") ||
                    !blocked.Add($"{checkId}\0{code}\0{severity}"))
                {
                    failureCode = "guardrails_data_finding";
                    return false;
                }
                findings.Add(new NativeAcsGuardrailsBlockedFinding(checkId, code, severity));
            }

            policy = new NativeAcsGuardrailsPolicy(checks.ToImmutable(), findings.ToImmutable());
            failureCode = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            return false;
        value = property.GetString()!;
        return true;
    }

    private static bool TryNullableString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            return false;
        value = property.GetString();
        return true;
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item))
                    return true;
        }
        return false;
    }

    private static bool HasProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        return element.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool Matches(NativeAcsArtifactFile file, byte[] expected) =>
        file.Bytes.AsSpan().SequenceEqual(expected);

    private static NativeAcsUnifiedPolicyInspection Invalid(
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics,
        string code,
        string message,
        string? path = null)
    {
        diagnostics.Add(new NativeAcsDiagnostic(code, message, path));
        return new(false, [], null, null, null, diagnostics.ToImmutable());
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing unified policy asset: {name}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

public sealed record NativeAcsModelUsePolicyRule(
    string Effect,
    string ModelSelectionReference,
    string ModelId,
    string SourceMode,
    string? ProviderType,
    string? ProviderId,
    string ConfigurationRevision,
    string ConfigurationHash);

public sealed record NativeAcsModelUsePolicy(
    ImmutableArray<NativeAcsModelUsePolicyRule> Rules);

public sealed record NativeAcsGuardrailsRequiredCheck(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision);

public sealed record NativeAcsGuardrailsBlockedFinding(
    string CheckId,
    string Code,
    string Severity);

public sealed record NativeAcsGuardrailsPolicy(
    ImmutableArray<NativeAcsGuardrailsRequiredCheck> RequiredChecks,
    ImmutableArray<NativeAcsGuardrailsBlockedFinding> BlockedFindings);

public sealed record NativeAcsUnifiedPolicyInspection(
    bool IsValid,
    ImmutableArray<NativeAcsDomainId> Domains,
    NativeAcsFiniteNetworkPolicy? NetworkPolicy,
    NativeAcsModelUsePolicy? ModelUsePolicy,
    NativeAcsGuardrailsPolicy? GuardrailsPolicy,
    ImmutableArray<NativeAcsDiagnostic> Diagnostics);
