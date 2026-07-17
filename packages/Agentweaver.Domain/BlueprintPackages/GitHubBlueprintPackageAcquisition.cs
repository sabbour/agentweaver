using System.Text.RegularExpressions;

namespace Agentweaver.Domain.BlueprintPackages;

/// <summary>Structured GitHub repository coordinates for acquiring one package. URLs are not accepted.</summary>
public sealed record GitHubBlueprintPackageLocator(
    string Owner,
    string Repository,
    string? PackageRootPath = null,
    string? Ref = null)
{
    private static readonly Regex Name = new(
        @"\A[A-Za-z0-9](?:[A-Za-z0-9.-]{0,99})\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Validates that coordinates identify only a github.com repository and a canonical POSIX path.</summary>
    public void Validate()
    {
        if (!IsRepositoryName(Owner) || !IsRepositoryName(Repository))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.InvalidLocator,
                "GitHub owner and repository names are invalid.");
        GitHubBlueprintPackagePath.ValidateRoot(PackageRootPath);
        if (Ref is not null && !GitHubBlueprintPackagePath.IsSafeGitRef(Ref))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.InvalidLocator,
                "GitHub ref is invalid.");
    }

    internal static bool IsRepositoryName(string? value) =>
        value is not null && value.Length <= 100 && Name.IsMatch(value)
        && value[^1] != '.' && value[^1] != '-';
}

/// <summary>An immutable Git object snapshot resolved from a requested ref.</summary>
public sealed record GitHubBlueprintPackageCommit(string CommitSha, string TreeSha);

/// <summary>One Git tree entry returned for an immutable tree object.</summary>
public sealed record GitHubBlueprintPackageTreeEntry(string Path, string Type, string Mode, string Sha, long? Size);

/// <summary>An immutable Git tree object. Truncated package listings are never accepted.</summary>
public sealed record GitHubBlueprintPackageTree(
    string Sha,
    IReadOnlyList<GitHubBlueprintPackageTreeEntry> Entries,
    bool IsTruncated);

/// <summary>Bytes returned for one Git blob object.</summary>
public sealed record GitHubBlueprintPackageBlob(string Sha, byte[] Bytes);

/// <summary>Authenticated GitHub API boundary used by Blueprint package acquisition.</summary>
public interface IGitHubBlueprintPackageClient
{
    Task<GitHubBlueprintPackageCommit> ResolveCommitAsync(
        GitHubBlueprintPackageLocator locator,
        CancellationToken ct = default);
    Task<GitHubBlueprintPackageTree> ReadTreeAsync(
        GitHubBlueprintPackageLocator locator,
        string commitSha,
        string treeSha,
        bool recursive,
        CancellationToken ct = default);
    Task<GitHubBlueprintPackageBlob> ReadBlobAsync(
        GitHubBlueprintPackageLocator locator,
        string commitSha,
        string blobSha,
        CancellationToken ct = default);
}

public enum GitHubBlueprintPackageAcquisitionFailure
{
    InvalidLocator,
    AuthenticationRequired,
    NotFound,
    Forbidden,
    RateLimited,
    RefMoved,
    ObjectChanged,
    MalformedContent,
    Transport,
}

/// <summary>Fail-closed acquisition error with a stable category and credential-free message.</summary>
public sealed class GitHubBlueprintPackageAcquisitionException : Exception
{
    public GitHubBlueprintPackageAcquisitionException(
        GitHubBlueprintPackageAcquisitionFailure failure,
        string message)
        : base(message) => Failure = failure;

    public GitHubBlueprintPackageAcquisitionFailure Failure { get; }
}

public static class GitHubBlueprintPackagePath
{
    private static readonly Regex Sha = new(@"\A[a-f0-9]{40}\z", RegexOptions.CultureInvariant);

    public static void ValidateRoot(string? value)
    {
        if (value is null || value.Length == 0) return;
        if (!IsCanonicalPosixPath(value))
            throw new GitHubBlueprintPackageAcquisitionException(
                GitHubBlueprintPackageAcquisitionFailure.InvalidLocator,
                "Package root path must be a canonical relative POSIX path.");
    }

    public static bool IsCanonicalPosixPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 240 || value[0] == '/' || value[^1] == '/'
            || value.Contains('\\') || !value.IsNormalized())
            return false;
        return value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".."
            && !segment.Any(char.IsControl));
    }

    public static bool IsFullSha(string? value) => value is not null && Sha.IsMatch(value);

    public static bool IsSafeGitRef(string value) =>
        value.Length is > 0 and <= 255
        && value[0] != '-' && value[^1] != '.'
        && !value.Contains('\\') && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && !value.EndsWith(".lock", StringComparison.Ordinal)
        && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
            || character is '~' or '^' or ':' or '?' or '*' or '[');
}
