using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Policy.Acs;

public enum NativeAcsDomainId
{
    Actions,
    PlatformNetwork,
    ModelUse,
    Guardrails,
    ToolPermissions,
    Skills
}

public static class NativeAcsComposableDomainsProfile
{
    public const string ProfileId = "composable-domains-v1";
    public const string ActionsPolicyId = "agentweaver_actions";
    public const string NetworkPolicyId = "agentweaver_platform_network";
    public const string SelectorPolicyId = "agentweaver_domains_v1";

    internal const string ActionsModulePath = "policy/actions.rego";
    internal const string NetworkModulePath = "policy/network.rego";
    internal const string SelectorModulePath = "policy/domains.rego";
    internal const string ActionsDataPath = "policy/actions/data.json";
    internal const string NetworkDataPath = "policy/data.json";

    private const string ManifestResource = "Agentweaver.Policy.Acs.ComposableDomainsV1.manifest.yaml";
    private const string ActionsModuleResource = "Agentweaver.Policy.Acs.ComposableDomainsV1.policy.actions.rego";
    private const string SelectorModuleResource = "Agentweaver.Policy.Acs.ComposableDomainsV1.policy.domains.rego";
    private const string NetworkModuleResource = "Agentweaver.Policy.Acs.FiniteNetworkV1.policy.network.rego";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] ManifestBytes = ReadAsset(ManifestResource);
    private static readonly byte[] ActionsModuleBytes = ReadAsset(ActionsModuleResource);
    private static readonly byte[] SelectorModuleBytes = ReadAsset(SelectorModuleResource);
    private static readonly byte[] NetworkModuleBytes = ReadAsset(NetworkModuleResource);

    public static bool IsCandidate(NativeAcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.Files.IsDefault &&
            artifact.Files.Any(file =>
                file?.RelativePath is ActionsModulePath or SelectorModulePath or ActionsDataPath))
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

    public static NativeAcsComposableDomainsInspection Inspect(NativeAcsArtifact artifact) =>
        InspectCore(artifact, projectScope: false);

    public static NativeAcsComposableDomainsInspection InspectProject(NativeAcsArtifact artifact) =>
        InspectCore(artifact, projectScope: true);

    private static NativeAcsComposableDomainsInspection InspectCore(NativeAcsArtifact artifact, bool projectScope)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var diagnostics = ImmutableArray.CreateBuilder<NativeAcsDiagnostic>();

        if (artifact.ManifestBytes.IsDefaultOrEmpty ||
            artifact.ManifestBytes.Length > NativeAcsArtifactValidator.MaximumManifestBytes ||
            !artifact.ManifestBytes.AsSpan().SequenceEqual(ManifestBytes))
        {
            diagnostics.Add(new NativeAcsDiagnostic(
                "composed_manifest",
                "The composed ACS manifest does not match the reviewed profile."));
            return Invalid(diagnostics);
        }

        if (artifact.Files.IsDefault || artifact.Files.Length != (projectScope ? 4 : 5))
        {
            diagnostics.Add(new NativeAcsDiagnostic(
                "composed_file_closure",
                "The composed ACS artifact must contain the reviewed domain files."));
            return Invalid(diagnostics);
        }

        var files = new Dictionary<string, NativeAcsArtifactFile>(StringComparer.Ordinal);
        long totalBytes = artifact.ManifestBytes.Length;
        foreach (var file in artifact.Files)
        {
            if (file is null ||
                string.IsNullOrEmpty(file.RelativePath) ||
                file.Bytes.IsDefaultOrEmpty ||
                file.Bytes.Length > NativeAcsArtifactValidator.MaximumFileBytes ||
                !files.TryAdd(file.RelativePath, file))
            {
                diagnostics.Add(new NativeAcsDiagnostic(
                    "composed_file_closure",
                    "The composed ACS artifact contains an invalid or duplicate file."));
                return Invalid(diagnostics);
            }

            totalBytes += file.Bytes.Length;
        }

        if (totalBytes > NativeAcsArtifactValidator.MaximumArtifactBytes ||
            !files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
            [
                ActionsModulePath,
                NetworkModulePath,
                SelectorModulePath,
                ActionsDataPath,
                .. projectScope ? [] : new[] { NetworkDataPath }
            ]))
        {
            diagnostics.Add(new NativeAcsDiagnostic(
                "composed_file_closure",
                "The composed ACS artifact must contain only the reviewed domain files."));
            return Invalid(diagnostics);
        }

        if (!Matches(files[ActionsModulePath], ActionsModuleBytes) ||
            !Matches(files[NetworkModulePath], NetworkModuleBytes) ||
            !Matches(files[SelectorModulePath], SelectorModuleBytes))
        {
            diagnostics.Add(new NativeAcsDiagnostic(
                "composed_module",
                "Composed ACS Rego modules must match the reviewed templates."));
            return Invalid(diagnostics);
        }

        try
        {
            var actualHash = NativeAcsArtifactValidator.ComputeContentSha256(artifact);
            if (!string.Equals(actualHash, artifact.ContentSha256, StringComparison.Ordinal))
            {
                diagnostics.Add(new NativeAcsDiagnostic(
                    "artifact_hash",
                    "The ACS artifact hash does not match all supplied bytes."));
                return Invalid(diagnostics);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            diagnostics.Add(new NativeAcsDiagnostic(
                "artifact_hash",
                "The ACS artifact hash could not be verified."));
            return Invalid(diagnostics);
        }

        if (!ValidateActionsData(files[ActionsDataPath].Bytes, out var actionsCode))
        {
            diagnostics.Add(new NativeAcsDiagnostic(
                actionsCode,
                "The actions domain data does not match its closed schema.",
                ActionsDataPath));
            return Invalid(diagnostics);
        }

        NativeAcsFiniteNetworkPolicy? networkPolicy = null;
        if (!projectScope)
        {
            try
            {
                var networkJson = StrictUtf8.GetString(files[NetworkDataPath].Bytes.AsSpan());
                var networkArtifact = NativeAcsFiniteNetworkProfile.CreateArtifact(networkJson);
                var networkResult = NativeAcsFiniteNetworkProfile.Validate(networkArtifact);
                if (!networkResult.IsValid || networkResult.Policy is null)
                {
                    diagnostics.AddRange(networkResult.Diagnostics);
                    return Invalid(diagnostics);
                }

                networkPolicy = networkResult.Policy;
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(new NativeAcsDiagnostic(
                    "network_data_encoding",
                    "The network domain data must use valid UTF-8.",
                    NetworkDataPath));
                return Invalid(diagnostics);
            }
        }

        return new NativeAcsComposableDomainsInspection(
            true,
            projectScope ? [NativeAcsDomainId.Actions] : [NativeAcsDomainId.Actions, NativeAcsDomainId.PlatformNetwork],
            networkPolicy,
            []);
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
            return new NativeAcsDomainChangeClassification(false, [], beforeInspection.Diagnostics);
        if (!afterInspection.IsValid)
            return new NativeAcsDomainChangeClassification(false, [], afterInspection.Diagnostics);

        var beforeFiles = before.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var afterFiles = after.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var changedDomains = ImmutableArray.CreateBuilder<NativeAcsDomainId>();
        if (!beforeFiles[ActionsDataPath].Bytes.AsSpan().SequenceEqual(afterFiles[ActionsDataPath].Bytes.AsSpan()))
            changedDomains.Add(NativeAcsDomainId.Actions);
        if (!beforeFiles[NetworkDataPath].Bytes.AsSpan().SequenceEqual(afterFiles[NetworkDataPath].Bytes.AsSpan()))
            changedDomains.Add(NativeAcsDomainId.PlatformNetwork);

        return new NativeAcsDomainChangeClassification(true, changedDomains.ToImmutable(), []);
    }

    public static NativeAcsArtifact CreateArtifact(string actionsDataJson, string networkDataJson)
    {
        ArgumentNullException.ThrowIfNull(networkDataJson);
        return CreateArtifactCore(actionsDataJson, networkDataJson);
    }

    public static NativeAcsArtifact CreateProjectArtifact(string actionsDataJson) =>
        CreateArtifactCore(actionsDataJson, null);

    private static NativeAcsArtifact CreateArtifactCore(string actionsDataJson, string? networkDataJson)
    {
        ArgumentNullException.ThrowIfNull(actionsDataJson);
        var artifact = new NativeAcsArtifact(
            ManifestBytes.ToImmutableArray(),
            [
                new NativeAcsArtifactFile(ActionsModulePath, ActionsModuleBytes.ToImmutableArray()),
                new NativeAcsArtifactFile(ActionsDataPath, StrictUtf8.GetBytes(actionsDataJson).ToImmutableArray()),
                new NativeAcsArtifactFile(SelectorModulePath, SelectorModuleBytes.ToImmutableArray()),
                new NativeAcsArtifactFile(NetworkModulePath, NetworkModuleBytes.ToImmutableArray()),
                .. networkDataJson is null ? [] :
                    new[] { new NativeAcsArtifactFile(NetworkDataPath, StrictUtf8.GetBytes(networkDataJson).ToImmutableArray()) }
            ],
            string.Empty);
        return artifact with
        {
            ContentSha256 = NativeAcsArtifactValidator.ComputeContentSha256(artifact)
        };
    }

    internal static string GetPolicyId(NativeAcsDomainId domain) => domain switch
    {
        NativeAcsDomainId.Actions => ActionsPolicyId,
        NativeAcsDomainId.PlatformNetwork => NetworkPolicyId,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    internal static NativeAcsComposableDomainsInspection Invalid(
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics) =>
        new(false, [], null, diagnostics.ToImmutable());

    private static bool ValidateActionsData(ImmutableArray<byte> bytes, out string failureCode)
    {
        failureCode = "actions_data_json";
        try
        {
            using var document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions { MaxDepth = 32 });
            if (HasDuplicateProperties(document.RootElement))
            {
                failureCode = "actions_data_duplicate_key";
                return false;
            }

            var root = document.RootElement;
            if (!HasProperties(root, "allowed_action_ids") ||
                !root.TryGetProperty("allowed_action_ids", out var ids) ||
                ids.ValueKind != JsonValueKind.Array)
            {
                failureCode = "actions_data_shape";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in ids.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    !IsIdentifier(item.GetString()) ||
                    !seen.Add(item.GetString()!))
                {
                    failureCode = "actions_data_action_id";
                    return false;
                }
            }

            failureCode = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static (bool IsValid, string Code) ValidateActionsData(ImmutableArray<byte> bytes)
    {
        var valid = ValidateActionsData(bytes, out var code);
        return (valid, code);
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

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
        var actual = element.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        return actual.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool Matches(NativeAcsArtifactFile file, byte[] expected) =>
        file.Bytes.AsSpan().SequenceEqual(expected);

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(NativeAcsComposableDomainsProfile).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing composable domains asset: {name}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

public sealed record NativeAcsComposableDomainsInspection(
    bool IsValid,
    ImmutableArray<NativeAcsDomainId> Domains,
    NativeAcsFiniteNetworkPolicy? NetworkPolicy,
    ImmutableArray<NativeAcsDiagnostic> Diagnostics);

public sealed record NativeAcsDomainChangeClassification(
    bool IsValid,
    ImmutableArray<NativeAcsDomainId> ChangedDomains,
    ImmutableArray<NativeAcsDiagnostic> Diagnostics);
