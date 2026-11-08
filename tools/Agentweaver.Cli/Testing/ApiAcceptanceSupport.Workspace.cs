using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli;

internal static partial class ApiAcceptanceSupport
{
    private static readonly string[] WorkspaceFields =
    [
        "projectId", "expectedProjectRevision", "connectionId", "connectionRevision",
        "installationId", "repositoryId", "repositoryFullName", "refKind", "refName",
        "commitSha", "filePath", "expectedByteLength", "contentSha256",
    ];

    public static void ValidateWorkspaceFixturePlan(JsonObject plan, JsonObject environment)
    {
        RequireExactKeys(plan, ["workspace"], "Workspace fixture plan");
        JsonObject workspace = RequireObject(plan["workspace"], "Workspace read assertions are required.");
        RequireExactKeys(workspace, WorkspaceFields, "Workspace read assertions");

        string projectId = RequireString(workspace, "projectId", "Workspace project ID");
        string connectionId = RequireString(workspace, "connectionId", "Workspace connection ID");
        string repositoryFullName = RequireString(workspace, "repositoryFullName", "Workspace repository");
        string refKind = RequireString(workspace, "refKind", "Workspace ref kind");
        string refName = RequireString(workspace, "refName", "Workspace ref name");
        string commitSha = RequireString(workspace, "commitSha", "Workspace commit");
        string filePath = RequireString(workspace, "filePath", "Workspace file path");
        string contentSha256 = RequireString(workspace, "contentSha256", "Workspace content digest");

        if (!IsCanonicalGuid(projectId) ||
            !TryLong(workspace["expectedProjectRevision"], out long projectRevision) || projectRevision < 1 ||
            !IsConnectionId(connectionId) ||
            !TryLong(workspace["connectionRevision"], out long connectionRevision) || connectionRevision < 1 ||
            !TryLong(workspace["installationId"], out long installationId) || installationId < 1 ||
            !TryLong(workspace["repositoryId"], out long repositoryId) || repositoryId < 1 ||
            !IsRepositoryFullName(repositoryFullName) ||
            refKind is not ("branch" or "tag") ||
            !IsGitRefName(refName) ||
            !IsLowerHex(commitSha, 40) ||
            !IsNormalizedRepositoryPath(filePath) ||
            !TryLong(workspace["expectedByteLength"], out long byteLength) ||
            byteLength is < 0 or > 1024 * 1024 ||
            !IsLowerHex(contentSha256, 64) ||
            !IsCanonicalGuid(GetString(environment, "actorId") ?? string.Empty) ||
            !IsCanonicalGuid(GetString(environment, "tenantId") ?? string.Empty))
            throw new InvalidDataException("The Workspace read assertions are invalid.");
    }

    public static JsonObject WorkspaceResult(
        string featureStatus,
        JsonObject? sourceEvidence,
        IEnumerable<JsonObject> steps,
        JsonObject observations,
        string? failure) => new()
    {
        ["kind"] = "workspace-acceptance-result",
        ["schemaVersion"] = 1,
        ["surface"] = "api",
        ["featureStatus"] = featureStatus,
        ["fullP1Status"] = "not-evaluated",
        ["exitCode"] = featureStatus switch { "passed" => 0, "failed" => 1, _ => 2 },
        ["sourceEvidence"] = sourceEvidence is null ? null : Redact(sourceEvidence),
        ["steps"] = ToArray(steps),
        ["observations"] = Redact(observations),
        ["failure"] = string.IsNullOrWhiteSpace(failure) ? null : new JsonObject { ["message"] = RedactText(failure) },
    };

    internal static string Sha1GitBlobHex(ReadOnlySpan<byte> content)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        byte[] input = new byte[prefix.Length + content.Length];
        prefix.CopyTo(input, 0);
        content.CopyTo(input.AsSpan(prefix.Length));
        try { return Convert.ToHexStringLower(SHA1.HashData(input)); }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsConnectionId(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsRepositoryFullName(string value)
    {
        if (value.Length is < 3 or > 201) return false;
        var slash = value.IndexOf('/');
        if (slash < 1 || slash != value.LastIndexOf('/') || slash == value.Length - 1)
            return false;
        string owner = value[..slash];
        string name = value[(slash + 1)..];
        return owner.Length <= 100 && name.Length <= 100 &&
            owner.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
            name is not ("." or "..") &&
            name.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool IsGitRefName(string value)
    {
        if (value.Length is < 1 or > 255 ||
            value.StartsWith('/') || value.EndsWith('/') ||
            value.StartsWith('.') || value.EndsWith('.') ||
            value.EndsWith(".lock", StringComparison.Ordinal) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character) ||
                character is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            return false;
        return value.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..") &&
            segment[0] != '.' && segment[^1] != '.' &&
            !segment.EndsWith(".lock", StringComparison.Ordinal));
    }

    private static bool IsNormalizedRepositoryPath(string value)
    {
        if (value.Length is < 1 or > 4096 ||
            value.StartsWith('/') || value.EndsWith('/') ||
            value.Contains('\\') ||
            value.Any(char.IsControl) ||
            !value.IsNormalized(NormalizationForm.FormC))
            return false;
        return value.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
