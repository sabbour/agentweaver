using System.Collections.Immutable;
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
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, name),
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
        string? fieldSelector = null)
    {
        ArgumentNullException.ThrowIfNull(labelSelector);
        if (fieldSelector is { Length: > 1024 } || fieldSelector?.Any(char.IsControl) == true)
            throw new ArgumentException("Kubernetes field selectors must be bounded and contain no control characters.",
                nameof(fieldSelector));
        var path = NamespacedResourcePath(apiGroup, resource, kubernetesNamespace, null);
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
        string? name)
    {
        ValidatePathSegment(resource, nameof(resource));
        ValidatePathSegment(kubernetesNamespace, nameof(kubernetesNamespace));
        var prefix = string.IsNullOrEmpty(apiGroup)
            ? "api/v1"
            : $"apis/{ValidateApiGroup(apiGroup)}/v1beta1";
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
}
