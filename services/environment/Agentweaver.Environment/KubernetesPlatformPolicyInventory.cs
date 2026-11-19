using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Environment;

public sealed class PlatformPolicyProofException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record KubernetesPlatformPolicyRecord(
    string ApiVersion,
    string Kind,
    string Namespace,
    string Name,
    string Uid,
    string ResourceVersion,
    string CanonicalContent,
    JsonElement Content);

public sealed record KubernetesPlatformPolicyInventory(
    string PhysicalTarget,
    string Namespace,
    string NamespaceUid,
    string NetworkPolicyListResourceVersion,
    string CiliumPolicyListResourceVersion,
    string CiliumClusterwidePolicyListResourceVersion,
    ImmutableArray<KubernetesPlatformPolicyRecord> Policies,
    string SnapshotId)
{
    public KubernetesPlatformPolicyInventory Replace(
        KubernetesPlatformPolicyRecord replacement) =>
        this with
        {
            Policies = Policies
                .Where(policy => !SameIdentity(policy, replacement))
                .Append(replacement)
                .OrderBy(policy => policy.Kind, StringComparer.Ordinal)
                .ThenBy(policy => policy.Namespace, StringComparer.Ordinal)
                .ThenBy(policy => policy.Name, StringComparer.Ordinal)
                .ToImmutableArray(),
        };

    public static bool SameIdentity(
        KubernetesPlatformPolicyRecord left,
        KubernetesPlatformPolicyRecord right) =>
        left.Kind == right.Kind &&
        left.Namespace == right.Namespace &&
        left.Name == right.Name;
}

public sealed record KubernetesPlatformPolicyCapture(
    string Namespace,
    string NamespaceUid,
    long SealEpoch,
    string GuardDigest,
    string ProfileDigest,
    string EvaluatorVersion,
    string CeilingVersion,
    string CeilingHash);

public sealed class KubernetesPlatformPolicyInventoryReader(HttpClient httpClient)
{
    internal Uri? BaseAddress => httpClient.BaseAddress;
    internal const int MaxPagesPerCollection = 16;
    internal const int MaxObjects = 2_048;
    internal const int MaxBytes = 8 * 1024 * 1024;

    public async Task<KubernetesPlatformPolicyInventory> CaptureAsync(
        string physicalTarget,
        KubernetesPlatformPolicyCapture capture,
        CancellationToken cancellationToken)
    {
        Validate(capture);
        var origin = GetOrigin();
        var namespacePath =
            $"api/v1/namespaces/{Uri.EscapeDataString(capture.Namespace)}";
        using var namespaceDocument =
            await ReadDocumentAsync(namespacePath, cancellationToken).ConfigureAwait(false);
        var namespaceUid = RequiredString(namespaceDocument.RootElement, "metadata", "uid");
        if (!string.Equals(namespaceUid, capture.NamespaceUid, StringComparison.Ordinal))
            throw Failure("policy_fence_unavailable", "The target namespace UID does not match the frozen target.");

        var network = await ListAsync(
            $"apis/networking.k8s.io/v1/namespaces/{Uri.EscapeDataString(capture.Namespace)}/networkpolicies",
            "NetworkPolicy",
            cancellationToken).ConfigureAwait(false);
        var cilium = await ListAsync(
            $"apis/cilium.io/v2/namespaces/{Uri.EscapeDataString(capture.Namespace)}/ciliumnetworkpolicies",
            "CiliumNetworkPolicy",
            cancellationToken).ConfigureAwait(false);
        var clusterwide = await ListAsync(
            "apis/cilium.io/v2/ciliumclusterwidenetworkpolicies",
            "CiliumClusterwideNetworkPolicy",
            cancellationToken).ConfigureAwait(false);

        var policies = network.Objects.Concat(cilium.Objects).Concat(clusterwide.Objects)
            .OrderBy(policy => policy.Kind, StringComparer.Ordinal)
            .ThenBy(policy => policy.Namespace, StringComparer.Ordinal)
            .ThenBy(policy => policy.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        if (policies.Length > MaxObjects)
            throw Failure("policy_inventory_unavailable", "The policy inventory exceeds the supported object bound.");
        EnsureUnique(policies);

        var snapshotId = HashSnapshot(
            physicalTarget,
            capture,
            policies,
            origin);
        return new(
            physicalTarget,
            capture.Namespace,
            namespaceUid,
            network.ResourceVersion,
            cilium.ResourceVersion,
            clusterwide.ResourceVersion,
            policies,
            snapshotId);
    }

    public async Task<KubernetesPlatformPolicyRecord> GetNamedAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(
            $"apis/cilium.io/v2/namespaces/{Uri.EscapeDataString(@namespace)}/ciliumnetworkpolicies/" +
            Uri.EscapeDataString(name),
            cancellationToken).ConfigureAwait(false);
        return ReadPolicy(document.RootElement, expectedKind: "CiliumNetworkPolicy");
    }

