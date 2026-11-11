using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Policy.Acs;

/// <summary>Fixed native assets and closed data grammar for the finite-network-v1 profile.</summary>
public static class NativeAcsFiniteNetworkProfile
{
    public const string ProfileId = "finite-network-v1";

    private const string ManifestResource = "Agentweaver.Policy.Acs.FiniteNetworkV1.manifest.yaml";
    private const string ModuleResource = "Agentweaver.Policy.Acs.FiniteNetworkV1.policy.network.rego";
    private const string CatalogResource = "Agentweaver.Policy.Acs.FiniteNetworkV1.catalogs.hosted-copilot-v1.json";
    private const string ModulePath = "policy/network.rego";
    private const string DataPath = "policy/data.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] ManifestBytes = ReadAsset(ManifestResource);
    private static readonly byte[] ModuleBytes = ReadAsset(ModuleResource);
    private static readonly NativeAcsNetworkSystemCatalog TrustedCatalog = ReadCatalog();

    /// <summary>Builds a content-pinned artifact with the fixed manifest and module.</summary>
    public static NativeAcsArtifact CreateArtifact(string dataJson)
    {
        ArgumentNullException.ThrowIfNull(dataJson);
        var files = ImmutableArray.Create(
            new NativeAcsArtifactFile(ModulePath, ModuleBytes.ToImmutableArray()),
            new NativeAcsArtifactFile(DataPath, StrictUtf8.GetBytes(dataJson).ToImmutableArray()));
        var artifact = new NativeAcsArtifact(
            ManifestBytes.ToImmutableArray(),
            files,
            string.Empty);
        return artifact with
        {
            ContentSha256 = NativeAcsArtifactValidator.ComputeContentSha256(artifact)
        };
    }

    /// <summary>True when the supplied files or manifest claim the finite network profile.</summary>
    public static bool IsCandidate(NativeAcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.Files.IsDefault &&
            artifact.Files.Any(file => file?.RelativePath == ModulePath))
            return true;

