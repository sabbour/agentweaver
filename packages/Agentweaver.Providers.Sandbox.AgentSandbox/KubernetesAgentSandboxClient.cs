using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

public sealed class KubernetesAgentSandboxClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public KubernetesAgentSandboxClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null ||
            !string.Equals(_httpClient.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(_httpClient.BaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(_httpClient.BaseAddress.Query) ||
            !string.IsNullOrEmpty(_httpClient.BaseAddress.Fragment))
            throw new ArgumentException("The Kubernetes API client requires an HTTPS base address.",
                nameof(httpClient));
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

    public async Task<JsonElement?> GetAsync(
        string apiGroup,
        string resource,
        string kubernetesNamespace,
        string name,
        CancellationToken cancellationToken = default,
        string? apiVersion = null)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, name, apiVersion),
            null,
            "read-resource",
            effectMayHaveApplied: false,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await RequireSuccessAsync(response, "read-resource", false).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: false, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async Task<JsonElement?> GetClusterAsync(
        string apiGroup,
        string apiVersion,
        string resource,
        string name,
        CancellationToken cancellationToken = default)
    {
        var path = ClusterResourcePath(apiGroup, apiVersion, resource, name);
        using var response = await SendAsync(
            HttpMethod.Get,
            path,
            null,
            "read-cluster-resource",
            effectMayHaveApplied: false,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await RequireSuccessAsync(response, "read-cluster-resource", false).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: false, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async Task<KubernetesApiServerVersion> ReadApiServerVersionAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/version",
            null,
            "read-version",
            effectMayHaveApplied: false,
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, "read-version", false).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: false, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("major", out var majorElement) ||
            majorElement.ValueKind != JsonValueKind.String ||
            !int.TryParse(majorElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !root.TryGetProperty("minor", out var minorElement) ||
            minorElement.ValueKind != JsonValueKind.String ||
            minorElement.GetString() is not { } minorText)
            throw new SandboxProviderException(
                "kubernetes_version_invalid",
                "Kubernetes returned an invalid API-server version.",
                effectMayHaveApplied: false);
        var minorDigits = new string(minorText.TakeWhile(char.IsAsciiDigit).ToArray());
        if (major < 1 ||
            !int.TryParse(minorDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !root.TryGetProperty("gitVersion", out var gitVersionElement) ||
            gitVersionElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(gitVersionElement.GetString()))
            throw new SandboxProviderException(
                "kubernetes_version_invalid",
                "Kubernetes returned an invalid API-server version.",
                effectMayHaveApplied: false);
        return new KubernetesApiServerVersion(major, minor, gitVersionElement.GetString()!);
    }

    public async Task<JsonElement> DryRunCreatePodAsync(
        string kubernetesNamespace,
        JsonElement body,
        string schedulingGateName,
        CancellationToken cancellationToken = default)
    {
        ValidatePathSegment(kubernetesNamespace, nameof(kubernetesNamespace));
        ArgumentNullException.ThrowIfNull(schedulingGateName);
        var version = await ReadApiServerVersionAsync(cancellationToken).ConfigureAwait(false);
        if (!version.SupportsPodSchedulingGates)
            throw new SandboxProviderException(
                "kubernetes_scheduling_gates_unsupported",
                "The Kubernetes API server does not support Pod scheduling gates.",
                effectMayHaveApplied: false);

        using var content = JsonContent(body);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"{NamespacedResourcePath(string.Empty, "pods", kubernetesNamespace, null)}?dryRun=All",
            content,
            "dry-run-create-pod",
            effectMayHaveApplied: false,
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, "dry-run-create-pod", false).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: false, cancellationToken).ConfigureAwait(false);
        var admitted = document.RootElement;
        if (!TryGetSchedulingGates(admitted, out var gates) ||
            gates.Length != 1 ||
            !string.Equals(gates[0], schedulingGateName, StringComparison.Ordinal))
            throw new SandboxProviderException(
                "kubernetes_scheduling_gate_rejected",
                "The Kubernetes API server did not retain the requested Pod scheduling gate.",
                effectMayHaveApplied: false);
        return admitted.Clone();
    }

    public async Task<JsonElement> CreateAsync(
        string apiGroup,
        string resource,
        string kubernetesNamespace,
        JsonElement body,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent(body);
        using var response = await SendAsync(
            HttpMethod.Post,
            NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, null),
            content,
            "create-resource",
            effectMayHaveApplied: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new SandboxProviderException(
                "kubernetes_conflict",
                "Kubernetes reported a resource creation conflict.",
                effectMayHaveApplied: true);
        await RequireSuccessAsync(response, "create-resource", true).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: true, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async Task<IReadOnlyList<JsonElement>> ListAsync(
        string apiGroup,
        string resource,
        string kubernetesNamespace,
        ImmutableDictionary<string, string> labelSelector,
        CancellationToken cancellationToken = default,
        string? fieldSelector = null,
        string? apiVersion = null)
    {
        ArgumentNullException.ThrowIfNull(labelSelector);
        if (fieldSelector is { Length: > 1024 } || fieldSelector?.Any(char.IsControl) == true)
            throw new ArgumentException("Kubernetes field selectors must be bounded and contain no control characters.",
                nameof(fieldSelector));
        var path = NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, null, apiVersion);
        var selectors = new List<string>(2);
        if (labelSelector.Count > 0)
        {
            var selector = string.Join(
                ",",
                labelSelector.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
            selectors.Add($"labelSelector={Uri.EscapeDataString(selector)}");
        }
        if (!string.IsNullOrWhiteSpace(fieldSelector))
            selectors.Add($"fieldSelector={Uri.EscapeDataString(fieldSelector)}");
        if (selectors.Count > 0)
            path = $"{path}?{string.Join("&", selectors)}";

        using var response = await SendAsync(
            HttpMethod.Get,
            path,
            null,
            "list-resources",
            effectMayHaveApplied: false,
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, "list-resources", false).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: false, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new SandboxProviderException(
                "kubernetes_response_invalid",
                "Kubernetes returned an invalid resource list.",
                effectMayHaveApplied: false);
        return items.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public async Task DeleteAsync(
        string apiGroup,
        string resource,
        string kubernetesNamespace,
        string name,
        string expectedUid,
        bool foreground,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedUid))
            throw new ArgumentException("A Kubernetes resource UID is required.", nameof(expectedUid));
        var body = JsonSerializer.SerializeToElement(new
        {
            apiVersion = "v1",
            kind = "DeleteOptions",
            propagationPolicy = foreground ? "Foreground" : "Background",
            preconditions = new { uid = expectedUid }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, name))
        {
            Content = JsonContent(body)
        };
        using var response = await SendAsync(
            request,
            "delete-resource",
            effectMayHaveApplied: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await RequireSuccessAsync(response, "delete-resource", true).ConfigureAwait(false);
    }

    public async Task SetAnnotationAsync(
        string apiGroup,
        string resource,
        string kubernetesNamespace,
        string name,
        string expectedUid,
        string annotationName,
        string annotationValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedUid))
            throw new ArgumentException("A Kubernetes resource UID is required.", nameof(expectedUid));
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationName);
        ArgumentNullException.ThrowIfNull(annotationValue);
        var patch = JsonSerializer.SerializeToElement(new object[]
        {
            new { op = "test", path = "/metadata/uid", value = expectedUid },
            new { op = "add", path = $"/metadata/annotations/{EscapeJsonPointer(annotationName)}", value = annotationValue }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, name))
        {
            Content = JsonContent(patch, "application/json-patch+json")
        };
        using var response = await SendAsync(
            request,
            "patch-resource",
            effectMayHaveApplied: true,
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, "patch-resource", true).ConfigureAwait(false);
    }

    public async Task<JsonElement> RemovePodSchedulingGateAsync(
        SandboxBuildTestPodReference pod,
        string gateName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        _ = pod.Validate();
        ValidateBoundedIdentifier(gateName, nameof(gateName), 128);
        var patch = JsonSerializer.SerializeToElement(new object[]
        {
            new { op = "test", path = "/metadata/uid", value = pod.Uid },
            new { op = "test", path = "/metadata/resourceVersion", value = pod.ResourceVersion },
            new { op = "test", path = "/spec/schedulingGates/0/name", value = gateName },
            new { op = "remove", path = "/spec/schedulingGates/0" }
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            NamespacedResourcePath(string.Empty, "pods", pod.KubernetesNamespace, pod.Name))
        {
            Content = JsonContent(patch, "application/json-patch+json")
        };
        using var response = await SendAsync(
            request,
            "release-pod-scheduling-gate",
            effectMayHaveApplied: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict ||
            response.StatusCode == HttpStatusCode.UnprocessableEntity)
            throw new SandboxProviderException(
                "kubernetes_pod_gate_precondition_failed",
                "The Pod UID, resource version, or scheduling gate changed before release.",
                effectMayHaveApplied: false);
        await RequireSuccessAsync(response, "release-pod-scheduling-gate", true).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(
            response, effectMayHaveApplied: true, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    public async Task<SandboxBuildTestOutputCapture> ReadPodLogsBoundedAsync(
        SandboxBuildTestPodReference pod,
        string containerName,
        int maximumOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        _ = pod.Validate();
        ValidateBoundedIdentifier(containerName, nameof(containerName), 128);
        if (maximumOutputBytes is < 1 or > SandboxBuildTestLimits.MaximumLogOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));

        var current = await GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false);
        RequirePodUid(current, pod.Uid);
        var path = $"{NamespacedResourcePath(string.Empty, "pods", pod.KubernetesNamespace, pod.Name)}/log" +
            $"?container={Uri.EscapeDataString(containerName)}&follow=true&timestamps=false";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendStreamingAsync(
            request, "read-pod-logs", effectMayHaveApplied: false, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response, "read-pod-logs", false).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var captured = new MemoryStream(maximumOutputBytes);
        var buffer = new byte[Math.Min(8192, maximumOutputBytes + 1)];
        long observedBytes = 0;
        while (true)
        {
            var remaining = maximumOutputBytes - (int)captured.Length;
            var readLimit = Math.Min(buffer.Length, remaining + 1);
            var read = await input.ReadAsync(buffer.AsMemory(0, readLimit), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            observedBytes += read;
            var capturedRead = Math.Min(read, remaining);
            if (capturedRead > 0)
                captured.Write(buffer, 0, capturedRead);
            if (read > capturedRead)
                break;
        }

        var verified = await GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false);
        RequirePodUid(verified, pod.Uid);
        var bytes = captured.ToArray();
        return new SandboxBuildTestOutputCapture(
            pod.Uid,
            containerName,
            bytes.ToImmutableArray(),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            observedBytes > bytes.LongLength,
            observedBytes);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string operation,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        return await SendAsync(request, operation, effectMayHaveApplied, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendStreamingAsync(
        HttpRequestMessage request,
        string operation,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new SandboxProviderException(
                "kubernetes_transport_error",
                $"Kubernetes {operation} request failed.",
                effectMayHaveApplied);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new SandboxProviderException(
                "kubernetes_transport_error",
                $"Kubernetes {operation} request failed.",
                effectMayHaveApplied);
        }
    }

    private static Task RequireSuccessAsync(
        HttpResponseMessage response,
        string operation,
        bool effectMayHaveApplied) =>
        response.IsSuccessStatusCode
            ? Task.CompletedTask
            : Task.FromException(new SandboxProviderException(
                "kubernetes_api_error",
                $"Kubernetes {operation} request failed with HTTP {(int)response.StatusCode}.",
                effectMayHaveApplied));

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new SandboxProviderException(
                "kubernetes_response_too_large",
                "Kubernetes returned a response larger than the configured limit.",
                effectMayHaveApplied);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > MaximumResponseBytes)
                throw new SandboxProviderException(
                    "kubernetes_response_too_large",
                    "Kubernetes returned a response larger than the configured limit.",
                    effectMayHaveApplied);
            output.Write(buffer, 0, read);
        }

        try
        {
            return JsonDocument.Parse(output.ToArray());
        }
        catch (JsonException)
        {
            throw new SandboxProviderException(
                "kubernetes_response_invalid",
                "Kubernetes returned invalid JSON.",
                effectMayHaveApplied);
        }
    }

    private static string NamespacedResourcePath(
        string apiGroup,
        string resource,
        string kubernetesNamespace,
        string? name,
        string? apiVersion = null)
    {
        ValidatePathSegment(resource, nameof(resource));
        ValidatePathSegment(kubernetesNamespace, nameof(kubernetesNamespace));
        var prefix = string.IsNullOrEmpty(apiGroup)
            ? $"api/{ValidatePathSegment(apiVersion ?? "v1", nameof(apiVersion))}"
            : $"apis/{ValidateApiGroup(apiGroup)}/{ValidatePathSegment(apiVersion ?? "v1beta1", nameof(apiVersion))}";
        var path = $"{prefix}/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/{resource}";
        return name is null
            ? path
            : $"{path}/{Uri.EscapeDataString(ValidatePathSegment(name, nameof(name)))}";
    }

    private static string ClusterResourcePath(
        string apiGroup,
        string apiVersion,
        string resource,
        string name)
    {
        ValidatePathSegment(apiVersion, nameof(apiVersion));
        ValidatePathSegment(resource, nameof(resource));
        ValidatePathSegment(name, nameof(name));
        var prefix = string.IsNullOrEmpty(apiGroup)
            ? $"api/{apiVersion}"
            : $"apis/{ValidateApiGroup(apiGroup)}/{apiVersion}";
        return $"{prefix}/{resource}/{Uri.EscapeDataString(name)}";
    }

    private static string ValidateApiGroup(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
            throw new ArgumentException("A Kubernetes API group is invalid.", nameof(value));
        return value;
    }

    private static string ValidatePathSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 ||
            value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("A Kubernetes resource path segment is invalid.", name);
        return value;
    }

    private static string EscapeJsonPointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static StringContent JsonContent(JsonElement body, string mediaType = "application/json") =>
        new(body.GetRawText(), Encoding.UTF8, mediaType);

    private static bool TryGetSchedulingGates(JsonElement pod, out string[] gates)
    {
        gates = [];
        if (!pod.TryGetProperty("spec", out var spec) ||
            !spec.TryGetProperty("schedulingGates", out var gateArray) ||
            gateArray.ValueKind != JsonValueKind.Array)
            return false;
        var values = new List<string>();
        foreach (var gate in gateArray.EnumerateArray())
        {
            if (!gate.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String ||
                name.GetString() is not { } value)
                return false;
            values.Add(value);
        }
        gates = values.ToArray();
        return true;
    }

    private static void ValidateBoundedIdentifier(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded Kubernetes identifier is required.", name);
    }

    private static void RequirePodUid(JsonElement? pod, string expectedUid)
    {
        if (pod is not { } value ||
            !value.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("uid", out var uid) ||
            uid.ValueKind != JsonValueKind.String ||
            !string.Equals(uid.GetString(), expectedUid, StringComparison.Ordinal))
            throw new SandboxProviderException(
                "kubernetes_pod_uid_mismatch",
                "The Pod name no longer identifies the expected Pod UID.",
                effectMayHaveApplied: false);
    }
}

public sealed record KubernetesApiServerVersion(int Major, int Minor, string GitVersion)
{
    public bool SupportsPodSchedulingGates => Major > 1 || Major == 1 && Minor >= 30;
}