    private async Task<ListCapture> ListAsync(
        string collectionPath,
        string itemKind,
        CancellationToken cancellationToken)
    {
        var path = collectionPath + "?limit=100";
        string? listResourceVersion = null;
        var objects = ImmutableArray.CreateBuilder<KubernetesPlatformPolicyRecord>();
        var pages = 0;
        long bytes = 0;
        while (true)
        {
            if (++pages > MaxPagesPerCollection)
                throw Failure("policy_inventory_unavailable", "The policy inventory exceeds the supported page bound.");
            var document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            using (document)
            {
                var root = document.RootElement;
                ValidateNoDuplicateProperties(root);
                if (RequiredString(root, "apiVersion") != (itemKind == "NetworkPolicy"
                        ? "networking.k8s.io/v1"
                        : "cilium.io/v2") ||
                    RequiredString(root, "kind") != itemKind + "List")
                    throw Failure("policy_inventory_unavailable", "Kubernetes returned the wrong policy list kind.");
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
                    !root.TryGetProperty("metadata", out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty("resourceVersion", out var resourceVersionElement) ||
                    resourceVersionElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(resourceVersionElement.GetString()))
                    throw Failure("policy_inventory_unavailable", "Kubernetes returned an incomplete policy list.");
                var pageResourceVersion = resourceVersionElement.GetString()!;
                listResourceVersion ??= pageResourceVersion;
                if (listResourceVersion != pageResourceVersion)
                    throw Failure("policy_inventory_unavailable", "A policy list crossed a Kubernetes resource-version boundary.");
                bytes += root.GetRawText().Length;
                if (bytes > MaxBytes || objects.Count + items.GetArrayLength() > MaxObjects)
                    throw Failure("policy_inventory_unavailable", "The policy inventory exceeds supported bounds.");
                foreach (var item in items.EnumerateArray())
                    objects.Add(ReadPolicy(item, itemKind));

                var next = metadata.TryGetProperty("continue", out var continuation)
                    ? continuation.GetString()
                    : null;
                if (string.IsNullOrEmpty(next))
                    break;
                path = collectionPath + "?limit=100&continue=" + Uri.EscapeDataString(next);
            }
        }
        return new(listResourceVersion!, objects.ToImmutable());
    }