        try
        {
            return !artifact.ManifestBytes.IsDefaultOrEmpty &&
                StrictUtf8.GetString(artifact.ManifestBytes.AsSpan())
                    .Contains("agentweaver_platform_network", StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Validates the artifact closure, protected catalog, and supported JSON data shape.</summary>
    public static NativeAcsFiniteNetworkValidationResult Validate(NativeAcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ManifestBytes.IsDefaultOrEmpty ||
            !artifact.ManifestBytes.AsSpan().SequenceEqual(ManifestBytes))
            return Invalid("network_manifest_mismatch");
        if (artifact.Files.IsDefault || artifact.Files.Length != 2)
            return Invalid("network_artifact_closure");

        var modules = artifact.Files.Where(file => file?.RelativePath == ModulePath).ToArray();
        var dataFiles = artifact.Files.Where(file => file?.RelativePath == DataPath).ToArray();
        if (modules.Length != 1 || dataFiles.Length != 1)
            return Invalid("network_artifact_closure");
        var module = modules[0];
        var data = dataFiles[0];
        if (module is null || data is null ||
            !module.Bytes.AsSpan().SequenceEqual(ModuleBytes) ||
            data.Bytes.IsDefaultOrEmpty)
            return Invalid("network_artifact_closure");

        string actualHash;
        try
        {
            actualHash = NativeAcsArtifactValidator.ComputeContentSha256(artifact);
        }
        catch (ArgumentException)
        {
            return Invalid("network_artifact_hash");
        }

        if (!string.Equals(actualHash, artifact.ContentSha256, StringComparison.Ordinal))
            return Invalid("network_artifact_hash");

        try
        {
            using var document = JsonDocument.Parse(
                data.Bytes.ToArray(),
                new JsonDocumentOptions { MaxDepth = 32 });
            if (HasDuplicateProperties(document.RootElement))
                return Invalid("network_data_duplicate_key");
            if (!TryReadPolicy(document.RootElement, out var policy, out var code))
                return Invalid(code);
            return new NativeAcsFiniteNetworkValidationResult(
                true,
                policy,
                ImmutableArray<NativeAcsDiagnostic>.Empty);
        }
        catch (JsonException)
        {
            return Invalid("network_data_json");
        }
        catch (DecoderFallbackException)
        {
            return Invalid("network_data_encoding");
        }
    }

    private static bool TryReadPolicy(
        JsonElement root,
        out NativeAcsFiniteNetworkPolicy policy,
        out string failureCode)
    {
        policy = null!;
        failureCode = "network_data_shape";
        if (!HasProperties(root, "agentweaver") ||
            !root.TryGetProperty("agentweaver", out var agentweaver) ||
            !HasProperties(agentweaver, "platform_egress") ||
            !agentweaver.TryGetProperty("platform_egress", out var egress) ||
            !HasProperties(egress, "profile", "mode", "approved", "system") ||
            !TryGetString(egress, "profile", out var profile))
            return false;

        if (profile != ProfileId)
        {
            failureCode = "network_profile_unsupported";
            return false;
        }

        if (!TryGetString(egress, "mode", out var modeText) ||
            modeText is not ("Allowlist" or "PublicHttps") ||
            !egress.TryGetProperty("approved", out var approvedElement) ||
            !TryReadDestinations(approvedElement, out var approved, out failureCode) ||
            !egress.TryGetProperty("system", out var system) ||
            !HasProperties(system, "catalog", "destinations") ||
            !TryGetString(system, "catalog", out var catalogRevision) ||
            !system.TryGetProperty("destinations", out var systemElement) ||
            !TryReadDestinations(systemElement, out var systemDestinations, out failureCode))
            return false;

        if (!string.Equals(catalogRevision, TrustedCatalog.Revision, StringComparison.Ordinal))
        {
            failureCode = "network_catalog_unsupported";
            return false;
        }

        if (!SameDestinations(systemDestinations, TrustedCatalog.Destinations))
        {
            failureCode = "network_catalog_mismatch";
            return false;
        }

        var seen = new HashSet<(string Host, int Port)>();
        foreach (var destination in approved.Concat(systemDestinations))
        {
            if (!seen.Add((destination.Host, destination.Port)))
            {
                failureCode = "network_destination_duplicate";
                return false;
            }
        }

        policy = new NativeAcsFiniteNetworkPolicy(
            modeText == "Allowlist" ? PlatformEgressMode.Allowlist : PlatformEgressMode.PublicHttps,
            catalogRevision,
            approved,
            systemDestinations);
        failureCode = string.Empty;
        return true;
    }

    private static bool TryReadDestinations(
        JsonElement element,
        out ImmutableArray<PlatformEgressDestination> destinations,
        out string failureCode)
    {
        destinations = [];
        failureCode = "network_data_shape";
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var result = ImmutableArray.CreateBuilder<PlatformEgressDestination>();
        foreach (var item in element.EnumerateArray())
        {
            if (!HasProperties(item, "host", "port") ||
                !TryGetString(item, "host", out var host) ||
                !item.TryGetProperty("port", out var portElement) ||
                portElement.ValueKind != JsonValueKind.Number ||
                !portElement.TryGetInt32(out var port))
                return false;

            if (PlatformEgressHostValidator.Validate(host, port) is not null)
            {
                failureCode = "network_destination_invalid";
                return false;
            }

            result.Add(new PlatformEgressDestination(host, port));
        }

        destinations = result
            .OrderBy(destination => destination.Host, StringComparer.Ordinal)
            .ThenBy(destination => destination.Port)
            .ToImmutableArray();
        return true;
    }

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

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return true;
    }

    private static bool SameDestinations(
        ImmutableArray<PlatformEgressDestination> left,
        ImmutableArray<PlatformEgressDestination> right) =>
        left.Length == right.Length &&
        left.SequenceEqual(right);

    private static NativeAcsFiniteNetworkValidationResult Invalid(string code) =>
        new(false, null, [new NativeAcsDiagnostic(code, "The finite network artifact is not supported.")]);

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(NativeAcsFiniteNetworkProfile).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing finite network asset: {name}");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static NativeAcsNetworkSystemCatalog ReadCatalog()
    {
        using var document = JsonDocument.Parse(ReadAsset(CatalogResource));
        var root = document.RootElement;
        if (!TryGetString(root, "revision", out var revision) ||
            !root.TryGetProperty("destinations", out var destinations) ||
            !TryReadDestinations(destinations, out var parsed, out _))
            throw new InvalidOperationException("The finite network system catalog is invalid.");
        return new NativeAcsNetworkSystemCatalog(revision, parsed);
    }
}

public sealed record NativeAcsFiniteNetworkPolicy(
    PlatformEgressMode Mode,
    string CatalogRevision,
    ImmutableArray<PlatformEgressDestination> ApprovedDestinations,
    ImmutableArray<PlatformEgressDestination> SystemDestinations);

public sealed record NativeAcsNetworkSystemCatalog(
    string Revision,
    ImmutableArray<PlatformEgressDestination> Destinations);

public sealed record NativeAcsFiniteNetworkValidationResult(
    bool IsValid,
    NativeAcsFiniteNetworkPolicy? Policy,
    ImmutableArray<NativeAcsDiagnostic> Diagnostics);
