using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed record ProjectTeamSyncPlan(
    string ChangeSetHash,
    ImmutableArray<SourceControlTeamSyncChange> Changes,
    ImmutableArray<ProjectTeamSyncChangeSummary> ChangeSummaries)
{
    public bool NothingToSync => Changes.IsDefaultOrEmpty;
}

internal sealed record ProjectTeamSyncRepositoryBinding(long InstallationId, long RepositoryId);

/// <summary>Projects the Projects-owned roster into reviewable `.squad/` file changes.</summary>
internal static class ProjectTeamSyncPlanner
{
    private const int MaximumFiles = 250;
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumTotalBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions HashJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds a deterministic plan without loading repository files as a second team registry.</summary>
    public static ProjectTeamSyncPlan CreatePlan(
        ProjectTeamSyncProjection projection,
        SourceControlTeamSyncSnapshot snapshot,
        ProjectTeamSyncRepositoryBinding repositoryBinding)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(repositoryBinding);
        if (repositoryBinding.InstallationId <= 0 || repositoryBinding.RepositoryId <= 0)
            throw new CoordinationException(
                "team_sync_repository_contract_invalid", StatusCodes.Status502BadGateway);
        if (!string.Equals(snapshot.Repository, projection.Authority.Repository.FullName,
                StringComparison.OrdinalIgnoreCase))
            throw new CoordinationException(
                "team_sync_repository_contract_invalid", StatusCodes.Status502BadGateway);

        // Project configuration is the sole roster; only its cast members get active team charter files.
        var desiredFiles = CreateDesiredFiles(projection);
        if (desiredFiles.Count > MaximumFiles ||
            desiredFiles.Values.Any(content => content.Length > MaximumFileBytes) ||
            desiredFiles.Values.Sum(content => (long)content.Length) > MaximumTotalBytes)
            throw new CoordinationException(
                "team_sync_projection_too_large", StatusCodes.Status409Conflict);

        var currentFiles = snapshot.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var changes = ImmutableArray.CreateBuilder<SourceControlTeamSyncChange>();
        var summaries = ImmutableArray.CreateBuilder<ProjectTeamSyncChangeSummary>();

        foreach (var (path, content) in desiredFiles)
        {
            currentFiles.TryGetValue(path, out var current);
            if (current is not null && current.Content.AsSpan().SequenceEqual(content))
                continue;

            var kind = current is null ? "added" : "modified";
            changes.Add(new SourceControlTeamSyncChange(path, current?.BlobSha, content));
            summaries.Add(new ProjectTeamSyncChangeSummary(path, kind));
        }

