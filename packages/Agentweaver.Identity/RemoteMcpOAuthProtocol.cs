using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed class RemoteMcpOAuthServerMetadata
{
    internal RemoteMcpOAuthServerMetadata(
        string bindingHash,
        Uri resource,
        Uri issuer,
        Uri authorizationEndpoint,
        Uri tokenEndpoint,
        IReadOnlyList<string>? scopesSupported)
    {
        BindingHash = bindingHash;
        Resource = resource;
        Issuer = issuer;
        AuthorizationEndpoint = authorizationEndpoint;
        TokenEndpoint = tokenEndpoint;
        ScopesSupported = scopesSupported;
    }

    public string BindingHash { get; }
    public Uri Resource { get; }
    public Uri Issuer { get; }
    public Uri AuthorizationEndpoint { get; }
    public Uri TokenEndpoint { get; }
    public IReadOnlyList<string>? ScopesSupported { get; }
}

public sealed class RemoteMcpOAuthProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class RemoteMcpOAuthProtocol
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const string InvalidMetadata = "remote_mcp_metadata_invalid";
    private const string BindingMismatch = "remote_mcp_metadata_binding_mismatch";
    private const string UnsupportedProfile = "remote_mcp_metadata_profile_unsupported";
    private const string UnapprovedEndpoint = "remote_mcp_metadata_endpoint_unapproved";
    private const string InvalidAuthorizationRequest = "remote_mcp_authorization_request_invalid";

    public static RemoteMcpOAuthServerMetadata ValidateMetadata(
        RemoteMcpOAuthConnectionBinding binding,
        ReadOnlyMemory<byte> protectedResourceJson,
        ReadOnlyMemory<byte> authorizationServerJson,
        IEnumerable<Uri> approvedOAuthEndpoints)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(approvedOAuthEndpoints);

        var allowedEndpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in approvedOAuthEndpoints)
        {
            if (endpoint is null || !IsCanonicalHttpsUri(endpoint.OriginalString, out var canonical))
                throw new RemoteMcpOAuthProtocolException(UnapprovedEndpoint);
            allowedEndpoints.Add(canonical.AbsoluteUri);
        }

        if (protectedResourceJson.Length is 0 or > MaximumMetadataBytes ||
            authorizationServerJson.Length is 0 or > MaximumMetadataBytes)
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);

        using var resourceDocument = ParseObject(protectedResourceJson);
        using var serverDocument = ParseObject(authorizationServerJson);

        var resource = ReadCanonicalUri(resourceDocument.RootElement, "resource");
        if (!string.Equals(resource.AbsoluteUri, binding.Resource.AbsoluteUri, StringComparison.Ordinal))
            throw new RemoteMcpOAuthProtocolException(BindingMismatch);

        var authorizationServers = ReadCanonicalUriArray(resourceDocument.RootElement, "authorization_servers");
        if (!authorizationServers.Contains(binding.Issuer.AbsoluteUri, StringComparer.Ordinal))
            throw new RemoteMcpOAuthProtocolException(BindingMismatch);

        var resourceScopes = ReadOptionalStringArray(resourceDocument.RootElement, "scopes_supported");
        if (resourceScopes is not null &&
            binding.Scopes.Any(scope => !resourceScopes.Contains(scope, StringComparer.Ordinal)))
            throw new RemoteMcpOAuthProtocolException(UnsupportedProfile);

        var issuer = ReadCanonicalUri(serverDocument.RootElement, "issuer");
        if (!string.Equals(issuer.AbsoluteUri, binding.Issuer.AbsoluteUri, StringComparison.Ordinal))
            throw new RemoteMcpOAuthProtocolException(BindingMismatch);

        var authorizationEndpoint = ReadCanonicalUri(serverDocument.RootElement, "authorization_endpoint");
        var tokenEndpoint = ReadCanonicalUri(serverDocument.RootElement, "token_endpoint");
        if (!allowedEndpoints.Contains(authorizationEndpoint.AbsoluteUri) ||
            !allowedEndpoints.Contains(tokenEndpoint.AbsoluteUri))
            throw new RemoteMcpOAuthProtocolException(UnapprovedEndpoint);

        var responseTypes = ReadRequiredStringArray(serverDocument.RootElement, "response_types_supported");
        var challengeMethods = ReadRequiredStringArray(
            serverDocument.RootElement, "code_challenge_methods_supported");
        if (!responseTypes.Contains("code", StringComparer.Ordinal) ||
            !challengeMethods.Contains("S256", StringComparer.Ordinal))
            throw new RemoteMcpOAuthProtocolException(UnsupportedProfile);

        var grantTypes = ReadOptionalStringArray(serverDocument.RootElement, "grant_types_supported");
        if (grantTypes is not null &&
            !grantTypes.Contains("authorization_code", StringComparer.Ordinal))
            throw new RemoteMcpOAuthProtocolException(UnsupportedProfile);
        var serverScopes = ReadOptionalStringArray(serverDocument.RootElement, "scopes_supported");
        if (serverScopes is not null &&
            binding.Scopes.Any(scope => !serverScopes.Contains(scope, StringComparer.Ordinal)))
            throw new RemoteMcpOAuthProtocolException(UnsupportedProfile);

        var supportedScopes = resourceScopes ?? serverScopes;
        return new RemoteMcpOAuthServerMetadata(
            binding.BindingHash,
            resource,
            issuer,
            authorizationEndpoint,
            tokenEndpoint,
            supportedScopes is null ? null : Array.AsReadOnly(supportedScopes));
    }

    public static Uri BuildAuthorizationRequestUri(
        RemoteMcpOAuthConnectionBinding binding,
        RemoteMcpOAuthServerMetadata metadata,
        string clientId,
        RemoteMcpOAuthConsentMaterial material)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(material);
        if (!string.Equals(metadata.BindingHash, binding.BindingHash, StringComparison.Ordinal) ||
            metadata.Resource != binding.Resource ||
            metadata.Issuer != binding.Issuer ||
            string.IsNullOrWhiteSpace(clientId) || clientId.Length > 256 ||
            clientId.Any(char.IsControl) || !material.IsFor(binding))
            throw new RemoteMcpOAuthProtocolException(InvalidAuthorizationRequest);

        var parameters = new[]
        {
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("redirect_uri", binding.RedirectUri.AbsoluteUri),
            new KeyValuePair<string, string>("scope", string.Join(' ', binding.Scopes)),
            new KeyValuePair<string, string>("state", material.State.GetValue()),
            new KeyValuePair<string, string>("code_challenge", material.PkceChallenge),
            new KeyValuePair<string, string>("code_challenge_method", "S256"),
            new KeyValuePair<string, string>("resource", binding.Resource.AbsoluteUri)
        };

        var protectedNames = parameters.Select(parameter => parameter.Key).ToHashSet(StringComparer.Ordinal);
        var existingQuery = metadata.AuthorizationEndpoint.Query.TrimStart('?');
        if (existingQuery.Length > 0 &&
            existingQuery.Split('&').Any(part =>
            {
                var encodedName = part.Split('=', 2)[0].Replace('+', ' ');
                return protectedNames.Contains(Uri.UnescapeDataString(encodedName));
            }))
            throw new RemoteMcpOAuthProtocolException(InvalidAuthorizationRequest);

        var appendedQuery = string.Join("&", parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        var builder = new UriBuilder(metadata.AuthorizationEndpoint)
        {
            Query = existingQuery.Length == 0 ? appendedQuery : $"{existingQuery}&{appendedQuery}"
        };
        return builder.Uri;
    }

    private static JsonDocument ParseObject(ReadOnlyMemory<byte> json)
    {
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
            }
            return document;
        }
        catch (JsonException)
        {
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
        }
    }

    private static Uri ReadCanonicalUri(JsonElement root, string name)
    {
        var value = ReadRequiredString(root, name);
        if (!IsCanonicalHttpsUri(value, out var uri))
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
        return uri;
    }

    private static string[] ReadCanonicalUriArray(JsonElement root, string name)
    {
        var values = ReadRequiredStringArray(root, name);
        foreach (var value in values)
        {
            if (!IsCanonicalHttpsUri(value, out var uri) ||
                !string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal))
                throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
        }
        return values;
    }

    private static string[] ReadRequiredStringArray(JsonElement root, string name) =>
        ReadStringArray(root, name, required: true)!;

    private static string[]? ReadOptionalStringArray(JsonElement root, string name) =>
        ReadStringArray(root, name, required: false);

    private static string[]? ReadStringArray(JsonElement root, string name, bool required)
    {
        if (!TryGetSingleProperty(root, name, out var value))
        {
            if (required)
                throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
            return null;
        }
        if (value.ValueKind != JsonValueKind.Array)
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(item.GetString()) ||
                item.GetString()!.Any(char.IsControl))
                throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
            values.Add(item.GetString()!);
        }
        if (values.Count == 0 || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
        return values.ToArray();
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!TryGetSingleProperty(root, name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()) ||
            value.GetString()!.Any(char.IsControl))
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
        return value.GetString()!;
    }

    private static bool TryGetSingleProperty(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals(name))
                continue;
            value = property.Value;
            count++;
        }
        if (count > 1)
            throw new RemoteMcpOAuthProtocolException(InvalidMetadata);
        return count == 1;
    }

    private static bool IsCanonicalHttpsUri(string value, out Uri uri)
    {
        if (value.Length <= 2048 &&
            Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Fragment) &&
            string.Equals(parsed.OriginalString, parsed.AbsoluteUri, StringComparison.Ordinal))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }
}
