using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Cli.Auth;

public sealed record NativeRepoAppSelectionIntent(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("connectionId")] string ConnectionId,
    [property: JsonPropertyName("connectionRevision")] long ConnectionRevision,
    [property: JsonPropertyName("installationId")] long InstallationId,
    [property: JsonPropertyName("repositoryId")] long RepositoryId,
    [property: JsonPropertyName("repositoryFullName")] string RepositoryFullName,
    [property: JsonPropertyName("operationId")] string OperationId)
{
    internal static string Encode(NativeRepoAppSelectionIntent intent)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
        try
        {
            if (bytes.Length > 4096)
                throw new InvalidOperationException("The Workspace selection request is too large.");
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal bool Matches(NativeRepoAppSelection selection) =>
        Version == 1 &&
        ActorId == selection.ActorId &&
        TenantId == selection.TenantId &&
        ProjectId == selection.ProjectId &&
        ConnectionId == selection.ConnectionId &&
        ConnectionRevision == selection.ConnectionRevision &&
        InstallationId == selection.InstallationId &&
        RepositoryId == selection.RepositoryId &&
        RepositoryFullName == selection.RepositoryFullName &&
        OperationId == selection.OperationId;
}

public interface INativeRepoAppSelectionHandoff
{
    Task<NativeRepoAppSelection> AcquireRepositorySelectionAsync(
        CliIdentity identity,
        NativeRepoAppSelectionIntent intent,
        InteractionPolicy interaction,
        CancellationToken ct);
}

public sealed class NativeRepoAppSelection : IDisposable
{
    private string? _code;

    internal NativeRepoAppSelection(
        string actorId,
        string tenantId,
        string projectId,
        string operationId,
        string code,
        string connectionId,
        long connectionRevision,
        long installationId,
        long repositoryId,
        string repositoryFullName,
        DateTimeOffset expiresAt)
    {
        ActorId = actorId;
        TenantId = tenantId;
        ProjectId = projectId;
        OperationId = operationId;
        _code = code;
        ConnectionId = connectionId;
        ConnectionRevision = connectionRevision;
        InstallationId = installationId;
        RepositoryId = repositoryId;
        RepositoryFullName = repositoryFullName;
        ExpiresAt = expiresAt;
    }

    public string ActorId { get; }
    public string TenantId { get; }
    public string ProjectId { get; }
    public string OperationId { get; }
    public string Code => _code ?? throw new ObjectDisposedException(nameof(NativeRepoAppSelection));
    public string ConnectionId { get; }
    public long ConnectionRevision { get; }
    public long InstallationId { get; }
    public long RepositoryId { get; }
    public string RepositoryFullName { get; }
    public DateTimeOffset ExpiresAt { get; }

    public void Dispose() => Interlocked.Exchange(ref _code, null);

    public override string ToString() => nameof(NativeRepoAppSelection) + " [REDACTED]";
}

internal static class NativeRepoAppSelectionIntentValidator
{
    private static readonly HashSet<string> OperationIds = new(StringComparer.Ordinal)
    {
        "listWorkspaceRepositoryRefs",
        "readWorkspaceRepositoryTree",
        "readWorkspaceRepositoryFile",
    };

    public static bool IsValid(NativeRepoAppSelectionIntent intent) =>
        IsCanonicalGuid(intent.ActorId) &&
        IsCanonicalGuid(intent.TenantId) &&
        IsCanonicalGuid(intent.ProjectId) &&
        IsConnectionId(intent.ConnectionId) &&
        intent.ConnectionRevision > 0 &&
        intent.InstallationId > 0 &&
        intent.RepositoryId > 0 &&
        IsRepositoryFullName(intent.RepositoryFullName) &&
        IsOperationId(intent.OperationId);

    public static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    public static bool IsConnectionId(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsOperationId(string value) => OperationIds.Contains(value);

    public static bool IsRepositoryFullName(string value)
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
        if (value.Length is 0 or > 100 || value[0] == '-' || value[^1] == '-') return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                return false;
        }
        return true;
    }

    private static bool IsRepositoryName(ReadOnlySpan<char> value)
    {
        if (value.Length is 0 or > 100 || value is "." or "..") return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return false;
        }
        return true;
    }
}
