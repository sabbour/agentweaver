using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

/// <summary>Whether a project marketplace source is currently available for browsing.</summary>
public enum ProjectMarketplaceSourceState
{
    Active,
    Removed
}

/// <summary>Revisioned GitHub source settings stored under one project.</summary>
public sealed record ProjectMarketplaceSourceRecord
{
    public required string ProjectId { get; init; }
    public Guid SourceId { get; init; }
    public required string Name { get; init; }
    public required string NormalizedName { get; init; }
    public required string Repository { get; init; }
    public required string RequestedRef { get; init; }
    public string? Subpath { get; init; }
    public long Revision { get; init; }
    public ProjectMarketplaceSourceState State { get; init; }
    public required string CreatedByActorId { get; init; }
    public required string UpdatedByActorId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Normalized name, repository, requested ref, and optional subpath ready for persistence.</summary>
public sealed record ProjectMarketplaceSourceDefinition(
    string Name,
    string Repository,
    string RequestedRef,
    string? Subpath);

/// <summary>Strict request to create a marketplace source; omitted name/ref use repository and main defaults.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateMarketplaceSourceRequest
{
    public string? Name { get; init; }
    public required string Repository { get; init; }
    public string? RequestedRef { get; init; }
    public string? Subpath { get; init; }
}

/// <summary>Strict request to replace source settings at an expected revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateMarketplaceSourceRequest
{
    public required long ExpectedRevision { get; init; }
    public string? Name { get; init; }
    public required string Repository { get; init; }
    public string? RequestedRef { get; init; }
    public string? Subpath { get; init; }
}

/// <summary>Public source metadata; omits actor IDs and persistence-only normalized fields.</summary>
public sealed record MarketplaceSourceView(
    Guid SourceId,
    string Name,
    string Repository,
    string RequestedRef,
    string? Subpath,
    long Revision,
    ProjectMarketplaceSourceState State)
{
    /// <summary>Projects a stored source into its API representation.</summary>
    public static MarketplaceSourceView From(ProjectMarketplaceSourceRecord source) =>
        new(
            source.SourceId,
            source.Name,
            source.Repository,
            source.RequestedRef,
            source.Subpath,
            source.Revision,
            source.State);
}

/// <summary>Bounded browse query tied to the source revision that the user inspected.</summary>
public sealed record MarketplaceBrowseRequest
{
    public required long ExpectedSourceRevision { get; init; }
    public string? Query { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = SkillMarketplaceBrowseService.DefaultPageSize;
}

/// <summary>A discoverable skill manifest location and its optional frontmatter description.</summary>
public sealed record MarketplaceBrowseCandidate(
    string Location,
    string Name,
    string? Description);

/// <summary>One source revision resolved to an exact commit and paged skill-manifest candidates.</summary>
public sealed record MarketplaceBrowsePage(
    Guid SourceId,
    long SourceRevision,
    string RequestedRef,
    string ResolvedCommitSha,
    IReadOnlyList<MarketplaceBrowseCandidate> Candidates,
    int Total,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>Request to preview one selected path at an exact source revision and commit.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketplaceSkillPreviewRequest
{
    public required long ExpectedSourceRevision { get; init; }
    public required string ResolvedCommitSha { get; init; }
    public required string SelectedPath { get; init; }
}

/// <summary>Request to import one selected path with a content digest, idempotency key, and optional skill revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarketplaceSkillImportRequest
{
    public required long ExpectedSourceRevision { get; init; }
    public required string ResolvedCommitSha { get; init; }
    public required string SelectedPath { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string ExpectedContentDigest { get; init; }
    public string? SkillId { get; init; }
    public long? ExpectedRevision { get; init; }
}

/// <summary>Persistence boundary for project marketplace source records and revision-fenced updates.</summary>
public interface IProjectMarketplaceSourceStore
{
    /// <summary>Lists sources for one project in deterministic order.</summary>
    Task<IReadOnlyList<ProjectMarketplaceSourceRecord>> ListByProjectAsync(
        string projectId,
        bool includeRemoved,
        CancellationToken cancellationToken);

    /// <summary>Reads one source by its project and source IDs.</summary>
    Task<ProjectMarketplaceSourceRecord?> GetAsync(
        string projectId,
        Guid sourceId,
        CancellationToken cancellationToken);

    /// <summary>Creates a source and enforces active-name uniqueness in the database.</summary>
    Task<ProjectMarketplaceSourceRecord> CreateAsync(
        ProjectMarketplaceSourceRecord source,
        CancellationToken cancellationToken);

    /// <summary>Updates a source only if its current active revision equals the supplied revision.</summary>
    Task<ProjectMarketplaceSourceRecord> UpdateAsync(
        string projectId,
        Guid sourceId,
        long expectedRevision,
        ProjectMarketplaceSourceDefinition definition,
        string actorId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    /// <summary>Creates a removed-state revision only when the expected source revision is current.</summary>
    Task<ProjectMarketplaceSourceRecord> RemoveAsync(
        string projectId,
        Guid sourceId,
        long expectedRevision,
        string actorId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}

/// <summary>Stable marketplace error code and HTTP status for request, authority, persistence, or upstream failures.</summary>
public sealed class MarketplaceSourceException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    /// <summary>Creates the not-found response used for absent, foreign, or removed sources.</summary>
    public static MarketplaceSourceException NotFound() =>
        new("marketplace_source_not_found", "The marketplace source was not found.", StatusCodes.Status404NotFound);

    /// <summary>Creates a conflict when the caller's source revision is no longer current.</summary>
    public static MarketplaceSourceException RevisionConflict() =>
        new("marketplace_source_revision_conflict", "The marketplace source changed; refresh it and try again.", StatusCodes.Status409Conflict);

    /// <summary>Creates a conflict for a duplicate active source name within one project.</summary>
    public static MarketplaceSourceException NameConflict() =>
        new("marketplace_source_name_conflict", "A marketplace source with that name already exists in this project.", StatusCodes.Status409Conflict);

    /// <summary>Creates a bad-request response for invalid source input.</summary>
    public static MarketplaceSourceException InvalidRequest(string message) =>
        new("invalid_marketplace_request", message, StatusCodes.Status400BadRequest);

    /// <summary>Creates a forbidden response when current project authority is missing.</summary>
    public static MarketplaceSourceException Forbidden() =>
        new("forbidden", "The authenticated caller is not authorized for this operation.", StatusCodes.Status403Forbidden);

    /// <summary>Creates a temporary-unavailable response for a failed upstream read.</summary>
    public static MarketplaceSourceException Unavailable() =>
        new("marketplace_source_unavailable", "The marketplace source is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);

    /// <summary>Creates a gateway-timeout response when the bounded upstream read expires.</summary>
    public static MarketplaceSourceException TimedOut() =>
        new("marketplace_source_timeout", "The marketplace source did not respond before the operation timed out.", StatusCodes.Status504GatewayTimeout);

    /// <summary>Creates a bad-gateway response for malformed or inconsistent upstream data.</summary>
    public static MarketplaceSourceException InvalidUpstream(string message) =>
        new("invalid_marketplace_response", message, StatusCodes.Status502BadGateway);

    /// <summary>Creates an unprocessable response when a repository exceeds supported browse/import bounds.</summary>
    public static MarketplaceSourceException UnsupportedSourceSize() =>
        new("marketplace_source_too_large", "The marketplace source exceeds the supported browse limits.", StatusCodes.Status422UnprocessableEntity);
}

/// <summary>Validates and canonicalizes the source name, HTTPS GitHub repository, ref, and safe relative subpath.</summary>
public static partial class MarketplaceSourceDefinition
{
    private static readonly Regex NamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepositoryPartPattern = new(
        "^[A-Za-z0-9_.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Applies defaults and validates every stored source field before persistence.</summary>
    public static ProjectMarketplaceSourceDefinition Normalize(
        string? name,
        string? repository,
        string? requestedRef,
        string? subpath)
    {
        if (!TryParseRepository(repository, out var owner, out var repo))
            throw MarketplaceSourceException.InvalidRequest(
                "Repository must be a GitHub repository in owner/repository or https://github.com/owner/repository form.");

        var normalizedName = string.IsNullOrWhiteSpace(name) ? repo : name.Trim();
        if (!NamePattern.IsMatch(normalizedName))
            throw MarketplaceSourceException.InvalidRequest(
                "Source name must be 1-64 characters of letters, digits, spaces, '.', '_' or '-'.");

        var normalizedRef = string.IsNullOrWhiteSpace(requestedRef) ? "main" : requestedRef.Trim();
        if (!IsValidRef(normalizedRef))
            throw MarketplaceSourceException.InvalidRequest("The requested Git ref is invalid.");

        var normalizedSubpath = NormalizeSubpath(subpath);
        return new ProjectMarketplaceSourceDefinition(
            normalizedName,
            $"{owner}/{repo}",
            normalizedRef,
            normalizedSubpath);
    }

    /// <summary>Accepts only a GitHub owner/repository path or canonical HTTPS GitHub repository URL.</summary>
    public static bool TryParseRepository(string? input, out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var value = input.Trim();
        string path;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Port != 443 ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                return false;
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            path = value.Trim('/');
        }

        var parts = path.Split('/');
        if (parts.Length != 2)
            return false;
        owner = parts[0];
        repository = parts[1];
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];
        if (!IsRepositoryPart(owner) || !IsRepositoryPart(repository))
        {
            owner = string.Empty;
            repository = string.Empty;
            return false;
        }

        owner = owner.ToLowerInvariant();
        repository = repository.ToLowerInvariant();
        return true;
    }

    /// <summary>Returns null for an omitted path and rejects traversal, separators, control characters, and oversized paths.</summary>
    public static string? NormalizeSubpath(string? subpath)
    {
        if (string.IsNullOrWhiteSpace(subpath))
            return null;

        var normalized = subpath.Trim().Trim('/');
        var parts = normalized.Split('/');
        if (normalized.Length > 1024 ||
            normalized.Contains('\\') ||
            normalized.Any(char.IsControl) ||
            parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw MarketplaceSourceException.InvalidRequest("The source subpath must be a safe relative path.");
        return normalized;
    }

    /// <summary>Checks the requested Git ref against the supported conservative character and component rules.</summary>
    public static bool IsValidRef(string requestedRef)
    {
        if (string.IsNullOrWhiteSpace(requestedRef) ||
            requestedRef.Length > 255 ||
            requestedRef.StartsWith('/') ||
            requestedRef.EndsWith('/') ||
            requestedRef.Contains("//", StringComparison.Ordinal) ||
            requestedRef.Contains("..", StringComparison.Ordinal) ||
            requestedRef.Contains("@{", StringComparison.Ordinal) ||
            requestedRef.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or '/')))
            return false;

        return requestedRef.Split('/').All(part =>
            part.Length > 0 &&
            !part.StartsWith('.') &&
            !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRepositoryPart(string value) =>
        value.Length is > 0 and <= 100 &&
        value is not "." and not ".." &&
        RepositoryPartPattern.IsMatch(value);

    /// <summary>Canonicalizes source names for the database's case-insensitive uniqueness key.</summary>
    public static string NormalizeName(string name) => name.ToUpperInvariant();
}
