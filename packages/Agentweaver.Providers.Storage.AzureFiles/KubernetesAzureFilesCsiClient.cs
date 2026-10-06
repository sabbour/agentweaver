using System.Collections.Immutable;
using System.Numerics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Storage.AzureFiles;

public sealed class KubernetesAzureFilesCsiClient : IAzureFilesCsiClient
{
    private readonly HttpClient _httpClient;

    public KubernetesAzureFilesCsiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null ||
            !string.Equals(_httpClient.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(_httpClient.BaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(_httpClient.BaseAddress.Query) ||
            !string.IsNullOrEmpty(_httpClient.BaseAddress.Fragment))
            throw new ArgumentException("The Kubernetes API client requires an HTTPS base address.", nameof(httpClient));
    }

    public string ClusterIdentity
    {
        get
        {
            var target = _httpClient.BaseAddress!.GetLeftPart(UriPartial.Path).TrimEnd('/');
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(target));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }

    public async Task<AzureFilesStorageClassSnapshot?> GetStorageClassAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"apis/storage.k8s.io/v1/storageclasses/{Segment(name)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await RequireSuccessAsync(response, "read-storage-class").ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var metadata = RequiredObject(root, "metadata");
        return new AzureFilesStorageClassSnapshot(
            RequiredString(metadata, "name"),
            RequiredString(root, "provisioner"),
            ReadStringArray(root, "mountOptions"));
    }

    public async Task<AzureFilesClaimSnapshot> EnsureClaimAsync(
        AzureFilesClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await GetClaimAsync(request.Namespace, request.Name, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agentweaver.dev/project-id"] = request.ProjectId,
            ["agentweaver.dev/volume-id"] = request.VolumeId,
            ["agentweaver.dev/generation"] = request.ResourceGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["agentweaver.dev/owner-kind"] = request.Owner.Kind.ToString(),
            ["agentweaver.dev/owner-id"] = request.Owner.Id
        };
        if (request.EnvironmentId is not null)
            annotations["agentweaver.dev/environment-id"] = request.EnvironmentId;

        var payload = JsonSerializer.Serialize(new
        {
            apiVersion = "v1",
            kind = "PersistentVolumeClaim",
            metadata = new
            {
                name = request.Name,
                @namespace = request.Namespace,
                annotations
            },
            spec = new
            {
                accessModes = new[] { ToKubernetesAccessMode(request.AccessMode) },
                storageClassName = request.StorageClassName,
                resources = new
                {
                    requests = new Dictionary<string, string>
                    {
                        ["storage"] = $"{request.CapacityGiB}Gi"
                    }
                }
            }
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var create = await _httpClient.PostAsync(
            $"api/v1/namespaces/{Segment(request.Namespace)}/persistentvolumeclaims",
            content,
            cancellationToken).ConfigureAwait(false);
        if (create.StatusCode == HttpStatusCode.Conflict)
        {
            var raced = await GetClaimAsync(request.Namespace, request.Name, cancellationToken)
                .ConfigureAwait(false);
            return raced ?? throw new AzureFilesCsiException(
                "claim_create_conflict",
                "Kubernetes reported a claim conflict but the existing claim could not be read.");
        }
        await RequireSuccessAsync(create, "create-claim").ConfigureAwait(false);
        using var document = await ReadDocumentAsync(create, cancellationToken).ConfigureAwait(false);
        return ReadClaim(document.RootElement);
    }

    public async Task<AzureFilesClaimSnapshot?> GetClaimAsync(
        string kubernetesNamespace,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/v1/namespaces/{Segment(kubernetesNamespace)}/persistentvolumeclaims/{Segment(name)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await RequireSuccessAsync(response, "read-claim").ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return ReadClaim(document.RootElement);
    }

    public async Task<AzureFilesPersistentVolumeSnapshot?> GetPersistentVolumeAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/v1/persistentvolumes/{Segment(name)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await RequireSuccessAsync(response, "read-persistent-volume").ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return ReadPersistentVolume(document.RootElement);
    }

    public async Task SetPersistentVolumeReclaimPolicyAsync(
        string name,
        string expectedUid,
        WorkspaceVolumeReclaimPolicy reclaimPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUid);
        var policy = reclaimPolicy switch
        {
            WorkspaceVolumeReclaimPolicy.Delete => "Delete",
            WorkspaceVolumeReclaimPolicy.Retain => "Retain",
            _ => throw new ArgumentOutOfRangeException(nameof(reclaimPolicy))
        };
        var patch = JsonSerializer.Serialize(new object[]
        {
            new { op = "test", path = "/metadata/uid", value = expectedUid },
            new { op = "replace", path = "/spec/persistentVolumeReclaimPolicy", value = policy }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"api/v1/persistentvolumes/{Segment(name)}")
        {
            Content = new StringContent(
                patch,
                System.Text.Encoding.UTF8,
                "application/json-patch+json")
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, "set-reclaim-policy").ConfigureAwait(false);
    }

    public async Task DeleteClaimAsync(
        string kubernetesNamespace,
        string name,
        string expectedUid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUid);
        var payload = JsonSerializer.Serialize(new
        {
            apiVersion = "v1",
            kind = "DeleteOptions",
            preconditions = new { uid = expectedUid }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/v1/namespaces/{Segment(kubernetesNamespace)}/persistentvolumeclaims/{Segment(name)}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await RequireSuccessAsync(response, "delete-claim").ConfigureAwait(false);
    }

    private static AzureFilesClaimSnapshot ReadClaim(JsonElement root)
    {
        var metadata = RequiredObject(root, "metadata");
        var spec = RequiredObject(root, "spec");
        var phase = root.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.Object
            ? OptionalString(status, "phase") ?? "Pending"
            : "Pending";
        var resources = RequiredObject(spec, "resources");
        var requests = RequiredObject(resources, "requests");
        return new AzureFilesClaimSnapshot(
            RequiredString(metadata, "namespace"),
            RequiredString(metadata, "name"),
            RequiredString(metadata, "uid"),
            phase,
            OptionalString(spec, "volumeName"),
            RequiredString(spec, "storageClassName"),
            ParseCapacityGiB(RequiredString(requests, "storage")),
            ReadStringArray(spec, "accessModes")
                .Select(ParseAccessMode)
                .ToImmutableArray(),
            ReadStringDictionary(metadata, "annotations"));
    }

    private static AzureFilesPersistentVolumeSnapshot ReadPersistentVolume(JsonElement root)
    {
        var metadata = RequiredObject(root, "metadata");
        var spec = RequiredObject(root, "spec");
        var claim = RequiredObject(spec, "claimRef");
        return new AzureFilesPersistentVolumeSnapshot(
            RequiredString(metadata, "name"),
            RequiredString(metadata, "uid"),
            RequiredString(spec, "storageClassName"),
            RequiredString(spec, "persistentVolumeReclaimPolicy"),
            RequiredString(claim, "namespace"),
            RequiredString(claim, "name"),
            RequiredString(claim, "uid"));
    }

    private static JsonElement RequiredObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new AzureFilesCsiException(
                "kubernetes_response_invalid",
                $"Kubernetes response is missing object '{name}'.");

    private static string RequiredString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new AzureFilesCsiException(
                "kubernetes_response_invalid",
                $"Kubernetes response is missing string '{name}'.");

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ImmutableArray<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new AzureFilesCsiException(
                    "kubernetes_response_invalid",
                    $"Kubernetes response array '{name}' contains a non-string value."))
            .ToImmutableArray();
    }

    private static ImmutableDictionary<string, string> ReadStringDictionary(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new AzureFilesCsiException(
                    "kubernetes_response_invalid",
                    $"Kubernetes response object '{name}' contains a non-string value.");
            result.Add(property.Name, property.Value.GetString()!);
        }
        return result.ToImmutable();
    }

    private static long ParseCapacityGiB(string quantity)
    {
        var suffix = new[] { "Ki", "Mi", "Gi", "Ti", "Pi", "Ei" }
            .FirstOrDefault(candidate => quantity.EndsWith(candidate, StringComparison.Ordinal));
        if (suffix is not null &&
            BigInteger.TryParse(
                quantity[..^suffix.Length],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) &&
            value > BigInteger.Zero)
        {
            var (divisor, multiplier) = suffix switch
            {
                "Ki" => (BigInteger.Pow(1024, 2), BigInteger.One),
                "Mi" => (new BigInteger(1024), BigInteger.One),
                "Gi" => (BigInteger.One, BigInteger.One),
                "Ti" => (BigInteger.One, new BigInteger(1024)),
                "Pi" => (BigInteger.One, BigInteger.Pow(1024, 2)),
                "Ei" => (BigInteger.One, BigInteger.Pow(1024, 3)),
                _ => throw new InvalidOperationException("Unsupported Kubernetes binary storage unit.")
            };
            if (value % divisor == BigInteger.Zero)
            {
                var gibibytes = value / divisor * multiplier;
                if (gibibytes <= long.MaxValue)
                    return (long)gibibytes;
            }
        }

        throw new AzureFilesCsiException(
            "kubernetes_response_invalid",
            "Kubernetes claim capacity is not an integral GiB quantity.");
    }

    private static string ToKubernetesAccessMode(WorkspaceVolumeAccessMode accessMode) => accessMode switch
    {
        WorkspaceVolumeAccessMode.ReadWriteOnce => "ReadWriteOnce",
        WorkspaceVolumeAccessMode.ReadWriteMany => "ReadWriteMany",
        WorkspaceVolumeAccessMode.ReadOnlyMany => "ReadOnlyMany",
        _ => throw new ArgumentOutOfRangeException(nameof(accessMode))
    };

    private static WorkspaceVolumeAccessMode ParseAccessMode(string accessMode) => accessMode switch
    {
        "ReadWriteOnce" => WorkspaceVolumeAccessMode.ReadWriteOnce,
        "ReadWriteMany" => WorkspaceVolumeAccessMode.ReadWriteMany,
        "ReadOnlyMany" => WorkspaceVolumeAccessMode.ReadOnlyMany,
        _ => throw new AzureFilesCsiException(
            "kubernetes_response_invalid",
            "Kubernetes claim contains an unsupported access mode.")
    };

    private static string Segment(string value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A Kubernetes resource name is required.", nameof(value))
            : value);

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new AzureFilesCsiException(
                "kubernetes_response_invalid",
                $"Kubernetes returned invalid JSON ({exception.GetType().Name}).");
        }
    }

    private static Task RequireSuccessAsync(HttpResponseMessage response, string operation) =>
        response.IsSuccessStatusCode
            ? Task.CompletedTask
            : Task.FromException(new AzureFilesKubernetesApiException(operation, response.StatusCode));
}
