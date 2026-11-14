using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;

namespace Agentweaver.Identity.Broker;

internal sealed record NativeRepoAppSelectionIntent(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("connectionRevision")] long? ConnectionRevision,
    [property: JsonPropertyName("installationId")] long? InstallationId,
    [property: JsonPropertyName("repositoryId")] long? RepositoryId,
    [property: JsonPropertyName("repositoryFullName")] string RepositoryFullName,
    [property: JsonPropertyName("operationId")] string OperationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NativeRepoAppSelectionPayload(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("connectionRevision")] long ConnectionRevision,
    [property: JsonPropertyName("installationId")] long InstallationId,
    [property: JsonPropertyName("repositoryId")] long RepositoryId,
    [property: JsonPropertyName("repositoryFullName")] string RepositoryFullName,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

internal static class NativeRepoAppSelectionHandoff
{
    internal const string RequestParameter = "workspace_selection_request";
    internal const string CompletionParameter = "workspace_selection_completion";
    internal const string TicketPayloadClaim = "aw:native_repo_app_selection_payload";
    internal const string TicketIntentHashClaim = "aw:native_repo_app_selection_intent_hash";
    internal const string ReviewRoute = "/consent/native-repo-app";
    internal const string ReviewPath = "/connect/consent/native-repo-app";
    private const string ProtectorPurpose = "agentweaver.identity.broker.native-repo-app-selection.v1";
    private const string NativeClientId = "agentweaver-cli";
    private const string NativeRedirectUri = "http://127.0.0.1:49173/oauth/callback";
    private const int MaximumIntentBytes = 4096;
    private const int MaximumEncodedIntentLength = 5462;
    private const int MaximumProtectedPayloadLength = 16384;

    private static readonly HashSet<string> OperationIds = new(StringComparer.Ordinal)
    {
        "listWorkspaceRepositoryRefs",
        "readWorkspaceRepositoryTree",
        "readWorkspaceRepositoryFile",
    };

    internal const int ExplicitIntentVersion = 1;
    internal const int ResolvedIntentVersion = 2;

    private static readonly string[] IntentFields =
    [
        "version", "actorId", "tenantId", "projectId", "connectionId",
        "connectionRevision", "installationId", "repositoryId", "repositoryFullName", "operationId",
    ];

    private static readonly string[] ResolvedIntentFields =
    [
        "version", "actorId", "tenantId", "projectId", "connectionId", "repositoryFullName", "operationId",
    ];

    private static readonly string[] PayloadFields =
    [
        "version", "actorId", "tenantId", "projectId", "operationId", "code", "connectionId",
        "connectionRevision", "installationId", "repositoryId", "repositoryFullName", "expiresAt",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
    };

    public static bool TryReadAuthorizationIntent(
        IQueryCollection query,
        OpenIddictRequest request,
        RequestedRunBinding? runBinding,
        out NativeRepoAppSelectionIntent? intent,
        out string? encoded)
    {
        intent = null;
        encoded = null;
        var values = query[RequestParameter];
        if (values.Count == 0) return true;
        if (values.Count != 1 || values[0] is not { } value ||
            request.ClientId != NativeClientId ||
            request.ResponseType != OpenIddictConstants.ResponseTypes.Code ||
            request.CodeChallengeMethod != OpenIddictConstants.CodeChallengeMethods.Sha256 ||
            request.CodeChallenge is not { Length: 43 } challenge ||
            challenge.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            request.HasPromptValue("none") ||
            runBinding is not null ||
            !string.Equals(request.RedirectUri, NativeRedirectUri, StringComparison.Ordinal) ||
            !HasExactNativeScopes(request.GetScopes()))
            return false;

        if (!TryDecodeIntent(value, out var parsed)) return false;
        intent = parsed;
        encoded = value;
        return true;
    }

    public static bool TryReadCompletionHandle(IQueryCollection query, out string? handle)
    {
        handle = null;
        var values = query[CompletionParameter];
        if (values.Count == 0) return true;
        if (values.Count != 1 || values[0] is not { } value || !IsOpaqueHandle(value))
            return false;
        handle = value;
        return true;
    }

