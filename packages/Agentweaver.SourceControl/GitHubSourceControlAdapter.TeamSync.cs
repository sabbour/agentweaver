using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed partial class GitHubSourceControlAdapter
{
    private const int TeamSyncMaximumFiles = 250;
    private const int TeamSyncMaximumFileBytes = 512 * 1024;
    private const int TeamSyncMaximumTotalBytes = 4 * 1024 * 1024;
    private const int TeamSyncMaximumResponseBytes = 6 * 1024 * 1024;

    /// <summary>Reads regular `.squad/` blobs at one repository ref without changing GitHub state.</summary>
    public async Task<SourceControlTeamSyncSnapshot> ReadTeamSyncTreeAsync(
        SourceControlRepositoryIdentity repository,
        string reference,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        // Validate caller-controlled ref data before making any authenticated provider request.
        ValidateTeamSyncRepositoryRequest(repository, reference, credential);
        var index = await ReadTeamSyncIndexAsync(repository, reference, credential, cancellationToken)
            .ConfigureAwait(false);
        var files = ImmutableArray.CreateBuilder<SourceControlTeamSyncFile>(index.Files.Count);
        foreach (var entry in index.Files.Values.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                RepositoryUri(repository, $"git/blobs/{Uri.EscapeDataString(entry.BlobSha)}"),
                credential,
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "Reading the GitHub team-sync blob");
            using var document = await ReadTeamSyncJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("encoding", out var encoding) ||
                encoding.ValueKind != JsonValueKind.String ||
                encoding.GetString() != "base64" ||
                !root.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
                throw InvalidResponse("GitHub returned an invalid team-sync blob.");

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
                    "GitHub returned an invalid team-sync blob.",
                    response.StatusCode,
                    exception);
            }
            if (bytes.Length != entry.Size || !MatchesGitBlobSha(entry.BlobSha, bytes))
                throw InvalidResponse("GitHub returned team-sync blob bytes that do not match the tree.");
            files.Add(new SourceControlTeamSyncFile(entry.Path, entry.BlobSha, bytes, entry.Executable));
        }

        return new SourceControlTeamSyncSnapshot(
            repository.FullName,
            reference,
            index.CommitSha,
            files.ToImmutable());
    }

    /// <summary>Commits `.squad/` changes only after the previewed head and every expected blob still match.</summary>
    public async Task<SourceControlTeamSyncCommit> CommitTeamSyncChangesAsync(
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        SourceControlTeamSyncCommitRequest request,
        CancellationToken cancellationToken)
    {
        // Validate every proposed path and byte limit before the preflight reads or any GitHub write.
        ValidateTeamSyncCommitRequest(repository, credential, request);
        var index = await ReadTeamSyncIndexAsync(repository, request.Branch, credential, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(index.CommitSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase))
            throw TeamSyncConflict("The repository branch changed after the team-sync preview.");

        foreach (var change in request.Changes)
        {
            index.Files.TryGetValue(change.Path, out var current);
            if (change.ExpectedBlobSha is null
                    ? current is not null
                    : current is null || !string.Equals(
                        current.BlobSha, change.ExpectedBlobSha, StringComparison.OrdinalIgnoreCase))
                throw TeamSyncConflict("A team-sync file changed after the preview.");
        }

        ValidateFinalTeamSyncPaths(index.Files, request.Changes);

        // All rejectable path, size, base, and blob checks finish before the first write request.
        var treeEntries = new List<object>(request.Changes.Length);
        foreach (var change in request.Changes)
        {
            index.Files.TryGetValue(change.Path, out var current);
            if (change.Content is null)
            {
                treeEntries.Add(new
                {
                    path = change.Path,
                    mode = current?.Mode ?? "100644",
                    type = "blob",
                    sha = (string?)null
                });
                continue;
            }

            using var blobResponse = await SendAsync(
                HttpMethod.Post,
                RepositoryUri(repository, "git/blobs"),
                credential,
                JsonContent.Create(new { content = Convert.ToBase64String(change.Content), encoding = "base64" }),
                cancellationToken,
                operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
            EnsureSuccess(blobResponse, "Creating a GitHub team-sync blob", writeMayHaveChangedRemoteState: true);
            using var blobDocument = await ReadTeamSyncJsonAsync(blobResponse, cancellationToken).ConfigureAwait(false);
            var blobSha = ReadTeamSyncSha(blobDocument.RootElement, "sha");
            treeEntries.Add(new
            {
                path = change.Path,
                mode = current?.Mode ?? "100644",
                type = "blob",
                sha = blobSha
            });
        }

        using var treeResponse = await SendAsync(
            HttpMethod.Post,
            RepositoryUri(repository, "git/trees"),
            credential,
            JsonContent.Create(new { base_tree = index.TreeSha, tree = treeEntries }),
            cancellationToken,
            operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
        EnsureSuccess(treeResponse, "Creating a GitHub team-sync tree", writeMayHaveChangedRemoteState: true);
        using var treeDocument = await ReadTeamSyncJsonAsync(treeResponse, cancellationToken).ConfigureAwait(false);
        var treeSha = ReadTeamSyncSha(treeDocument.RootElement, "sha");

        using var commitResponse = await SendAsync(
            HttpMethod.Post,
            RepositoryUri(repository, "git/commits"),
            credential,
            JsonContent.Create(new
            {
                message = request.Message,
                tree = treeSha,
                parents = new[] { index.CommitSha }
            }),
            cancellationToken,
            operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
        EnsureSuccess(commitResponse, "Creating a GitHub team-sync commit", writeMayHaveChangedRemoteState: true);
        using var commitDocument = await ReadTeamSyncJsonAsync(commitResponse, cancellationToken).ConfigureAwait(false);
        var commitSha = ReadTeamSyncSha(commitDocument.RootElement, "sha");

        // GitHub's non-forced ref update is the atomic branch write and rejects a concurrent advance.
        using var refResponse = await SendAsync(
            HttpMethod.Patch,
            RepositoryUri(repository, "git/refs/heads/" + EscapeGitHubPath(request.Branch)),
            credential,
            JsonContent.Create(new { sha = commitSha, force = false }),
            cancellationToken,
            operationMayHaveChangedRemoteState: true).ConfigureAwait(false);
        EnsureSuccess(refResponse, "Updating the GitHub team-sync branch", writeMayHaveChangedRemoteState: true);
        using var refDocument = await ReadTeamSyncJsonAsync(refResponse, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                ReadTeamSyncSha(ReadObject(refDocument.RootElement, "object"), "sha"),
                commitSha,
                StringComparison.OrdinalIgnoreCase))
            throw new SourceControlOperationException(
                SourceControlFailureCode.RemoteOutcomeUncertain,
                "GitHub returned an unexpected team-sync ref update.");

        return new SourceControlTeamSyncCommit(
            commitSha,
            request.Branch,
            new Uri($"https://github.com/{repository.Owner}/{repository.Name}/commit/{commitSha}"));
    }

    private async Task<TeamSyncTreeIndex> ReadTeamSyncIndexAsync(
        SourceControlRepositoryIdentity repository,
        string reference,
        SecretCredential credential,
        CancellationToken cancellationToken)
    {
        // Resolve the ref to immutable commit and tree SHAs before reading any file entries.
        using var commitResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, "commits/" + EscapeGitHubPath(reference)),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(commitResponse, "Reading the GitHub team-sync ref");
        using var commitDocument = await ReadTeamSyncJsonAsync(commitResponse, cancellationToken).ConfigureAwait(false);
        var commitSha = ReadTeamSyncSha(commitDocument.RootElement, "sha");
        var treeSha = ReadTeamSyncSha(ReadObject(ReadObject(commitDocument.RootElement, "commit"), "tree"), "sha");

        using var treeResponse = await SendAsync(
            HttpMethod.Get,
            RepositoryUri(repository, $"git/trees/{Uri.EscapeDataString(treeSha)}?recursive=1"),
            credential,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(treeResponse, "Reading the GitHub team-sync tree");
        using var treeDocument = await ReadTeamSyncJsonAsync(treeResponse, cancellationToken).ConfigureAwait(false);
        var root = treeDocument.RootElement;
        if (!root.TryGetProperty("truncated", out var truncated) ||
            truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            truncated.GetBoolean() ||
            !root.TryGetProperty("tree", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw InvalidResponse("GitHub returned an incomplete team-sync tree.");

        var files = new Dictionary<string, TeamSyncTreeEntry>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
                throw InvalidResponse("GitHub returned an invalid team-sync tree entry.");
            var path = pathElement.GetString()!;
            var isSquadRoot = path == ".squad";
            if (!isSquadRoot && !path.StartsWith(".squad/", StringComparison.Ordinal))
                continue;
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("mode", out var modeElement) ||
                modeElement.ValueKind != JsonValueKind.String)
                throw InvalidResponse("GitHub returned an invalid team-sync tree entry.");
            var type = typeElement.GetString()!;
            var mode = modeElement.GetString()!;
            if (isSquadRoot)
            {
                if (type != "tree" || mode != "040000")
                    throw new SourceControlOperationException(
                        SourceControlFailureCode.InvalidBinding,
                        "The .squad root is not a regular repository directory.");
                continue;
            }
            ValidateTeamSyncPath(path);
            if (type == "tree")
                continue;
            if (type != "blob" || mode == "120000" || mode == "160000" ||
                mode is not ("100644" or "100755"))
                throw new SourceControlOperationException(
                    SourceControlFailureCode.InvalidBinding,
                    "The .squad/ tree contains a symlink or unsupported entry.");
            var blobSha = ReadTeamSyncSha(item, "sha");
            if (!item.TryGetProperty("size", out var sizeElement) ||
                !sizeElement.TryGetInt32(out var size) ||
                size < 0 || size > TeamSyncMaximumFileBytes)
                throw new SourceControlOperationException(
                    SourceControlFailureCode.InvalidRequest,
                    "A .squad/ file exceeds the permitted size.");
            totalBytes = checked(totalBytes + size);
            if (totalBytes > TeamSyncMaximumTotalBytes || files.Count >= TeamSyncMaximumFiles)
                throw new SourceControlOperationException(
                    SourceControlFailureCode.InvalidRequest,
                    "The .squad/ tree exceeds the permitted size.");
            if (!files.TryAdd(path, new TeamSyncTreeEntry(path, blobSha, mode, size)))
                throw InvalidResponse("GitHub returned duplicate .squad/ paths.");
        }
        return new TeamSyncTreeIndex(commitSha, treeSha, files);
    }

    private static async Task<JsonDocument> ReadTeamSyncJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Bound provider responses before parsing so a repository cannot force unbounded memory use.
        if (response.Content.Headers.ContentLength is > TeamSyncMaximumResponseBytes)
            throw InvalidResponse("GitHub returned an oversized team-sync response.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > TeamSyncMaximumResponseBytes)
                throw InvalidResponse("GitHub returned an oversized team-sync response.");
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
                "GitHub returned malformed team-sync JSON.",
                response.StatusCode,
                exception);
        }
    }

    private static void ValidateTeamSyncRepositoryRequest(
        SourceControlRepositoryIdentity repository,
        string reference,
        SecretCredential credential)
    {
        // Team sync operates on an explicit repository and ref, never an ambient checkout.
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsValidGitReference(reference))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The team-sync repository ref is invalid.");
    }

    private static void ValidateTeamSyncCommitRequest(
        SourceControlRepositoryIdentity repository,
        SecretCredential credential,
        SourceControlTeamSyncCommitRequest request)
    {
        // Reject malformed sets before reading or writing so invalid requests have no remote side effects.
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidGitReference(request.Branch) ||
            !IsGitSha(request.ExpectedBaseSha) ||
            string.IsNullOrWhiteSpace(request.Message) ||
            request.Message.Length > 200 ||
            request.Message.Any(char.IsControl) ||
            request.Changes.IsDefaultOrEmpty ||
            request.Changes.Length > TeamSyncMaximumFiles)
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "The team-sync commit request is invalid.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var change in request.Changes)
        {
            if (change is null)
                throw new SourceControlOperationException(
                    SourceControlFailureCode.InvalidRequest,
                    "The team-sync commit request contains an empty change.");
            ValidateTeamSyncPath(change.Path);
            if (!paths.Add(change.Path) ||
                change.ExpectedBlobSha is not null && !IsGitSha(change.ExpectedBlobSha) ||
                change.Content is null && change.ExpectedBlobSha is null ||
                change.Content is { Length: > TeamSyncMaximumFileBytes })
                throw new SourceControlOperationException(
                    SourceControlFailureCode.InvalidRequest,
                    "The team-sync commit request contains an invalid change.");
            if (change.Content is not null)
            {
                totalBytes = checked(totalBytes + change.Content.Length);
                if (totalBytes > TeamSyncMaximumTotalBytes)
                    throw new SourceControlOperationException(
                        SourceControlFailureCode.InvalidRequest,
                        "The team-sync commit exceeds the permitted size.");
            }
        }
    }

    private static void ValidateTeamSyncPath(string path)
    {
        // Slash-delimited validation rejects traversal and platform-specific path aliases.
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 1024 ||
            !path.StartsWith(".squad/", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Any(character => char.IsControl(character) || character == '\0') ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidRequest,
                "Team sync accepts only regular paths beneath .squad/.");
    }

    private static void ValidateFinalTeamSyncPaths(
        IReadOnlyDictionary<string, TeamSyncTreeEntry> currentFiles,
        ImmutableArray<SourceControlTeamSyncChange> changes)
    {
        // Check the complete post-commit file set so GitHub cannot reject a file/directory collision after blob writes.
        var finalPaths = new HashSet<string>(currentFiles.Keys, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.Content is null)
                finalPaths.Remove(change.Path);
            else
                finalPaths.Add(change.Path);
        }

        foreach (var path in finalPaths)
        {
            var separator = path.IndexOf('/');
            while (separator >= 0)
            {
                if (finalPaths.Contains(path[..separator]))
                    throw new SourceControlOperationException(
                        SourceControlFailureCode.InvalidRequest,
                        "The team-sync changes contain a file and directory path conflict.");
                separator = path.IndexOf('/', separator + 1);
            }
        }
    }

    private static bool IsValidGitReference(string value)
    {
        // Limit refs to ordinary branch/ref characters and reject Git's ambiguous revision syntax.
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value.StartsWith('/') || value.EndsWith('/') || value.EndsWith('.') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment.Length == 0 || segment.StartsWith('.') ||
                segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or '/')))
            return false;
        return true;
    }

    private static string EscapeGitHubPath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static string ReadTeamSyncSha(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !IsGitSha(value.GetString()))
            throw InvalidResponse("GitHub returned an invalid team-sync SHA.");
        return value.GetString()!;
    }

    private static bool MatchesGitBlobSha(string expected, byte[] content)
    {
        // Verify GitHub's base64 payload against the Git object hash, not only the JSON metadata.
        var header = Encoding.ASCII.GetBytes($"blob {content.Length.ToString(CultureInfo.InvariantCulture)}\0");
        var bytes = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        Buffer.BlockCopy(content, 0, bytes, header.Length, content.Length);
        return string.Equals(
            Convert.ToHexStringLower(SHA1.HashData(bytes)),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static SourceControlOperationException TeamSyncConflict(string message) =>
        new(SourceControlFailureCode.Conflict, message, HttpStatusCode.Conflict);

    private sealed record TeamSyncTreeEntry(string Path, string BlobSha, string Mode, int Size)
    {
        public bool Executable => Mode == "100755";
    }

    private sealed record TeamSyncTreeIndex(
        string CommitSha,
        string TreeSha,
        Dictionary<string, TeamSyncTreeEntry> Files);
}