        // Remove stale canonical charter files, but leave histories and unrelated `.squad/` content untouched.
        var desiredPaths = desiredFiles.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var current in snapshot.Files
                     .Where(file => IsCanonicalCharterPath(file.Path) && !desiredPaths.Contains(file.Path))
                     .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            changes.Add(new SourceControlTeamSyncChange(current.Path, current.BlobSha, null));
            summaries.Add(new ProjectTeamSyncChangeSummary(current.Path, "removed"));
        }

        var orderedChanges = changes
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        var orderedSummaries = summaries
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        var changeSetHash = orderedChanges.IsDefaultOrEmpty
            ? string.Empty
            : ComputeChangeSetHash(projection, snapshot, repositoryBinding, orderedChanges);
        return new ProjectTeamSyncPlan(changeSetHash, orderedChanges, orderedSummaries);
    }

    /// <summary>Renders the configured roster and charters using safe, stable repository paths.</summary>
    private static SortedDictionary<string, byte[]> CreateDesiredFiles(ProjectTeamSyncProjection projection)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var members = projection.Members
            .OrderBy(member => member.Order)
            .ThenBy(member => member.AgentId, StringComparer.Ordinal)
            .ToArray();
        var duplicateSlugs = members
            .GroupBy(member => CreateSlug(member.Name), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var roster = new StringBuilder()
            .Append("# Squad Team\n\n")
            .Append("> ").Append(SingleLine(projection.ProjectName)).Append("\n\n")
            .Append("## Coordinator\n\n")
            .Append("| Name | Role | Notes |\n")
            .Append("|------|------|-------|\n")
            .Append("| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |\n\n")
            .Append("## Members\n\n")
            .Append("| Name | Role | Charter | Status |\n")
            .Append("|------|------|---------|--------|\n");

        foreach (var member in members)
        {
            var slug = CreateSlug(member.Name);
            if (duplicateSlugs.Contains(slug))
                slug += "-" + ShortIdentityHash(member.AgentId);
            var path = $".squad/agents/{slug}/charter.md";
            roster.Append("| ").Append(TableCell(member.Name))
                .Append(" | ").Append(TableCell(member.Role))
                .Append(" | ").Append(path)
                .Append(" | active |\n");
            var charter = $"# {SingleLine(member.Name)} - {SingleLine(member.Role)}\n\n" +
                NormalizeNewlines(member.Charter).Trim() + "\n";
            files.Add(path, Encoding.UTF8.GetBytes(charter));
        }

        roster.Append("\n## Project Context\n\n")
            .Append("- **Project:** ").Append(SingleLine(projection.ProjectName)).Append('\n')
            .Append("- **Universe:** ").Append(SingleLine(projection.CastingUniverse ?? "default")).Append('\n');
        files.Add(".squad/team.md", Encoding.UTF8.GetBytes(roster.ToString()));
        return files;
    }

    /// <summary>Hashes repository identity, head, configuration revision, paths, old blobs, and target bytes.</summary>
    private static string ComputeChangeSetHash(
        ProjectTeamSyncProjection projection,
        SourceControlTeamSyncSnapshot snapshot,
        ProjectTeamSyncRepositoryBinding repositoryBinding,
        ImmutableArray<SourceControlTeamSyncChange> changes)
    {
        // Length-stable JSON with sorted paths makes the preview token reproducible across request retries.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            projection.ProjectId,
            projection.ConfigurationRevision,
            snapshot.Repository,
            snapshot.Reference,
            snapshot.CommitSha,
            RepositoryBinding = new
            {
                repositoryBinding.InstallationId,
                repositoryBinding.RepositoryId,
            },
            Changes = changes.Select(change => new
            {
                change.Path,
                change.ExpectedBlobSha,
                ContentSha256 = change.Content is null
                    ? null
                    : Convert.ToHexStringLower(SHA256.HashData(change.Content))
            })
        }, HashJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    /// <summary>Recognizes only regular canonical member charter paths; other team files remain untouched.</summary>
    private static bool IsCanonicalCharterPath(string path)
    {
        const string agentsPrefix = ".squad/agents/";
        const string charterSuffix = "/charter.md";
        if (!path.StartsWith(agentsPrefix, StringComparison.Ordinal) ||
            !path.EndsWith(charterSuffix, StringComparison.Ordinal))
            return false;

        var memberDirectory = path[agentsPrefix.Length..^charterSuffix.Length];
        // Only direct member directories are generated; preserve nested and alumni repository content.
        return memberDirectory.Length > 0 &&
            !memberDirectory.Contains('/') &&
            !string.Equals(memberDirectory, "_alumni", StringComparison.Ordinal);
    }

    /// <summary>Creates a portable path segment and falls back to an identity hash for non-ASCII names.</summary>
    private static string CreateSlug(string value)
    {
        var result = new StringBuilder(value.Length);
        var separator = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '_')
            {
                result.Append(character);
                separator = false;
            }
            else if (!separator && result.Length > 0)
            {
                result.Append('-');
                separator = true;
            }
        }

        var slug = result.ToString().Trim('-');
        return slug.Length == 0 ? "agent-" + ShortIdentityHash(value) : slug;
    }

    /// <summary>Escapes a table cell without changing the displayed user-authored name or role.</summary>
    private static string TableCell(string value) =>
        SingleLine(value).Replace("|", "&#124;", StringComparison.Ordinal);

    /// <summary>Removes line breaks from text embedded in Markdown headings and table context.</summary>
    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>Normalizes authored charter line endings before hashing and writing repository bytes.</summary>
    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Returns a deterministic suffix for distinct identities that normalize to the same path.</summary>
    private static string ShortIdentityHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10];
}