    public static bool TryReadIntent(string? encoded, out NativeRepoAppSelectionIntent? intent)
    {
        if (encoded is null)
        {
            intent = null;
            return false;
        }
        return TryDecodeIntent(encoded, out intent);
    }

    public static bool IsValidReviewContinuation(
        PendingAuthorization pending,
        NativeRepoAppSelectionIntent intent,
        Guid ownerId) =>
        pending.NativeRepoAppSelectionReview &&
        pending.ClientId == NativeClientId &&
        pending.RedirectUri == NativeRedirectUri &&
        pending.SubjectUserId == ownerId &&
        pending.ProjectId is null &&
        pending.RunId is null &&
        pending.CodeChallengeMethod == OpenIddictConstants.CodeChallengeMethods.Sha256 &&
        pending.CodeChallenge is { Length: 43 } challenge &&
        challenge.All(character =>
            char.IsAsciiLetterOrDigit(character) && character != '=' || character is '-' or '_') &&
        HasExactNativeScopes((pending.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)) &&
        intent.ActorId == ownerId.ToString("D");

    public static IDataProtector CreateProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector(ProtectorPurpose);

    public static string ProtectPayload(IDataProtector protector, NativeRepoAppSelectionPayload payload)
    {
        var serialized = JsonSerializer.Serialize(payload, JsonOptions);
        var protectedPayload = protector.Protect(serialized);
        if (protectedPayload.Length > MaximumProtectedPayloadLength)
            throw new InvalidOperationException("The Workspace selection handoff could not be completed.");
        return protectedPayload;
    }

