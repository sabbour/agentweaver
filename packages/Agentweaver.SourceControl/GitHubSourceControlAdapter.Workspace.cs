using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed partial class GitHubSourceControlAdapter
{
    private const int WorkspaceBranchPageSize = 49;
    private const int WorkspaceTagPageSize = 50;
    private const int WorkspaceMaximumRefsPage = 10_000;
    private const int WorkspaceMaximumTagDepth = 8;
    private const int WorkspaceMaximumTreeNodes = 25_000;
    private const int WorkspaceMaximumTreeResponseBytes = 8 * 1024 * 1024;
    private const int WorkspaceMaximumFileBytes = 1024 * 1024;
    private const int WorkspaceMaximumFileResponseBytes = 2 * 1024 * 1024;
    private const int WorkspaceBinaryProbeBytes = 8 * 1024;

    public async Task<SourceControlWorkspaceRefsPage> ReadWorkspaceRefsAsync(
        SourceControlRepositoryIdentity repository,
        string defaultBranch,
        string? cursor,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsValidWorkspaceGitReference(defaultBranch))
            throw WorkspaceInvalidRequest("The configured default branch is invalid.");

        var page = ParseWorkspacePage(cursor);
        SourceControlWorkspaceRef? defaultRef = null;
        if (page == 1)
        {
            using var defaultResponse = await SendAsync(
                HttpMethod.Get,
                RepositoryUri(repository, "branches/" + EscapeGitHubPath(defaultBranch)),
                credential,
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(defaultResponse, "Reading the GitHub default branch");
            using var defaultDocument = await ReadWorkspaceJsonAsync(
                defaultResponse, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            defaultRef = ReadWorkspaceRef(defaultDocument.RootElement, SourceControlWorkspaceRefKind.Branch, true);
            if (!string.Equals(defaultRef.Name, defaultBranch, StringComparison.Ordinal))
                throw WorkspaceInvalidResponse("GitHub returned a different default branch.");
        }

        using var branchesResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, string.Create(
                CultureInfo.InvariantCulture,
                $"branches?per_page={WorkspaceBranchPageSize}&page={page}")),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(branchesResponse, "Listing GitHub branches");
        var branchesHaveNext = HasNextLink(branchesResponse);
        using var branchesDocument = await ReadWorkspaceJsonAsync(
            branchesResponse, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var branches = ReadWorkspaceRefs(
            branchesDocument.RootElement, SourceControlWorkspaceRefKind.Branch, WorkspaceBranchPageSize);

        using var tagsResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, string.Create(
                CultureInfo.InvariantCulture,
                $"tags?per_page={WorkspaceTagPageSize}&page={page}")),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(tagsResponse, "Listing GitHub tags");
        var tagsHaveNext = HasNextLink(tagsResponse);
        using var tagsDocument = await ReadWorkspaceJsonAsync(
            tagsResponse, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var listedTags = ReadWorkspaceRefs(
            tagsDocument.RootElement, SourceControlWorkspaceRefKind.Tag, WorkspaceTagPageSize);
        var tags = new SourceControlWorkspaceRef[listedTags.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, listedTags.Count),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8 },
            async (index, ct) =>
            {
                tags[index] = await ResolveWorkspaceTagAsync(
                    repository, listedTags[index].Name, credential, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var items = new List<SourceControlWorkspaceRef>(WorkspaceBranchPageSize + WorkspaceTagPageSize + 1);
        if (defaultRef is not null)
            items.Add(defaultRef);
        items.AddRange(branches.Where(reference =>
            !string.Equals(reference.Name, defaultBranch, StringComparison.Ordinal)));
        items.AddRange(tags);
        if (items.Count > 100)
            throw WorkspaceInvalidResponse("GitHub returned more refs than the requested page bound.");
        if (page == WorkspaceMaximumRefsPage && (branchesHaveNext || tagsHaveNext))
            throw WorkspaceResponseTooLarge("The repository has too many refs to browse.");

        return new SourceControlWorkspaceRefsPage(
            repository.FullName,
            defaultBranch,
            [.. items],
            branchesHaveNext || tagsHaveNext
                ? (page + 1).ToString(CultureInfo.InvariantCulture)
                : null);
    }

    private async Task<SourceControlWorkspaceRef> ResolveWorkspaceTagAsync(
        SourceControlRepositoryIdentity repository,
        string name,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        var expectedRef = "refs/tags/" + name;
        using var referenceResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, "git/ref/tags/" + EscapeGitHubPath(name)),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(referenceResponse, "Resolving a GitHub repository tag");
        using var referenceDocument = await ReadWorkspaceJsonAsync(
            referenceResponse, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (!referenceDocument.RootElement.TryGetProperty("ref", out var referenceName) ||
            referenceName.ValueKind != JsonValueKind.String ||
            !string.Equals(referenceName.GetString(), expectedRef, StringComparison.Ordinal))
            throw WorkspaceInvalidResponse("GitHub resolved a different repository tag.");

        var target = ReadWorkspaceGitObject(referenceDocument.RootElement, "object");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; target.Type == "tag"; depth++)
        {
            if (depth == WorkspaceMaximumTagDepth || !seen.Add(target.Sha))
                throw WorkspaceInvalidResponse("GitHub returned an invalid annotated tag chain.");
            using var tagResponse = await SendAsync(
                HttpMethod.Get,
                RepositoryUri(repository, "git/tags/" + Uri.EscapeDataString(target.Sha)),
                credential,
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(tagResponse, "Resolving an annotated GitHub repository tag");
            using var tagDocument = await ReadWorkspaceJsonAsync(
                tagResponse, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    ReadTeamSyncSha(tagDocument.RootElement, "sha"),
                    target.Sha,
                    StringComparison.OrdinalIgnoreCase))
                throw WorkspaceInvalidResponse("GitHub returned a different annotated tag.");
            target = ReadWorkspaceGitObject(tagDocument.RootElement, "object");
        }

        if (target.Type != "commit")
            throw WorkspaceInvalidResponse("GitHub repository tags must resolve to commits.");
        return new SourceControlWorkspaceRef(name, SourceControlWorkspaceRefKind.Tag, target.Sha, false);
    }

    private static (string Type, string Sha) ReadWorkspaceGitObject(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
            throw WorkspaceInvalidResponse("GitHub returned an invalid repository ref target.");
        return (type.GetString()!, ReadTeamSyncSha(value, "sha"));
    }

    public async Task<SourceControlWorkspaceTree> ReadWorkspaceTreeAsync(
        SourceControlRepositoryIdentity repository,
        string commitSha,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        ValidateWorkspaceCommitSha(commitSha);
        var index = await ReadWorkspaceTreeIndexAsync(repository, commitSha, credential, cancellationToken)
            .ConfigureAwait(false);
        return new SourceControlWorkspaceTree(
            repository.FullName,
            commitSha.ToLowerInvariant(),
            index.CommitSha,
            index.Items);
    }

    public async Task<SourceControlWorkspaceFile> ReadWorkspaceFileAsync(
        SourceControlRepositoryIdentity repository,
        string commitSha,
        string path,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        ValidateWorkspaceCommitSha(commitSha);
        ValidateWorkspacePath(path);

        // Verify the exact path is a regular blob reachable from this commit before reading it.
        var index = await ReadWorkspaceTreeIndexAsync(repository, commitSha, credential, cancellationToken)
            .ConfigureAwait(false);
        var entry = index.Items.SingleOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.Ordinal));
        if (entry is null)
            throw new SourceControlOperationException(
                SourceControlFailureCode.NotFound, "The repository file was not found.");
        if (entry.Kind != SourceControlWorkspaceEntryKind.File || entry.BlobSha is null)
            throw WorkspaceInvalidRequest("Only regular repository files can be viewed.");
        if (entry.Size is null or < 0 or > WorkspaceMaximumFileBytes)
            throw WorkspaceResponseTooLarge("The repository file exceeds the 1 MiB viewer limit.");

        using var response = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, $"git/blobs/{Uri.EscapeDataString(entry.BlobSha)}"),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Reading the GitHub repository file");
        using var document = await ReadWorkspaceJsonAsync(
            response, WorkspaceMaximumFileResponseBytes, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("encoding", out var encoding) ||
            encoding.ValueKind != JsonValueKind.String ||
            encoding.GetString() != "base64" ||
            !root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            throw WorkspaceInvalidResponse("GitHub returned an invalid repository file.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(string.Concat(content.GetString()!.Where(character =>
                !char.IsWhiteSpace(character))));
        }
        catch (FormatException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidResponse,
                "GitHub returned an invalid repository file.",
                response.StatusCode,
                exception);
        }
        if (bytes.Length != entry.Size || !MatchesGitBlobSha(entry.BlobSha, bytes))
            throw WorkspaceInvalidResponse("GitHub returned file bytes that do not match the selected commit.");

        var sampleLength = Math.Min(bytes.Length, WorkspaceBinaryProbeBytes);
        var isBinary = IsBinarySample(bytes.AsSpan(0, sampleLength), sampleLength == bytes.Length);
        return new SourceControlWorkspaceFile(
            repository.FullName,
            path,
            index.CommitSha,
            isBinary ? null : Encoding.UTF8.GetString(bytes),
            isBinary,
            isBinary ? null : WorkspaceLanguage(path),
            bytes.Length);
    }

    private async Task<WorkspaceTreeIndex> ReadWorkspaceTreeIndexAsync(
        SourceControlRepositoryIdentity repository,
        string commitSha,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        using var commitResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, "commits/" + Uri.EscapeDataString(commitSha)),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(commitResponse, "Resolving the GitHub repository commit");
        using var commitDocument = await ReadWorkspaceJsonAsync(
            commitResponse, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var resolvedCommitSha = ReadTeamSyncSha(commitDocument.RootElement, "sha");
        if (!string.Equals(resolvedCommitSha, commitSha, StringComparison.OrdinalIgnoreCase))
            throw WorkspaceInvalidResponse("GitHub resolved a different repository commit.");
        var treeSha = ReadTeamSyncSha(
            ReadObject(ReadObject(commitDocument.RootElement, "commit"), "tree"), "sha");

        using var treeResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, $"git/trees/{Uri.EscapeDataString(treeSha)}?recursive=1"),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(treeResponse, "Reading the GitHub repository tree");
        using var treeDocument = await ReadWorkspaceJsonAsync(
            treeResponse, WorkspaceMaximumTreeResponseBytes, cancellationToken).ConfigureAwait(false);
        var root = treeDocument.RootElement;
        if (!root.TryGetProperty("sha", out var responseTreeSha) ||
            responseTreeSha.ValueKind != JsonValueKind.String ||
            !string.Equals(responseTreeSha.GetString(), treeSha, StringComparison.OrdinalIgnoreCase))
            throw WorkspaceInvalidResponse("GitHub returned a different repository tree.");
        if (!root.TryGetProperty("truncated", out var truncated) ||
            truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !root.TryGetProperty("tree", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw WorkspaceInvalidResponse("GitHub returned an invalid repository tree.");
        if (truncated.GetBoolean() || entries.GetArrayLength() > WorkspaceMaximumTreeNodes)
            throw WorkspaceResponseTooLarge("The repository tree exceeds the 25,000-entry limit.");

        var items = ImmutableArray.CreateBuilder<SourceControlWorkspaceTreeNode>(entries.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("mode", out var modeElement) ||
                modeElement.ValueKind != JsonValueKind.String)
                throw WorkspaceInvalidResponse("GitHub returned an invalid repository tree entry.");
            var path = pathElement.GetString()!;
            if (!IsValidWorkspacePath(path) || !paths.Add(path))
                throw WorkspaceInvalidResponse("GitHub returned an unsafe or duplicate repository path.");

            var type = typeElement.GetString();
            var mode = modeElement.GetString();
            var kind = (type, mode) switch
            {
                ("tree", "040000") => SourceControlWorkspaceEntryKind.Directory,
                ("blob", "100644" or "100755") => SourceControlWorkspaceEntryKind.File,
                ("blob", "120000") => SourceControlWorkspaceEntryKind.SymbolicLink,
                ("commit", "160000") => SourceControlWorkspaceEntryKind.Submodule,
                _ => SourceControlWorkspaceEntryKind.Unsupported
            };
            string? blobSha = null;
            long? size = null;
            if (kind is SourceControlWorkspaceEntryKind.File or
                SourceControlWorkspaceEntryKind.SymbolicLink or SourceControlWorkspaceEntryKind.Submodule)
                blobSha = ReadTeamSyncSha(item, "sha");
            if (kind == SourceControlWorkspaceEntryKind.File)
            {
                if (!item.TryGetProperty("size", out var sizeElement) ||
                    !sizeElement.TryGetInt64(out var length) ||
                    length < 0)
                    throw WorkspaceInvalidResponse("GitHub returned an invalid repository file size.");
                size = length;
            }
            items.Add(new SourceControlWorkspaceTreeNode(path, kind, size, blobSha));
        }
        return new WorkspaceTreeIndex(resolvedCommitSha, items.ToImmutable());
    }

    private static SourceControlWorkspaceRef ReadWorkspaceRef(
        JsonElement element,
        SourceControlWorkspaceRefKind kind,
        bool isDefault)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            !IsValidWorkspaceGitReference(nameElement.GetString()) ||
            !element.TryGetProperty("commit", out var commit) ||
            commit.ValueKind != JsonValueKind.Object)
            throw WorkspaceInvalidResponse("GitHub returned an invalid repository ref.");
        return new SourceControlWorkspaceRef(
            nameElement.GetString()!,
            kind,
            ReadTeamSyncSha(commit, "sha"),
            isDefault);
    }

    private static bool IsValidWorkspaceGitReference(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 || value == "@" ||
            value.StartsWith('/') || value.EndsWith('/') || value.EndsWith('.') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith('.') ||
                segment.EndsWith(".lock", StringComparison.Ordinal)) ||
            value.Any(character => character <= ' ' || character == '\u007f' ||
                character is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            return false;
        return true;
    }

    private static List<SourceControlWorkspaceRef> ReadWorkspaceRefs(
        JsonElement root,
        SourceControlWorkspaceRefKind kind,
        int maximum)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > maximum)
            throw WorkspaceInvalidResponse("GitHub returned an invalid repository refs page.");
        return root.EnumerateArray().Select(element => ReadWorkspaceRef(element, kind, false)).ToList();
    }

    private static async Task<JsonDocument> ReadWorkspaceJsonAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            throw WorkspaceResponseTooLarge("GitHub returned a response larger than the permitted bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw WorkspaceResponseTooLarge("GitHub returned a response larger than the permitted bound.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        try
        {
            return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        }
        catch (JsonException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidResponse,
                "GitHub returned malformed repository JSON.",
                response.StatusCode,
                exception);
        }
    }

    private static bool HasNextLink(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values) &&
        values.Any(value => value.Split(',').Any(link =>
            link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase) ||
            link.Contains("rel=next", StringComparison.OrdinalIgnoreCase)));

    private static int ParseWorkspacePage(string? cursor)
    {
        if (cursor is null)
            return 1;
        if (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var page) ||
            page is < 2 or > WorkspaceMaximumRefsPage)
            throw WorkspaceInvalidRequest("The repository refs cursor is invalid.");
        return page;
    }

    private static void ValidateWorkspaceCommitSha(string commitSha)
    {
        if (!IsGitSha(commitSha))
            throw WorkspaceInvalidRequest("The repository commit must be a full Git SHA.");
    }

    private static void ValidateWorkspacePath(string path)
    {
        if (!IsValidWorkspacePath(path) || HasEncodedPathStructure(path))
            throw WorkspaceInvalidRequest("The repository file path is invalid.");
    }

    private static bool IsValidWorkspacePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 1024 ||
            path.StartsWith('/') ||
            path.EndsWith('/') ||
            path.Contains('\\') ||
            path.Any(char.IsControl))
            return false;
        var segments = path.Split('/');
        return segments.Length <= 64 && segments.All(segment => segment is not ("" or "." or ".."));
    }

    private static bool HasEncodedPathStructure(string path)
    {
        var candidate = path;
        for (var depth = 0; depth < 8; depth++)
        {
            var decoded = Uri.UnescapeDataString(candidate);
            if (string.Equals(decoded, candidate, StringComparison.Ordinal))
                return false;
            if (decoded.Count(character => character == '/') != candidate.Count(character => character == '/') ||
                decoded.Contains('\\') ||
                decoded.Any(char.IsControl) ||
                decoded.Split('/').Any(segment => segment is "" or "." or ".."))
                return true;
            candidate = decoded;
        }
        return Uri.UnescapeDataString(candidate) != candidate;
    }

    private static bool IsBinarySample(ReadOnlySpan<byte> sample, bool isCompleteFile)
    {
        if (sample.Contains((byte)0))
            return true;
        try
        {
            _ = new UTF8Encoding(false, true).GetDecoder().GetCharCount(sample, isCompleteFile);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    private static string WorkspaceLanguage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".css" => "css",
            ".html" => "html",
            ".js" or ".mjs" or ".cjs" => "javascript",
            ".json" => "json",
            ".md" or ".markdown" => "markdown",
            ".py" => "python",
            ".sh" => "bash",
            ".sql" => "sql",
            ".ts" => "typescript",
            ".tsx" => "tsx",
            ".xml" => "xml",
            ".yaml" or ".yml" => "yaml",
            _ => "plaintext"
        };

    private static SourceControlOperationException WorkspaceInvalidRequest(string message) =>
        new(SourceControlFailureCode.InvalidRequest, message);

    private static SourceControlOperationException WorkspaceInvalidResponse(string message) =>
        new(SourceControlFailureCode.InvalidResponse, message);

    private static SourceControlOperationException WorkspaceResponseTooLarge(string message) =>
        new(SourceControlFailureCode.ResponseTooLarge, message);

    private sealed record WorkspaceTreeIndex(
        string CommitSha,
        ImmutableArray<SourceControlWorkspaceTreeNode> Items);
}