    private static KubernetesPlatformPolicyRecord ReadPolicy(JsonElement element, string expectedKind)
    {
        ValidateNoDuplicateProperties(element);
        var apiVersion = RequiredString(element, "apiVersion");
        var kind = RequiredString(element, "kind");
        if (kind != expectedKind)
            throw Failure("policy_inventory_unavailable", $"Kubernetes returned a non-{expectedKind} item.");
        var metadata = RequiredObject(element, "metadata");
        var name = RequiredString(metadata, "name");
        var uid = RequiredString(metadata, "uid");
        var resourceVersion = RequiredString(metadata, "resourceVersion");
        var @namespace = expectedKind == "CiliumClusterwideNetworkPolicy"
            ? string.Empty
            : RequiredString(metadata, "namespace");
        if (metadata.TryGetProperty("deletionTimestamp", out var deletion) &&
            deletion.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            throw Failure("policy_inventory_unavailable", "A policy object is being deleted.");
        var expected = expectedKind switch
        {
            "NetworkPolicy" => "networking.k8s.io/v1",
            "CiliumNetworkPolicy" or "CiliumClusterwideNetworkPolicy" => "cilium.io/v2",
            _ => string.Empty,
        };
        if (apiVersion != expected)
            throw Failure("policy_inventory_unavailable", "A policy object has an unsupported API version.");
        return new(
            apiVersion,
            kind,
            @namespace,
            name,
            uid,
            resourceVersion,
            CanonicalJson(element),
            element.Clone());
    }

    private async Task<JsonDocument> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Gone or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound or
            HttpStatusCode.Unauthorized)
            throw Failure("policy_inventory_unavailable", "Kubernetes policy inventory access is unavailable.");
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest ||
            !response.IsSuccessStatusCode)
            throw Failure("policy_inventory_unavailable", "Kubernetes policy inventory access failed.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaxBytes)
            throw Failure("policy_inventory_unavailable", "A Kubernetes policy response exceeds the supported bound.");
        try
        {
            var document = JsonDocument.Parse(bytes);
            try
            {
                ValidateNoDuplicateProperties(document.RootElement);
            }
            catch
            {
                document.Dispose();
                throw;
            }
            return document;
        }
        catch (JsonException)
        {
            throw Failure("policy_inventory_unavailable", "Kubernetes returned malformed JSON.");
        }
    }

    private string GetOrigin()
    {
        var address = httpClient.BaseAddress;
        if (address is null || !address.IsAbsoluteUri || address.UserInfo.Length > 0 ||
            address.Query.Length > 0 || address.Fragment.Length > 0)
            throw Failure("policy_fence_unavailable", "The Kubernetes API target identity is unavailable.");
        return address.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            .TrimEnd('/');
    }

    private static string HashSnapshot(
        string physicalTarget,
        KubernetesPlatformPolicyCapture capture,
        ImmutableArray<KubernetesPlatformPolicyRecord> policies,
        string origin)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "agentweaver.policy-snapshot.v1");
        Add(hash, physicalTarget);
        Add(hash, origin);
        Add(hash, capture.Namespace);
        Add(hash, capture.NamespaceUid);
        Add(hash, capture.SealEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, capture.GuardDigest);
        Add(hash, capture.ProfileDigest);
        Add(hash, capture.EvaluatorVersion);
        Add(hash, capture.CeilingVersion);
        Add(hash, capture.CeilingHash);
        foreach (var policy in policies)
        {
            Add(hash, policy.ApiVersion);
            Add(hash, policy.Kind);
            Add(hash, policy.Namespace);
            Add(hash, policy.Name);
            Add(hash, policy.Uid);
            Add(hash, policy.ResourceVersion);
            Add(hash, policy.CanonicalContent);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(length, checked((ulong)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void EnsureUnique(IEnumerable<KubernetesPlatformPolicyRecord> policies)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in policies)
            if (!identities.Add($"{policy.Kind}\0{policy.Namespace}\0{policy.Name}") ||
                !tokens.Add($"{policy.Kind}\0{policy.Namespace}\0{policy.Name}\0{policy.Uid}\0{policy.ResourceVersion}"))
                throw Failure("policy_inventory_unavailable", "The policy inventory contains a duplicate object.");
    }

    private static string CanonicalJson(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
            _ => element.GetRawText(),
        };

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Failure("policy_inventory_unavailable", "A policy object contains duplicate JSON properties.");
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray())
                ValidateNoDuplicateProperties(value);
    }

    private static void Validate(KubernetesPlatformPolicyCapture capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capture.Namespace);
        if (string.IsNullOrWhiteSpace(capture.NamespaceUid) || capture.SealEpoch <= 0 ||
            !IsSha256(capture.GuardDigest) ||
            !IsSha256(capture.ProfileDigest) ||
            string.IsNullOrWhiteSpace(capture.EvaluatorVersion) ||
            string.IsNullOrWhiteSpace(capture.CeilingVersion) ||
            !IsSha256(capture.CeilingHash))
            throw Failure("policy_fence_unavailable", "The frozen policy target is incomplete.");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Failure("policy_inventory_unavailable", "A required Kubernetes object is malformed.");
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw Failure("policy_inventory_unavailable", "A required Kubernetes string is malformed.");
        return value.GetString()!;
    }

    private static string RequiredString(JsonElement parent, string objectName, string propertyName) =>
        RequiredString(RequiredObject(parent, objectName), propertyName);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static PlatformPolicyProofException Failure(string code, string message) =>
        new(code, message);

    private sealed record ListCapture(
        string ResourceVersion,
        ImmutableArray<KubernetesPlatformPolicyRecord> Objects);
}