    public static bool TryUnprotectPayload(
        IDataProtector protector,
        string? protectedPayload,
        out NativeRepoAppSelectionPayload? payload)
    {
        payload = null;
        if (protectedPayload is not { Length: > 0 and <= MaximumProtectedPayloadLength }) return false;
        try
        {
            var json = protector.Unprotect(protectedPayload);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (!HasExactFields(document.RootElement, PayloadFields) ||
                JsonSerializer.Deserialize<NativeRepoAppSelectionPayload>(document.RootElement.GetRawText(), JsonOptions)
                    is not { } parsed ||
                !IsValidPayload(parsed))
                return false;
            payload = parsed;
            return true;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static bool MatchesIntent(
        NativeRepoAppSelectionPayload payload,
        NativeRepoAppSelectionIntent intent,
        Guid ownerId) =>
        IsValidIntent(intent) &&
        payload.Version == intent.Version &&
        payload.ActorId == intent.ActorId &&
        payload.ActorId == ownerId.ToString("D") &&
        payload.TenantId == intent.TenantId &&
        payload.ProjectId == intent.ProjectId &&
        payload.OperationId == intent.OperationId &&
        payload.ConnectionId == intent.ConnectionId &&
        payload.RepositoryFullName == intent.RepositoryFullName &&
        (payload.Version == ResolvedIntentVersion ||
            payload.ConnectionRevision == intent.ConnectionRevision &&
            payload.InstallationId == intent.InstallationId &&
            payload.RepositoryId == intent.RepositoryId);

    /// <summary>
    /// Rebuilds the intent that the client sent. For version 2, the client did not send the
    /// connection revision, installation ID, or repository ID.
    /// </summary>
    public static NativeRepoAppSelectionIntent RequestedIntent(NativeRepoAppSelectionPayload payload) =>
        payload.Version == ResolvedIntentVersion
            ? new(payload.Version, payload.ActorId, payload.TenantId, payload.ProjectId, payload.ConnectionId,
                null, null, null, payload.RepositoryFullName, payload.OperationId)
            : ResolvedIntent(payload);

    /// <summary>Returns a full intent with the values that the payload binds.</summary>
    public static NativeRepoAppSelectionIntent ResolvedIntent(NativeRepoAppSelectionPayload payload) =>
        new(payload.Version, payload.ActorId, payload.TenantId, payload.ProjectId, payload.ConnectionId,
            payload.ConnectionRevision, payload.InstallationId, payload.RepositoryId,
            payload.RepositoryFullName, payload.OperationId);

    public static string HashIntent(NativeRepoAppSelectionIntent intent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool TryDecodeIntent(string encoded, out NativeRepoAppSelectionIntent? intent)
    {
        intent = null;
        if (encoded.Length is 0 or > MaximumEncodedIntentLength ||
            encoded.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
            encoded.Length % 4 == 1)
            return false;

        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(encoded);
        }
        catch (FormatException)
        {
            return false;
        }
        try
        {
            if (bytes.Length is 0 or > MaximumIntentBytes ||
                !string.Equals(WebEncoders.Base64UrlEncode(bytes), encoded, StringComparison.Ordinal))
                return false;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var version) ||
                version switch
                {
                    ExplicitIntentVersion => !HasExactFields(document.RootElement, IntentFields),
                    ResolvedIntentVersion => !HasExactFields(document.RootElement, ResolvedIntentFields),
                    _ => true,
                } ||
                JsonSerializer.Deserialize<NativeRepoAppSelectionIntent>(document.RootElement.GetRawText(), JsonOptions)
                    is not { } parsed ||
                !IsValidIntent(parsed))
                return false;
            intent = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool HasExactNativeScopes(IEnumerable<string> scopes)
    {
        string[] values = scopes.ToArray();
        return values.Length == 4 &&
            values.ToHashSet(StringComparer.Ordinal).SetEquals(
                ["openid", "api.read", "offline_access", "projects.admin"]);
    }

    private static bool HasExactFields(JsonElement element, IReadOnlyCollection<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
        }
        return names.Count == required.Count && names.SetEquals(required);
    }

    private static bool IsValidIntent(NativeRepoAppSelectionIntent intent) =>
        intent.Version switch
        {
            ExplicitIntentVersion =>
                intent.ConnectionRevision > 0 && intent.InstallationId > 0 && intent.RepositoryId > 0,
            ResolvedIntentVersion =>
                intent.ConnectionRevision is null && intent.InstallationId is null && intent.RepositoryId is null,
            _ => false,
        } &&
        intent.ActorId is { } actorId && IsCanonicalGuid(actorId) &&
        intent.TenantId is { } tenantId && IsCanonicalGuid(tenantId) &&
        intent.ProjectId is { } projectId && IsCanonicalGuid(projectId) &&
        intent.ConnectionId is { } connectionId &&
        IsConnectionId(connectionId) &&
        intent.RepositoryFullName is { } repositoryFullName &&
        IsRepositoryFullName(repositoryFullName) &&
        intent.OperationId is { } operationId &&
        OperationIds.Contains(operationId);

    private static bool IsValidPayload(NativeRepoAppSelectionPayload payload) =>
        payload.Version is ExplicitIntentVersion or ResolvedIntentVersion &&
        IsCanonicalGuid(payload.ActorId) &&
        IsCanonicalGuid(payload.TenantId) &&
        IsCanonicalGuid(payload.ProjectId) &&
        OperationIds.Contains(payload.OperationId) &&
        payload.Code.Length == 64 &&
        payload.Code.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        IsConnectionId(payload.ConnectionId) &&
        payload.ConnectionRevision > 0 &&
        payload.InstallationId > 0 &&
        payload.RepositoryId > 0 &&
        IsRepositoryFullName(payload.RepositoryFullName) &&
        payload.ExpiresAt.Offset == TimeSpan.Zero;

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsOpaqueHandle(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsConnectionId(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsRepositoryFullName(string value)
    {
        if (value.Length is < 3 or > 201) return false;
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator != value.LastIndexOf('/') || separator == value.Length - 1)
            return false;
        return IsRepositoryOwner(value.AsSpan(0, separator)) &&
            IsRepositoryName(value.AsSpan(separator + 1));
    }

    private static bool IsRepositoryOwner(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 100 || value[0] == '-' || value[^1] == '-') return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-') return false;
        }
        return true;
    }

    private static bool IsRepositoryName(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 100 || value is "." or "..") return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
                return false;
        }
        return true;
    }
}
